using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Topdf;
internal static class UnifiedUiTest
{
    static void Check(bool value,string text){if(!value)throw new Exception(text);}
    static async Task Until(Func<bool> value){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));while(!value())await Task.Delay(100,timeout.Token);}
    public static int Run(string input)
    {
        var result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new MainWindow();window.Show();
            try {
                var source=Path.GetFullPath(input);var hash=SHA256.HashData(File.ReadAllBytes(source));window.Add([source]);
                await Until(()=>window.ActivePdfEditor!=null);var editor=window.ActivePdfEditor!;await editor.Ready.WaitAsync(TimeSpan.FromSeconds(90));
                var viewer=(WebView2)window.FindName("PdfViewer");Check(editor.IsViewing&&viewer.Visibility==Visibility.Visible,"Initially viewing with ribbon");Check(((Button)window.FindName("EditPdfButton")).Visibility==Visibility.Collapsed,"No separate edit button");
                var tool=editor.RibbonButtons.Single(x=>x.Tag.ToString()=="5");tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Until(()=>!editor.IsViewing);Check(!editor.IsViewing&&viewer.Visibility==Visibility.Collapsed,"Tool click immediately enables edit");
                editor.AddText(60,350,"통합 화면 수정 321,000원");
                var before=System.Text.Json.JsonSerializer.Serialize(editor.CurrentPlan);
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                await Until(()=>editor.IsViewing);Check(before==System.Text.Json.JsonSerializer.Serialize(editor.CurrentPlan),"Escape preserves plan");
                var shown=viewer.Source!.LocalPath;Check(shown!=source&&File.Exists(shown),"Modified PDF visible before saving");
                var texts=await PdfEditService.TextAsync(shown,Path.GetDirectoryName(shown)!,0,CancellationToken.None);Check(texts.Any(x=>x.Text.Contains("321,000")),"Native preview contains edit");
                Check(editor.RibbonButtons.Single(x=>x.IsChecked==true).Tag.ToString()=="-1","View icon selected");
                tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Until(()=>!editor.IsViewing);Check(!editor.IsViewing&&editor.CurrentPlan.Pages[0].Elements.Count==1,"Same tool resumes unsaved edit");
                editor.Undo();Check(editor.CurrentPlan.Pages[0].Elements.Count==0,"History retained after Escape");editor.Redo();
                await editor.ReturnToViewingAsync();
                await editor.NavigateViewingPdf!(viewer.Source!.LocalPath,2,100);
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)))while((await editor.ReadViewingPosition!())?.Page!=2)await Task.Delay(100,timeout.Token);
                tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Until(()=>!editor.IsViewing);
                Check(((ListBox)editor.FindName("EditPages")).SelectedIndex==1,"Native current page follows into editing");
                var folder=Path.GetFullPath("artifacts/unified-test");Directory.CreateDirectory(folder);var project=Path.Combine(folder,Guid.NewGuid()+".topdf");await editor.SaveProjectAsync(project);
                window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var image=File.Create(Path.Combine(folder,"unified-editor.png")))encoder.Save(image);
                await editor.ReturnToViewingAsync();
                var other=Path.Combine(folder,Guid.NewGuid()+".pdf");File.Copy(source,other);await window.OpenFilesAsync([other]);await Until(()=>window.ActivePdfEditor!=null&&!ReferenceEquals(window.ActivePdfEditor,editor));await window.ActivePdfEditor!.Ready;
                Check(window.ActivePdfEditor.IsViewing,"Tab switching remains available");
                await window.OpenFilesAsync([project]);await Until(()=>window.ActivePdfEditor?.CurrentPlan.Pages.FirstOrDefault()?.Elements.Count==1);await window.ActivePdfEditor!.Ready;
                Check(window.ActivePdfEditor.IsViewing,"Editable project opens in unified view");
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Original preserved");Check(Application.Current.Windows.Count==1,"Single window");
                Console.WriteLine("PASS unified UI: persistent compact ribbon, direct tool activation, Escape native preview with unsaved edits, same-tool resume, undo/redo retained, PDF/project/tab navigation, single window, source unchanged");result=0;
            } catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{if(window.ActivePdfEditor is {} editor)typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(editor,false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}
