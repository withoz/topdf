using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Topdf;
public partial class PdfEditorView
{
    public int ViewColumns {get;private set;}=1;
    bool pagePaneWanted = true;
    public Func<Task>? ToggleNativePagePane {get;set;}
    readonly SemaphoreSlim previewSlots=new(2);
    readonly Dictionary<string,string> pagePreviews=[];
    readonly Dictionary<Image,CancellationTokenSource> previewRequests=[];
    sealed class SpreadRow : List<PageTile>,INotifyPropertyChanged
    {
        public int Columns {get;init;}
        public double Width=>Count==0?0:this.Max(x=>x.Width+10)*Columns;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh()=>PropertyChanged?.Invoke(this,new(nameof(Width)));
    }
    readonly List<SpreadRow> spreadRows=[];
    sealed class PageTile : INotifyPropertyChanged
    {
        public int Index {get;init;}
        public int Number=>Index+1;
        public required string Pdf {get;init;}
        public double Ratio {get;init;}
        double width;
        public double Width {get=>width;set{width=value;PropertyChanged?.Invoke(this,new(nameof(Width)));PropertyChanged?.Invoke(this,new(nameof(Height)));}}
        public double Height=>Width*Ratio;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    async void ChangePageLayout(object sender,RoutedEventArgs e)
    {
        if(sender is Button {Tag:string text}&&int.TryParse(text,out var count))await SetViewColumnsAsync(count);
    }
    async void UseSharedPageView(object sender,RoutedEventArgs e)=>await SetSharedPageViewAsync();
    public async Task SetViewColumnsAsync(int count)
    {
        if(count<1||count>3||IsBusy)return;
        useSharedPageView=false;zoomFit=count==1?"page":"width";ViewColumns=count;
        PageLayoutOptions.IsChecked=false;await ReturnToViewingAsync(true);
    }
    async void TogglePagePane(object sender,RoutedEventArgs e)
    {
        pagePaneWanted=!pagePaneWanted;
        if(IsNativeViewing&&ToggleNativePagePane!=null){await ToggleNativePagePane();return;}
        RefreshPagePane();
    }
    void HidePagePane(object sender,RoutedEventArgs e){pagePaneWanted=false;RefreshPagePane();}
    bool twoThumbnailColumns;
    void ChangeThumbnailColumns(object sender,SizeChangedEventArgs e)
    {
        var twoColumns=e.NewSize.Width>=260;
        if(twoColumns==twoThumbnailColumns)return;
        twoThumbnailColumns=twoColumns;
        var panel=twoColumns?"TwoColumnThumbnails":"SingleColumnThumbnails";
        EditPages.ItemsPanel=(ItemsPanelTemplate)PageNavigationPanel.Resources[panel];
        ScrollViewer.SetCanContentScroll(EditPages,!twoColumns);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>{
            if(EditPages.SelectedItem is {} page)EditPages.ScrollIntoView(page);
            if(twoThumbnailColumns)RefreshVisibleThumbnails(EditPages);
        }));
    }
    void ThumbnailScrolled(object sender,ScrollChangedEventArgs e)
    {
        if(EditPages.IsVisible)
            RefreshVisibleThumbnails(EditPages);
    }
    void RefreshVisibleThumbnails(DependencyObject parent)
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++){
            var child=VisualTreeHelper.GetChild(parent,index);
            if(child is Image image){
                if(image.Source==null&&!previewRequests.ContainsKey(image))
                    LoadPageThumbnail(image,new RoutedEventArgs());
            }else RefreshVisibleThumbnails(child);
        }
    }
    void RefreshPagePane()
    {
        var visibility=pagePaneWanted&&!IsNativeViewing?Visibility.Visible:Visibility.Collapsed;
        PageNavigationPanel.Visibility=PagePaneResize.Visibility=visibility;
    }

    void ResizePagePane(object sender,DragDeltaEventArgs e)=>SetPagePaneWidth(PageNavigationPanel.Width+e.HorizontalChange);
    void ResetPagePaneWidth(object sender,MouseButtonEventArgs e)
    {
        SetPagePaneWidth(195);
        e.Handled=true;
    }
    void SetPagePaneWidth(double width)
    {
        width=Math.Clamp(width,120,Math.Max(120,Math.Min(420,ActualWidth-300)));
        if(Math.Abs(PageNavigationPanel.Width-width)<.1)return;
        var anchor=new Point(EditScroll.ViewportWidth/2,EditScroll.ViewportHeight/2);
        var pagePoint=EditScroll.TranslatePoint(anchor,EditSurface);
        PageNavigationPanel.Width=width;
        UpdateLayout();
        if(!EditingArea.IsVisible||zoomFit!=null)return;
        var after=EditSurface.TranslatePoint(pagePoint,EditScroll);
        EditScroll.ScrollToHorizontalOffset(EditScroll.HorizontalOffset+after.X-EditScroll.ViewportWidth/2);
        EditScroll.ScrollToVerticalOffset(EditScroll.VerticalOffset+after.Y-EditScroll.ViewportHeight/2);
    }
    void BuildSpreads(string pdf,bool liveOverlays)
    {
        CancelSpreadStroke();
        HideSpreadStampPreview();
        bakedSpreadGraphics.Clear();
        bakedSpreadGraphics.UnionWith(plan.Pages.SelectMany(page=>page.Elements)
            .Where(element=>!liveOverlays||(element.Kind=="replace"&&element.PreserveSourceStyle))
            .Select(element=>element.Id));
        spreadRows.Clear();
        for(int first=0;first<plan.Pages.Count;first+=ViewColumns) {
            var row=new SpreadRow {Columns=ViewColumns};for(int i=first;i<Math.Min(first+ViewColumns,plan.Pages.Count);i++) {
                var page=plan.Pages[i];var data=info!.Pages[page.SourceIndex];var w=page.Rotation is 90 or 270?data.Height:data.Width;var h=page.Rotation is 90 or 270?data.Width:data.Height;
                row.Add(new(){Index=i,Pdf=pdf,Ratio=h/w});
            }spreadRows.Add(row);
        }
        SpreadPages.Tag=ViewColumns;SpreadPages.ItemsSource=null;SpreadPages.ItemsSource=spreadRows;UpdateSpreadSizes();
    }
    async Task PrepareSpreadAsync(string pdf)
    {
        var first=Math.Max(0,EditPages.SelectedIndex)/ViewColumns*ViewColumns;
        for(var index=first;index<Math.Min(first+ViewColumns,plan.Pages.Count);index++){
            var key=PreviewKey(pdf,index,0,null,1100);
            if(pagePreviews.ContainsKey(key))continue;
            var path=await PdfEditService.RenderAsync(pdf,work,index,0,lifetime.Token,null,1100);
            CachePagePreview(key,path);
        }
    }
    void ScrollSpreadToSelection()
    {
        var index=EditPages.SelectedIndex;
        if(index>=0&&index/ViewColumns<spreadRows.Count)
            SpreadPages.ScrollIntoView(spreadRows[index/ViewColumns]);
    }
    void SpreadScrolled(object sender,ScrollChangedEventArgs e)
    {
        if(e.HorizontalChange!=0||e.VerticalChange!=0||e.ViewportWidthChange!=0||e.ViewportHeightChange!=0)ScheduleViewportRender();
        if(!IsSpreadViewing||ViewColumns!=1||changingView||e.VerticalChange==0||
            e.OriginalSource is not ScrollViewer scroll)return;
        var center=scroll.ViewportHeight/2;
        for(var index=0;index<spreadRows.Count;index++){
            if(SpreadPages.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row)continue;
            var top=row.TranslatePoint(new Point(),scroll).Y;
            if(top>center||top+row.ActualHeight<=center)continue;
            updating=true;
            try {EditPages.SelectedIndex=index;}
            finally {updating=false;}
            EditPageNumber.Text=(index+1).ToString();
            break;
        }
    }
    void ResizeSpreads(object sender,SizeChangedEventArgs e){UpdateSpreadSizes();}
    void UpdateSpreadSizes()
    {
        if(info==null||SpreadPages.ActualWidth<=0)return;
        var fit=Math.Max(60,(SpreadPages.ActualWidth-35)/ViewColumns-12);
        foreach(var row in spreadRows){foreach(var tile in row){
            var page=plan.Pages[tile.Index];var data=info.Pages[page.SourceIndex];var pageWidth=page.Rotation is 90 or 270?data.Height:data.Width;
            tile.Width=zoomFit is "width" or "page"?fit:pageWidth*CurrentZoomPercent/75;
            if(zoomFit=="page")tile.Width=Math.Min(tile.Width,Math.Max(50,SpreadPages.ActualHeight-45)/tile.Ratio);
        }row.Refresh();}
        ScheduleViewportRender();
        if(zoomFit!=null&&spreadRows.FirstOrDefault()?.FirstOrDefault() is {} first){var page=plan.Pages[first.Index];var data=info.Pages[page.SourceIndex];SetZoomLabel(first.Width/(page.Rotation is 90 or 270?data.Height:data.Width)*75);}

    }
    void SpreadPageClicked(object sender,RoutedEventArgs e)
    {
        if(sender is Button {DataContext:PageTile tile})EditPages.SelectedIndex=tile.Index;
    }
    void ThumbnailContextChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(sender is not Image image)return;
        CancelPreviewImage(image,new RoutedEventArgs());
        image.Source=null;
        LoadPageThumbnail(image,new RoutedEventArgs());
    }
    void ThumbnailVisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(e.NewValue is true)LoadPageThumbnail(sender,new RoutedEventArgs());
    }
    void SpreadContextChanged(object sender,DependencyPropertyChangedEventArgs e){if(sender is Image image){image.Source=null;image.Tag=null;ClearSpreadDetail(image);if(image.IsLoaded)LoadSpreadImage(sender,new RoutedEventArgs());}}
    void UnloadSpreadImage(object sender,RoutedEventArgs e)
    {
        if(sender is Image image){image.Tag=null;ClearSpreadDetail(image);}
        CancelPreviewImage(sender,e);
    }
    static void ClearSpreadDetail(Image image)
    {
        if(image.Parent is Grid grid && grid.Children.OfType<Canvas>().FirstOrDefault() is {} layer){layer.Children.Clear();layer.Tag=null;}
    }
    void SpreadImageSizeChanged(object sender,SizeChangedEventArgs e) => LoadSpreadImage(sender,e);
    async void LoadSpreadImage(object sender,RoutedEventArgs e)
    {
        if(sender is not Image {DataContext:PageTile tile} image)return;
        if(!image.IsLoaded)return;
        ScheduleViewportRender();
        var dpi=VisualTreeHelper.GetDpi(image);
        var requested=Math.Max(tile.Width*dpi.DpiScaleX,tile.Width*tile.Ratio*dpi.DpiScaleY);
        if(requested>4200&&image.Source!=null){CancelPreviewImage(image,e);image.Tag=null;return;}
        var pixels=requested>4200?1100:(int)Math.Clamp(Math.Ceiling(requested/300)*300,1100,4200);
        if(image.Tag is int previous&&previous==pixels)return;
        image.Tag=pixels;
        if(image.Source==null&&pagePreviews.TryGetValue(PreviewKey(tile.Pdf,tile.Index,0,null,1100),out var warm))image.Source=LoadImage(warm);
        if(pagePreviews.TryGetValue(PreviewKey(tile.Pdf,tile.Index,0,null,pixels),out var cached)){
            CancelPreviewImage(image,e);
            image.Source=LoadImage(cached);
            return;
        }
        await FillPreviewAsync(image,tile,tile.Pdf,tile.Index,0,null,pixels);
    }
    async void LoadPageThumbnail(object sender,RoutedEventArgs e)
    {
        if(sender is not Image {DataContext:PdfEditPage page, IsLoaded:true, IsVisible:true, Source:null} image||
            info==null||previewRequests.ContainsKey(image))return;
        if(twoThumbnailColumns){
            var top=image.TranslatePoint(new Point(),EditPages).Y;
            if(!image.IsVisible||EditPages.ActualHeight<=0||top>2*EditPages.ActualHeight||
                top+image.ActualHeight< -EditPages.ActualHeight)return;
        }
        var source=plan.Pages.IndexOf(page);
        var copy=page.Elements.Any(element=>element.Kind=="replace")?new PdfEditPlan {
            SourceSha256=plan.SourceSha256,Pages=[new(){SourceIndex=page.SourceIndex,Elements=page.Elements.Where(element=>element.Kind=="replace").ToList()}]
        }:null;

        if(viewingPath!=null&&viewingPlan==JsonSerializer.Serialize(plan,ProjectJson))await FillPreviewAsync(image,page,viewingPath,source,0,null,420);
        else await FillPreviewAsync(image,page,snapshot,page.SourceIndex,page.Rotation,copy,420);
    }
    void CancelPreviewImage(object sender,RoutedEventArgs e)
    {
        if(sender is Image image&&previewRequests.Remove(image,out var old))old.Cancel();
    }
    string PreviewKey(string pdf,int page,int rotation,PdfEditPlan? copy,int pixels) =>
        pdf+":"+page+":"+rotation+":"+pixels+(copy==null?"":JsonSerializer.Serialize(copy,ProjectJson));
    void CachePagePreview(string key,string path)
    {
        if(pagePreviews.Count>=128)pagePreviews.Remove(pagePreviews.Keys.First());
        pagePreviews[key]=path;
    }
    async Task FillPreviewAsync(Image image,object context,string pdf,int page,int rotation,PdfEditPlan? copy,int pixels)
    {
        CancelPreviewImage(image,new RoutedEventArgs());
        using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewRequests[image]=request;
        var token=request.Token;
        try {
            var key=PreviewKey(pdf,page,rotation,copy,pixels);
            while(EditingArea.IsVisible&&EditSurface.Visibility==Visibility.Hidden)await Task.Delay(30,token);
            await previewSlots.WaitAsync(token);
            string path;
            try {
                if(!ReferenceEquals(image.DataContext,context)||!image.IsVisible)return;
                if(!pagePreviews.TryGetValue(key,out path!)) {
                    path=await PdfEditService.RenderAsync(pdf,work,page,rotation,token,copy,pixels);
                    token.ThrowIfCancellationRequested();
                    CachePagePreview(key,path);
                }
            } finally {previewSlots.Release();}
            if(ReferenceEquals(image.DataContext,context)&&!token.IsCancellationRequested)image.Source=LoadImage(path);
        } catch(OperationCanceledException) {}
        catch(Exception) {/* Keep the document available when a thumbnail fails. */}
        finally {
            if(previewRequests.TryGetValue(image,out var active)&&ReferenceEquals(active,request))previewRequests.Remove(image);
        }
    }
}
