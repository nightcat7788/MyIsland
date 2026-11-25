// MyIsland macOS 温度探针（无 root）
//
// 两个真实硬件通道（macmon v0.12 同款协议，2026-11-23 实测 macOS 27.2 Apple Silicon）：
//  ┌ IOHID 温度传感器：CPU(PMU tdie*/pACC/eACC)、SSD(NAND)
//  │   IOHIDServiceClientCopyEvent(sc, 15, 0, 0) + IOHIDEventGetFloatValue(ev, 15<<16)
//  └ SMC（AppleSMCKeysEndpoint，selector 2 + KeyData 80B）：GPU(Tg*)、CPU 备用(Tp*)
//      key_count via #KEY + data8=5；枚举 data8=8；info data8=9；读值 data8=5
//
// 输出（单行，供 C# 解析）：CPU=<c> GPU=<c> SSD=<c>   （无读数则该项空）
// 编译：clang -O2 -framework IOKit -framework CoreFoundation -o island-thermal thermal.m

#import <stdio.h>
#import <string.h>
#import <IOKit/IOKitLib.h>
#import <CoreFoundation/CoreFoundation.h>

// ---------- IOHID 通道 ----------
typedef void* HidClient;
typedef void* HidService;
typedef void* HidEvent;
extern HidClient IOHIDEventSystemClientCreate(CFAllocatorRef);
extern int       IOHIDEventSystemClientSetMatching(HidClient, CFDictionaryRef);
extern CFArrayRef IOHIDEventSystemClientCopyServices(HidClient);
extern CFStringRef IOHIDServiceClientCopyProperty(HidService, CFStringRef);
extern HidEvent  IOHIDServiceClientCopyEvent(HidService, long long, int, long long);
extern double    IOHIDEventGetFloatValue(HidEvent, long long);
static CFNumberRef cfnum(int v) { return CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &v); }

// ---------- SMC 通道（macmon 协议） ----------
typedef struct { uint8_t major, minor, build, reserved; uint16_t release; } Vers;
typedef struct { uint32_t size, type; uint8_t attrs; } KeyInfo;
typedef struct {
    uint32_t key; Vers vers; uint16_t pver, plen; uint32_t p3, p4, p5;
    KeyInfo info; uint8_t result, status, data8; uint32_t data32; uint8_t bytes[32];
} KeyData;  // 80 bytes on Apple Silicon
static io_connect_t smcConn = 0;
static int smc_call(KeyData *in, KeyData *out) {
    size_t sz = sizeof(KeyData);
    kern_return_t r = IOConnectCallStructMethod(smcConn, 2, in, sizeof(KeyData), out, &sz);
    if (r != KERN_SUCCESS) return -1;
    return out->result;
}
static uint32_t fourcc(const char *s) {
    return ((uint32_t)s[0] << 24) | ((uint32_t)s[1] << 16) | ((uint32_t)s[2] << 8) | s[3];
}

// 读 SMC 温度：只累加前缀匹配 prefix[0],prefix[1] 的 flt 键，返回平均值（0=无）
static double smc_temp_avg(const char *p0, const char *p1) {
    if (!smcConn) return 0;
    const uint32_t FLT = 1718383648;  // "flt " (macmon)
    // key_count via #KEY + data8=5
    uint32_t hashKey = fourcc("#KEY");
    KeyData q = {0}, qi = {0};
    q.data8 = 9; q.key = hashKey;
    if (smc_call(&q, &qi) != 0) return 0;
    KeyData c = {0}, cv = {0};
    c.data8 = 5; c.key = hashKey; c.info = qi.info;
    if (smc_call(&c, &cv) != 0) return 0;
    uint32_t count = (cv.bytes[0] << 24) | (cv.bytes[1] << 16) | (cv.bytes[2] << 8) | cv.bytes[3];
    if (count == 0 || count > 20000) return 0;
    double sum = 0; int n = 0;
    for (uint32_t i = 0; i < count; i++) {
        KeyData in = {0}, out = {0};
        in.data8 = 8; in.data32 = i;
        if (smc_call(&in, &out) != 0) continue;
        char name[5] = {0};
        name[0] = out.key >> 24; name[1] = out.key >> 16;
        name[2] = out.key >> 8;  name[3] = out.key;
        // 匹配 Tg* / Tp* 且第二字符等于 p1
        if (name[0] != p0[0] || name[1] != p1[0]) continue;
        KeyData iq = {0}, ii = {0};
        iq.data8 = 9; iq.key = out.key;
        if (smc_call(&iq, &ii) != 0) continue;
        if (ii.info.size != 4 || ii.info.type != FLT) continue;
        KeyData rd = {0}, val = {0};
        rd.data8 = 5; rd.key = out.key; rd.info = ii.info;
        if (smc_call(&rd, &val) != 0) continue;
        float t; memcpy(&t, val.bytes, 4);
        if (t > 0 && t < 150) { sum += t; n++; }
    }
    return n ? sum / n : 0;
}

