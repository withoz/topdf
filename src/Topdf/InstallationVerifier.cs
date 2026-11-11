using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;


namespace Topdf;

/// <summary>Opt-in diagnostics using the installed application's real conversion, editor and export path.</summary>
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
            var sources = Directory.GetFiles(inputDirectory).Where(path=>IndependentConversion.Supports(path)||Path.GetExtension(path).Equals(".ppt",StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).ToArray();
            if (sources.Length == 0) throw new ArgumentException("No supported test documents found.");
            var hashes = sources.ToDictionary(p => p, Hash);
            window = new MainWindow(preferSharedPageEditor:true);
            window.Show();
            if(!BuildFeatures.SmartEnabled && (window.FindName("SmartToggle") is not System.Windows.Controls.Primitives.ToggleButton { Visibility: Visibility.Collapsed }
                || File.Exists(Path.Combine(AppContext.BaseDirectory,"engines","document_tools.py"))))
                throw new InvalidOperationException("스마트 기능 제외 설정을 확인하지 못했습니다.");
            await window.OpenFilesAsync(sources);
            var files = (DataGrid)window.FindName("Files");
            success = true;
            foreach (var job in files.Items.Cast<FileJob>().ToArray()) {
                var preview = false;
                var editedExport = false;
                string? previewError = null;
                if (job.Status == "완료" && job.Pdf != null) {
                    try {
                        files.SelectedItem = job;
                        var editor = window.ActivePdfEditor ?? throw new InvalidOperationException("PDF 편집 화면을 열 수 없습니다.");
                        await editor.Ready.WaitAsync(TimeSpan.FromSeconds(90));
                        preview = ((Image)editor.FindName("EditPageImage")).Source != null;
                        if(!preview)throw new InvalidOperationException("편집할 페이지를 표시할 수 없습니다.");
                        editor.AddText(30,30,"TOPDF 편집 검증");
                        if(!editor.HasPlanChanges)throw new InvalidOperationException("편집 항목을 추가할 수 없습니다.");
                        Directory.CreateDirectory(AppPaths.Cache);
                        var edited = Path.Combine(AppPaths.Cache, "verify-edit-" + Guid.NewGuid().ToString("N") + ".pdf");
                        try {
                            try { await editor.SaveCopyAsync(edited); }
                            catch { editor.Undo(); throw; }
                            using var stream=File.OpenRead(edited);
                            var header=new byte[5];
                            editedExport=stream.Read(header)==header.Length&&System.Text.Encoding.ASCII.GetString(header)=="%PDF-";
                        } finally {
                            if(File.Exists(edited))File.Delete(edited);
                        }
                    } catch (Exception ex) { previewError = ex.Message; }
                }
                var unchanged = hashes[job.Source] == Hash(job.Source);
                var passed = job.Status == "완료" && preview && editedExport && unchanged;
                success &= passed;
                results.Add(new { name = job.Name, extension = Path.GetExtension(job.Source), engine = job.Status=="완료" ? job.Detail : IndependentConversion.EngineName(job.Source), status = job.Status, preview, editedExport, originalUnchanged = unchanged, sourceSha256 = hashes[job.Source], pdfSha256 = job.Pdf != null && File.Exists(job.Pdf) ? Hash(job.Pdf) : null, detail = job.Detail, previewError, passed });
            }
        } catch (Exception ex) { error = ex.Message; success = false; }
        finally { window?.Close(); }
        if (!reportAllowed) { Console.Error.WriteLine(error); return 1; }
        var report = new { schemaVersion = 1, success, error, smartEnabled = BuildFeatures.SmartEnabled, appVersion = typeof(App).Assembly.GetName().Version?.ToString(), appSha256 = Hash(typeof(App).Assembly.Location), bundledPython = File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe")), results };
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
