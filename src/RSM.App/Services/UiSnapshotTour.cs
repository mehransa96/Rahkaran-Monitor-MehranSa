using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RSM.App.Localization;
using RSM.App.ViewModels;
using UiLanguage = RSM.Core.Model.UiLanguage;
using RSM.Safety.Files;

namespace RSM.App.Services;

/// <summary>
/// <c>RSM.exe --ui-snapshots</c> (documentation and the publish smoke test): shows every page once in Persian and once
/// in English, renders the window to PNG under <c>&lt;data&gt;/ui-snapshots/</c> through <see cref="IDataFolder"/>, then
/// exits. It connects to nothing: no site is selected and no live view is started.
/// </summary>
public static class UiSnapshotTour
{
    /// <summary>Also prints a small HTML page with <paramref name="pdf"/> and records the outcome in <c>ui-snapshots/pdf-smoke.txt</c>.</summary>
    public static async Task RunAsync(Window window, MainViewModel main, IDataFolder folder, RSM.Hosting.IPdfPrinter pdf)
    {
        await folder.WriteAllTextAsync("reports/rsm-pdf-smoke.html",
            "<!DOCTYPE html><html lang=\"fa\" dir=\"rtl\"><head><meta charset=\"utf-8\"><title>RSM</title></head><body><h1>RSM PDF</h1><p>آزمون چاپ PDF — فقط خواندنی</p></body></html>",
            CancellationToken.None);
        string outcome;
        try
        {
            var (path, reason) = await pdf.PrintAsync("reports/rsm-pdf-smoke.html", "reports/rsm-pdf-smoke.pdf", CancellationToken.None);
            outcome = path is not null && new FileInfo(path).Length > 0 ? $"ok {new FileInfo(path).Length}" : $"unavailable {reason}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = $"error {ex.GetType().Name}: {ex.Message}";
        }

        await folder.WriteAllTextAsync("ui-snapshots/pdf-smoke.txt", outcome, CancellationToken.None);

        var original = Loc.Instance.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.Fa, UiLanguage.En })
            {
                Loc.Instance.SetLanguage(language);
                var index = 0;
                foreach (var item in main.NavItems)
                {
                    main.SelectedNav = item;
                    await Settle(window);
                    var name = $"ui-snapshots/{(language == UiLanguage.Fa ? "fa" : "en")}-{++index:00}-{item.TitleKey.Replace("Nav_", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}.png";
                    await folder.WriteAllBytesAsync(name, Render(window), CancellationToken.None);
                }
            }
        }
        finally
        {
            Loc.Instance.SetLanguage(original);
        }
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static byte[] Render(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
