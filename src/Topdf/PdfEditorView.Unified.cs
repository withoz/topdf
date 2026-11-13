using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace Topdf;
public sealed record PdfViewingPosition(int Page,double PageWidth,Point? ViewportCenter=null);
public partial class PdfEditorView
{
    bool changingView;
    bool editorOnly, useSharedPageView, sharedPageViewing;
    public bool IsBusy=>saving||changingView;
    public Func<Task<PdfViewingPosition?>>? ReadViewingPosition {get;set;}
    public void RequestPdfSave()=>SaveEditedPdf(this,new RoutedEventArgs());
    bool IsNativeViewing => NativeViewingHost?.Content!=null && NativeViewingHost.Visibility==Visibility.Visible;
    bool IsSpreadViewing => SpreadPages?.Visibility==Visibility.Visible;
    public bool IsViewing => sharedPageViewing || IsNativeViewing || IsSpreadViewing;
    public bool HasPlanChanges => info!=null && (snapshot!=baselineSnapshot || plan.Pages.Count!=info.Pages.Length || plan.Pages.Where((p,i)=>p.SourceIndex!=i || p.Rotation!=0 || p.Elements.Count!=0).Any());
    public Func<string,int,double,Task>? NavigateViewingPdf {get;set;}
    string? viewingPath, viewingPlan;

