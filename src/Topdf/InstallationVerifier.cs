using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;

namespace Topdf;

/// <summary>Opt-in diagnostics using the installed application's real conversion and preview path.</summary>
public static class InstallationVerifier
{
    public static async Task<int> RunAsync(string inputDirectory, string reportFile)
    {
        MainWindow? window = null;
        var results = new List<object>();
        string? error = null;
        var success = false;
        var reportAllowed = false;
        try {
            inputDirectory = Path.GetFullPath(inputDirectory);
            reportFile = Path.GetFullPath(reportFile);
            if (!reportFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Verification report must be JSON.");
            if (File.Exists(reportFile)) throw new IOException("Verification report already exists. Choose a new path.");
            reportAllowed = true;
            var sources = Directory.GetFiles(inputDirectory).Where(IndependentConversion.Supports).OrderBy(p => p).ToArray();
            if (sources.Length == 0) throw new ArgumentException("No supported test documents found.");
            var hashes = sources.ToDictionary(p => p, Hash);
            window = new MainWindow();
            window.Show();
            await window.OpenFilesAsync(sources);
            var files = (DataGrid)window.FindName("Files");
            var viewer = (WebView2)window.FindName("PdfViewer");
            success = true;
            foreach (var job in files.Items.Cast<FileJob>().ToArray()) {
                var preview = false;
                string? previewError = null;
                if (job.Status == "완료" && job.Pdf != null) {
                    try {
                        files.SelectedItem = job;
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        while (viewer.CoreWebView2 == null || viewer.Visibility != Visibility.Visible || viewer.Source?.LocalPath != job.Pdf)
                            await Task.Delay(100, timeout.Token);
                        var loaded = new TaskCompletionSource<bool>();
                        void Finished(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e.IsSuccess);
                        viewer.CoreWebView2.NavigationCompleted += Finished;
                        try { viewer.CoreWebView2.Reload(); preview = await loaded.Task.WaitAsync(timeout.Token); }
                        finally { viewer.CoreWebView2.NavigationCompleted -= Finished; }
                    } catch (Exception ex) { previewError = ex.Message; }
                }
                var unchanged = hashes[job.Source] == Hash(job.Source);
                var passed = job.Status == "완료" && preview && unchanged;
                success &= passed;
                results.Add(new { name = job.Name, extension = Path.GetExtension(job.Source), engine = IndependentConversion.EngineName(job.Source), status = job.Status, preview, originalUnchanged = unchanged, sourceSha256 = hashes[job.Source], pdfSha256 = job.Pdf != null && File.Exists(job.Pdf) ? Hash(job.Pdf) : null, detail = job.Detail, previewError, passed });
            }
        } catch (Exception ex) { error = ex.Message; success = false; }
        finally { window?.Close(); }
        if (!reportAllowed) { Console.Error.WriteLine(error); return 1; }
        var report = new { schemaVersion = 1, success, error, appVersion = typeof(App).Assembly.GetName().Version?.ToString(), appSha256 = Hash(typeof(App).Assembly.Location), bundledPython = File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe")), results };
        try {
            // Never overwrite an existing diagnostic or source file, including on failure.
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportFile))!);
            using var output = new FileStream(reportFile, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        return success ? 0 : 1;
    }

    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
