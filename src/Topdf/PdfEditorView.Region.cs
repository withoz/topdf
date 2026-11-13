using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Topdf;

public partial class PdfEditorView
{
    void DrawNoteRegions(Canvas canvas, PdfEditPage page)
    {
        foreach (var other in plan.Pages)
            foreach (var note in other.Elements)
                AddNoteRegionVisual(canvas, note, page.SourceIndex, ReferenceEquals(other, page));
    }

    static void AddNoteRegionVisual(Canvas canvas, PdfEditElement note, int sourceIndex, bool local)
    {
        if (note.Kind != "note" || note.NoteRegion is not { } region || region.SourceIndex != sourceIndex ||
            !double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.Width <= 0 || region.Height <= 0) return;
        var marker = new System.Windows.Shapes.Path {
            Tag = local ? note : null, Stroke = Brushes.DarkOrange, StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false
        };
        canvas.Children.Add(marker);
        if (local) UpdateNoteRegionVisual(canvas, note);
        else marker.Data = new RectangleGeometry(new Rect(region.X, region.Y, region.Width, region.Height));
    }

    static void UpdateNoteRegionVisual(Canvas canvas, PdfEditElement note, bool outlineOnly = false)
    {
        if (note.NoteRegion is not { } region) return;
        var marker = canvas.Children.OfType<System.Windows.Shapes.Path>()
            .LastOrDefault(path => ReferenceEquals(path.Tag, note));
        if (marker == null) return;
        var bounds = new Rect(region.X, region.Y, region.Width, region.Height);
        var geometry = new GeometryGroup();
        geometry.Children.Add(new RectangleGeometry(bounds));
        if (!outlineOnly)
        {
            var box = new Rect(note.X, note.Y, note.Width, note.Height);
            var rotation = new RotateTransform(note.Rotation, box.X + box.Width / 2, box.Y + box.Height / 2);
            var closest = (from start in Corners(bounds)
                           from corner in Corners(box)
                           let end = rotation.Transform(corner)
                           orderby (end - start).LengthSquared
                           select (Start: start, End: end)).First();
            geometry.Children.Add(new LineGeometry(closest.Start, closest.End));
        }
        marker.Data = geometry;
    }
    static Point[] Corners(Rect bounds) => [bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight];

    Canvas? regionCanvas;
    Rectangle? regionOutline;
    Point regionStart;
    int regionPage;
    bool regionRunning;

    async void RegionToolClicked(object sender, RoutedEventArgs e)
    {
        if (RegionTool.IsChecked != true) { ClearRegion(); return; }
        if (info == null || IsBusy || regionRunning) { RegionTool.IsChecked = false; return; }
        CancelPendingEditGesture();
        CancelSpreadStroke();
        StopContinuousLine();
        ApplyInlineText();
        if (IsViewing && !CanEditSpread) await EnterFromViewingAsync();
        if (IsViewing && !CanEditSpread) RegionTool.IsChecked = false;
    }

    bool BeginRegion(Canvas canvas, int pageIndex, MouseButtonEventArgs e)
    {
        if (RegionTool.IsChecked != true) return false;
        if (regionRunning || IsBusy || pageIndex < 0 || pageIndex >= plan.Pages.Count) return true;
        ClearRegion();
        ApplyInlineText();
        if (!canvas.CaptureMouse()) return true;
        regionCanvas = canvas;
        regionPage = pageIndex;
        regionStart = RegionPoint(canvas, e);
        regionOutline = new Rectangle {
            Stroke = Brushes.DodgerBlue, Fill = new SolidColorBrush(Color.FromArgb(48, 30, 144, 255)),
            StrokeThickness = 1, IsHitTestVisible = false
        };
        Panel.SetZIndex(regionOutline, 250);
        canvas.Children.Add(regionOutline);
        return true;
    }

    static Point RegionPoint(Canvas canvas, MouseEventArgs e)
    {
        var point = e.GetPosition(canvas);
        return new Point(Math.Clamp(point.X, 0, canvas.ActualWidth), Math.Clamp(point.Y, 0, canvas.ActualHeight));
    }

