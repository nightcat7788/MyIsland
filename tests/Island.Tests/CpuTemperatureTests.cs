using System;
using System.Collections.Generic;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// CPU 温度取值规则的用例（2026-11-26，参考 LiteMonitor 的传感器匹配口径重做）。
///
/// 背景：不同 CPU 暴露的传感器名字差别很大（Intel: CPU Package / Core Max / P-Core #n；
/// AMD: Core (Tdie) / Core (Tctl) / Core (Tctl/Tdie)），而且 Intel 的
/// "P-Core #n Distance to TjMax" 名字里带 core、但它是**距离**不是温度。
/// 规则因此必须是"分层匹配 + 显式排除干扰项"，不能只写名字白名单。
///
/// 规则本体在 src/Island.App/Services/CpuTemperatureRule.cs（纯函数，可链进测试工程）。
/// </summary>
internal static class CpuTemperatureTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- 本机实测数据（本机 Intel Core Ultra 5 225H，2026-11-26 thermal-diag.log 原始值） ----------
        var real = new List<CpuTemperatureCandidate>
        {
            new("Core Max", 58),
            new("Core Average", 49),
            new("P-Core #1", 50),
            new("P-Core #2", 48),
            new("E-Core #1", 50),
            new("E-Core #5", 48),
            new("CPU Package", 58),
            new("P-Core #1 Distance to TjMax", 55),
            new("E-Core #9 Distance to TjMax", 61),
        };
        var pick = CpuTemperatureRule.Pick(real);
        check("本机实测传感器全集：选中封装温度 CPU Package = 58（旧口径会取 Core Average = 49）",
            pick is { } p1 && p1.Value == 58 && p1.Source == CpuTemperatureSource.Package
            && p1.SensorName == "CPU Package");

        // ---------- 分层优先级 ----------
        check("封装温度优先于平均值",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core Average", 49), new("CPU Package", 58),
            }) is { } p2 && p2.Value == 58 && p2.Source == CpuTemperatureSource.Package);

        check("封装温度优先于逐核读数",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("P-Core #1", 90), new("CPU Package", 58),
            }) is { } p3 && p3.Value == 58);

        check("AMD：Tdie 优先于 Tctl（Tctl 虚高 10–27°C）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core (Tctl)", 70), new("Core (Tdie)", 58),
            }) is { } p4 && p4.Value == 58 && p4.Source == CpuTemperatureSource.Tdie);

        check("AMD：Tctl/Tdie 合并值优先于平均值",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core Average", 49), new("Core (Tctl/Tdie)", 55),
            }) is { } p5 && p5.Value == 55 && p5.Source == CpuTemperatureSource.TctlTdie);

        check("AMD：只有 Tctl 时也认（Tctl 单独出现）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core (Tctl)", 70),
            }) is { } p6 && p6.Value == 70 && p6.Source == CpuTemperatureSource.Tctl);

        check("AMD：Tdie 类里多个 CCD 取最热的一个",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core (Tdie)", 58), new("CCD1 (Tdie)", 70), new("CCD2 (Tdie)", 61),
            }) is { } p7 && p7.Value == 70);

        check("只有 Core Max 时认它（硬件库聚合的最热核心）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core Max", 58),
            }) is { } p8 && p8.Value == 58 && p8.Source == CpuTemperatureSource.CoreMax);

        check("只有逐核读数时取最大值（LiteMonitor 的多核最大口径）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("P-Core #1", 50), new("P-Core #2", 48), new("E-Core #5", 58),
            }) is { } p9 && p9.Value == 58 && p9.Source == CpuTemperatureSource.PerCoreMax);

        check("只有平均值时兜底用它（优先级最低）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("Core Average", 49),
            }) is { } p10 && p10.Value == 49 && p10.Source == CpuTemperatureSource.Average);

        // ---------- 干扰项排除（这一段是本次重做的核心） ----------
        check("Distance to TjMax 是距离不是温度，绝不能被选中",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("P-Core #1", 50), new("P-Core #1 Distance to TjMax", 55),
            }) is { } p11 && p11.Value == 50 && p11.SensorName == "P-Core #1");

        check("只有 Distance to TjMax 时读数未知（null，不给假数）",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("P-Core #1 Distance to TjMax", 55),
            }) is null);

        check("SoC / VRM / 水泵 / 水冷液 / 风扇都不是核心温度",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("SoC", 70), new("CPU VRM", 80), new("CPU Pump", 40),
                new("Liquid", 30), new("CPU Fan", 0),
            }) is null);

        check("干扰项与真实读数混在一起时只取真实读数",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("SoC", 70), new("CPU VRM", 80), new("P-Core #1", 50),
                new("P-Core #1 Distance to TjMax", 55),
            }) is { } p12 && p12.Value == 50);

        // ---------- 坏值与空输入（不伪装） ----------
        check("空候选列表 → 读数未知",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>()) is null);
        check("null 候选列表 → 读数未知",
            CpuTemperatureRule.Pick(null) is null);
        check("零值与负值是坏读数，直接丢弃",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("CPU Package", 0), new("Core Average", -5),
            }) is null);
        check("超过物理上限（125°C）的读数是坏值",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("CPU Package", 300),
            }) is null);
        check("坏值被丢弃后仍能选到好读数",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("CPU Package", 0), new("Core Average", 49),
            }) is { } p13 && p13.Value == 49);
        check("读不到就是读不到：全干扰项时不返回 0% 之类的假读数",
            CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>
            {
                new("P-Core #1 Distance to TjMax", 55), new("SoC", 70),
            }) is null);
    }
}
