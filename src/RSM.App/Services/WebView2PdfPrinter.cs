using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RSM.Hosting;
using RSM.Safety.Files;

namespace RSM.App.Services;

/// <summary>
/// PDF export (Q1) with WebView2, kept offline and inside the data folder:
/// <list type="bullet">
/// <item>the saved report is served from a virtual host (<c>rsm-report.invalid</c>, a reserved TLD that never resolves)
/// mapped onto the reports folder; every other request (http, file, data fetches) is answered 403 by RSM itself;</item>
/// <item>scripts are disabled (the report has none) and background networking/updates are switched off;</item>
/// <item>WebView2's profile lives in <c>&lt;data&gt;/webview2</c>, and the PDF comes back as a stream that is written
/// through <see cref="IDataFolder"/>.</item>
/// </list>
/// This is the only file that uses WebView2 (pinned by RSM.ArchTests).
/// </summary>
public sealed class WebView2PdfPrinter(IDataFolder folder) : IPdfPrinter
{
    private const string Host = "rsm-report.invalid";

    private const string BrowserArguments =
        "--disable-background-networking --disable-component-update --disable-sync --no-pings --disable-domain-reliability --disable-features=msSmartScreenProtection";

    public async Task<(string? PdfPath, string? UnavailableReason)> PrintAsync(string htmlRelativePath, string pdfRelativePath, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                return (null, "WebView2 runtime is not installed.");
            }
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return (null, "WebView2 runtime is not installed.");
        }

        return await Application.Current.Dispatcher.InvokeAsync(() => PrintOnUiThreadAsync(htmlRelativePath, pdfRelativePath, cancellationToken)).Task.Unwrap();
    }

    private async Task<(string?, string?)> PrintOnUiThreadAsync(string htmlRelativePath, string pdfRelativePath, CancellationToken cancellationToken)
    {
        var html = folder.Resolve(htmlRelativePath);
        var environment = await CoreWebView2Environment.CreateAsync(null, folder.Resolve("webview2"), new CoreWebView2EnvironmentOptions(BrowserArguments));
        var view = new WebView2();
        var window = new Window
        {
            Width = 900,
            Height = 700,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Content = view,
        };
        window.Show();
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, Path.GetDirectoryName(html)!, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked by RSM (offline)", string.Empty);
                }
            };

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            using (cancellationToken.Register(() => loaded.TrySetCanceled()))
            {
                core.Navigate($"https://{Host}/{Uri.EscapeDataString(Path.GetFileName(html))}");
                if (!await loaded.Task)
                {
                    return (null, "The report could not be loaded for printing.");
                }
            }

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.Orientation = CoreWebView2PrintOrientation.Portrait;
            using var pdf = await core.PrintToPdfStreamAsync(settings);
            using var buffer = new MemoryStream();
            await pdf.CopyToAsync(buffer, cancellationToken);
            await folder.WriteAllBytesAsync(pdfRelativePath, buffer.ToArray(), cancellationToken);
            return (folder.Resolve(pdfRelativePath), null);
        }
        finally
        {
            window.Close();
            view.Dispose();
        }
    }
}
