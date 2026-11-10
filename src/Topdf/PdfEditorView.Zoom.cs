using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    public double CurrentZoomPercent {get;private set;}=100;
    public Func<string,Task<bool>>? NativeZoomCommand {get;set;}
    readonly SemaphoreSlim zoomActions=new(1);
    bool syncingZoom;
    string? zoomFit;
    CancellationTokenSource? zoomResize,sharpPreview;
    readonly System.Windows.Threading.DispatcherTimer zoomMonitor=new(){Interval=TimeSpan.FromMilliseconds(350)};
    bool zoomMonitorStarted,zoomControlWasDown,zoomMonitorReading;
    void StartZoomMonitor()
    {
        if(zoomMonitorStarted)return;zoomMonitorStarted=true;
        zoomMonitor.Tick+=async(_,_)=>{
            var control=Keyboard.Modifiers.HasFlag(ModifierKeys.Control);var refresh=control||zoomControlWasDown;zoomControlWasDown=control;
            if(!refresh||zoomMonitorReading||!IsViewing||ViewColumns!=1||zoomActions.CurrentCount==0||saving||changingView||Window.GetWindow(this)?.IsActive!=true)return;
            zoomMonitorReading=true;try{var previous=CurrentZoomPercent;await SyncViewingZoomAsync();if(control&&Math.Abs(previous-CurrentZoomPercent)>.5)zoomFit=null;}catch(OperationCanceledException){}catch(Exception){}finally{zoomMonitorReading=false;}
        };
        zoomMonitor.Start();
    }

    void SetZoomLabel(double value)
    {
        value=Math.Clamp(value,5,800);if(Math.Abs(value-Math.Round(value))<.2)value=Math.Round(value);
        CurrentZoomPercent=value;syncingZoom=true;
        try{
            var label=value.ToString("0.#",CultureInfo.InvariantCulture)+"%";
            var option=EditZoom.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==label);
            if(option==null){option=new ComboBoxItem {Content=label};EditZoom.Items.Add(option);}EditZoom.SelectedItem=option;
        }finally{syncingZoom=false;}
    }
    void ApplyZoom()=>EditSurface.LayoutTransform=new ScaleTransform(CurrentZoomPercent/75,CurrentZoomPercent/75);
    async void ZoomChanged(object sender,SelectionChangedEventArgs e)
    {
        if(syncingZoom||EditSurface==null||EditZoom.SelectedItem is not ComboBoxItem item)return;
        if(!double.TryParse(item.Content.ToString()!.TrimEnd('%'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value))return;
        if(changingView){SetZoomLabel(value);ApplyZoom();return;}
        await SetUnifiedZoomAsync(value);
    }
    public async Task SyncViewingZoomAsync()
    {
        if(!IsViewing||ViewColumns!=1||ReadViewingPosition==null||info==null)return;
        PdfViewingPosition? position=null;
        for(int attempt=0;attempt<6;attempt++){
            lifetime.Token.ThrowIfCancellationRequested();position=await ReadViewingPosition();
            if(position?.PageWidth>0)break;await Task.Delay(80,lifetime.Token);
        }
        if(position!=null&&position.PageWidth>0&&IsViewing){var i=Math.Clamp(position.Page-1,0,plan.Pages.Count-1);var page=plan.Pages[i];var data=info.Pages[page.SourceIndex];SetZoomLabel(position.PageWidth/(page.Rotation is 90 or 270?data.Height:data.Width)*75);ApplyZoom();}
    }
    async Task NativeZoomAsync(string command)
    {
        if(!IsViewing||ViewColumns!=1)return;
        await SyncViewingZoomAsync();lifetime.Token.ThrowIfCancellationRequested();
        if(!IsViewing||ViewColumns!=1)return;
        if(NativeZoomCommand==null||!await NativeZoomCommand(command))throw new InvalidOperationException("PDF 확대 컨트롤을 사용할 수 없습니다. PDF를 다시 열어 주세요.");
        await Task.Delay(160,lifetime.Token);await SyncViewingZoomAsync();
    }
    async Task RunZoomAsync(Func<Task> action)
    {
        try{await zoomActions.WaitAsync(lifetime.Token);try{if(info!=null&&!saving&&!changingView)await action();}finally{zoomActions.Release();}}
        catch(OperationCanceledException){}catch(Exception ex){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}
    }
    public Task SetUnifiedZoomAsync(double percent,Point? anchor=null)=>RunZoomAsync(async()=>{
        percent=Math.Clamp(percent,5,800);zoomFit=null;
        if(IsViewing&&ViewColumns==1){
            await SyncViewingZoomAsync();
            for(int i=0;i<32&&Math.Abs(CurrentZoomPercent-percent)>.6;i++){
                if(!IsViewing||ViewColumns!=1||changingView)break;
                var previous=CurrentZoomPercent;var direction=previous<percent?"in":"out";
                await NativeZoomAsync(direction);if(Math.Abs(CurrentZoomPercent-previous)<.1)break;
                if((previous-percent)*(CurrentZoomPercent-percent)<0){
                    if(Math.Abs(CurrentZoomPercent-percent)>Math.Abs(previous-percent))await NativeZoomAsync(direction=="in"?"out":"in");break;
                }
            }
        }else if(IsViewing){SetZoomLabel(percent);UpdateSpreadSizes();}
        else SetEditingZoom(percent,anchor);
    });
    void SetEditingZoom(double percent,Point? anchor)
    {
        var point=anchor??new Point(EditScroll.ViewportWidth/2,EditScroll.ViewportHeight/2);
        var document=EditScroll.TranslatePoint(point,EditSurface);
        SetZoomLabel(percent);ApplyZoom();UpdateZoomAdorners();EditScroll.UpdateLayout();
        var after=EditSurface.TranslatePoint(document,EditScroll);
        EditScroll.ScrollToHorizontalOffset(EditScroll.HorizontalOffset+after.X-point.X);
        EditScroll.ScrollToVerticalOffset(EditScroll.VerticalOffset+after.Y-point.Y);
        ScheduleSharperPreview();
    }
    public Task StepUnifiedZoomAsync(int direction)=>RunZoomAsync(async()=>{
        zoomFit=null;
        if(IsViewing&&ViewColumns==1)await NativeZoomAsync(direction>0?"in":"out");
        else {var next=Math.Clamp(CurrentZoomPercent*Math.Pow(1.15,direction),5,800);if(IsViewing){SetZoomLabel(next);UpdateSpreadSizes();}else SetEditingZoom(next,null);}
    });
    void UpdateZoomAdorners()
    {
        var scale=Math.Max(.01,(EditSurface.LayoutTransform as ScaleTransform)?.ScaleX??1);
        foreach(var thumb in EditCanvas.Children.OfType<System.Windows.Controls.Primitives.Thumb>()){
            var center=new Point(Canvas.GetLeft(thumb)+thumb.Width/2,Canvas.GetTop(thumb)+thumb.Height/2);
            thumb.Width=thumb.Height=10/scale;thumb.BorderThickness=new Thickness(2/scale);
            Canvas.SetLeft(thumb,center.X-thumb.Width/2);Canvas.SetTop(thumb,center.Y-thumb.Height/2);
        }
    }
    async void ZoomIn(object sender,RoutedEventArgs e)=>await StepUnifiedZoomAsync(1);
    async void ZoomOut(object sender,RoutedEventArgs e)=>await StepUnifiedZoomAsync(-1);
    async void FitWidth(object sender,RoutedEventArgs e)=>await FitUnifiedZoomAsync(false);
    async void FitPage(object sender,RoutedEventArgs e)=>await FitUnifiedZoomAsync(true);
    public Task FitUnifiedZoomAsync(bool page)=>RunZoomAsync(async()=>{
        zoomFit=page?"page":"width";
        if(IsViewing&&ViewColumns==1)await NativeZoomAsync(zoomFit);
        else if(IsViewing)UpdateSpreadSizes();
        else{
            var size=SizeOfPage();var scale=Math.Max(20,EditScroll.ActualWidth-60)/size.W;
            if(page)scale=Math.Min(scale,Math.Max(20,EditScroll.ActualHeight-60)/size.H);
            SetEditingZoom(scale*75,null);EditScroll.ScrollToHorizontalOffset(0);if(page)EditScroll.ScrollToVerticalOffset(0);
        }
    });
    async void ZoomMouseWheel(object sender,MouseWheelEventArgs e)
    {
        if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)||info==null||saving)return;
        e.Handled=true;var value=CurrentZoomPercent*Math.Pow(1.15,e.Delta/120.0);
        await SetUnifiedZoomAsync(value,IsViewing?null:e.GetPosition(EditScroll));
    }
    async Task<bool> HandleZoomKeyAsync(KeyEventArgs e)
    {
        if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)||info==null)return false;
        if(e.Key is Key.Add or Key.OemPlus){e.Handled=true;await StepUnifiedZoomAsync(1);return true;}
        if(e.Key is Key.Subtract or Key.OemMinus){e.Handled=true;await StepUnifiedZoomAsync(-1);return true;}
        if(e.Key is Key.D0 or Key.NumPad0){e.Handled=true;await FitUnifiedZoomAsync(false);return true;}
        return false;
    }
    async void ZoomViewportChanged(object sender,SizeChangedEventArgs e)
    {
        if(zoomFit==null||info==null)return;
        var mode=zoomFit;zoomResize?.Cancel();zoomResize?.Dispose();zoomResize=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=zoomResize.Token;
        try{await Task.Delay(180,token);if(!token.IsCancellationRequested&&zoomFit==mode)await FitUnifiedZoomAsync(mode=="page");}catch(OperationCanceledException){}
    }
    async void ScheduleSharperPreview()
    {
        sharpPreview?.Cancel();sharpPreview?.Dispose();
        if(IsViewing||Page==null||CurrentZoomPercent<150||selectedSource!=null||addedTextCommit!=null||drawing!=null)return;
        sharpPreview=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=sharpPreview.Token;var version=renderVersion;var page=Page;
        try{
            await Task.Delay(250,token);var size=SizeOfPage();var pixels=(int)Math.Clamp(Math.Max(size.W,size.H)*CurrentZoomPercent/75*VisualTreeHelper.GetDpi(this).DpiScaleX,1800,3600);
            var key=$"sharp:{page.SourceIndex}:{page.Rotation}:{pixels}:"+System.Text.Json.JsonSerializer.Serialize(page.Elements.Where(x=>x.Kind=="replace"));
            if(!renders.TryGetValue(key,out var path)){path=await PdfEditService.RenderAsync(snapshot,work,page.SourceIndex,page.Rotation,token,PdfEditService.Clone(plan),pixels);renders[key]=path;}
            if(!token.IsCancellationRequested&&version==renderVersion&&!IsViewing&&selectedSource==null&&addedTextCommit==null)EditPageImage.Source=LoadImage(path);
        }catch(OperationCanceledException){}catch(Exception){/* Keep the existing visible preview if optional sharpening fails. */}
    }
}
