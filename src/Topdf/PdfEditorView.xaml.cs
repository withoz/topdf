using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Topdf;

public partial class PdfEditorView : UserControl
{
    readonly string input;
    string baselineSnapshot="";
    readonly string suggestedName;
    readonly string work = Path.Combine(AppPaths.Cache, "pdf-edit", Guid.NewGuid().ToString("N"));
    readonly CancellationTokenSource lifetime = new();
    readonly TaskCompletionSource ready = new();
    readonly Stack<History> undo = new(), redo = new();
    readonly Dictionary<string, BitmapImage> images = new();
    readonly Dictionary<string, string> renders = new();
    readonly Dictionary<int, PdfSourceText[]> sourceTexts = new();
    PdfSourceText? selectedSource;
    TextBox? inlineText;
    bool selectingText;

    PdfEditInfo? info;
    PdfEditPlan plan = new();
    string snapshot = "", selectedId = "";
    string? stamp;
    bool updating, dirty, saving;
    long renderVersion;
    CancellationTokenSource? renderCancellation;
    PdfEditElement? dragging;
    Point dragStart;
    double dragX, dragY;
    bool dragThresholdReached;
    History? dragBefore;
    sealed record History(PdfEditPlan Plan, int Page, string Selected, string[] Selection, string Snapshot, PdfEditInfo Info);

