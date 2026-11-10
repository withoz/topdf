using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace Topdf;
public sealed record PdfViewingPosition(int Page,double PageWidth);
public partial class PdfEditorView
{
    bool changingView;
    public bool IsBusy=>saving||changingView;
    public Func<Task<PdfViewingPosition?>>? ReadViewingPosition {get;set;}
    public void RequestPdfSave()=>SaveEditedPdf(this,new RoutedEventArgs());
    public bool IsViewing => NativeViewingHost?.Content!=null && (NativeViewingHost.Visibility==Visibility.Visible||SpreadPages.Visibility==Visibility.Visible);
    public bool HasPlanChanges => info!=null && (plan.Pages.Count!=info.Pages.Length || plan.Pages.Where((p,i)=>p.SourceIndex!=i || p.Rotation!=0 || p.Elements.Count!=0).Any());
    public Func<string,int,double,Task>? NavigateViewingPdf {get;set;}
    string? viewingPath, viewingPlan;

    public void AttachNativeViewer(FrameworkElement viewer,string path)
    {
        StartZoomMonitor();EditStatus.Visibility=Visibility.Collapsed;NativeViewingHost.Content=viewer;NativeViewingHost.Visibility=Visibility.Visible;EditingArea.Visibility=Visibility.Collapsed;ViewTool.Visibility=Visibility.Visible;
        RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
    }
    public void AttachDocumentTabs(FrameworkElement tabs)
    {
        DocumentTabsHost.Content=tabs;DocumentTabsHost.Visibility=Visibility.Visible;
    }
    public void DetachDocumentTabs()
    {
        DocumentTabsHost.Content=null;DocumentTabsHost.Visibility=Visibility.Collapsed;
    }
    public FrameworkElement? DetachNativeViewer()
    {
        var viewer=NativeViewingHost.Content as FrameworkElement;NativeViewingHost.Content=null;return viewer;
    }
    public void EnterEditing()
    {
        if(NativeViewingHost.Content==null || info==null || saving)return;
        SpreadPages.Visibility=Visibility.Collapsed;NativeViewingHost.Visibility=Visibility.Collapsed;((FrameworkElement)NativeViewingHost.Content).Visibility=Visibility.Collapsed;EditingArea.Visibility=Visibility.Visible;
        RefreshPagePane();RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
    }
    async void RibbonToolClicked(object sender,RoutedEventArgs e)
    {
        if(changingView)return;
        if(sender is RadioButton {Tag:string tag} && tag=="-1"){await ReturnToViewingAsync();return;}
        TextToolsButton.IsChecked=DrawingToolsButton.IsChecked=MarkupToolsButton.IsChecked=false;
        await EnterFromViewingAsync();
        if(EditTool.SelectedIndex==4 && stamp==null)ChooseStamp(this,new RoutedEventArgs());
    }
    public async Task EnterFromViewingAsync()
    {
        if(!IsViewing){EnterEditing();return;}if(changingView||saving||info==null)return;
        changingView=true;EnableEditing(false);
        try {
            var position=SpreadPages.Visibility==Visibility.Visible?new PdfViewingPosition(EditPages.SelectedIndex+1,spreadRows.SelectMany(x=>x).FirstOrDefault(x=>x.Index==EditPages.SelectedIndex)?.Width??0):ReadViewingPosition==null?null:await ReadViewingPosition();
            if(position!=null && position.Page>=1 && position.Page<=plan.Pages.Count) {
                EditPages.SelectedIndex=position.Page-1;
                if(position.PageWidth>0) {
                    var zoom=Math.Clamp(position.PageWidth/SizeOfPage().W*75,5,800).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"%";
                    var option=EditZoom.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==zoom);
                    if(option==null){option=new ComboBoxItem {Content=zoom};EditZoom.Items.Add(option);}EditZoom.SelectedItem=option;
                }
                await RenderPageAsync();
            }
            EnterEditing();
        } catch(OperationCanceledException) {} catch(Exception ex){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}
        finally{changingView=false;EnableEditing(info!=null&&!saving);}
    }
    public async Task<string> GetViewingPdfAsync()
    {
        if(info==null || saving)throw new InvalidOperationException("문서 준비가 끝난 뒤 사용하세요.");
        EndGraphic();FinishObjectDrag();ApplyInlineText();
        if(selectedSource!=null && inlineText!=null && inlineText.Text!=(Page?.Elements.FirstOrDefault(x=>x.SourceTextId==selectedSource.Id)?.Text??selectedSource.Text))throw new InvalidOperationException("입력 내용을 적용한 뒤 다시 시도하세요.");
        if(!HasPlanChanges)return snapshot;
        var state=JsonSerializer.Serialize(plan,ProjectJson);
        if(viewingPath!=null && viewingPlan==state && File.Exists(viewingPath))return viewingPath;
        saving=true;EnableEditing(false);
        try{var path=await PdfEditService.ExportAsync(snapshot,work,PdfEditService.Clone(plan),lifetime.Token);viewingPath=path;viewingPlan=state;return path;}
        finally{saving=false;EnableEditing(true);}
    }
    public async Task ReturnToViewingAsync(bool force=false)
    {
        if(NativeViewingHost.Content is not FrameworkElement viewer || info==null || saving || changingView || IsViewing&&!force)return;
        changingView=true;
        try {
            PenOptions.IsChecked=TextToolsButton.IsChecked=DrawingToolsButton.IsChecked=MarkupToolsButton.IsChecked=MoreOptions.IsChecked=false;
            var path=await GetViewingPdfAsync();
            EnableEditing(false);
            if(ViewColumns==1 && NavigateViewingPdf!=null)await NavigateViewingPdf(path,EditPages.SelectedIndex+1,CurrentZoomPercent);
            if(ViewColumns>1)BuildSpreads(path);
            selectedSource=null;selectedId="";selectedIds.Clear();
            EditingArea.Visibility=Visibility.Collapsed;NativeViewingHost.Visibility=ViewColumns==1?Visibility.Visible:Visibility.Collapsed;SpreadPages.Visibility=ViewColumns>1?Visibility.Visible:Visibility.Collapsed;viewer.Visibility=ViewColumns==1?Visibility.Visible:Visibility.Collapsed;RefreshPagePane();
            RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
            if(ViewColumns==1){if(zoomFit!=null&&NativeZoomCommand!=null)await NativeZoomAsync(zoomFit);else await SyncViewingZoomAsync();}
            EditStatus.Visibility=Visibility.Collapsed;EditStatus.Text="보기";if(ViewColumns==1)viewer.Focus();else SpreadPages.Focus();
        } catch(OperationCanceledException) {} catch(Exception ex){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}
        finally{changingView=false;EnableEditing(info!=null&&!saving);}
    }
}
