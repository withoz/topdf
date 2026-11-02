using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Topdf;

/// <summary>Runs the bundled XLS parser/renderer in WebView2; no Office/Node installation is used.</summary>
public static class XlsBrowserWorker
{
    const string EngineUrl = "https://topdf-engine.invalid/index.html";
    public static async Task ConvertAsync(string source, string target)
    {
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new IOException("원본에 덮어쓸 수 없습니다.");
        if (new FileInfo(source).Length > 50 * 1024 * 1024) throw new IOException("XLS 독립 변환의 입력 한도는 50MB입니다.");
        var job = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(job);
        File.Copy(source, Path.Combine(job, "input.xls"), overwrite: false);
        var engineFolder = Path.Combine(AppContext.BaseDirectory, "engines", "vendor", "ream");
        var browser = new WebView2();
        var window = new Window { Content = browser, Width = 1, Height = 1, Left = -10000, Top = -10000, Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            window.Show();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(job, "ream-webview"));
            await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(45));
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping("topdf-engine.invalid", engineFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping("topdf-input.invalid", job, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("topdf-fonts.invalid", Environment.GetFolderPath(Environment.SpecialFolder.Fonts), CoreWebView2HostResourceAccessKind.Allow);
            var allowed = new HashSet<string>(StringComparer.Ordinal) {
                EngineUrl, "https://topdf-engine.invalid/ream.bundle.mjs",
                "https://topdf-input.invalid/input.xls", "https://topdf-fonts.invalid/malgun.ttf", "https://topdf-fonts.invalid/malgunbd.ttf"
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => {
                if (!allowed.Contains(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            };
            core.NavigationStarting += (_, e) => { if (e.Uri != EngineUrl) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, e) => completion.TrySetException(new IOException("XLS 렌더러가 종료되었습니다: " + e.ProcessFailedKind));
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess) completion.TrySetException(new IOException("XLS 엔진 페이지를 열 수 없습니다: " + e.WebErrorStatus)); };
            core.WebMessageReceived += (_, e) => { if (e.Source == EngineUrl) completion.TrySetResult(e.WebMessageAsJson); };
            core.Navigate(EngineUrl);
            var message = await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
            using var result = JsonDocument.Parse(message);
            var data = result.RootElement;
            if (!data.GetProperty("ok").GetBoolean()) throw new InvalidDataException(data.GetProperty("error").GetString());
            var bytes = Convert.FromBase64String(data.GetProperty("pdf").GetString()!);
            if (bytes.Length < 5 || System.Text.Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw new InvalidDataException("XLS 엔진의 결과가 PDF가 아닙니다.");
            await File.WriteAllBytesAsync(target, bytes);
            await File.WriteAllTextAsync(target + ".conversion.json", JsonSerializer.Serialize(new {
                engine = "Ream 1.33.1 + TOPDF merged-cell shrink fix", runtime = "bundled JavaScript in WebView2",
                localOnly = true, losses = data.GetProperty("losses").Clone()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { browser.Dispose(); window.Close(); }
    }
}
