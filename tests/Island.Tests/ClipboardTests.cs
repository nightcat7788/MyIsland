using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Services;

namespace Island.Tests;

internal static class ClipboardTests
{
    public static void Run(Action<string, bool> check)
    {
        ClipboardService.Clear();
        var now = DateTime.Now;
        check("剪贴板：超大文本拒绝，不占满历史", ClipboardService.CreateTextItem(new string('a', 1024 * 1024), now) is null);
        var text = ClipboardService.CreateTextItem("第一行\r\n第二行", now)!;
        ClipboardService.Store(text);
        ClipboardService.Store(text);
        check("剪贴板：连续相同文本去重", ClipboardService.Count == 1);
        check("剪贴板：预览不含换行", !ClipboardService.LastPreview.Contains('\n'));

        BitmapSource Image(byte value, int width = 2, int height = 2)
        {
            var pixels = Enumerable.Repeat(value, width * height * 4).ToArray();
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        }
        var first = ClipboardService.CreateImageItem(Image(1), now)!;
        var same = ClipboardService.CreateImageItem(Image(1), now)!;
        var second = ClipboardService.CreateImageItem(Image(2), now)!;
        check("剪贴板：图片持有冻结像素而非仅描述", first.Image is { IsFrozen: true } && first.ImageDigest.Length == 64);
        ClipboardService.Clear();
        ClipboardService.Store(first);
        ClipboardService.Store(same);
        check("剪贴板：相同图片按内容去重", ClipboardService.Count == 1);
        ClipboardService.Store(second);
        check("剪贴板：同尺寸不同内容图片不误去重", ClipboardService.Count == 2);
        check("剪贴板：图片像素计入预算", first.StoredBytes >= 16);
        check("剪贴板：超预算图片拒绝", ClipboardService.CreateImageItem(Image(0, 1024, 1024), now) is null);

        ClipboardService.Clear();
        for (int i = 0; i < 5; i++)
            ClipboardService.Store(ClipboardService.CreateImageItem(Image((byte)i, 400, 400), now)!);
        check("剪贴板：混合载荷严格小于2MB", ClipboardService.Items.Sum(i => i.StoredBytes) <= 2 * 1024 * 1024 && ClipboardService.Count == 3);
        ClipboardService.Clear();
        ClipboardService.Store(text with { At = now.AddHours(-25) });
        check("剪贴板：读取时清除过期条目", ClipboardService.Count == 0 && ClipboardService.Items.Count == 0 && ClipboardService.LastPreview == "");
        for (int i = 0; i < 60; i++) ClipboardService.Store(ClipboardService.CreateTextItem(i.ToString(), now)!);
        check("剪贴板：最多保留50条", ClipboardService.Count == 50);
        check("剪贴板：识别敏感和禁止历史格式", ClipboardService.IsSensitiveFormat("ExcludeClipboardContentFromMonitorProcessing") && ClipboardService.IsSensitiveFormat("Clipboard Viewer Ignore") && ClipboardService.IsSensitiveFormat("CanIncludeInClipboardHistory"));
        check("剪贴板：普通格式不误判敏感", !ClipboardService.IsSensitiveFormat("UnicodeText"));
        check("剪贴板：空记录恢复安全失败", !ClipboardService.TryRestore(null));
        check("剪贴板：过期记录不恢复", !ClipboardService.TryRestore(text with { At = now.AddHours(-25) }));
        bool restoredOnMta = true;
        var worker = new System.Threading.Thread(() => restoredOnMta = ClipboardService.TryRestore(first));
        worker.SetApartmentState(System.Threading.ApartmentState.MTA);
        worker.Start();
        worker.Join();
        check("剪贴板：非STA恢复安全失败且不改系统剪贴板", !restoredOnMta);
        ClipboardService.Clear();
    }
}
