using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Topdf;

public partial class MainWindow
{
    List<string>? recentFiles;
    static string RecentFilePath => Path.Combine(AppPaths.DataRoot,"recent-files.json");

    List<string> RecentFiles
    {
        get {
            if(recentFiles!=null)return recentFiles;
            try {
                recentFiles=File.Exists(RecentFilePath)
                    ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFilePath)) ?? []
                    : [];
            } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {
                recentFiles=[];
            }
            return recentFiles;
        }
    }

    void RememberOpenedFiles(IEnumerable<string> paths)
    {
        var recent=paths.Concat(RecentFiles).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        recentFiles=recent;
        try {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(RecentFilePath,JsonSerializer.Serialize(recent));
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {
            // Keep recent files available in this session if settings cannot be written.
        }
    }

    void FileMenuOpened(object sender,RoutedEventArgs e)
    {
        if(!ReferenceEquals(sender,e.Source))return;
        MenuSaveProject.CommandTarget=MenuExportPdf.CommandTarget=pdfEditor?.MenuCanvasTarget;
        MenuSaveProject.IsEnabled=MenuExportPdf.IsEnabled=pdfEditor!=null;
        MenuCloseTab.IsEnabled=SelectedJob!=null;
        RecentFileMenu.Items.Clear();
        foreach(var path in RecentFiles.Where(File.Exists)) {
            var item=new MenuItem{Header=Path.GetFileName(path).Replace("_","__"),ToolTip=path};
            item.Click+=async(_,_)=>await OpenFilesAsync([path]);
            RecentFileMenu.Items.Add(item);
        }
        RecentFileMenu.IsEnabled=RecentFileMenu.Items.Count>0;
    }

    void EditMenuOpened(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem menu||!ReferenceEquals(sender,e.Source))return;
        MenuFind.IsEnabled=pdfEditor!=null&&!pdfEditor.IsBusy;
        var focused=FocusManager.GetFocusedElement(this);
        foreach(var item in menu.Items.OfType<MenuItem>().Where(item=>item.Command!=null)) {
            item.CommandTarget=pdfEditor?.GetMenuEditTarget(focused);
            item.IsEnabled=pdfEditor!=null;
        }
    }

    void ViewMenuOpened(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem menu||!ReferenceEquals(sender,e.Source))return;
        foreach(var item in menu.Items.OfType<MenuItem>().Where(item=>item.Tag!=null))
        {
            item.IsEnabled=pdfEditor!=null&&!pdfEditor.IsBusy;
            if(Equals(item.Tag,"properties"))item.IsChecked=pdfEditor?.IsObjectPropertiesOpen==true;
        }
    }

    void WindowMenuOpened(object sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem menu||!ReferenceEquals(sender,e.Source))return;
        menu.Items.Clear();
        foreach(var job in jobs) {
            var item=new MenuItem{Header=job.TabName.Replace("_","__"),ToolTip=job.Source,IsCheckable=true,IsChecked=ReferenceEquals(SelectedJob,job)};
            item.Click+=(_,_)=>Files.SelectedItem=job;
            menu.Items.Add(item);
        }
        if(menu.Items.Count==0)menu.Items.Add(new MenuItem{Header="열린 문서 없음",IsEnabled=false});
    }

    async void ViewMenuAction(object sender,RoutedEventArgs e)
    {
        if(pdfEditor is not {} editor||sender is not MenuItem {Tag:string action})return;
        if(action=="properties")editor.ToggleObjectProperties();
        else if(action=="outline")editor.ToggleOutlineFromMenu();
        else if(action is "width" or "page")await editor.FitUnifiedZoomAsync(action=="page");
        else if(int.TryParse(action,out var columns))await editor.SetViewColumnsAsync(columns);
    }

    void FindFromMenu(object sender,RoutedEventArgs e)=>pdfEditor?.FindFromMenu();
    void CloseSelectedDocument(object sender,RoutedEventArgs e){if(SelectedJob is {} job)CloseDocumentTab(job);}
    void ExitApplication(object sender,RoutedEventArgs e)=>Close();
    void ShowMenuShortcuts(object sender,RoutedEventArgs e)=>MessageBox.Show(this,
        "Ctrl+O  열기\nCtrl+S  편집 파일 저장\nCtrl+Shift+S  PDF 내보내기\nCtrl+F  찾기\nCtrl+W  문서 닫기\nCtrl+Q  끝내기\nF11  전체 화면 전환",
        "키보드 단축키");
    void ShowAppInformation(object sender,RoutedEventArgs e)=>MessageBox.Show(this,
        $"TOPDF {typeof(App).Assembly.GetName().Version?.ToString(3)}\nPDF 편집기","TOPDF 정보");
}

public partial class PdfEditorView
{
    internal IInputElement MenuCanvasTarget=>EditCanvas;
    internal IInputElement GetMenuEditTarget(IInputElement? focused)=>
        focused is DependencyObject element&&IsAncestorOf(element)&&IsTextCommandTarget(focused) ? focused : EditCanvas;
    internal void ToggleOutlineFromMenu()=>TogglePagePane(this,new RoutedEventArgs());
    internal void FindFromMenu()=>OpenPageSearch();
}