    public string? ResultPath { get; private set; }
    public Task Ready => ready.Task;
    internal (int Count, int Current) ExportPagePosition => (plan.Pages.Count, Math.Max(1, EditPages.SelectedIndex + 1));
    public PdfEditPlan CurrentPlan => PdfEditService.Clone(plan);
    PdfEditPage? Page => EditPages.SelectedItem as PdfEditPage;
    PdfEditElement? Selected => Page?.Elements.FirstOrDefault(e => e.Id == selectedId);
    double FontSizeValue => double.Parse(((ComboBoxItem)EditFontSize.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
    string ColorValue => (EditColor.SelectedItem as ComboBoxItem)?.Tag as string ?? EditColor.SelectedIndex switch { 1 => "#C92127", 2 => "#1755B5", _ => "#111111" };
    string Tool => ((ComboBoxItem)EditTool.SelectedItem).Content.ToString()!;

    public PdfEditorView(string pdf, string? displayName = null)
    {
        InitializeComponent();
        if(!BuildFeatures.SmartEnabled)PageOcrMenuItem.Visibility=Visibility.Collapsed; InitializeRibbon();InitializeEditCommands(); input = Path.GetFullPath(pdf);
        suggestedName = Path.GetFileNameWithoutExtension(displayName ?? input) + "_수정.pdf";

        EnableEditing(false);
        EditPages.PreviewMouseDown += (_,_) => ApplyInlineText();
        Loaded += async (_, _) => { if (initialized) return; initialized = true; await InitializeAsync(); };

    }
    bool initialized;
    public event EventHandler? Saved;
    Window DialogOwner => Window.GetWindow(this);
    public void DisposeEditor() { StopDocumentPan();zoomMonitor.Stop();ClearPageSearch();StopRecovery();lifetime.Cancel(); viewportWorker.Dispose(); renderCancellation?.Cancel(); }
    public bool CanClose() { var e = new CancelEventArgs(); CheckClosing(this,e); return !e.Cancel; }
    async Task InitializeAsync()
    {
        try {
            Directory.CreateDirectory(work);
            viewportWorker.WarmUp();
            if(Path.GetExtension(input).Equals(".topdf",StringComparison.OrdinalIgnoreCase)){await LoadProjectAsync(input);EnableEditing(true);ready.TrySetResult();return;}
            snapshot = Path.Combine(work, "input.pdf");
            if (new FileInfo(input).Length > 200_000_000) throw new IOException("편집은 200MB 이하 PDF를 지원합니다.");
            await using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write)) await source.CopyToAsync(target, lifetime.Token);
            info = await PdfEditService.InspectAsync(snapshot, work, lifetime.Token);
            baselineSnapshot=snapshot;
            plan = new() { SourceSha256 = info.SourceSha256, Pages = info.Pages.Select(p => new PdfEditPage { SourceIndex = p.SourceIndex }).ToList() };
            MarkClean(); BindPages(0); EnableEditing(true);
            RefreshPdfNotes(openIfPresent:true);
            await RenderPageAsync(); ready.TrySetResult();
        } catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception ex) { EditStatus.Text = ex.Message; ready.TrySetException(ex); }
    }
    async void GoToPage(object sender, RoutedEventArgs e) {
        if (saving || info == null) return;
        ApplyInlineText();
        if(int.TryParse(EditPageNumber.Text,out var number))await NavigateToPageAsync(number);
        else EditStatus.Text=$"1~{plan.Pages.Count} 사이의 페이지 번호를 입력하세요.";
    }
    public async Task NavigateToPageAsync(int number)
    {
        if(IsBusy||info==null)return;
        if(number<1||number>plan.Pages.Count){EditStatus.Text=$"1~{plan.Pages.Count} 사이의 페이지 번호를 입력하세요.";return;}
        ApplyInlineText();
        if(IsSpreadViewing){
            EditPages.SelectedIndex=number-1;
            EditPages.ScrollIntoView(EditPages.SelectedItem);
            ScrollSpreadToSelection();
            return;
        }
        if(EditPages.SelectedIndex!=number-1) {
            updating=true;
            try {EditPages.SelectedIndex=number-1;}
            finally {updating=false;}
            selectedId="";selectedIds.Clear();selectedSource=null;
            await RenderPageAsync();
        }
        EditPages.ScrollIntoView(EditPages.SelectedItem);
        if(IsViewing&&!sharedPageViewing)await ReturnToViewingAsync(true);
        else {
            EditScroll.ScrollToVerticalOffset(0);
            EditScroll.UpdateLayout();
        }
    }
    void PageNumberKey(object sender, KeyEventArgs e) { if(e.Key==Key.Enter) {GoToPage(sender,e);e.Handled=true;} }
    void EnableEditing(bool enabled) { EditorRibbon.IsEnabled=enabled;TextTools.IsEnabled=DrawingTools.IsEnabled=MarkupTools.IsEnabled=enabled; EditorTools.IsEnabled = enabled; EditorHistory.IsEnabled = enabled; EditorSave.IsEnabled = enabled; EditPages.IsEnabled = enabled; EditCanvas.IsEnabled = enabled; ObjectPropertiesFields.IsEnabled=enabled; }
    (double W, double H) SizeOfPage()
    {
        var data = info!.Pages[Page!.SourceIndex];
        return Page.Rotation is 90 or 270 ? (data.Height, data.Width) : (data.Width, data.Height);
    }
    void BindPages(int index)
    {
        for(int i=0;i<plan.Pages.Count;i++)plan.Pages[i].DisplayNumber=i+1;
        updating = true; EditPages.ItemsSource = null; EditPages.ItemsSource = plan.Pages;
        EditPages.SelectedIndex = Math.Clamp(index, 0, plan.Pages.Count-1); updating = false;
    }
    async void EditorPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if(updating||info==null)return;
        if(IsSpreadViewing){
            selectedId="";
            selectedIds.Clear();
            RefreshSpreadSelection();
            RefreshObjectProperties();
            EditPageNumber.Text=(EditPages.SelectedIndex+1).ToString();
            ScrollSpreadToSelection();
            return;
        }
        selectedId="";selectedIds.Clear();selectedSource=null;
        await RenderPageAsync();
    }
    (string Snapshot,int Page,int Rotation)? displayedPage;
    async Task RenderPageAsync()
    {
        StopContinuousLine();
        if (Page == null || info == null) return;
        ClearViewportPatch();
        var page = Page; var version = ++renderVersion;
        EditPageNumber.Text = (EditPages.SelectedIndex+1).ToString();
        renderCancellation?.Cancel(); renderCancellation?.Dispose();
        renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = renderCancellation.Token;
        var identity=(snapshot,page.SourceIndex,page.Rotation);
        var refreshInPlace=displayedPage==identity && EditSurface.Visibility==Visibility.Visible;
        if(!refreshInPlace){
            EditSurface.Visibility = Visibility.Hidden; EditCanvas.IsEnabled = false;
            EditStatus.Text = "페이지를 불러오고 있습니다…";
        }
        try {
            var replacements=System.Text.Json.JsonSerializer.Serialize(page.Elements.Where(element=>element.Kind=="replace"));
            var key=$"{page.SourceIndex}:{page.Rotation}:{replacements}";
            var textTask=sourceTexts.TryGetValue(page.SourceIndex,out var cachedText)
                ? Task.FromResult(cachedText)
                : PdfEditService.TextAsync(snapshot,work,page.SourceIndex,token);
            var imageTask=renders.TryGetValue(key,out var cachedImage)
                ? Task.FromResult(cachedImage)
                : PdfEditService.RenderAsync(snapshot,work,page.SourceIndex,page.Rotation,token,plan);
            try { await Task.WhenAll(textTask,imageTask); }
            catch (Exception) when (textTask.IsFaulted && imageTask.IsCompletedSuccessfully && !token.IsCancellationRequested) {
                // A text worker failure must not prevent the page image from opening.
            }
            if(version!=renderVersion||token.IsCancellationRequested)return;
            var image=await imageTask;
            renders[key]=image;
            if(textTask.IsCompletedSuccessfully)sourceTexts[page.SourceIndex]=textTask.Result;
            var bitmap=LoadImage(image);
            if(refreshInPlace && inlineText!=null){
                UpdateInlineBasePage(bitmap);
            }else{
                EditPageImage.Source=bitmap;
            }
            displayedPage=identity;
            if(!refreshInPlace){
                var size = SizeOfPage(); EditSurface.Width = size.W; EditSurface.Height = size.H;
                EditCanvas.Width = size.W; EditCanvas.Height = size.H;
                DrawElements(); ApplyZoom(); EditSurface.Visibility = Visibility.Visible; EditCanvas.IsEnabled = !saving;
            }
            ScheduleViewportRender();
            var pageLabel=$"{EditPages.SelectedIndex+1}/{plan.Pages.Count}쪽";
            EditStatus.Text = !textTask.IsCompletedSuccessfully ? $"{pageLabel} · 본문을 읽지 못했습니다. 페이지 보기와 주석은 사용할 수 있습니다."
                : textTask.Result.Length == 0 ? $"{pageLabel} · 선택 가능한 본문이 없습니다. 스캔·윤곽선·특수 PDF 구조는 OCR 또는 원본 수정이 필요합니다."
                : $"{pageLabel} · 본문 클릭 → 직접 입력 → Enter 적용";
        } catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == renderVersion){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;EditSurface.Visibility=Visibility.Visible;EditCanvas.IsEnabled=!saving;} }
    }
    BitmapImage LoadImage(string path)
    {
        if (images.TryGetValue(path, out var cached)) return cached;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze();
        while(images.Count>0&&(images.Count>=8||images.Values.Sum(x=>(long)x.PixelWidth*x.PixelHeight*4)+(long)image.PixelWidth*image.PixelHeight*4>96000000))images.Remove(images.Keys.First());
        images[path] = image;
        return image;
    }
    History Capture() => new(PdfEditService.Clone(plan), EditPages.SelectedIndex, selectedId, selectedIds.ToArray(),snapshot,info!);
    void Commit(History previous,bool keepSpread=false) { if(!keepSpread)EnterEditing(); undo.Push(previous); if(undo.Count>100){var recent=undo.Take(100).Reverse().ToArray();undo.Clear();foreach(var entry in recent)undo.Push(entry);} redo.Clear(); SetDirty(true); if(!keepSpread)DrawElements();CommandManager.InvalidateRequerySuggested(); }
    void DrawElements()
    {
        selectingText = true; EditCanvas.Children.Clear(); inlineText = null; selectingText = false; if (Page == null) { RefreshObjectProperties(); return; }
        DrawSourceTexts();
        selectedIds.IntersectWith(Page.Elements.Select(x=>x.Id));
        foreach (var element in Page.Elements) {
            if(selectedSource!=null && element.SourceTextId==selectedSource.Id)continue;
            if (DrawGraphic(element)) continue;
            var padding = element.Kind == "note" ? 7.0 : 1;
            FrameworkElement content = element.Kind == "stamp" ? new Image { Source = LoadImage(element.ImagePath!), Stretch = Stretch.Fill } :
                element.Kind=="replace" && element.PreserveSourceStyle ? new Border {Background=Brushes.Transparent} : new TextBlock { Text = element.Text, FontFamily = new FontFamily(element.FontFamily), TextAlignment = AlignmentOf(element), FontSize = element.FontSize, LineHeight = element.FontSize*1.35, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)new BrushConverter().ConvertFromString(element.Color)! };
            var border = new AnnotationBorder { Width = element.Width, Height = element.Height, Child = content, Padding = new Thickness(element.Kind == "stamp" ? 0 : padding), Background = element.Kind == "note" ? new SolidColorBrush(Color.FromRgb(255,243,175)) : TextBoxFillBrush(element), IsHitTestVisible=false, IsSelected = ShowSelectionDecorations && (element.Id == selectedId || selectedIds.Contains(element.Id)), Tag = element, RenderTransformOrigin = new Point(.5,.5), RenderTransform = new RotateTransform(element.Rotation) };
            border.MouseLeftButtonDown += ElementMouseDown;
            Canvas.SetLeft(border, element.X); Canvas.SetTop(border, element.Y); EditCanvas.Children.Add(border);
        }
        DrawResizeHandle();UpdateZoomAdorners();DrawSearchMark();RefreshObjectTools();RefreshObjectProperties();
        if(!IsViewing)TextProperties.Visibility=selectedSource!=null || Selected?.Kind is "text" or "note" or "replace" || EditTool.SelectedIndex is 0 or 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
    }
    void Clamp(PdfEditElement element)
    {
        var size = SizeOfPage(); var bounds=AnnotationBounds(element); var bw=bounds.Width; var bh=bounds.Height;
        if (bw > size.W+.000001 || bh > size.H+.000001) throw new InvalidOperationException("항목이 페이지보다 큽니다. 먼저 크기를 줄여 주세요.");
        bw=Math.Min(bw,size.W);bh=Math.Min(bh,size.H);
        var cx = Math.Clamp(element.X+element.Width/2, bw/2, size.W-bw/2);
        var cy = Math.Clamp(element.Y+element.Height/2, bh/2, size.H-bh/2);
        element.X = cx-element.Width/2; element.Y = cy-element.Height/2;
    }
    public void AddText(double x, double y, string text, bool note = false)
    {
        if (Page == null || info == null || saving) return;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 5000) { EditStatus.Text = "내용을 1~5000자로 입력하세요."; return; }
        var size = SizeOfPage(); var width = Math.Min(note ? 200 : 240, size.W-10);
        if (width < 30 || size.H < 35) { EditStatus.Text = "페이지가 너무 작아 항목을 추가할 수 없습니다."; return; }
        var element = new PdfEditElement { Kind = note ? "note" : "text", AutoWidth = !note, Text = text, X = x, Y = y, Width = width, FontSize = FontSizeValue, Color = ColorValue };
        SetObjectStyle(element); FitTextBox(element,text);
        Clamp(element); var before = Capture();selectedIds.Clear();selectedSource=null; Page.Elements.Add(element); selectedId = element.Id; Commit(before);
    }
    double TextHeight(PdfEditElement element)
    {
        if(element.PreserveSourceStyle)return element.FontSize*1.35*Math.Max(1,element.Text.Split('\n').Length)+2;
        var pad = element.Kind == "note" ? 7 : 1;
        var formatted = new FormattedText(element.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(element.FontFamily), element.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, element.Width-pad*2), LineHeight = element.FontSize*1.35 };
        return Math.Max(element.Kind == "note" ? 64 : 28, formatted.Height+pad*2+element.FontSize*.7);
    }
    void PageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!EditCanvas.IsEnabled || IsViewing || saving || Page == null) return;
        var point = e.GetPosition(EditCanvas);
        if(Tool=="선택"){BeginAreaSelection(point,Keyboard.Modifiers);e.Handled=true;return;}
        if (BeginGraphic(point)) { e.Handled = true; return; }
        if (Tool is "글자" or "메모") { AddText(point.X, point.Y, Tool=="메모"?"메모":"텍스트", Tool == "메모");EditTool.SelectedIndex=1; BeginAddedText();e.Handled=true;return; }
        else if (Tool == "도장") {
            PlaceStamp(point);
            e.Handled=true;
        } else { selectedIds.Clear(); selectedId = ""; selectedSource = null; DrawElements(); }
    }
    Image? stampPreview;

    Rect StampBounds(BitmapSource image,Point point)
    {
        var size=SizeOfPage();
        return StampBoundsForPage(image,point,size.W,size.H);
    }

    static Rect StampBoundsForPage(BitmapSource image,Point point,double pageWidth,double pageHeight)
    {
        var scale=Math.Min(Math.Min(120,pageWidth-10)/image.PixelWidth,(pageHeight-10)/image.PixelHeight);
        if(scale<=0)return Rect.Empty;
        var width=scale*image.PixelWidth;
        var height=scale*image.PixelHeight;
        return new Rect(Math.Clamp(point.X,0,Math.Max(0,pageWidth-width)),
            Math.Clamp(point.Y-height,0,Math.Max(0,pageHeight-height)),width,height);
    }

    void HideStampPreview()
    {
        HideSpreadStampPreview();
        if(stampPreview==null)return;
        EditCanvas.Children.Remove(stampPreview);
        stampPreview=null;
    }

    void StampPointerLeft(object sender,MouseEventArgs e)=>HideStampPreview();

    void ShowStampPreview(Point point)
    {
        if(stamp==null||Page==null||IsBusy||IsViewing||Tool!="도장"){HideStampPreview();return;}
        var image=LoadImage(stamp);
        var bounds=StampBounds(image,point);
        if(bounds.IsEmpty){HideStampPreview();return;}
        stampPreview??=new Image { IsHitTestVisible=false,Opacity=.65,Stretch=Stretch.Fill };
        stampPreview.Source=image;
        stampPreview.Width=bounds.Width;
        stampPreview.Height=bounds.Height;
        Canvas.SetLeft(stampPreview,bounds.X);
        Canvas.SetTop(stampPreview,bounds.Y);
        Panel.SetZIndex(stampPreview,150);
        if(!EditCanvas.Children.Contains(stampPreview))EditCanvas.Children.Add(stampPreview);
    }

    void PlaceStamp(Point point)
    {
        if(IsBusy||IsViewing||Page==null)return;
        if(stamp==null){ShowPageOperationError("도장 이미지를 먼저 선택하세요.");return;}
        try {
            var bounds=StampBounds(LoadImage(stamp),point);
            if(bounds.IsEmpty)return;
            var item=new PdfEditElement {
                Kind="stamp",ImagePath=stamp,X=bounds.X,Y=bounds.Y,Width=bounds.Width,Height=bounds.Height
            };
            HideStampPreview();
            var before=Capture();
            Page.Elements.Add(item);
            selectedIds.Clear();
            selectedIds.Add(item.Id);
            selectedSource=null;
            selectedId=item.Id;
            EditTool.SelectedIndex=1;
            Commit(before);
            Keyboard.Focus(EditCanvas);
        } catch(Exception ex){ShowPageOperationError(ex.Message);}
    }

    void ElementMouseDown(object sender, MouseButtonEventArgs e)
    {
        if(sender is Border {Tag:PdfEditElement {Kind:"note"} note} && Tool=="메모" && !saving){selectedId=note.Id;BeginAddedText();e.Handled=true;return;}
        if (Tool is not ("선택" or "본문") || sender is not AnnotationBorder { Tag: PdfEditElement item } || saving) return;
        Keyboard.Focus(EditCanvas);
        if ((Keyboard.Modifiers&(ModifierKeys.Control|ModifierKeys.Shift))!=0) {
            selectedSource=null;
            if(!string.IsNullOrEmpty(selectedId))selectedIds.Add(selectedId);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){if(!selectedIds.Add(item.Id))selectedIds.Remove(item.Id);}else selectedIds.Add(item.Id);
            selectedId = selectedIds.FirstOrDefault() ?? ""; SyncRibbonObjectProperties(Selected); DrawElements(); e.Handled=true; return;
        }
        if (!selectedIds.Contains(item.Id)) { selectedIds.Clear(); selectedIds.Add(item.Id); }
        groupOrigins = Page!.Elements.Where(x=>selectedIds.Contains(x.Id)).ToDictionary(x=>x.Id,x=>new Point(x.X,x.Y));
        if(e.ClickCount==2 && item.Kind=="replace" && item.SourceTextId!=null){SelectSourceText(item.SourceTextId);e.Handled=true;return;}
        if(e.ClickCount==2 && item.Kind is "text" or "note") {selectedId=item.Id;BeginAddedText();e.Handled=true;return;}
        SyncLineType(item);
        SyncRibbonObjectProperties(item);
        selectedSource = null; selectedId = item.Id; dragging = item; dragThresholdReached = false; dragBefore = Capture();
        dragStart = e.GetPosition(EditCanvas); dragX = item.X; dragY = item.Y;
        if (item.Kind != "stamp") {
            EditText.Text = item.Text;
            if(item.Kind is not ("text" or "note" or "replace"))
                SyncTextFormat(item.FontSize,item.Color,item.FontFamily,null);
        }
        EditCanvas.CaptureMouse(); RefreshSelectionDecorations(); e.Handled = true;
    }
    void PageMouseMove(object sender, MouseEventArgs e)
    {
        if(selectionBox!=null){UpdateAreaSelection(e.GetPosition(EditCanvas));e.Handled=true;return;}
        if(Tool=="도장"){ShowStampPreview(e.GetPosition(EditCanvas));return;}
        if (MoveGraphic(e)) return;
        if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        MoveDraggedObjects(e.GetPosition(EditCanvas));
    }
    void MoveDraggedObjects(Point point)
    {
        if(dragging==null)return;
        var delta=point-dragStart;
        if(!dragThresholdReached) {
            var scale=EditSurface.LayoutTransform as ScaleTransform;
            if(Math.Abs(delta.X)*(scale?.ScaleX??1)<SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y)*(scale?.ScaleY??1)<SystemParameters.MinimumVerticalDragDistance)return;
            dragThresholdReached=true;
        }
        var items=Objects();var size=SizeOfPage();
        if(items.Count==0)return;
        var originalBounds=items.Select(item=>{
            var bounds=AnnotationBounds(item);
            var origin=groupOrigins[item.Id];
            bounds.Offset(origin.X-item.X,origin.Y-item.Y);
            return bounds;
        }).ToArray();
        var minX=originalBounds.Min(x=>x.Left);var maxX=originalBounds.Max(x=>x.Right);
        var minY=originalBounds.Min(x=>x.Top);var maxY=originalBounds.Max(x=>x.Bottom);
        var dx=Math.Clamp(delta.X,-minX,Math.Max(-minX,size.W-maxX));var dy=Math.Clamp(delta.Y,-minY,Math.Max(-minY,size.H-maxY));
        foreach(var item in items){var origin=groupOrigins[item.Id];item.X=origin.X+dx;item.Y=origin.Y+dy;}
        DrawElements();
    }
    void PageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if(FinishAreaSelection()){e.Handled=true;return;}
        if(continuousLineStart!=null)return;
        if (EndGraphic()) return;
        FinishObjectDrag();
    }
    void FinishObjectDrag()
    {
        if(dragging==null)return;var item=dragging;var before=dragBefore;
        dragging=null;dragThresholdReached=false;dragBefore=null;EditCanvas.ReleaseMouseCapture();
        var moved=item.X!=dragX||item.Y!=dragY;
        if(moved && before!=null)Commit(before);
        if(!moved && item.Kind=="note" && Objects().Count==1){selectedId=item.Id;BeginAddedText();return;}
        DrawElements();
    }
    bool importingStamp;

    async void ChooseStamp(object sender, RoutedEventArgs e)
    {
        if(importingStamp||IsBusy||info==null)return;
        var dialog = new OpenFileDialog {
            Title = "도장 이미지 선택 (20MB 이하, 가로 × 세로 4,000만 픽셀 이하)",
            Filter = "도장 이미지 (*.png, *.jpg, *.jpeg)|*.png;*.jpg;*.jpeg"
        };
        HideStampPreview();
        if (dialog.ShowDialog(DialogOwner) != true) {
            if(stamp==null&&Tool=="도장")EditTool.SelectedIndex=1;
            return;
        }
        importingStamp=true;
        try {
            if (new FileInfo(dialog.FileName).Length > 20_000_000) throw new IOException("도장은 20MB 이하 이미지를 선택하세요.");
            var image=new BitmapImage();
            image.BeginInit();
            image.CacheOption=BitmapCacheOption.OnLoad;
            image.UriSource=new Uri(dialog.FileName);
            image.EndInit();
            image.Freeze();
            if((long)image.PixelWidth*image.PixelHeight>40_000_000)throw new IOException("도장 이미지의 크기 한도를 초과했습니다.");
            var settings=new StampImportWindow(image,DialogOwner);
            if(settings.ShowDialog()!=true) {
                if(stamp==null&&Tool=="도장")EditTool.SelectedIndex=1;
                return;
            }
            var candidate=Path.Combine(work,Guid.NewGuid().ToString("N")+(settings.RemoveBackground?".png":Path.GetExtension(dialog.FileName)));
            if(settings.RemoveBackground) {
                var background=settings.BackgroundColor;
                var tolerance=settings.Tolerance;
                var output=await Task.Run(()=>StampImportWindow.RemoveEdgeBackground(image,background,tolerance));
                if(lifetime.IsCancellationRequested)return;
                var encoder=new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(output));
                using var stream=File.Create(candidate);
                encoder.Save(stream);
                if(stream.Length>20_000_000)throw new IOException("변환된 도장 이미지가 20MB를 초과했습니다.");
            } else File.Copy(dialog.FileName,candidate,false);
            stamp=candidate;
            if(!CanEditSpread)await EnterFromViewingAsync();
            if(!IsViewing||CanEditSpread)EditTool.SelectedIndex=4;
            EditStatus.Text="도장을 넣을 위치를 클릭하세요.";
        } catch (Exception ex) { ShowPageOperationError(ex.Message); }
        finally { importingStamp=false; }
    }
    void ResizeElement(double factor)
    {
        if (Page == null || Objects().Count == 0) return;
        var before = Capture(); foreach (var item in Objects()) {
        if(item.Kind=="highlight")SetHighlightLength(item,item.X,item.Y,item.Width,item.Width*factor);
        else {item.Width=Math.Max(24,item.Width*factor);item.Height=Math.Max(20,item.Height*factor);}
        try { Clamp(item); } catch (Exception ex) { Restore(before); EditStatus.Text = ex.Message; return; } } Commit(before);
    }
    void ShrinkElement(object sender, RoutedEventArgs e) => ResizeElement(.8);
    void GrowElement(object sender, RoutedEventArgs e) => ResizeElement(1.25);
    void DeleteElement(object sender, RoutedEventArgs e)
    {
        if(saving||Page==null)return;
        var items=Objects();
        if(items.Count==0)return;
        var before=Capture();
        foreach(var item in items){
            if(item.Kind=="replace")item.Text="";
            else Page.Elements.Remove(item);
        }
        selectedIds.Clear();selectedId="";Commit(before);
        if(items.Any(x=>x.Kind=="replace"))_=RenderPageAsync();
    }
    public void RotateSelectedPage(int degrees)
    {
        if (IsBusy || Page == null || info == null || degrees % 90 != 0) return;
        ApplyInlineText();
        var turns = ((degrees/90)%4+4)%4; if (turns == 0) return;
        var before = Capture();
        for (var step = 0; step < turns; step++) {
            var size = SizeOfPage();
            foreach (var element in Page.Elements) {
                var cx = element.X+element.Width/2; var cy = element.Y+element.Height/2;
                element.X = size.H-cy-element.Width/2; element.Y = cx-element.Height/2;
                element.Rotation = (element.Rotation+90)%360;
            }
            Page.Rotation = (Page.Rotation+90)%360;
        }
        selectedSource = null; Commit(before); _ = RenderPageAsync();
    }
    void RotateLeft(object sender, RoutedEventArgs e) => RotateSelectedPage(-90);
    void RotateRight(object sender, RoutedEventArgs e) => RotateSelectedPage(90);
    void MovePage(int offset)
    {
        if(IsBusy||info==null)return;
        ApplyInlineText();
        var index = EditPages.SelectedIndex; var target = index+offset;
        if (index < 0 || target < 0 || target >= plan.Pages.Count) return;
        var before = Capture(); var page = plan.Pages[index]; plan.Pages.RemoveAt(index); plan.Pages.Insert(target, page); BindPages(target); Commit(before);
    }
    void MovePageUp(object sender, RoutedEventArgs e) => MovePage(-1);
    void MovePageDown(object sender, RoutedEventArgs e) => MovePage(1);
    void Restore(History history,bool renderEditor=true) {
        var current=Page;var target=history.Plan.Pages[history.Page];
        var sourceChanged=snapshot!=history.Snapshot;
        if(sourceChanged)UsePageSource(history.Snapshot,history.Info);
        var render=sourceChanged||current==null||current.SourceIndex!=target.SourceIndex||current.Rotation!=target.Rotation||System.Text.Json.JsonSerializer.Serialize(current.Elements.Where(x=>x.Kind=="replace"))!=System.Text.Json.JsonSerializer.Serialize(target.Elements.Where(x=>x.Kind=="replace"));
        selectedIds.Clear();selectedIds.UnionWith(history.Selection);selectedSource=null;plan=PdfEditService.Clone(history.Plan);selectedId=history.Selected;BindPages(history.Page);SyncRibbonObjectProperties(Selected);
        if(!renderEditor)return;
        if(!render || !sourceChanged && current?.SourceIndex==target.SourceIndex && current.Rotation==target.Rotation)
            DrawElements();
        if(render)_ = RenderPageAsync();
    }
    public void Undo() { if(IsBusy||info==null)return;if(IsSpreadViewing&&ViewColumns>1){if(CancelPendingEditGesture())return;if(spreadStroke!=null||spreadLine!=null)CancelSpreadStroke();else _=RestoreSpreadHistoryAsync(undo,redo);return;}StopContinuousLine();EnterEditing();if(FinishAreaSelection(true)){CommandManager.InvalidateRequerySuggested();return;}if(drawing!=null){DiscardGraphic();CommandManager.InvalidateRequerySuggested();return;}if(CancelPendingEditGesture()){CommandManager.InvalidateRequerySuggested();return;}if(undo.Count==0)return;redo.Push(Capture());Restore(undo.Pop());SetDirty(!IsClean());CommandManager.InvalidateRequerySuggested(); }
    public void Redo() { if(IsBusy||info==null||drawing!=null||dragging!=null||cancelEditGesture!=null||selectionBox!=null||spreadStroke!=null)return;if(IsSpreadViewing&&ViewColumns>1){_=RestoreSpreadHistoryAsync(redo,undo);return;}EnterEditing();if(redo.Count==0)return;undo.Push(Capture());Restore(redo.Pop());SetDirty(!IsClean());CommandManager.InvalidateRequerySuggested(); }
    async void EditorShortcuts(object sender, KeyEventArgs e)
    {
        if(e.Key==Key.F&&Keyboard.Modifiers==ModifierKeys.Control) {
            e.Handled=true;OpenPageSearch();return;
        }
        if(e.Key==Key.Escape&&PageSearchBar.Visibility==Visibility.Visible) {
            e.Handled=true;ClosePageSearch(this,new RoutedEventArgs());return;
        }
        if(await HandleZoomKeyAsync(e))return;
        if(e.Key==Key.Escape&&CanEditSpread&&CancelPendingEditGesture()){e.Handled=true;return;}
        if(e.Key==Key.Escape&&IsSpreadViewing&&ViewColumns>1&&(CurrentGraphicKind!=null||Tool=="도장")){
            CancelSpreadStroke();
            EditTool.SelectedIndex=1;
            e.Handled=true;
            return;
        }
        if(e.Key==Key.Enter&&spreadLine!=null&&!IsTextCommandTarget(e.OriginalSource)){CancelSpreadStroke();e.Handled=true;return;}
        if(e.Key==Key.Enter && continuousLineStart!=null && !IsTextCommandTarget(e.OriginalSource)){StopContinuousLine();e.Handled=true;return;}
        if(e.Key==Key.Escape && !IsViewing && !IsBusy && info!=null) {
            e.Handled=true;
            FinishAreaSelection(true);
            CancelPendingEditGesture();
            StopToolAndSelect();
            return;
        }
        if(!ObjectPropertiesPane.IsKeyboardFocusWithin && Keyboard.FocusedElement is not TextBox && !IsViewing){
            if(saving||changingView||Page==null)return;
            if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down){var step=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?10:1;NudgeSelection(e.Key==Key.Left?-step:e.Key==Key.Right?step:0,e.Key==Key.Up?-step:e.Key==Key.Down?step:0);e.Handled=true;return;}
            if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.D){CopyObjects();PasteObjects();e.Handled=true;}
        }
    }

    public async Task SaveCopyAsync(string destination,bool markClean=true)
    {
        if (info == null || saving) throw new InvalidOperationException("PDF를 준비한 뒤 저장하세요.");
        if (File.Exists(destination)) throw new IOException("기존 파일을 덮어쓰지 않습니다. 새 파일 이름을 선택하세요.");
        EndGraphic(); ApplyInlineText();
        saving = true; EnableEditing(false); EditStatus.Text = "수정본을 저장하고 있습니다…";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var result = await PdfEditService.ExportAsync(snapshot, work, PdfEditService.Clone(plan), timeout.Token);
            await PdfEditService.SaveNewAsync(result, destination, timeout.Token);
            ResultPath = destination; if(markClean)MarkClean(); EditStatus.Text = "수정본 저장 완료 · 원본 PDF는 보존했습니다.";
        } finally { saving = false; EnableEditing(true); }
    }
    public async Task SaveAndFinishAsync(string destination) {
        await SaveCopyAsync(destination); Saved?.Invoke(this,EventArgs.Empty);
    }
    async void SaveEditedPdf(object sender, RoutedEventArgs e)
    {
        if (info == null || saving) return;
        var dialog = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = suggestedName, OverwritePrompt = false };
        if (dialog.ShowDialog(DialogOwner) != true) return;
        try { if(editorOnly)await SaveCopyAsync(dialog.FileName,markClean:false);else await SaveAndFinishAsync(dialog.FileName); }
        catch (Exception ex) { ShowPageOperationError(ex.Message); }
    }
    void CheckClosing(object? sender, CancelEventArgs e)
    {
        ApplyInlineText();
        if (saving || changingView) { e.Cancel = true; EditStatus.Text = "저장이 끝난 뒤 창을 닫아 주세요."; return; }
        if (!dirty) return;
        var choice = MessageBox.Show(DialogOwner, editorOnly ? "수정 내용을 편집 파일로 저장할까요?" : "수정 내용을 새 PDF로 저장할까요?", "PDF 수정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.No) { SetDirty(false); return; }
        e.Cancel = true; if (choice == MessageBoxResult.Yes) { if(editorOnly)SaveProject(this,new RoutedEventArgs());else SaveEditedPdf(this,new RoutedEventArgs()); }
    }
}
