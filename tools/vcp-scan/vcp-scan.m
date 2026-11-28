@import Foundation;
@import IOKit;
#include "i2c.h"
#include <assert.h>

// 退出码约定（供 .NET 侧判断能否永久放弃该 VCP）：
//   1 = 通用失败（连接问题 / 参数错误 / 写入失败）——下次应重试
//   3 = 显示器明确不支持该 VCP —— 可以永久跳过，重试没意义
static int fail(const char *message) {
    fprintf(stderr, "%s\n", message);
    return 1;
}

// 明确"不支持此功能"——单独退出码 3
static int failUnsupported(const char *message) {
    fprintf(stderr, "%s\n", message);
    return 3;
}

static bool number(const char *text, unsigned long maximum, unsigned long *value) {
    if (!text || !*text || *text == '-') return false;
    char *end;
    errno = 0;
    *value = strtoul(text, &end, 10);
    return errno == 0 && *end == '\0' && *value <= maximum;
}

static DDCValue readValueOnce(DDCTransport transport, UInt8 control) {
    DDCPacket request = createDDCPacket(control);
    prepareDDCRead(&request);
    DDCValue invalid = {-1, -1};
    if (performDDCWriteAtChipAddress(transport.service, transport.chipAddress, &request)) return invalid;
    DDCPacket reply = {};
    reply.inputAddr = request.inputAddr;
    if (performDDCReadAtChipAddress(transport.service, transport.chipAddress, &reply)) return invalid;
    if (reply.data[4] != control) return invalid;
    return convertI2CtoDDC((char *)reply.data);
}

static bool validValue(UInt8 control, DDCValue value) {
    if (value.curValue < 0 || value.maxValue < 0) return false;
    if (control == MUTE) return value.curValue == 1 || value.curValue == 2;
    return value.maxValue > 0 && value.curValue <= value.maxValue;
}

static double monotonicTime(void) { return NSProcessInfo.processInfo.systemUptime; }

static DDCValue readValueWithRetry(DDCTransport transport, UInt8 control, double deadline,
                                 DDCValue (*readOnce)(DDCTransport, UInt8), int (*wait)(useconds_t)) {
    DDCValue value = {-1, -1};
    for (int attempt = 0; attempt < 5; attempt++) {
        if (monotonicTime() >= deadline) break;
        value = readOnce(transport, control);
        if (validValue(control, value) || value.curValue == DDC_UNSUPPORTED_VALUE) break;
        if (attempt < 4) {
            useconds_t delay = 100000 << attempt;
            if (monotonicTime() + delay / 1000000.0 >= deadline) break;
            wait(delay);
        }
    }
    return value;
}

static DDCValue readValue(DDCTransport transport, UInt8 control, double deadline) {
    return readValueWithRetry(transport, control, deadline, readValueOnce, usleep);
}

static bool validWrite(UInt8 control, unsigned long requested, DDCValue value) {
    if (control == MUTE) return requested == 1 || requested == 2;
    return requested <= (unsigned long)value.maxValue;
}

static int testReads, testFailures;
static useconds_t testWaited;
static DDCValue testRead(DDCTransport transport, UInt8 control) {
    (void)transport;
    assert(control == LUMINANCE);
    return ++testReads <= testFailures ? (DDCValue){-1, -1} : (DDCValue){59, 100};
}
static int testWait(useconds_t delay) { testWaited += delay; return 0; }

