using Island.App.Services;

namespace Island.Tests;

/// <summary>剪贴板去重、文本智能分类与常驻置顶淘汰规则测试；不读取或改写系统剪贴板。</summary>
internal static class ClipboardTests
{
    public static void Run(Action<string, bool> check)
    {
        var image = new ClipboardItem(DateTime.Now, "image", "图片 100x100", "") { Sequence = 100 };
        check("同一剪贴板序号的图片会按重复事件去重",
            ClipboardService.IsDuplicate(image, "image", "", 100));
        check("连续的不同图片不会因 Full 都为空而误去重",
            !ClipboardService.IsDuplicate(image, "image", "", 101));

        var text = new ClipboardItem(DateTime.Now, "text", "hello", "hello") { Sequence = 100 };
        check("相同文本仍按全文去重", ClipboardService.IsDuplicate(text, "text", "hello", 101));
        check("不同文本不会误去重", !ClipboardService.IsDuplicate(text, "text", "world", 101));
        check("文本与同序号的图片不会串类型", !ClipboardService.IsDuplicate(image, "text", "", 100));

        var link = ClipboardService.ClassifyText("https://github.com/example/repo");
        check("单行 URL 识别为 link 分类", link.Category == "link" && link.Lines == 1);

        var hex6 = ClipboardService.ClassifyText("#2e9bff");
        var hex8 = ClipboardService.ClassifyText("#2E9BFFCC");
        check("6 位与 8 位 CSS 十六进制色值识别为 color 并规范化前 7 位色码",
            hex6.Category == "color" && hex6.HexColor == "#2E9BFF"
            && hex8.Category == "color" && hex8.HexColor == "#2E9BFF");

        var code = ClipboardService.ClassifyText("public static void Main()\n{\n    Console.WriteLine(42);\n}");
        check("多行代码片段识别为 code 且准确统计行数",
            code.Category == "code" && code.Lines == 4);

        var list = new List<ClipboardItem>
        {
            new(DateTime.Now, "text", "newest", "newest") { Sequence = 3 },
            new(DateTime.Now.AddHours(-2), "text", "unpinned-old", "unpinned-old") { Sequence = 2 },
            new(DateTime.Now.AddDays(-5), "text", "pinned-ancient", "pinned-ancient") { Sequence = 1, Pinned = true },
        };
        ClipboardService.TrimList(list, maxCount: 2, maxAge: TimeSpan.FromDays(1), maxBytes: 1024 * 1024);
        check("超限与超时淘汰时保留常驻 Pinned 条目并优先淘汰非常驻旧条目",
            list.Count == 2 && list.Any(x => x.Pinned && x.Full == "pinned-ancient")
            && list.Any(x => x.Full == "newest"));
    }
}
