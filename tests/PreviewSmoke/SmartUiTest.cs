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
                await window.OpenFilesAsync([scan]);
                var jobs=(DataGrid)window.FindName("Files");var job=(FileJob)jobs.Items[0];
                if(job.Analysis?.Pages[0].NeedsOcr!=true)throw new Exception("Initial scan quality check missing");
                var toggle=(ToggleButton)window.FindName("SmartToggle");toggle.IsChecked=true;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                var popup=(Popup)window.FindName("SmartPopup");
                if(!popup.IsOpen || popup.HorizontalOffset>=0)throw new Exception("Smart panel placement incorrect");
                var ocr=Buttons(popup.Child).Single(b=>b.Content?.ToString()=="스캔 문자 읽기 (OCR)");
                ocr.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
                Console.WriteLine("PASS: real smart popup, automatic scan check, OCR button, indexed amount search, page evidence, source preserved");
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
