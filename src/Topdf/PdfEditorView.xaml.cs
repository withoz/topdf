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
    Border? dragVisual;
    History? dragBefore;
    sealed record History(PdfEditPlan Plan, int Page, string Selected, string[] Selection);

    public string? ResultPath { get; private set; }
    public Task Ready => ready.Task;
    public PdfEditPlan CurrentPlan => PdfEditService.Clone(plan);
    PdfEditPage? Page => EditPages.SelectedItem as PdfEditPage;
    PdfEditElement? Selected => Page?.Elements.FirstOrDefault(e => e.Id == selectedId);
    double FontSizeValue => double.Parse(((ComboBoxItem)EditFontSize.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
    string ColorValue => (EditColor.SelectedItem as ComboBoxItem)?.Tag as string ?? EditColor.SelectedIndex switch { 1 => "#C92127", 2 => "#1755B5", _ => "#111111" };
    string Tool => ((ComboBoxItem)EditTool.SelectedItem).Content.ToString()!;

    public PdfEditorView(string pdf, string? displayName = null)
    {
        InitializeComponent(); InitializeRibbon(); input = Path.GetFullPath(pdf);
        suggestedName = Path.GetFileNameWithoutExtension(displayName ?? input) + "_수정.pdf";

        EnableEditing(false);
        EditPages.PreviewMouseDown += (_,_) => ApplyInlineText();
        Loaded += async (_, _) => { if (initialized) return; initialized = true; await InitializeAsync(); };

    }
    bool initialized;
    public event EventHandler? Saved;
    Window DialogOwner => Window.GetWindow(this);
    public void DisposeEditor() { zoomMonitor.Stop();lifetime.Cancel(); renderCancellation?.Cancel(); }
    public bool CanClose() { var e = new CancelEventArgs(); CheckClosing(this,e); return !e.Cancel; }
    async Task InitializeAsync()
    {
        try {
            Directory.CreateDirectory(work);
            if(Path.GetExtension(input).Equals(".topdf",StringComparison.OrdinalIgnoreCase)){await LoadProjectAsync(input);EnableEditing(true);ready.TrySetResult();return;}
            snapshot = Path.Combine(work, "input.pdf");
            if (new FileInfo(input).Length > 200_000_000) throw new IOException("편집은 200MB 이하 PDF를 지원합니다.");
            await using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write)) await source.CopyToAsync(target, lifetime.Token);
            info = await PdfEditService.InspectAsync(snapshot, work, lifetime.Token);
            plan = new() { SourceSha256 = info.SourceSha256, Pages = info.Pages.Select(p => new PdfEditPage { SourceIndex = p.SourceIndex }).ToList() };
            MarkClean(); BindPages(0); EnableEditing(true);
            await RenderPageAsync(); ready.TrySetResult();
        } catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception ex) { EditStatus.Text = ex.Message; ready.TrySetException(ex); }
    }
    async void GoToPage(object sender, RoutedEventArgs e) {
        if (saving || info == null) return;
        ApplyInlineText();
        if (int.TryParse(EditPageNumber.Text,out var number) && number>=1 && number<=plan.Pages.Count) { EditPages.SelectedIndex=number-1;EditPages.ScrollIntoView(EditPages.SelectedItem); if(IsViewing)await ReturnToViewingAsync(true); }
        else EditStatus.Text = $"1~{plan.Pages.Count} 사이의 페이지 번호를 입력하세요.";
    }
    void PageNumberKey(object sender, KeyEventArgs e) { if(e.Key==Key.Enter) {GoToPage(sender,e);e.Handled=true;} }
    void EnableEditing(bool enabled) { EditorRibbon.IsEnabled=enabled;MoreOptions.IsEnabled=enabled;TextTools.IsEnabled=DrawingTools.IsEnabled=MarkupTools.IsEnabled=enabled; EditorTools.IsEnabled = enabled; EditorHistory.IsEnabled = enabled; EditorSave.IsEnabled = enabled; EditPages.IsEnabled = enabled; EditCanvas.IsEnabled = enabled; }
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
    async void EditorPageChanged(object sender, SelectionChangedEventArgs e) { if (!updating && info != null) { selectedId = ""; selectedSource = null; await RenderPageAsync(); } }
    async Task RenderPageAsync()
    {
        if (Page == null || info == null) return;
        var page = Page; var version = ++renderVersion;
        EditPageNumber.Text = (EditPages.SelectedIndex+1).ToString();
        renderCancellation?.Cancel(); renderCancellation?.Dispose();
        renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = renderCancellation.Token;
        EditSurface.Visibility = Visibility.Hidden; EditCanvas.IsEnabled = false;
        EditStatus.Text = "페이지를 불러오고 있습니다…";
        try {
            if (!sourceTexts.ContainsKey(page.SourceIndex)) {
                var texts=await PdfEditService.TextAsync(snapshot, work, page.SourceIndex, token);
                if(version!=renderVersion || token.IsCancellationRequested)return;
                sourceTexts[page.SourceIndex]=texts;
            }
            var replacements = System.Text.Json.JsonSerializer.Serialize(page.Elements.Where(e=>e.Kind=="replace"));
            var key = $"{page.SourceIndex}:{page.Rotation}:{replacements}";
            if (!renders.TryGetValue(key, out var image)) {
                image = await PdfEditService.RenderAsync(snapshot, work, page.SourceIndex, page.Rotation, token, plan);
                renders[key] = image;
            }
            if (version != renderVersion || token.IsCancellationRequested) return;
            var size = SizeOfPage(); EditSurface.Width = size.W; EditSurface.Height = size.H;
            EditCanvas.Width = size.W; EditCanvas.Height = size.H;
            EditPageImage.Source = LoadImage(image);
            DrawElements(); ApplyZoom(); EditSurface.Visibility = Visibility.Visible; EditCanvas.IsEnabled = !saving;
            EditStatus.Text = sourceTexts[page.SourceIndex].Length == 0 ? $"{EditPages.SelectedIndex+1}/{plan.Pages.Count}쪽 · 선택 가능한 본문이 없습니다. 스캔·윤곽선·특수 PDF 구조는 OCR 또는 원본 수정이 필요합니다." : $"{EditPages.SelectedIndex+1}/{plan.Pages.Count}쪽 · 본문 클릭 → 직접 입력 → Enter 적용";
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
    History Capture() => new(PdfEditService.Clone(plan), EditPages.SelectedIndex, selectedId, selectedIds.ToArray());
    void Commit(History previous) { EnterEditing(); undo.Push(previous); if(undo.Count>100){var recent=undo.Take(100).Reverse().ToArray();undo.Clear();foreach(var entry in recent)undo.Push(entry);} redo.Clear(); dirty = true; DrawElements(); }
    void DrawElements()
    {
        selectingText = true; EditCanvas.Children.Clear(); inlineText = null; selectingText = false; if (Page == null) return;
        DrawSourceTexts();
        selectedIds.IntersectWith(Page.Elements.Select(x=>x.Id));
        foreach (var element in Page.Elements) {
            if(selectedSource!=null && element.SourceTextId==selectedSource.Id)continue;
            if (DrawGraphic(element)) continue;
            var padding = element.Kind == "note" ? 7.0 : 1;
            FrameworkElement content = element.Kind == "stamp" ? new Image { Source = LoadImage(element.ImagePath!), Stretch = Stretch.Fill } :
                element.Kind=="replace" && element.PreserveSourceStyle ? new Border {Background=Brushes.Transparent} : new TextBlock { Text = element.Text, FontFamily = new FontFamily(element.FontFamily), TextAlignment = AlignmentOf(element), FontSize = element.FontSize, LineHeight = element.FontSize*1.35, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)new BrushConverter().ConvertFromString(element.Color)! };
            var border = new Border { Width = element.Width, Height = element.Height, Child = content, Padding = new Thickness(element.Kind == "stamp" ? 0 : padding), Background = element.Kind == "note" ? new SolidColorBrush(Color.FromRgb(255,243,175)) : Brushes.Transparent, BorderThickness = new Thickness((element.Id == selectedId || selectedIds.Contains(element.Id)) ? 1 : 0), BorderBrush = Brushes.DodgerBlue, Tag = element, RenderTransformOrigin = new Point(.5,.5), RenderTransform = new RotateTransform(element.Rotation) };
            border.MouseLeftButtonDown += ElementMouseDown;
            Canvas.SetLeft(border, element.X); Canvas.SetTop(border, element.Y); EditCanvas.Children.Add(border);
        }
        DrawResizeHandle();UpdateZoomAdorners();
        if(!IsViewing)TextProperties.Visibility=selectedSource!=null || Selected?.Kind is "text" or "note" or "replace" || EditTool.SelectedIndex is 0 or 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
    }
    void Clamp(PdfEditElement element)
    {
        var size = SizeOfPage(); var bw = element.Rotation is 90 or 270 ? element.Height : element.Width; var bh = element.Rotation is 90 or 270 ? element.Width : element.Height;
        if (bw > size.W || bh > size.H) throw new InvalidOperationException("항목이 페이지보다 큽니다. 먼저 크기를 줄여 주세요.");
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
        var element = new PdfEditElement { Kind = note ? "note" : "text", Text = text, X = x, Y = y, Width = width, FontSize = FontSizeValue, Color = ColorValue };
        SetObjectStyle(element); element.Height = Math.Min(size.H-6, TextHeight(element));
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
        if (!EditCanvas.IsEnabled || saving || Page == null) return;
        var point = e.GetPosition(EditCanvas);
        if(Tool=="선택"){BeginAreaSelection(point,Keyboard.Modifiers);e.Handled=true;return;}
        if (BeginGraphic(point)) { e.Handled = true; return; }
        if (Tool is "글자" or "메모") { AddText(point.X, point.Y, Tool=="메모"?"메모":"텍스트", Tool == "메모");EditTool.SelectedIndex=1; BeginAddedText();e.Handled=true;return; }
        else if (Tool == "도장") {
            if (stamp == null) { EditStatus.Text = "도장 이미지를 먼저 선택하세요."; return; }
            var image = LoadImage(stamp); var size = SizeOfPage();
            var width = Math.Min(120, size.W-10); var height = width*image.PixelHeight/image.PixelWidth;
            if (height > size.H-10) { height = size.H-10; width = height*image.PixelWidth/image.PixelHeight; }
            var item = new PdfEditElement { Kind = "stamp", ImagePath = stamp, X = point.X, Y = point.Y, Width = width, Height = height };
            if (Math.Min(width, height) <= 0) return;
            Clamp(item); var before = Capture(); Page.Elements.Add(item); selectedId = item.Id; Commit(before);
        } else { selectedIds.Clear(); selectedId = ""; selectedSource = null; DrawElements(); }
    }
    void ElementMouseDown(object sender, MouseButtonEventArgs e)
    {
        if(sender is Border {Tag:PdfEditElement {Kind:"note"} note} && Tool=="메모" && !saving){selectedId=note.Id;BeginAddedText();e.Handled=true;return;}
        if (Tool is not ("선택" or "본문") || sender is not Border { Tag: PdfEditElement item } border || saving) return;
        Keyboard.Focus(EditCanvas);
        if ((Keyboard.Modifiers&(ModifierKeys.Control|ModifierKeys.Shift))!=0) {
            selectedSource=null;
            if(!string.IsNullOrEmpty(selectedId))selectedIds.Add(selectedId);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){if(!selectedIds.Add(item.Id))selectedIds.Remove(item.Id);}else selectedIds.Add(item.Id);
            selectedId = selectedIds.FirstOrDefault() ?? ""; DrawElements(); e.Handled=true; return;
        }
        if (!selectedIds.Contains(item.Id)) { selectedIds.Clear(); selectedIds.Add(item.Id); }
        groupOrigins = Page!.Elements.Where(x=>selectedIds.Contains(x.Id)).ToDictionary(x=>x.Id,x=>new Point(x.X,x.Y));
        if(e.ClickCount==2 && item.Kind=="replace" && item.SourceTextId!=null){SelectSourceText(item.SourceTextId);e.Handled=true;return;}
        if(e.ClickCount==2 && item.Kind is "text" or "note") {selectedId=item.Id;BeginAddedText();e.Handled=true;return;}
        selectedSource = null; selectedId = item.Id; dragging = item; dragVisual = border; dragBefore = Capture();
        dragStart = e.GetPosition(EditCanvas); dragX = item.X; dragY = item.Y;
        if (item.Kind != "stamp") {
            EditText.Text = item.Text;
            SelectFont(item.FontSize,item.Color);SelectOriginalFont(item.FontFamily);
        }
        EditCanvas.CaptureMouse(); border.BorderThickness = new Thickness(1); e.Handled = true;
    }
    void PageMouseMove(object sender, MouseEventArgs e)
    {
        if(selectionBox!=null){UpdateAreaSelection(e.GetPosition(EditCanvas));e.Handled=true;return;}
        if (MoveGraphic(e)) return;
        if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(EditCanvas);var items=Objects();var size=SizeOfPage();
        var minX=items.Min(x=>groupOrigins[x.Id].X+x.Width/2-(x.Rotation is 90 or 270?x.Height:x.Width)/2);
        var maxX=items.Max(x=>groupOrigins[x.Id].X+x.Width/2+(x.Rotation is 90 or 270?x.Height:x.Width)/2);
        var minY=items.Min(x=>groupOrigins[x.Id].Y+x.Height/2-(x.Rotation is 90 or 270?x.Width:x.Height)/2);
        var maxY=items.Max(x=>groupOrigins[x.Id].Y+x.Height/2+(x.Rotation is 90 or 270?x.Width:x.Height)/2);
        var dx=Math.Clamp(point.X-dragStart.X,-minX,size.W-maxX);var dy=Math.Clamp(point.Y-dragStart.Y,-minY,size.H-maxY);
        foreach(var item in items){var origin=groupOrigins[item.Id];item.X=origin.X+dx;item.Y=origin.Y+dy;}
        DrawElements();
    }
    void PageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if(FinishAreaSelection()){e.Handled=true;return;}
        if (EndGraphic()) return;
        FinishObjectDrag();
    }
    void FinishObjectDrag()
    {
        if(dragging==null)return;var item=dragging;var before=dragBefore;
        dragging=null;dragVisual=null;dragBefore=null;EditCanvas.ReleaseMouseCapture();
        var moved=Math.Abs(item.X-dragX)+Math.Abs(item.Y-dragY)>.1;
        if(moved && before!=null)Commit(before);
        if(!moved && item.Kind=="note" && Objects().Count==1){selectedId=item.Id;BeginAddedText();return;}
        DrawElements();
    }
    void ApplySelectedText(object sender, RoutedEventArgs e)
    {
        if (selectedSource != null) { ReplaceSourceText(selectedSource.Id, EditText.Text); return; }
        if (Selected is not { Kind: "text" or "note" or "replace" } item || (Selected.Kind != "replace" && string.IsNullOrWhiteSpace(EditText.Text))) return;
        var before = Capture(); item.Text = EditText.Text; item.FontSize = FontSizeValue; item.Color = ColorValue; var family=((ComboBoxItem)TextFont.SelectedItem).Content.ToString()!;if(family!=item.FontFamily)item.PreserveSourceStyle=false;item.FontFamily=family;item.Alignment=new[]{"left","center","right"}[TextAlign.SelectedIndex]; item.Height = Math.Min(SizeOfPage().H-6, TextHeight(item));
        try { KeepVisibleInlineDraft();Clamp(item); Commit(before);if(item.Kind=="replace")_ = RenderPageAsync(); } catch (Exception ex) { Restore(before); EditStatus.Text = ex.Message; }
    }
    void ChooseStamp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "도장 이미지|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(DialogOwner) != true) return;
        try {
            if (new FileInfo(dialog.FileName).Length > 20_000_000) throw new IOException("도장은 20MB 이하 이미지를 선택하세요.");
            stamp = Path.Combine(work, Guid.NewGuid().ToString("N")+Path.GetExtension(dialog.FileName));
            File.Copy(dialog.FileName, stamp, false); _ = LoadImage(stamp); EditTool.SelectedIndex = 4; EditStatus.Text = "도장을 넣을 위치를 클릭하세요.";
        } catch (Exception ex) { stamp = null; EditStatus.Text = ex.Message; }
    }
    void ResizeElement(double factor)
    {
        if (Page == null || Objects().Count == 0) return;
        var before = Capture(); foreach (var item in Objects()) { item.Width = Math.Max(24, item.Width*factor); item.Height = Math.Max(20, item.Height*factor);
        try { Clamp(item); } catch (Exception ex) { Restore(before); EditStatus.Text = ex.Message; return; } } Commit(before);
    }
    void ShrinkElement(object sender, RoutedEventArgs e) => ResizeElement(.8);
    void GrowElement(object sender, RoutedEventArgs e) => ResizeElement(1.25);
    void DeleteElement(object sender, RoutedEventArgs e) { if(saving||Page==null)return;var items=Objects(); if(items.Count==0)return;var before=Capture();foreach(var item in items)Page!.Elements.Remove(item);selectedIds.Clear();selectedId="";Commit(before);if(items.Any(x=>x.Kind=="replace")) _=RenderPageAsync(); }
    public void RotateSelectedPage(int degrees)
    {
        if (Page == null || info == null || degrees % 90 != 0) return;
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
    void DeletePage(object sender, RoutedEventArgs e)
    {
        if (Page == null) return;
        if (plan.Pages.Count == 1) { EditStatus.Text = "최소 한 페이지는 남겨야 합니다."; return; }
        var before = Capture(); var index = EditPages.SelectedIndex; plan.Pages.RemoveAt(index); BindPages(index); selectedId = ""; Commit(before); _ = RenderPageAsync();
    }
    void MovePage(int offset)
    {
        var index = EditPages.SelectedIndex; var target = index+offset;
        if (index < 0 || target < 0 || target >= plan.Pages.Count) return;
        var before = Capture(); var page = plan.Pages[index]; plan.Pages.RemoveAt(index); plan.Pages.Insert(target, page); BindPages(target); Commit(before);
    }
    void MovePageUp(object sender, RoutedEventArgs e) => MovePage(-1);
    void MovePageDown(object sender, RoutedEventArgs e) => MovePage(1);
    void Restore(History history) {
        var current=Page;var target=history.Plan.Pages[history.Page];
        var render=current==null||current.SourceIndex!=target.SourceIndex||current.Rotation!=target.Rotation||System.Text.Json.JsonSerializer.Serialize(current.Elements.Where(x=>x.Kind=="replace"))!=System.Text.Json.JsonSerializer.Serialize(target.Elements.Where(x=>x.Kind=="replace"));
        selectedIds.Clear();selectedIds.UnionWith(history.Selection);selectedSource=null;plan=PdfEditService.Clone(history.Plan);selectedId=history.Selected;BindPages(history.Page);
        if(render)_ = RenderPageAsync();else DrawElements();
    }
    public void Undo() { EnterEditing(); EndGraphic(); FinishObjectDrag(); if (undo.Count == 0) return; redo.Push(Capture()); Restore(undo.Pop()); dirty = !IsClean(); }
    public void Redo() { EnterEditing(); if (redo.Count == 0) return; undo.Push(Capture()); Restore(redo.Pop()); dirty = !IsClean(); }
    void UndoEdit(object sender, RoutedEventArgs e) => Undo();
    void RedoEdit(object sender, RoutedEventArgs e) => Redo();
    async void EditorShortcuts(object sender, KeyEventArgs e)
    {
        if(await HandleZoomKeyAsync(e))return;
        if(e.Key==Key.Escape && FinishAreaSelection(true)){e.Handled=true;return;}
        if(e.Key==Key.Escape && !IsViewing && EditTool.SelectedIndex>=5){e.Handled=true;StopDrawingAndSelect();return;}
        if(e.Key==Key.Escape && NativeViewingHost.Content!=null){e.Handled=true;await ReturnToViewingAsync();return;}
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveEditedPdf(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not TextBox && !IsViewing) {
            if(saving||changingView||Page==null)return;
            if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down && !IsViewing){var step=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?10:1;NudgeSelection(e.Key==Key.Left?-step:e.Key==Key.Right?step:0,e.Key==Key.Up?-step:e.Key==Key.Down?step:0);e.Handled=true;return;}
            if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.D){CopyObjects();PasteObjects();e.Handled=true;return;}
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { selectedIds.UnionWith(Page!.Elements.Select(x=>x.Id)); DrawElements(); e.Handled=true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { CopyObjects(); e.Handled=true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V) { PasteObjects(); e.Handled=true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteElement(sender, e); e.Handled = true; }
        }
    }
    public async Task SaveCopyAsync(string destination)
    {
        if (info == null || saving) throw new InvalidOperationException("PDF를 준비한 뒤 저장하세요.");
        if (File.Exists(destination)) throw new IOException("기존 파일을 덮어쓰지 않습니다. 새 파일 이름을 선택하세요.");
        EndGraphic(); ApplyInlineText();
        saving = true; EnableEditing(false); EditStatus.Text = "수정본을 저장하고 있습니다…";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var result = await PdfEditService.ExportAsync(snapshot, work, PdfEditService.Clone(plan), timeout.Token);
            await PdfEditService.SaveNewAsync(result, destination, timeout.Token);
            ResultPath = destination; MarkClean(); EditStatus.Text = "수정본 저장 완료 · 원본 PDF는 보존했습니다.";
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
        try { await SaveAndFinishAsync(dialog.FileName); }
        catch (Exception ex) { EditStatus.Text = ex.Message; }
    }
    void CheckClosing(object? sender, CancelEventArgs e)
    {
        ApplyInlineText();
        if (saving || changingView) { e.Cancel = true; EditStatus.Text = "저장이 끝난 뒤 창을 닫아 주세요."; return; }
        if (!dirty) return;
        var choice = MessageBox.Show(DialogOwner, "수정 내용을 새 PDF로 저장할까요?", "PDF 수정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.No) { dirty = false; return; }
        e.Cancel = true; if (choice == MessageBoxResult.Yes) SaveEditedPdf(this, new RoutedEventArgs());
    }
}
