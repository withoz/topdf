using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Topdf;

internal static class ExportUiTest
{
    public static int Run()
    {
        int result=1;
        var app=new Application();
        app.Startup+=(_,_)=>{
            var window=new MainWindow(); window.Show();
            try {
                var list=(DataGrid)window.FindName("Files");
                var jobs=new[] {new FileJob{Source="one.docx"},new FileJob{Source="two.xlsx"},new FileJob{Source="three.hwp"}};
                // Use the normal Add path to exercise change notifications and the displayed count.
                var folder=System.IO.Path.Combine(AppContext.BaseDirectory,"export-ui-fixtures",Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(folder);
                var paths=jobs.Select(j=>System.IO.Path.Combine(folder,j.Source)).ToArray();
                foreach(var path in paths)System.IO.File.WriteAllText(path,"UI-only placeholder; no conversion");
                window.Add(paths);
                list.UpdateLayout();
                var row=(DataGridRow)list.ItemContainerGenerator.ContainerFromIndex(0);
                var checkbox=Descendants(row).OfType<CheckBox>().Single();
                checkbox.IsChecked=true;
                checkbox.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
                if(!((FileJob)list.Items[0]).IsChecked || ((FileJob)list.Items[1]).IsChecked)throw new Exception("Row checkbox binding failed");
                var count=(Button)window.FindName("SaveCheckedButton");
                if(!count.Content.ToString()!.Contains("(1)"))throw new Exception("Selected count did not update");
                Descendants(window).OfType<Button>().Single(b=>(string?)b.Content=="전체 선택").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(list.Items.Cast<FileJob>().Any(j=>!j.IsChecked))throw new Exception("Select all failed");
                Descendants(window).OfType<Button>().Single(b=>(string?)b.Content=="선택 해제").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(list.Items.Cast<FileJob>().Any(j=>j.IsChecked))throw new Exception("Clear selection failed");
                Console.WriteLine("PASS: actual checkbox binding in read-only grid, selected count, select all and clear.");
                result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{window.Close();app.Shutdown();}
        };
        app.Run();return result;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=VisualTreeHelper.GetChild(parent,i);yield return child;
            foreach(var nested in Descendants(child))yield return nested;
        }
    }
}
