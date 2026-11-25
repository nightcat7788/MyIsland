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
//
// 2026-11-26 CPU 取值口径对齐 Windows 端（对照文件：src/Island.App/Services/CpuTemperatureRule.cs
//   规格：docs/superpowers/specs/2026-11-26-cpu-temperature-picker-design.md）：
//   · 原来把 PMU tdie / pACC / eACC 三个传感器求平均，现在改成“选一个”——
//     封装/硅片温度（PMU tdie，对应 Windows 的 Package/Tdie）才是 CPU 温度的标准口径，
//     平均值会低估降频风险；
//   · 显式排除干扰项：Distance to TjMax 是“离最高温度还有多远”的距离值而不是温度，
//     另有 SoC / VRM / 水泵 / 水冷液 / 风扇等，即使命中 tdie 等关键词也不能用；
//   · 优先级更高的传感器没有合法值时，继续往下试下一个优先级，而不是直接报 0；
//   · 同一优先级层里命中多个传感器时取读数最大的那条（同层取最大值），不依赖 IOHID
//     的枚举顺序，避免同一台机器两端出现数值漂移。
//   选中的来源只写 stderr（stdout 的输出格式一个字都不能改，C# 侧按格式解析）。

#import <stdio.h>
#import <string.h>
#import <IOKit/IOKitLib.h>
#import <CoreFoundation/CoreFoundation.h>

// ---------- 名字匹配工具（大小写不敏感） ----------
// 只做 ASCII 小写转换，不引入新的头文件
static char lower_c(char c) {
    return (c >= 'A' && c <= 'Z') ? (char)(c - 'A' + 'a') : c;
}
// 名字是否包含 needle（大小写不敏感）
static int name_contains_ci(const char *name, const char *needle) {
    size_t nl = strlen(needle);
    if (!nl) return 0;
    for (const char *p = name; *p; p++) {
        size_t i = 0;
        while (i < nl && p[i]) {
            if (lower_c(p[i]) != lower_c(needle[i])) break;
            i++;
        }
        if (i == nl) return 1;
    }
    return 0;
}
// 名字是否以 prefix 开头（大小写不敏感）
static int name_starts_ci(const char *name, const char *prefix) {
    size_t n = strlen(prefix);
    for (size_t i = 0; i < n; i++) {
        if (!name[i]) return 0;
        if (lower_c(name[i]) != lower_c(prefix[i])) return 0;
    }
    return 1;
}
// 名字是否完全等于 other（大小写不敏感）
static int name_equals_ci(const char *name, const char *other) {
    return name_starts_ci(name, other) && strlen(name) == strlen(other);
}

// ---------- CPU 通道选取规则（对齐 Windows 端 CpuTemperatureRule） ----------
// 显式排除的干扰项：即使名字命中 tdie 等关键词，也不能当作 CPU 温度。
// 典型例子是 “Distance to TjMax”（离最高温度还有多远，是距离不是温度），
// 另外还有 SoC / VRM / 水泵 / 水冷液 / 风扇等。
static const char *const kCpuExcluded[] = {
    "distance", "tjmax", "gpu", "fan", "airflow", "smc",
    "proximity", "virtual", "liquid", "coolant", "pump", "vrm", "soc", NULL
};
static int cpu_name_excluded(const char *name) {
    for (int i = 0; kCpuExcluded[i]; i++)
        if (name_contains_ci(name, kCpuExcluded[i])) return 1;
    return 0;
}

// CPU 温度候选的优先级（数字越小优先级越高），0 = 不是 CPU 温度候选
//   1) 名字以 “PMU tdie” 开头 —— Apple Silicon 的 CPU die 温度，
//      与 Windows 端的 Package/Tdie 属于同一类口径，首选
//   2) 名字包含 “tdie” —— 其它硅片温度
//   3) “pACC MTR Temp Sensor” —— P 核簇温度
//   4) “eACC MTR Temp Sensor” —— E 核簇温度
//   5) 兜底走 SMC 的 Tp* 平均值（在 main 里处理）
static int cpu_priority(const char *name) {
    if (name_starts_ci(name, "PMU tdie")) return 1;
    if (name_contains_ci(name, "tdie")) return 2;
    if (name_equals_ci(name, "pACC MTR Temp Sensor")) return 3;
    if (name_equals_ci(name, "eACC MTR Temp Sensor")) return 4;
    return 0;
}

// 已经选中的 CPU 温度传感器
typedef struct {
    double value;      // 温度（0 = 还没选中）
    int priority;      // 命中规则的优先级
    char source[128];  // 传感器名字（只用于 stderr 日志）
} CpuPick;

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

// 处理一个 IOHID 温度传感器读数
//   · SSD 通道保持现状（NAND / SSD Temp Sensor 求平均）
//   · CPU 通道改成“选一个”：按优先级取第一个有合法值的传感器，
//     不再把 PMU tdie / pACC / eACC 混在一起求平均（对齐 Windows 端 CpuTemperatureRule）
static void accum_hid(const char *name, double t, CpuPick *pick,
                      double *ssd, int *ns) {
    if (t <= 0 || t > 150) return;   // 非法读数一律当作“没有”
    if (!strncmp(name, "NAND", 4) || !strncmp(name, "SSD Temp Sensor", 15)) {
        *ssd += t; (*ns)++;            // SSD 保持现状（平均）
        return;
    }
    if (cpu_name_excluded(name)) return;   // 干扰项永不作为 CPU 温度
    int p = cpu_priority(name);
    if (p == 0) return;
    // 高优先级层已经有合法值就不看低优先级层；同一优先级层里取读数最大的那条
    // （与 Windows 端 CpuTemperatureRule 一致：同层取最大值，多 CCD / 多命名重复时取最热的），
    // 这样结果不依赖 IOHID 的枚举顺序，两端不会因为枚举次序不同而显示不同数值。
    // 优先级更高的传感器读数非法时它根本不会走到这里，下面的传感器自然被选中。
    if (pick->value > 0) {
        if (pick->priority < p) return;                  // 已有更高优先级的合法值
        if (pick->priority == p && pick->value >= t) return;  // 同级：保留更大的读数
    }
    pick->value = t;
    pick->priority = p;
    snprintf(pick->source, sizeof(pick->source), "%s", name);
}

int main(void) {
    double ssd = 0; int ns = 0;
    CpuPick pick = {0};   // CPU 通道：选中的那个传感器（不再求平均）

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
                    accum_hid(name, t, &pick, &ssd, &ns);
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
    if (pick.value <= 0) {
        double tp = smc_temp_avg("T", "p");   // IOHID 无 CPU 传感器时用 SMC Tp* 兜底
        if (tp > 0) {
            pick.value = tp; pick.priority = 5;
            snprintf(pick.source, sizeof(pick.source), "SMC Tp* 平均");
        }
    }

    char cbuf[32] = "", gbuf[32] = "", sbuf[32] = "";
    if (pick.value > 0) snprintf(cbuf, sizeof(cbuf), "%.1f", pick.value);
    if (gpu > 0) snprintf(gbuf, sizeof(gbuf), "%.1f", gpu);
    if (ns) snprintf(sbuf, sizeof(sbuf), "%.1f", ssd / ns);
    printf("CPU=%s GPU=%s SSD=%s\n", cbuf, gbuf, sbuf);
    // 选中来源只写 stderr，不影响上面 stdout 的解析格式
    if (pick.value > 0)
        fprintf(stderr, "thermal: cpu source = %s %.1f\n", pick.source, pick.value);
    else
        fprintf(stderr, "thermal: cpu source = (none)\n");
    if (smcConn) IOServiceClose(smcConn);
    return 0;
}