    bool UpdateRegion(MouseEventArgs e)
    {
        if (regionCanvas is not {} canvas || regionOutline is not {} outline) return false;
        var bounds = new Rect(regionStart, RegionPoint(canvas, e));
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);
        outline.Width = bounds.Width;
        outline.Height = bounds.Height;
        return true;
    }

    void ClearRegion()
    {
        var canvas = regionCanvas;
        var outline = regionOutline;
        regionCanvas = null;
        regionOutline = null;
        if (outline != null) canvas?.Children.Remove(outline);
        if (canvas?.IsMouseCaptured == true) canvas.ReleaseMouseCapture();
    }

    bool FinishRegion(MouseButtonEventArgs e)
    {
        if (regionCanvas is not {} canvas) return false;
        var point = e.GetPosition(this);
        var bounds = new Rect(regionStart, RegionPoint(canvas, e));
        var index = regionPage;
        ClearRegion();
        RegionTool.IsChecked = false;
        if (bounds.Width < 3 || bounds.Height < 3) return true;

        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Relative,
            HorizontalOffset = point.X, VerticalOffset = point.Y };
        void Add(string title, Func<Task> action)
        {
            var item = new MenuItem { Header = title };
            item.Click += async (_, _) => {
                if (regionRunning || lifetime.IsCancellationRequested || IsBusy || index >= plan.Pages.Count) return;
                regionRunning = true;
                try { await action(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception ex) { ShowPageOperationError(ex.Message); }
                finally { regionRunning = false; }
            };
            menu.Items.Add(item);
        }
        var page = plan.Pages[index];
        var source = snapshot;
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        Add("선택 영역에 메모 추가", () => {
            if (source != snapshot || !ReferenceEquals(plan.Pages[index], page) ||
                CanEditSpread && (!canvas.IsLoaded || canvas.DataContext is not PageTile tile || tile.Index != index)) return Task.CompletedTask;
            if (CanEditSpread) {
                updating = true;
                try { EditPages.SelectedIndex = index; }
                finally { updating = false; }
                EditPageNumber.Text = (index + 1).ToString();
            }
            else if (!ReferenceEquals(Page,page)) return Task.CompletedTask;
            selectedIds.Clear(); selectedId = "";
            var noteX = bounds.Right + 212 <= width ? bounds.Right + 12 :
                bounds.Left >= 212 ? bounds.Left - 212 : bounds.Left;
            var noteY = noteX == bounds.Left
                ? (bounds.Bottom + 76 <= height ? bounds.Bottom + 12 : bounds.Top - 76)
                : bounds.Top;
            AddTextElement(noteX, noteY, "메모", true, CanEditSpread,
                new PdfNoteRegion(page.SourceIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height));
            if (Selected is not {} note) return Task.CompletedTask;
            selectedIds.Add(note.Id);
            EditTool.SelectedIndex = 1;
            BeginAddedText(CanEditSpread ? canvas : null);
            return Task.CompletedTask;
        });
        Add("영역 텍스트 복사", async () => {
            var text = await PdfEditService.ReadRegionTextAsync(source, work, page.SourceIndex, page.Rotation, bounds, lifetime.Token);
            if (string.IsNullOrWhiteSpace(text)) { ShowPageOperationError("선택 영역에 복사할 PDF 텍스트가 없습니다."); return; }
            Clipboard.SetText(text);
            ShowRegionResult("영역 텍스트 복사", text);
        });
        if (BuildFeatures.SmartEnabled) {
            Add("영역 OCR", async () => ShowRegionResult("영역 OCR",
                await DocumentIntelligence.ReadRegionAsync(source, work, page, bounds, width, height, lifetime.Token)));
            Add("영역 요약", async () => {
                var token = lifetime.Token;
                var text = await PdfEditService.ReadRegionTextAsync(source, work, page.SourceIndex, page.Rotation, bounds, token);
                if (string.IsNullOrWhiteSpace(text))
                    text = await DocumentIntelligence.ReadRegionAsync(source, work, page, bounds, width, height, token);
                if (string.IsNullOrWhiteSpace(text)) { ShowPageOperationError("요약할 텍스트를 찾지 못했습니다."); return; }
                ShowRegionResult("영역 요약", await LocalDocumentAi.SummarizeRegionAsync(text, token));
            });
        }
        menu.IsOpen = true;
        return true;
    }

    void ShowRegionResult(string title, string text)
    {
        if (lifetime.IsCancellationRequested) return;
        var output = new TextBox { Text = string.IsNullOrWhiteSpace(text) ? "인식된 텍스트가 없습니다." : text,
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10), Margin = new Thickness(12) };
        var dialog = new Window { Owner = DialogOwner, Title = title, Width = 560, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = output, ShowInTaskbar = false };
        dialog.ShowDialog();
    }
}
