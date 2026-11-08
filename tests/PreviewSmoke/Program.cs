using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using Topdf;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("TOPDF_DATA_DIR", Path.Combine(AppContext.BaseDirectory, "test-data"));
        if(args.Length==2 && args[0]=="--font-views-ui") return FontViewsUiTest.Run(args[1]);
        if(args.Length==2 && args[0]=="--unified-ui") return UnifiedUiTest.Run(args[1]);
        if(args.Length==2 && args[0]=="--drawing-ui") return DrawingUiTest.Run(args[1]);
        if(args.Length==2 && args[0]=="--inline-edit-ui") return InlinePdfEditUiTest.Run(args[1]);
        if(args.Length==2 && args[0]=="--pdf-edit-ui") return PdfEditUiTest.Run(args[1]);
        if(args.Length==2 && args[0]=="--smart-ui") return SmartUiTest.Run(args[1]);
        if(args.Length==3 && args[0]=="--auto-folder") return AutoOpenTest.Run(args[1], args[2]);
        if(args.Length==2 && args[0]=="--shell-open") return ShellOpenTest.Run(Path.GetFullPath(args[1]));
        if(args.Length==1 && args[0]=="--export-ui") return ExportUiTest.Run();
        if (args.Length == 3 && args[0] == "--folder") return FolderTest.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length != 1 || !File.Exists(args[0])) { Console.Error.WriteLine("Pass an existing PDF path."); return 2; }
        var testRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var pdf = Path.Combine(testRoot, "미리보기 시험 #1.pdf");
        File.Copy(Path.GetFullPath(args[0]), pdf);
        var app = new Application();
        var result = 1;
        app.Startup += async (_, _) => {
            var window = new MainWindow();
            window.Show();
            try {
                var viewer = (WebView2)window.FindName("PdfViewer");
                window.Add(new[] { pdf });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                while (viewer.CoreWebView2 == null || viewer.Visibility != Visibility.Visible)
                    await Task.Delay(100, timeout.Token);
                var loaded = new TaskCompletionSource<bool>();
                viewer.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                // Re-navigate to observe a complete load even if the first navigation already finished.
                viewer.CoreWebView2.Reload();
                if (!await loaded.Task.WaitAsync(timeout.Token)) throw new Exception("PDF navigation failed.");
                if (viewer.Source?.LocalPath != pdf) throw new Exception("Wrong preview source.");
                Console.WriteLine("PASS: existing PDF auto-selection, WebView2 runtime initialization, embedded PDF navigation.");
                // A selected unsupported file must conceal the previous PDF.
                var list = (DataGrid)window.FindName("Files");
                var unsupported = Path.Combine(Path.GetDirectoryName(pdf)!, "preview-test.unsupported");
                await File.WriteAllTextAsync(unsupported, "Test placeholder.");
                window.Add(new[] { unsupported });
                list.SelectedIndex = 1;
                if (viewer.Visibility == Visibility.Visible) throw new Exception("Stale PDF visible for unsupported selection.");
                list.SelectedIndex = 0;
                while (viewer.Visibility != Visibility.Visible) await Task.Delay(100, timeout.Token);
                Console.WriteLine("PASS: unsupported selection clears preview; returning to PDF restores preview.");
                var rtf = Path.Combine(testRoot, "conversion.rtf");
                ((CheckBox)window.FindName("CompatibilityMode")).IsChecked = true;
                await File.WriteAllTextAsync(rtf, @"{\rtf1\ansi TOPDF embedded preview conversion test.}");
                window.Add(new[] { rtf });
                list.SelectedIndex = 2;
                await window.ConvertPendingAsync();
                var job = (FileJob)list.SelectedItem;
                while (job.Status is "대기" or "변환 중") await Task.Delay(100, timeout.Token);
                if (job.Status != "완료") throw new Exception("Conversion failed: " + job.Detail);
                while (viewer.Visibility != Visibility.Visible || viewer.Source?.LocalPath != job.Pdf) await Task.Delay(100, timeout.Token);
                Console.WriteLine("PASS: selected document automatically previews after real conversion; Korean/space/hash PDF path supported.");
                result = 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run();
        return result;
    }

    static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
}
