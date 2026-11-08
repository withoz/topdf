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

public partial class PdfEditorWindow : Window
{
    readonly string input;
    readonly string suggestedName;
    readonly string work = Path.Combine(AppPaths.Cache, "pdf-edit", Guid.NewGuid().ToString("N"));
    readonly CancellationTokenSource lifetime = new();
    readonly TaskCompletionSource ready = new();
    readonly Stack<History> undo = new(), redo = new();
    readonly Dictionary<string, BitmapImage> images = new();
    readonly Dictionary<string, string> renders = new();
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
    sealed record History(PdfEditPlan Plan, int Page, string Selected);

    public string? ResultPath { get; private set; }
    public Task Ready => ready.Task;
    public PdfEditPlan CurrentPlan => PdfEditService.Clone(plan);
    PdfEditPage? Page => EditPages.SelectedItem as PdfEditPage;
    PdfEditElement? Selected => Page?.Elements.FirstOrDefault(e => e.Id == selectedId);
    double FontSizeValue => double.Parse(((ComboBoxItem)EditFontSize.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture);
    string ColorValue => EditColor.SelectedIndex switch { 1 => "#C92127", 2 => "#1755B5", _ => "#111111" };
    string Tool => ((ComboBoxItem)EditTool.SelectedItem).Content.ToString()!;

    public PdfEditorWindow(string pdf, string? displayName = null)
    {
        InitializeComponent(); input = Path.GetFullPath(pdf);
        suggestedName = Path.GetFileNameWithoutExtension(displayName ?? input) + "_수정.pdf";
        Title = "TOPDF · PDF 수정 · " + Path.GetFileName(displayName ?? input);
        EnableEditing(false);
        Loaded += async (_, _) => await InitializeAsync();
        Closing += CheckClosing;
        Closed += (_, _) => { lifetime.Cancel(); renderCancellation?.Cancel(); };
    }
    async Task InitializeAsync()
    {
        try {
            Directory.CreateDirectory(work); snapshot = Path.Combine(work, "input.pdf");
            if (new FileInfo(input).Length > 200_000_000) throw new IOException("편집은 200MB 이하 PDF를 지원합니다.");
            await using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write)) await source.CopyToAsync(target, lifetime.Token);
            info = await PdfEditService.InspectAsync(snapshot, work, lifetime.Token);
            plan = new() { SourceSha256 = info.SourceSha256, Pages = info.Pages.Select(p => new PdfEditPage { SourceIndex = p.SourceIndex }).ToList() };
            BindPages(0); EnableEditing(true);
            await RenderPageAsync(); ready.TrySetResult();
        } catch (OperationCanceledException) { ready.TrySetCanceled(); }
        catch (Exception ex) { EditStatus.Text = ex.Message; ready.TrySetException(ex); }
    }
    void EnableEditing(bool enabled) { EditorTools.IsEnabled = enabled; EditorHistory.IsEnabled = enabled; EditorSave.IsEnabled = enabled; EditPages.IsEnabled = enabled; EditCanvas.IsEnabled = enabled; }
    (double W, double H) SizeOfPage()
    {
        var data = info!.Pages[Page!.SourceIndex];
        return Page.Rotation is 90 or 270 ? (data.Height, data.Width) : (data.Width, data.Height);
    }
    void BindPages(int index)
    {
        updating = true; EditPages.ItemsSource = null; EditPages.ItemsSource = plan.Pages;
        EditPages.SelectedIndex = Math.Clamp(index, 0, plan.Pages.Count-1); updating = false;
    }
    async void EditorPageChanged(object sender, SelectionChangedEventArgs e) { if (!updating && info != null) { selectedId = ""; await RenderPageAsync(); } }
    async Task RenderPageAsync()
    {
        if (Page == null || info == null) return;
        var page = Page; var version = ++renderVersion;
        renderCancellation?.Cancel(); renderCancellation?.Dispose();
        renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = renderCancellation.Token;
        EditSurface.Visibility = Visibility.Hidden; EditCanvas.IsEnabled = false;
        EditStatus.Text = "페이지를 불러오고 있습니다…";
        try {
            var key = $"{page.SourceIndex}:{page.Rotation}";
            if (!renders.TryGetValue(key, out var image)) {
                image = await PdfEditService.RenderAsync(snapshot, work, page.SourceIndex, page.Rotation, token);
                renders[key] = image;
            }
            if (version != renderVersion || token.IsCancellationRequested) return;
            var size = SizeOfPage(); EditSurface.Width = size.W; EditSurface.Height = size.H;
            EditCanvas.Width = size.W; EditCanvas.Height = size.H;
            EditPageImage.Source = LoadImage(image);
            DrawElements(); ApplyZoom(); EditSurface.Visibility = Visibility.Visible; EditCanvas.IsEnabled = !saving;
            EditStatus.Text = $"{EditPages.SelectedIndex+1}/{plan.Pages.Count}쪽 · 클릭해 추가, '선택' 도구로 끌어 이동";
        } catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == renderVersion) EditStatus.Text = ex.Message; }
    }
    BitmapImage LoadImage(string path)
    {
        if (images.TryGetValue(path, out var cached)) return cached;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path)); image.DecodePixelWidth = 1800; image.EndInit(); image.Freeze();
        if (images.Count >= 12) images.Remove(images.Keys.First());
        images[path] = image;
        return image;
    }
    History Capture() => new(PdfEditService.Clone(plan), EditPages.SelectedIndex, selectedId);
    void Commit(History previous) { undo.Push(previous); redo.Clear(); dirty = true; DrawElements(); }
    void DrawElements()
    {
        EditCanvas.Children.Clear(); if (Page == null) return;
        foreach (var element in Page.Elements) {
            var padding = element.Kind == "note" ? 7.0 : 1;
            FrameworkElement content = element.Kind == "stamp" ? new Image { Source = LoadImage(element.ImagePath!), Stretch = Stretch.Fill } :
                new TextBlock { Text = element.Text, FontFamily = new FontFamily("Malgun Gothic"), FontSize = element.FontSize, LineHeight = element.FontSize*1.35, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)new BrushConverter().ConvertFromString(element.Color)! };
            var border = new Border { Width = element.Width, Height = element.Height, Child = content, Padding = new Thickness(element.Kind == "stamp" ? 0 : padding), Background = element.Kind == "note" ? new SolidColorBrush(Color.FromRgb(255,243,175)) : Brushes.Transparent, BorderThickness = new Thickness(element.Id == selectedId ? 1 : 0), BorderBrush = Brushes.DodgerBlue, Tag = element, RenderTransformOrigin = new Point(.5,.5), RenderTransform = new RotateTransform(element.Rotation) };
            border.MouseLeftButtonDown += ElementMouseDown;
            Canvas.SetLeft(border, element.X); Canvas.SetTop(border, element.Y); EditCanvas.Children.Add(border);
        }
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
        element.Height = Math.Min(size.H-6, TextHeight(element));
        Clamp(element); var before = Capture(); Page.Elements.Add(element); selectedId = element.Id; Commit(before);
    }
    double TextHeight(PdfEditElement element)
    {
        var pad = element.Kind == "note" ? 7 : 1;
        var formatted = new FormattedText(element.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Malgun Gothic"), element.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, element.Width-pad*2), LineHeight = element.FontSize*1.35 };
        return Math.Max(element.Kind == "note" ? 64 : 28, formatted.Height+pad*2+element.FontSize*.7);
    }
    void PageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!EditCanvas.IsEnabled || saving || Page == null) return;
        var point = e.GetPosition(EditCanvas);
        if (Tool is "글자" or "메모") AddText(point.X, point.Y, EditText.Text, Tool == "메모");
        else if (Tool == "도장") {
            if (stamp == null) { EditStatus.Text = "도장 이미지를 먼저 선택하세요."; return; }
            var image = LoadImage(stamp); var size = SizeOfPage();
            var width = Math.Min(120, size.W-10); var height = width*image.PixelHeight/image.PixelWidth;
            if (height > size.H-10) { height = size.H-10; width = height*image.PixelWidth/image.PixelHeight; }
            var item = new PdfEditElement { Kind = "stamp", ImagePath = stamp, X = point.X, Y = point.Y, Width = width, Height = height };
            if (Math.Min(width, height) <= 0) return;
            Clamp(item); var before = Capture(); Page.Elements.Add(item); selectedId = item.Id; Commit(before);
        } else { selectedId = ""; DrawElements(); }
    }
    void ElementMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Tool != "선택" || sender is not Border { Tag: PdfEditElement item } border || saving) return;
        selectedId = item.Id; dragging = item; dragVisual = border; dragBefore = Capture();
        dragStart = e.GetPosition(EditCanvas); dragX = item.X; dragY = item.Y;
        if (item.Kind != "stamp") {
            EditText.Text = item.Text;
            EditFontSize.SelectedItem = EditFontSize.Items.Cast<ComboBoxItem>().FirstOrDefault(c => c.Content.ToString() == item.FontSize.ToString(CultureInfo.InvariantCulture)) ?? EditFontSize.Items[1];
            EditColor.SelectedIndex = item.Color == "#C92127" ? 1 : item.Color == "#1755B5" ? 2 : 0;
        }
        EditCanvas.CaptureMouse(); border.BorderThickness = new Thickness(1); e.Handled = true;
    }
    void PageMouseMove(object sender, MouseEventArgs e)
    {
        if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(EditCanvas); dragging.X = dragX+point.X-dragStart.X; dragging.Y = dragY+point.Y-dragStart.Y; Clamp(dragging);
        Canvas.SetLeft(dragVisual!, dragging.X); Canvas.SetTop(dragVisual!, dragging.Y);
    }
    void PageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (dragging == null) return;
        EditCanvas.ReleaseMouseCapture();
        if (Math.Abs(dragging.X-dragX)+Math.Abs(dragging.Y-dragY) > .1 && dragBefore != null) Commit(dragBefore);
        dragging = null; dragVisual = null; dragBefore = null; DrawElements();
    }
    void ApplySelectedText(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Kind: not "stamp" } item || string.IsNullOrWhiteSpace(EditText.Text)) return;
        var before = Capture(); item.Text = EditText.Text; item.FontSize = FontSizeValue; item.Color = ColorValue; item.Height = Math.Min(SizeOfPage().H-6, TextHeight(item));
        try { Clamp(item); Commit(before); } catch (Exception ex) { Restore(before); EditStatus.Text = ex.Message; }
    }
    void ChooseStamp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "도장 이미지|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != true) return;
        try {
            if (new FileInfo(dialog.FileName).Length > 20_000_000) throw new IOException("도장은 20MB 이하 이미지를 선택하세요.");
            stamp = Path.Combine(work, Guid.NewGuid().ToString("N")+Path.GetExtension(dialog.FileName));
            File.Copy(dialog.FileName, stamp, false); _ = LoadImage(stamp); EditTool.SelectedIndex = 3; EditStatus.Text = "도장을 넣을 위치를 클릭하세요.";
        } catch (Exception ex) { stamp = null; EditStatus.Text = ex.Message; }
    }
    void ResizeElement(double factor)
    {
        if (Selected is not { } item) return;
        var before = Capture(); item.Width = Math.Max(24, item.Width*factor); item.Height = Math.Max(20, item.Height*factor);
        try { Clamp(item); Commit(before); } catch (Exception ex) { Restore(before); EditStatus.Text = ex.Message; }
    }
    void ShrinkElement(object sender, RoutedEventArgs e) => ResizeElement(.8);
    void GrowElement(object sender, RoutedEventArgs e) => ResizeElement(1.25);
    void DeleteElement(object sender, RoutedEventArgs e) { if (Selected is not { } item) return; var before = Capture(); Page!.Elements.Remove(item); selectedId = ""; Commit(before); }
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
        Commit(before); _ = RenderPageAsync();
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
    void Restore(History history) { plan = PdfEditService.Clone(history.Plan); selectedId = history.Selected; BindPages(history.Page); _ = RenderPageAsync(); }
    public void Undo() { if (undo.Count == 0) return; redo.Push(Capture()); Restore(undo.Pop()); dirty = undo.Count > 0; }
    public void Redo() { if (redo.Count == 0) return; undo.Push(Capture()); Restore(redo.Pop()); dirty = true; }
    void UndoEdit(object sender, RoutedEventArgs e) => Undo();
    void RedoEdit(object sender, RoutedEventArgs e) => Redo();
    void ZoomChanged(object sender, SelectionChangedEventArgs e) { if (EditSurface != null) ApplyZoom(); }
    void ApplyZoom() { var text = ((ComboBoxItem)EditZoom.SelectedItem).Content.ToString()!.TrimEnd('%'); var zoom = double.Parse(text, CultureInfo.InvariantCulture)/100; EditSurface.LayoutTransform = new ScaleTransform(zoom,zoom); }
    void EditorShortcuts(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveEditedPdf(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not TextBox) {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
            else if (e.Key == Key.Delete) { DeleteElement(sender, e); e.Handled = true; }
        }
    }
    public async Task SaveCopyAsync(string destination)
    {
        if (info == null || saving) throw new InvalidOperationException("PDF를 준비한 뒤 저장하세요.");
        if (File.Exists(destination)) throw new IOException("기존 파일을 덮어쓰지 않습니다. 새 파일 이름을 선택하세요.");
        saving = true; EnableEditing(false); EditStatus.Text = "수정본을 저장하고 있습니다…";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var result = await PdfEditService.ExportAsync(snapshot, work, PdfEditService.Clone(plan), timeout.Token);
            await PdfEditService.SaveNewAsync(result, destination, timeout.Token);
            ResultPath = destination; dirty = false; EditStatus.Text = "수정본 저장 완료 · 원본 PDF는 보존했습니다.";
        } finally { saving = false; EnableEditing(true); }
    }
    async void SaveEditedPdf(object sender, RoutedEventArgs e)
    {
        if (info == null || saving) return;
        var dialog = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = suggestedName, OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        try { await SaveCopyAsync(dialog.FileName); Close(); }
        catch (Exception ex) { EditStatus.Text = ex.Message; }
    }
    void CheckClosing(object? sender, CancelEventArgs e)
    {
        if (saving) { e.Cancel = true; EditStatus.Text = "저장이 끝난 뒤 창을 닫아 주세요."; return; }
        if (!dirty) return;
        var choice = MessageBox.Show(this, "수정 내용을 새 PDF로 저장할까요?", "PDF 수정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.No) { dirty = false; return; }
        e.Cancel = true; if (choice == MessageBoxResult.Yes) SaveEditedPdf(this, new RoutedEventArgs());
    }
}
