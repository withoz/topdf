using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
                using(var initialization=new CancellationTokenSource(TimeSpan.FromSeconds(90)))while(window.ActivePdfEditor==null)await Task.Delay(100,initialization.Token);
                var editor=window.ActivePdfEditor??throw new Exception("No inline editor");
                await editor.Ready.WaitAsync(TimeSpan.FromSeconds(60));
                if(Application.Current.Windows.Count!=1 || Window.GetWindow(editor)!=window)throw new Exception("Editor opened outside main window");
                var sourceTool=editor.RibbonButtons.Single(x=>x.Tag.ToString()=="0");sourceTool.IsChecked=true;sourceTool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                using(var activating=new CancellationTokenSource(TimeSpan.FromSeconds(30)))while(editor.IsViewing)await Task.Delay(100,activating.Token);
                var item=editor.SourceTextItems.Single(t=>t.Text.Contains("123,456"));
                editor.SelectSourceText(item.Id);
                await Dispatcher.Yield(DispatcherPriority.Render);window.UpdateLayout();
                var inline=Descendants(editor).OfType<TextBox>().Single(t=>t.Text==item.Text && t!=editor.FindName("EditText"));
                inline.Text="수정 금액 987,654원";
                await Dispatcher.Yield(DispatcherPriority.Render);window.UpdateLayout();
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                using(var output=File.Create(Path.Combine(Path.GetDirectoryName(source)!,"inline-editor-ui.png")))png.Save(output);
                typeof(PdfEditorView).GetMethod("ApplyInlineText",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(editor,null);
                if(editor.CurrentPlan.Pages[0].Elements.Single().Kind!="replace")throw new Exception("Inline text was not applied");
                var surface=(FrameworkElement)editor.FindName("EditSurface");
                if(surface.Visibility!=Visibility.Visible)throw new Exception("Same-page update hid the page");
                editor.Undo();if(editor.CurrentPlan.Pages[0].Elements.Count!=0)throw new Exception("Undo original text failed");
                editor.Redo();if(editor.CurrentPlan.Pages[0].Elements.Single().Text!="수정 금액 987,654원")throw new Exception("Redo replacement failed");
                var folder=Path.GetDirectoryName(source)!;
                var replaced=Path.Combine(folder,"inline-replaced-"+Guid.NewGuid().ToString("N")+".pdf");
                await editor.SaveCopyAsync(replaced);
                var replacedText=await PdfEditService.TextAsync(replaced,folder,0,CancellationToken.None);
                // The selection engine can return punctuation in its own run.
                var digits=new string(string.Concat(replacedText.Select(line=>line.Text)).Where(char.IsDigit).ToArray());
                if(digits.Contains("123456")||digits.Split("987654").Length!=2)
                    throw new Exception("Saved replacement was missing or duplicated");
                typeof(PdfEditorView).GetMethod("DeleteElement",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(editor,[editor,new RoutedEventArgs()]);
                if(editor.CurrentPlan.Pages[0].Elements.Single().Text!="")throw new Exception("Deleting a replacement restored original text");
                editor.Undo();if(editor.CurrentPlan.Pages[0].Elements.Single().Text!="수정 금액 987,654원")throw new Exception("Undo deletion failed");
                editor.Redo();
                var target=Path.Combine(Path.GetDirectoryName(source)!,"inline-ui-"+Guid.NewGuid().ToString("N")+".pdf");
                await editor.SaveCopyAsync(target);
                var saved=await PdfEditService.TextAsync(target,Path.GetDirectoryName(target)!,0,CancellationToken.None);
                if(saved.Any(line=>line.Text.Contains("123,456")||line.Text.Contains("987,654"))||!saved.Any(line=>line.Text.Contains("7654321")))
                    throw new Exception("Deletion was not retained in the saved PDF");
                if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))))throw new Exception("Original changed");
                Console.WriteLine("PASS inline editing: page stays visible, replacement and deletion support undo/redo, deletion survives PDF export, source unchanged. "+target);result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally {
                if(window.ActivePdfEditor is {} editor)
                    typeof(PdfEditorView).GetMethod("MarkClean",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(editor,null);
                window.Close();app.Shutdown();
            }
        };
        app.Run();return result;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
