using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Topdf;
public partial class PdfEditorView
{
    public int ViewColumns {get;private set;}=1;
    bool pagePaneWanted;
    public Func<Task>? ToggleNativePagePane {get;set;}
    readonly SemaphoreSlim previewSlots=new(2);
    readonly Dictionary<string,string> pagePreviews=[];
    readonly List<List<PageTile>> spreadRows=[];
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
    public async Task SetViewColumnsAsync(int count)
    {
        if(count<1||count>3||IsBusy)return;ViewColumns=count;PageLayoutOptions.IsChecked=false;await ReturnToViewingAsync(true);
    }
    async void TogglePagePane(object sender,RoutedEventArgs e)
    {
        pagePaneWanted=!pagePaneWanted;
        if(IsViewing&&ViewColumns==1&&ToggleNativePagePane!=null){await ToggleNativePagePane();return;}
        RefreshPagePane();
    }
    void HidePagePane(object sender,RoutedEventArgs e){pagePaneWanted=false;RefreshPagePane();}
    void RefreshPagePane()=>PageNavigationPanel.Visibility=pagePaneWanted&&(!IsViewing||ViewColumns>1)?Visibility.Visible:Visibility.Collapsed;
    void BuildSpreads(string pdf)
    {
        spreadRows.Clear();
        for(int first=0;first<plan.Pages.Count;first+=ViewColumns) {
            var row=new List<PageTile>();for(int i=first;i<Math.Min(first+ViewColumns,plan.Pages.Count);i++) {
                var page=plan.Pages[i];var data=info!.Pages[page.SourceIndex];var w=page.Rotation is 90 or 270?data.Height:data.Width;var h=page.Rotation is 90 or 270?data.Width:data.Height;
                row.Add(new(){Index=i,Pdf=pdf,Ratio=h/w});
            }spreadRows.Add(row);
        }
        SpreadPages.Tag=ViewColumns;SpreadPages.ItemsSource=null;SpreadPages.ItemsSource=spreadRows;UpdateSpreadSizes();
        if(EditPages.SelectedIndex>=0&&spreadRows.Count>0)SpreadPages.ScrollIntoView(spreadRows[EditPages.SelectedIndex/ViewColumns]);
    }
    void ResizeSpreads(object sender,SizeChangedEventArgs e)=>UpdateSpreadSizes();
    void UpdateSpreadSizes()
    {
        var width=Math.Max(80,(SpreadPages.ActualWidth-35)/ViewColumns-12);foreach(var tile in spreadRows.SelectMany(x=>x))tile.Width=width;
    }
    void SpreadPageClicked(object sender,RoutedEventArgs e)
    {
        if(sender is Button {DataContext:PageTile tile})EditPages.SelectedIndex=tile.Index;
    }
    void ThumbnailContextChanged(object sender,DependencyPropertyChangedEventArgs e){if(sender is Image image){image.Source=null;if(image.IsLoaded)LoadPageThumbnail(sender,new RoutedEventArgs());}}
    void SpreadContextChanged(object sender,DependencyPropertyChangedEventArgs e){if(sender is Image image){image.Source=null;if(image.IsLoaded)LoadSpreadImage(sender,new RoutedEventArgs());}}
    async void LoadSpreadImage(object sender,RoutedEventArgs e)
    {
        if(sender is not Image {DataContext:PageTile tile} image)return;
        await FillPreviewAsync(image,tile,tile.Pdf,tile.Index,0,null,1100);
    }
    async void LoadPageThumbnail(object sender,RoutedEventArgs e)
    {
        if(sender is not Image {DataContext:PdfEditPage page} image||info==null)return;
        var source=plan.Pages.IndexOf(page);var copy=PdfEditService.Clone(plan);
        var data=info.Pages[page.SourceIndex];image.Height=150*(page.Rotation is 90 or 270?data.Width/data.Height:data.Height/data.Width);
        if(viewingPath!=null&&viewingPlan==JsonSerializer.Serialize(plan,ProjectJson))await FillPreviewAsync(image,page,viewingPath,source,0,null,420);
        else await FillPreviewAsync(image,page,snapshot,page.SourceIndex,page.Rotation,copy,420);
    }
    async Task FillPreviewAsync(Image image,object context,string pdf,int page,int rotation,PdfEditPlan? copy,int pixels)
    {
        try {
            var key=pdf+":"+page+":"+rotation+":"+pixels+(copy==null?"":JsonSerializer.Serialize(copy.Pages.First(p=>p.SourceIndex==page).Elements.Where(x=>x.Kind=="replace"),ProjectJson));
            await previewSlots.WaitAsync(lifetime.Token);
            string path;
            try{if(!ReferenceEquals(image.DataContext,context)||!image.IsVisible)return;if(!pagePreviews.TryGetValue(key,out path!)){path=await PdfEditService.RenderAsync(pdf,work,page,rotation,lifetime.Token,copy,pixels);pagePreviews[key]=path;}}
            finally{previewSlots.Release();}
            if(!ReferenceEquals(image.DataContext,context)||lifetime.IsCancellationRequested)return;
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(Path.GetFullPath(path));bitmap.DecodePixelWidth=pixels;bitmap.EndInit();bitmap.Freeze();image.Source=bitmap;
        }catch(OperationCanceledException){}catch(Exception){/* A failed thumbnail must not replace the document or discard edits. */}
    }
}
