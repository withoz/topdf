using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Topdf;

internal static class SmartUiTest
{
    public static int Run(string scan)
    {
        scan=Path.GetFullPath(scan);
        var hash=SHA256.HashData(File.ReadAllBytes(scan));
        var result=1;var app=new Application();
        app.Startup+=async (_,_)=>{
            var window=new MainWindow();window.Show();
            try {
                var pending=Path.Combine(AppContext.BaseDirectory,"pending-"+Guid.NewGuid()+".docx");
                File.Copy(scan,pending);
                window.Add([pending]);
                await window.OpenFromShellAsync([scan]);
                var jobs=(DataGrid)window.FindName("Files");
                var job=jobs.Items.Cast<FileJob>().Single(x=>x.Source==scan);
                if(job.Pdf!=scan || job.StatusLabel!="열림" || job.Analysis!=null || job.Quality!="" ||
                    jobs.Items.Cast<FileJob>().Single(x=>x.Source==pending).Status!="대기")
                    throw new Exception("Opening a PDF must preserve its original path and leave unrelated conversion and inspection idle");
                var toggle=(ToggleButton)window.FindName("SmartToggle");toggle.IsChecked=true;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                var popup=(Popup)window.FindName("SmartPopup");
                if(!popup.IsOpen || popup.HorizontalOffset>=0)throw new Exception("Smart panel placement incorrect");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var inspect=Buttons(popup.Child).Single(b=>b.Content?.ToString()=="문서 점검");
                inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while(job.Analysis==null)await Task.Delay(100,timeout.Token);
                if(job.Analysis.Pages[0].NeedsOcr!=true)throw new Exception("Requested scan quality check missing");
                var ocr=Buttons(popup.Child).Single(b=>b.Content?.ToString()=="스캔 문자 읽기 (OCR)");
                ocr.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while(job.Pdf==scan || job.Analysis==null)await Task.Delay(100,timeout.Token);
                if(!job.Analysis.Pages[0].Text.Contains("123,456,789"))throw new Exception("OCR text not indexed");
                ((TabControl)((Border)popup.Child).Child).SelectedIndex=1;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                ((TextBox)window.FindName("SearchQuery")).Text="123,456,789";
                var search=Buttons(popup.Child).Single(b=>b.Content?.ToString()=="검색");
                search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var hits=(ListBox)window.FindName("SearchResults");
                while(hits.Items.Count==0)await Task.Delay(100,timeout.Token);
                if(hits.Items[0] is not Evidence {Page:1})throw new Exception("Search page is wrong");
                if(!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(scan))))throw new Exception("Source changed");
                var single=new PdfEditorWindow(scan);
                single.Show();
                try {
                    await single.Ready;
                    var editor=single.View;
                    editor.AddText(35,35,"OCR 주석 유지");
                    var method=typeof(PdfEditorView).GetMethod("RunPageOcr",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                    method.Invoke(editor,[editor,new RoutedEventArgs()]);
                    while(editor.CurrentPlan.Pages[0].SourceIndex==0)await Task.Delay(100,timeout.Token);
                    if(editor.CurrentPlan.Pages.Count!=1||editor.CurrentPlan.Pages[0].Elements.Count!=1)throw new Exception("Page OCR must preserve page annotations");
                    var output=Path.Combine(AppContext.BaseDirectory,"ocr-page-"+Guid.NewGuid()+".pdf");
                    await editor.SaveCopyAsync(output);
                    var text=await PdfEditService.TextAsync(output,Path.GetDirectoryName(output)!,0,timeout.Token,includeInvisible:true);
                    if(!text.Any(line=>line.Text.Contains("123,456,789")))throw new Exception("Selected page OCR text missing from export");
                } finally {
                    typeof(PdfEditorView).GetField("dirty",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(single.View,false);
                    single.Close();
                }
                Console.WriteLine("PASS: real smart popup, direct PDF opening, isolated conversion queue, requested scan check, selected-page OCR, OCR button, indexed amount search, page evidence, source preserved");
                result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{window.Close();app.Shutdown();}
        };
        app.Run();return result;
    }
    static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is Button button)yield return button;
            foreach(var nested in Buttons(child))yield return nested;
        }
    }
}
