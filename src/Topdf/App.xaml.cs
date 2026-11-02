using System.Windows;
using System.IO;
using MiniSoftware;
namespace Topdf;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0] == "--convert-xls") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await XlsBrowserWorker.ConvertAsync(e.Args[1], e.Args[2]); Shutdown(0); }
            catch (Exception ex) {
                try { File.WriteAllText(e.Args[2] + ".error.txt", ex.ToString()); } catch (IOException) { }
                Console.Error.WriteLine(ex); Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--convert-xlsx") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try {
                var source = Path.GetFullPath(e.Args[1]); var target = Path.GetFullPath(e.Args[2]);
                if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) throw new IOException("원본에 덮어쓸 수 없습니다.");
                var input = XlsxInputAdapter.Prepare(source, Path.GetDirectoryName(target)!);
                MiniPdf.ConvertToPdf(input, target);
                Shutdown(0);
            } catch (Exception ex) {
                try { File.WriteAllText(e.Args[2] + ".error.txt", ex.ToString()); } catch (IOException) { }
                Console.Error.WriteLine(ex); Shutdown(1);
            }
            return;
        }
        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();
        if (e.Args.Length > 0) await window.OpenFromShellAsync(e.Args);
    }
}