    public void AttachSharedEditor()
    {
        editorOnly=true;
        useSharedPageView=true;
        SharedPageOption.Visibility=Visibility.Collapsed;
        EditStatus.Visibility=Visibility.Collapsed;
    }
    public void AttachNativeViewer(FrameworkElement viewer,string path)
    {
        StartZoomMonitor();EditStatus.Visibility=Visibility.Collapsed;NativeViewingHost.Content=viewer;NativeViewingHost.Visibility=Visibility.Visible;EditingArea.Visibility=Visibility.Collapsed;
        RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
    }
    internal void AttachWindowActions(FrameworkElement actions)
    {
        RibbonWindowActions.Content=actions;
        RibbonWindowActions.Visibility=Visibility.Visible;
    }
    internal void DetachWindowActions()
    {
        RibbonWindowActions.Content=null;
        RibbonWindowActions.Visibility=Visibility.Collapsed;
    }
    public void AttachDocumentTabs(FrameworkElement tabs)
    {
        DocumentTabsHost.Content=tabs;
        DocumentTabsHost.Visibility=Visibility.Visible;
        DocumentPathLabel.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding("SelectedItem.Source") { Source=tabs });
        DocumentPathBar.Visibility=Visibility.Visible;
    }
    public void DetachDocumentTabs()
    {
        DocumentTabsHost.Content=null;
        DocumentTabsHost.Visibility=Visibility.Collapsed;
        System.Windows.Data.BindingOperations.ClearBinding(DocumentPathLabel,TextBlock.TextProperty);
        DocumentPathBar.Visibility=Visibility.Collapsed;
    }
    public FrameworkElement? DetachNativeViewer()
    {
        var viewer=NativeViewingHost.Content as FrameworkElement;NativeViewingHost.Content=null;return viewer;
    }
    public void EnterEditing()
    {
        if((!editorOnly&&NativeViewingHost.Content==null) || info==null || saving)return;
        CancelSpreadStroke();
        HideSpreadStampPreview();
        sharedPageViewing=false;EditCanvas.IsHitTestVisible=true;
        SpreadPages.Visibility=Visibility.Collapsed;NativeViewingHost.Visibility=Visibility.Collapsed;if(NativeViewingHost.Content is FrameworkElement viewer)viewer.Visibility=Visibility.Collapsed;EditingArea.Visibility=Visibility.Visible;
        RefreshPagePane();RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
    }
    async void RibbonToolClicked(object sender,RoutedEventArgs e)
    {
        if(changingView)return;
        RegionTool.IsChecked=false;
        ClearRegion();
        TextToolsButton.IsChecked=DrawingToolsButton.IsChecked=MarkupToolsButton.IsChecked=false;
        CancelSpreadStroke();
        if(CanEditSpread&&Tool=="선택"){
            SpreadPages.Focus();
            return;
        }
        if(CanEditSpread&&Tool=="도장"){
            if(stamp==null)ChooseStamp(this,new RoutedEventArgs());
            else SpreadPages.Focus();
            return;
        }
        if(CanEditSpread&&(CurrentGraphicKind!=null || Tool is "메모" or "글자")){
            SpreadPages.Focus();
            return;
        }
        await EnterFromViewingAsync();
        if(EditTool.SelectedIndex==4 && stamp==null)ChooseStamp(this,new RoutedEventArgs());
    }
    public async Task EnterFromViewingAsync()
    {
        if(changingView||saving||info==null)return;
        if(!IsViewing||sharedPageViewing){EnterEditing();return;}
        changingView=true;EnableEditing(false);
        try {
            var position=SpreadPages.Visibility==Visibility.Visible?new PdfViewingPosition(EditPages.SelectedIndex+1,spreadRows.SelectMany(x=>x).FirstOrDefault(x=>x.Index==EditPages.SelectedIndex)?.Width??0):ReadViewingPosition==null?null:await ReadViewingPosition();
            if(position!=null && position.Page>=1 && position.Page<=plan.Pages.Count) {
                if(EditPages.SelectedIndex!=position.Page-1) {
                    selectedId="";selectedIds.Clear();selectedSource=null;
                    updating=true;
                    try{EditPages.SelectedIndex=position.Page-1;}
                    finally{updating=false;}
                }
                if(position.PageWidth>0)
                    SetZoomLabel(Math.Clamp(position.PageWidth/SizeOfPage().W*75,5,800));
                await RenderPageAsync();
            }
            EnterEditing();
            if(position?.ViewportCenter is Point center && double.IsFinite(center.X) && double.IsFinite(center.Y)) {
                UpdateLayout();
                var documentPoint=new Point(center.X*EditSurface.Width,center.Y*EditSurface.Height);
                var current=EditSurface.TranslatePoint(documentPoint,EditScroll);
                EditScroll.ScrollToHorizontalOffset(EditScroll.HorizontalOffset+current.X-EditScroll.ViewportWidth/2);
                EditScroll.ScrollToVerticalOffset(EditScroll.VerticalOffset+current.Y-EditScroll.ViewportHeight/2);
                EditScroll.UpdateLayout();
            }
            ScheduleSharperPreview();
        } catch(OperationCanceledException) {} catch(Exception ex){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}
        finally{changingView=false;EnableEditing(info!=null&&!saving);}
    }
    public async Task SetSharedPageViewAsync(bool startEditing=false)
    {
        if(IsBusy||info==null||(!editorOnly&&NativeViewingHost.Content==null))return;
        PageLayoutOptions.IsChecked=false;
        if(startEditing) {
            useSharedPageView=true;ViewColumns=1;
            EditTool.SelectedIndex=1;
            EnterEditing();
            await FitUnifiedZoomAsync(true);
            EditScroll.Focus();
            return;
        }
        if(IsViewing && !sharedPageViewing)await EnterFromViewingAsync();
        if(IsViewing && !sharedPageViewing)return;
        useSharedPageView=true;ViewColumns=1;
        await ReturnToViewingAsync(true);
    }
    void ShowSharedPageView()
    {
        EndGraphic();FinishObjectDrag();ApplyInlineText();
        if(selectedSource!=null && inlineText!=null && inlineText.Text!=(Page?.Elements.FirstOrDefault(x=>x.SourceTextId==selectedSource.Id)?.Text??selectedSource.Text))return;
        selectedSource=null;selectedId="";selectedIds.Clear();
        sharedPageViewing=true;EditCanvas.IsHitTestVisible=false;
        DrawElements();
        RefreshPagePane();
        RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
        EditScroll.Focus();
    }
    public async Task<string> GetViewingPdfAsync()
    {
        commitPendingOpacity?.Invoke();
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
    public Task ReturnToViewingAsync(bool force=false) => UpdateViewingAsync(force,false);

    string? spreadBackgroundSource,spreadBackgroundState,spreadBackgroundPath;

    async Task<string> GetSpreadBackgroundAsync()
    {
        var background=PdfEditService.Clone(plan);
        foreach(var page in background.Pages){
            page.Elements.RemoveAll(element=>element.Kind!="replace");
            // Keep the source text removal, but draw the replacement at its position in the overlay order.
            foreach(var element in page.Elements.Where(element=>!element.PreserveSourceStyle))
                element.Text="";
        }
        if(background.Pages.Count==info!.Pages.Length&&
            !background.Pages.Where((page,index)=>page.SourceIndex!=index||page.Rotation!=0||page.Elements.Count!=0).Any())
            return snapshot;

        var state=JsonSerializer.Serialize(background,ProjectJson);
        if(spreadBackgroundSource==snapshot&&spreadBackgroundState==state&&
            spreadBackgroundPath!=null&&File.Exists(spreadBackgroundPath))return spreadBackgroundPath;

        var path=await PdfEditService.ExportAsync(snapshot,work,background,lifetime.Token);
        spreadBackgroundSource=snapshot;
        spreadBackgroundState=state;
        spreadBackgroundPath=path;
        return path;
    }

    async Task UpdateViewingAsync(bool force,bool preserveSpreadSelection)
    {
        var viewer=NativeViewingHost.Content as FrameworkElement;
        if((!editorOnly&&viewer==null)||info==null||saving||changingView||(IsViewing&&!force))return;
        if(CanEditSpread)CancelPendingEditGesture();
        changingView=true;
        var viewport=preserveSpreadSelection?CaptureCanvasViewport():null;
        try {
            PenOptions.IsChecked=TextToolsButton.IsChecked=DrawingToolsButton.IsChecked=MarkupToolsButton.IsChecked=false;
            if(useSharedPageView&&ViewColumns==1&&EditingArea.Visibility==Visibility.Visible) {
                ShowSharedPageView();
                return;
            }
            var useSpread=ViewColumns>1||editorOnly;
            var liveOverlays=ViewColumns>1;
            string path;
            if(liveOverlays){
                EndGraphic();
                FinishObjectDrag();
                ApplyInlineText();
                EnableEditing(false);
                path=await GetSpreadBackgroundAsync();
            }else{
                path=await GetViewingPdfAsync();
                EnableEditing(false);
            }
            if(!useSpread && NavigateViewingPdf!=null)await NavigateViewingPdf(path,EditPages.SelectedIndex+1,CurrentZoomPercent);
            if(useSpread){
                await PrepareSpreadAsync(path);
                BuildSpreads(path,liveOverlays);
            }
            selectedSource=null;
            if(!preserveSpreadSelection){selectedId="";selectedIds.Clear();}
            sharedPageViewing=false;
            EditingArea.Visibility=Visibility.Collapsed;NativeViewingHost.Visibility=useSpread?Visibility.Collapsed:Visibility.Visible;SpreadPages.Visibility=useSpread?Visibility.Visible:Visibility.Collapsed;if(viewer!=null)viewer.Visibility=NativeViewingHost.Visibility;RefreshPagePane();
            RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
            if(useSpread){
                UpdateLayout();
                if(viewport!=null)RestoreCanvasViewport(viewport);
                else {ScrollSpreadToSelection();SpreadPages.Focus();}
            }else{
                if(zoomFit!=null&&NativeZoomCommand!=null)await NativeZoomAsync(zoomFit);else await SyncViewingZoomAsync();
                viewer?.Focus();
            }
            EditStatus.Visibility=Visibility.Collapsed;EditStatus.Text="보기";
        } catch(OperationCanceledException) {} catch(Exception ex){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}
        finally{changingView=false;EnableEditing(info!=null&&!saving);}
    }
}