static int selfTest(void) {
    DDCTransport fixture = {NULL, DDC_CHIP_ADDRESS_DEFAULT};
    double deadline = monotonicTime() + 4;
    testFailures = 4;
    DDCValue recovered = readValueWithRetry(fixture, LUMINANCE, deadline, testRead, testWait);
    assert(recovered.curValue == 59 && testReads == 5 && testWaited == 1500000);
    testReads = 0; testWaited = 0; testFailures = 5;
    assert(readValueWithRetry(fixture, LUMINANCE, deadline, testRead, testWait).curValue == -1);
    assert(testReads == 5 && testWaited == 1500000);
    testReads = 0; testWaited = 0; testFailures = 0;
    assert(readValueWithRetry(fixture, LUMINANCE, deadline, testRead, testWait).curValue == 59);
    assert(testReads == 1 && testWaited == 0);
    testReads = 0;
    assert(readValueWithRetry(fixture, LUMINANCE, 0, testRead, testWait).curValue == -1);
    assert(testReads == 0 && testWaited == 0);
    unsigned long parsed;
    assert(number("65535", 65535, &parsed) && parsed == 65535);
    assert(!number("-1", 65535, &parsed));
    assert(!number("65536", 65535, &parsed));
    assert(!number("20abc", 65535, &parsed));
    assert(validValue(VOLUME, (DDCValue){50, 100}));
    assert(!validValue(VOLUME, (DDCValue){101, 100}));
    assert(!validValue(VOLUME, (DDCValue){0, 0}));
    assert(validValue(MUTE, (DDCValue){1, 0}));
    assert(validValue(MUTE, (DDCValue){2, 0}));
    assert(!validValue(MUTE, (DDCValue){0, 0}));
    assert(validWrite(MUTE, 2, (DDCValue){1, 0}));
    assert(!validWrite(MUTE, 3, (DDCValue){1, 0}));
    assert(!validWrite(VOLUME, 101, (DDCValue){50, 100}));
    char reply[12] = {0x6e, (char)0x88, 0x02, 0, 0x10, 0, 0x01, 0x2c, 0, 0x80, 0, 0};
    uint8_t checksum = 0x50;
    for (int i = 0; i < 10; i++) checksum ^= (uint8_t)reply[i];
    reply[10] = (char)checksum;
    DDCValue value = convertI2CtoDDC(reply);
    assert(value.maxValue == 300 && value.curValue == 128);
    reply[3] = 1;
    checksum = 0x50;
    for (int i = 0; i < 10; i++) checksum ^= (uint8_t)reply[i];
    reply[10] = (char)checksum;
    assert(convertI2CtoDDC(reply).curValue == DDC_UNSUPPORTED_VALUE);
    reply[10] ^= 1;
    assert(convertI2CtoDDC(reply).curValue == -1);
    memset(reply, 0, sizeof(reply));
    assert(convertI2CtoDDC(reply).curValue == -1);
    reply[0] = 0x6e; reply[1] = (char)0x80; reply[2] = (char)0xbe;
    assert(convertI2CtoDDC(reply).curValue == -1);
    DDCPacket packet = createDDCPacket(LUMINANCE);
    prepareDDCWrite(&packet, 168);
    assert(packet.data[0] == 0x84 && packet.data[4] == 168 && packet.data[5] == 0);
    puts("DDC protocol self-test passed");
    return 0;
}

static void printJsonEscaped(const char *s) {
    if (!s) return;
    for (const unsigned char *p = (const unsigned char *)s; *p; p++) {
        if (*p == '"' || *p == '\\') { putchar('\\'); putchar(*p); }
        else if (*p < 0x20) { printf("\\u%04x", *p); }
        else { putchar(*p); }
    }
}

