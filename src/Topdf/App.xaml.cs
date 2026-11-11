using System.Windows;
using System.IO;
using MiniSoftware;
namespace Topdf;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        NasApproval? approval=null;
        if(NasApproval.ServerUrl!=null)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try {
                approval=new NasApproval();
                var status=await approval.CheckAsync();
                var worker=e.Args.Length==3 && e.Args[0] is "--verify-install" or "--convert-xls" or "--convert-xlsx";
                if(!status.Approved && (worker||!approval.WaitForApproval(status))) {
                    approval.Dispose();
                    Shutdown(3);
                    return;
                }
            } catch(Exception ex) {
                approval?.Dispose();
                if(e.Args.Length==0)MessageBox.Show(ex.Message,"TOPDF 승인");
                Shutdown(3);
                return;
            }
        }

        if (e.Args.Length == 3 && e.Args[0] == "--verify-install") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            approval?.Dispose();
            Shutdown(await InstallationVerifier.RunAsync(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--convert-xls") {
            approval?.Dispose();
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await XlsBrowserWorker.ConvertAsync(e.Args[1], e.Args[2]); Shutdown(0); }
            catch (Exception ex) {
                try { File.WriteAllText(e.Args[2] + ".error.txt", ex.ToString()); } catch (IOException) { }
                Console.Error.WriteLine(ex); Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--convert-xlsx") {
            approval?.Dispose();
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
        var window = new MainWindow(preferSharedPageEditor:true);
        window.Show();
        MainWindow=window;
        ShutdownMode=ShutdownMode.OnMainWindowClose;
        if (approval != null)
        {
            approval.AddUpdateMenu(window, window.HelpMenu);
            _ = MonitorApprovalAsync(approval, window);
        }
        if (e.Args.Length > 0) await window.OpenFromShellAsync(e.Args);
    }
    static async Task MonitorApprovalAsync(NasApproval approval,MainWindow window)
    {
        using(approval)
        using(var cancellation=new CancellationTokenSource())
        {
            window.Closed+=Cancel;
            try {await approval.MonitorAsync(window,cancellation.Token);}
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested) {}
            finally {window.Closed-=Cancel;}
            void Cancel(object? sender,EventArgs e)=>cancellation.Cancel();
        }
    }
}