static void accum_hid(const char *name, double t, double *cpu, int *nc,
                      double *ssd, int *ns) {
    if (t <= 0 || t > 150) return;
    if (!strncmp(name, "pACC MTR Temp Sensor", 20) ||
        !strncmp(name, "eACC MTR Temp Sensor", 20) ||
        !strncmp(name, "PMU tdie", 8)) {
        *cpu += t; (*nc)++;
    } else if (!strncmp(name, "NAND", 4) || !strncmp(name, "SSD Temp Sensor", 15)) {
        *ssd += t; (*ns)++;
    }
}

int main(void) {
    double cpu = 0, ssd = 0; int nc = 0, ns = 0;

    // ---- 通道 1：IOHID（CPU + SSD） ----
    const void *keys[] = { CFSTR("PrimaryUsagePage"), CFSTR("PrimaryUsage") };
    const void *vals[] = { cfnum(0xff00), cfnum(0x0005) };
    CFDictionaryRef match = CFDictionaryCreate(kCFAllocatorDefault, keys, vals, 2,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    HidClient sys = IOHIDEventSystemClientCreate(kCFAllocatorDefault);
    if (sys) {
        IOHIDEventSystemClientSetMatching(sys, match);
        CFArrayRef svcs = IOHIDEventSystemClientCopyServices(sys);
        if (svcs) {
            for (CFIndex i = 0; i < CFArrayGetCount(svcs); i++) {
                HidService sc = (HidService)CFArrayGetValueAtIndex(svcs, i);
                CFStringRef nameCF = IOHIDServiceClientCopyProperty(sc, CFSTR("Product"));
                if (!nameCF) continue;
                char name[128] = {0};
                CFStringGetCString(nameCF, name, sizeof(name), kCFStringEncodingUTF8);
                HidEvent ev = IOHIDServiceClientCopyEvent(sc, 15, 0, 0);
                if (ev) {
                    double t = IOHIDEventGetFloatValue(ev, 15LL << 16);
                    CFRelease(ev);
                    accum_hid(name, t, &cpu, &nc, &ssd, &ns);
                }
                CFRelease(nameCF);
            }
            CFRelease(svcs);
        }
        CFRelease(sys);
    }
    if (match) CFRelease(match);

    // ---- 通道 2：SMC（GPU Tg*；CPU Tp* 兜底） ----
    io_iterator_t it;
    if (IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("AppleSMC"), &it) == 0) {
        io_object_t obj;
        while ((obj = IOIteratorNext(it))) {
            char name[128] = {0};
            IORegistryEntryGetName(obj, name);
            if (!strcmp(name, "AppleSMCKeysEndpoint")) {
                if (IOServiceOpen(obj, mach_task_self(), 0, &smcConn) == 0) break;
            }
            IOObjectRelease(obj);
        }
        IOObjectRelease(it);
    }
    double gpu = smc_temp_avg("T", "g");
    if (!nc) {
        double tp = smc_temp_avg("T", "p");   // IOHID 无 CPU 传感器时用 SMC Tp* 兜底
        if (tp > 0) { cpu = tp; nc = 1; }
    }

    char cbuf[32] = "", gbuf[32] = "", sbuf[32] = "";
    if (nc) snprintf(cbuf, sizeof(cbuf), "%.1f", cpu / nc);
    if (gpu > 0) snprintf(gbuf, sizeof(gbuf), "%.1f", gpu);
    if (ns) snprintf(sbuf, sizeof(sbuf), "%.1f", ssd / ns);
    printf("CPU=%s GPU=%s SSD=%s\n", cbuf, gbuf, sbuf);
    if (smcConn) IOServiceClose(smcConn);
    return 0;
}
