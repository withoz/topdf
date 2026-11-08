using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Topdf;

internal static class InlinePdfEditUiTest
{
    public static int Run(string path)
    {
        var app=new Application();int result=1;
        app.Startup+=async(_,_)=>{
            var window=new MainWindow();window.Show();
            try {
                var source=Path.GetFullPath(path);var hash=SHA256.HashData(File.ReadAllBytes(source));
                window.Add([source]);
                var button=(Button)window.FindName("EditPdfButton");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editor=window.ActivePdfEditor??throw new Exception("No inline editor");
                await editor.Ready.WaitAsync(TimeSpan.FromSeconds(60));
                if(Application.Current.Windows.Count!=1 || Window.GetWindow(editor)!=window)throw new Exception("Editor opened outside main window");
                if(((WebView2)window.FindName("PdfViewer")).Visibility==Visibility.Visible)throw new Exception("Native PDF obscures editor");
                var item=editor.SourceTextItems.Single(t=>t.Text.Contains("123,456"));
                editor.SelectSourceText(item.Id);
                var inline=Descendants(editor).OfType<TextBox>().Single(t=>t.Text==item.Text && t!=editor.FindName("EditText"));
                inline.Text="수정 금액 987,654원";
                await Dispatcher.Yield(DispatcherPriority.Render);window.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                using(var output=File.Create(Path.Combine(Path.GetDirectoryName(source)!,"inline-editor-ui.png")))png.Save(output);
                Descendants(editor).OfType<Button>().Single(b=>(string?)b.Content=="내용 적용").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(editor.CurrentPlan.Pages[0].Elements.Single().Kind!="replace")throw new Exception("Inline text was not applied");
                editor.Undo();if(editor.CurrentPlan.Pages[0].Elements.Count!=0)throw new Exception("Undo original text failed");
                editor.Redo();if(editor.CurrentPlan.Pages[0].Elements.Single().Text!="수정 금액 987,654원")throw new Exception("Redo replacement failed");
                var target=Path.Combine(Path.GetDirectoryName(source)!,"inline-ui-"+Guid.NewGuid().ToString("N")+".pdf");
                await editor.SaveAndFinishAsync(target);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var files=(DataGrid)window.FindName("Files");
                while(window.ActivePdfEditor!=null || (files.SelectedItem as FileJob)?.Source!=target || ((WebView2)window.FindName("PdfViewer")).Visibility!=Visibility.Visible)await Task.Delay(100,timeout.Token);
                if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))))throw new Exception("Original changed");
                if(Application.Current.Windows.Count!=1 || (string?)button.Content!="수정")throw new Exception("Did not return to main viewer");
                Console.WriteLine("PASS main-window inline editing: no extra window, native viewer hidden, original text selected, direct typing/apply, undo/redo, new PDF save + new tab, source unchanged. "+target);result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally {
                if(window.ActivePdfEditor is {} editor) { ((TextBox)editor.FindName("EditText")).Text="";for(int i=0;i<30;i++)editor.Undo(); }
                window.Close();app.Shutdown();
            }
        };
        app.Run();return result;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