int main(int argc, const char **argv) {
    @autoreleasepool {
        if (argc == 2 && strcmp(argv[1], "--self-test") == 0) return selfTest();
        // set 的两种模式：
        //   set <id> <uuid> <vcp> <raw>      写绝对值
        //   set <id> <uuid> <vcp> p <percent> 写百分比（helper 内部读上限并换算）
        bool percentMode = argc == 7 && strcmp(argv[5], "p") == 0;
        bool setting = (argc == 6 || percentMode) && strcmp(argv[1], "set") == 0;
        // scan <id> <uuid>：遍历 0x00..0xFF 找出这块屏真正支持的 VCP
        bool scanMode = argc == 4 && strcmp(argv[1], "scan") == 0;
        if (!(argc == 5 && strcmp(argv[1], "read") == 0) && !setting && !scanMode)
            return fail("参数无效。");
        unsigned long displayID, control = 0, requested = 0;
        if (!number(argv[2], UINT32_MAX, &displayID))
            return fail("数值超出范围。");
        if (!scanMode) {
            if (!number(argv[4], UINT8_MAX, &control) ||
                (control != LUMINANCE && control != CONTRAST && control != VOLUME && control != MUTE &&
                 control != RED && control != GREEN && control != BLUE && control != INPUT &&
                 control != 0x04 && control != 0x05 && control != 0x06 && control != 0x08 &&
                 control != 0x0B && control != 0x0C && control != 0x14) ||
                (setting && !percentMode && !number(argv[5], UINT16_MAX, &requested)) ||
                (percentMode && !number(argv[6], 100, &requested))) return fail("数值超出范围。");
        }

        // Keep all queries and confirmation inside the Swift caller's five-second timeout.
        double deadline = monotonicTime() + 4;
        DisplayInfos displays[MAX_DISPLAYS] = {};
        CGDisplayCount count = getOnlineDisplayInfos(displays);
        DDCTransport transport = {NULL, DDC_CHIP_ADDRESS_DEFAULT};
        NSString *targetUuid = [NSString stringWithUTF8String:argv[3]];
        for (CGDisplayCount i = 0; i < count; i++) {
            if (targetUuid && displays[i].id == displayID &&
                [displays[i].uuid isEqualToString:targetUuid] &&
                !CGDisplayIsBuiltin(displays[i].id)) {
                transport = getDisplayDDCTransport(&displays[i]);
                break;
            }
        }
        releaseDisplayInfos(displays, count);
        if (!transport.service) return fail("未找到可用的显示器控制通道，请检查连接线或扩展坞。");

        // ---- scan：遍历全部 VCP，找出显示器真正支持的控制项 ----
        if (scanMode) {
            fputs("[{\"display\":\"", stdout);
            printJsonEscaped(argv[3]);
            fputs("\",\"vcps\":[", stdout);
            int first = 1;
            for (int vcp = 0; vcp <= 0xFF; vcp++) {
                double d = monotonicTime() + 0.6;
                DDCValue val = readValue(transport, (UInt8)vcp, d);
                if (val.curValue < 0 || val.maxValue < 0) continue;   // 不支持或未响应
                if (!first) printf(",");
                first = 0;
                printf("{\"vcp\":%d,\"current\":%d,\"max\":%d}", vcp, val.curValue, val.maxValue);
                fflush(stdout);
            }
            printf("]}]\n");
            CFRelease(transport.service);
            return 0;
        }

        int rc = 0;
        DDCValue value = readValue(transport, (UInt8)control, deadline);
        if (value.curValue == DDC_UNSUPPORTED_VALUE) {
            CFRelease(transport.service);
            return failUnsupported("显示器明确不支持此功能。");
        }
        if (!validValue((UInt8)control, value)) {
            CFRelease(transport.service);
            return fail("显示器未返回有效数据，请检查 DDC/CI 设置或连接后重试。");
        }
        if (setting) {
            if (percentMode) {
                if (value.maxValue <= 0) {
                    CFRelease(transport.service);
                    return fail("无法获取该控制项的上限。");
                }
                requested = (unsigned long)((value.maxValue * requested + 50) / 100);
            }
            if (!validWrite((UInt8)control, requested, value)) {
                CFRelease(transport.service);
                return fail("设置值不在显示器允许的范围内。");
            }
            DDCPacket packet = createDDCPacket((UInt8)control);
            prepareDDCWrite(&packet, (UInt16)requested);
            if (performDDCWriteAtChipAddress(transport.service, transport.chipAddress, &packet)) {
                CFRelease(transport.service);
                return fail("写入失败，请检查显示器连接。");
            }
            value = (DDCValue){-1, -1};
            for (int attempt = 0; attempt < 3; attempt++) {
                if (monotonicTime() + 0.1 >= deadline) break;
                usleep(100000);
                value = readValue(transport, (UInt8)control, deadline);
                if (value.curValue == (int)requested) break;
            }
            if (value.curValue != (int)requested) {
                fprintf(stderr, "显示器未确认目标值（目标 %lu，返回 %d）。请检查显示器设置或连接后重试。\n", requested, value.curValue);
                rc = 2;
            }
        }
        CFRelease(transport.service);
        if (rc == 0) printf("{\"current\":%d,\"maximum\":%d}\n", value.curValue, value.maxValue);
        return rc;
    }
}
