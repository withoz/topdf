using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Topdf;

internal static class FolderTest
{
    public static int Run(string folder, string output)
    {
        Directory.CreateDirectory(output);
        var sources = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
        var hashes = sources.ToDictionary(p => p, Hash);
        var results = new List<object>();
        var app = new Application();
        int failures = 0;
        app.Startup += async (_, _) => {
            var window = new MainWindow(); window.Show();
            try {
                var viewer = (WebView2)window.FindName("PdfViewer");
                var list = (DataGrid)window.FindName("Files");
                var service = new ConversionService();
                foreach (var source in sources) {
                    var timer = Stopwatch.StartNew();
                    string status = "FAIL", preview = "not tested", detail = "";
                    string? pdf = null;
                    try {
                        window.Add(new[] { source });
                        var job = list.Items.Cast<FileJob>().Single(j => j.Source == source);
                        list.SelectedItem = job;
                        if (!ConversionService.Supports(source)) {
                            if (job.Status != "미지원" || viewer.Visibility == Visibility.Visible) throw new Exception("Unsupported UI state incorrect");
                            status = "UNSUPPORTED"; preview = "unsupported notice PASS";
                        } else {
                            job.Pdf = await service.ConvertAsync(source, Path.Combine(output, "jobs"), CancellationToken.None);
                            job.Status = "완료";
                            pdf = Path.Combine(output, Path.GetFileName(source) + ".pdf");
                            File.Copy(job.Pdf, pdf, true);
                            list.SelectedItem = null;
                            list.SelectedItem = job;
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                            while (viewer.CoreWebView2 == null || viewer.Visibility != Visibility.Visible || viewer.Source?.LocalPath != job.Pdf)
                                await Task.Delay(100, timeout.Token);
                            var loaded = new TaskCompletionSource<bool>();
                            viewer.CoreWebView2.NavigationCompleted += Finished;
                            void Finished(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e.IsSuccess);
                            try {
                                viewer.CoreWebView2.Reload();
                                if (!await loaded.Task.WaitAsync(timeout.Token)) throw new Exception("Preview navigation failed");
                            } finally { viewer.CoreWebView2.NavigationCompleted -= Finished; }
                            status = "PASS"; preview = "embedded navigation PASS";
                        }
                    } catch (NotSupportedException ex) when (ConversionService.Supports(source) && !IndependentConversion.Supports(source)) { status = "QUALITY_BLOCKED"; detail = ex.Message; preview = "independent mode correctly refused unqualified engine"; }
                    catch (Exception ex) { failures++; detail = ex.Message; }
                    results.Add(new { source, status, pdf, preview, mode = "independent", requestedEngine = IndependentConversion.EngineName(source), seconds = Math.Round(timer.Elapsed.TotalSeconds, 2), detail, originalUnchanged = Hash(source) == hashes[source] });
                    Console.WriteLine($"{status}: {Path.GetFileName(source)} | {preview} | {detail}");
                    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
            } catch (Exception ex) { failures++; Console.Error.WriteLine(ex); }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run();
        return failures == 0 && sources.All(p => Hash(p) == hashes[p]) ? 0 : 1;
    }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
