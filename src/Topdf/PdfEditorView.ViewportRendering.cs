using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Topdf;

public partial class PdfEditorView
{
    const double MaximumZoom = 6400;
    readonly PdfViewportWorker viewportWorker = new();
    CancellationTokenSource? viewportRequest;
    sealed record ViewportPatch(string Source, int Page, int Rotation, long Revision, double Pixels, Rect Bounds, long InlineRevision);

    void ClearViewportPatch()
    {
        viewportRequest?.Cancel();
        EditDetailLayer.Children.Clear();
        EditDetailLayer.Tag = null;
    }

    void EditingScrolled(object sender, ScrollChangedEventArgs e) => ScheduleViewportRender();

    async void ScheduleViewportRender()
    {
        if (info == null || lifetime.IsCancellationRequested) return;
        viewportRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        viewportRequest = request;
        try {
            await Task.Delay(24, request.Token);
            if (IsSpreadViewing && GetSpreadScrollViewer() is { } spread) {
                foreach (var layer in VisibleSpreadLayers(spread).ToArray()) {
                    request.Token.ThrowIfCancellationRequested();
                    if (layer.DataContext is PageTile tile) {
                        await RenderViewportAsync(spread, layer, tile.Pdf, tile.Index, 0, 0, null, request.Token);
                    }
                }
            } else if (EditingArea.IsVisible && EditSurface.IsVisible && Page is { } page) {
                var detailPlan = selectedSource == null ? plan : inlineDetailPlan;
                if (detailPlan == null) return;
                await RenderViewportAsync(EditScroll, EditDetailLayer, snapshot, page.SourceIndex,
                    page.Rotation, renderVersion, detailPlan, request.Token);
            }
        } catch (OperationCanceledException) { }
        catch (TimeoutException) { /* Retain the base image after a worker timeout. */ }
        catch (IOException) { /* Retain the base image if a detail render fails. */ }
        catch (InvalidOperationException) { /* Layout can change while a page is recycled. */ }
        finally {
            if (ReferenceEquals(viewportRequest, request)) viewportRequest = null;
        }
    }

    static IEnumerable<Canvas> VisibleSpreadLayers(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Canvas { Name: "SpreadDetailLayer", IsVisible: true } layer) yield return layer;
            else if (child is not Canvas)
                foreach (var nested in VisibleSpreadLayers(child)) yield return nested;
        }
    }

    async Task RenderViewportAsync(ScrollViewer scroll, Canvas layer, string source, int page, int rotation,
        long revision, PdfEditPlan? renderPlan, CancellationToken token)
    {
        if (!layer.IsVisible || layer.ActualWidth <= 0 || layer.ActualHeight <= 0) return;
        var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement ?? scroll;
        var viewportBounds = new Rect(viewport.RenderSize);
        var scale = layer.TransformToVisual(viewport).TransformBounds(new Rect(layer.RenderSize));
        var dpi = VisualTreeHelper.GetDpi(layer);
        var fullPixels = Math.Max(scale.Width * dpi.DpiScaleX, scale.Height * dpi.DpiScaleY);
        var previous = layer.Tag as ViewportPatch;
        var inlineRevision = renderPlan != null && selectedSource != null ? inlineDetailRevision : 0;
        if (fullPixels <= 4200 || (previous != null &&
            (previous.Source != source || previous.Page != page || previous.Rotation != rotation ||
             previous.Revision != revision || previous.InlineRevision != inlineRevision || Math.Abs(previous.Pixels - fullPixels) > 1))) {
            layer.Children.Clear();
            layer.Tag = null;
            previous = null;
        }
        if (fullPixels <= 4200) return;

        var visible = Rect.Intersect(viewportBounds, scale);
        if (visible.IsEmpty || visible.Width < 2 || visible.Height < 2) return;
        Rect Normalize(Rect rect) => new((rect.X - scale.X) / scale.Width, (rect.Y - scale.Y) / scale.Height,
            rect.Width / scale.Width, rect.Height / scale.Height);
        if (previous?.Bounds.Contains(Normalize(visible)) == true) return;

        visible.Inflate(96, 96);
        visible.Intersect(scale);
        var area = Normalize(visible);
        var patch = new ViewportPatch(source, page, rotation, revision, fullPixels, area, inlineRevision);
        var file = await PdfEditService.RenderViewportAsync(source, work, page, rotation, token, renderPlan,
            (fullPixels, area), viewportWorker);
        try {
            token.ThrowIfCancellationRequested();
            if (!layer.IsVisible || (renderPlan != null && (revision != renderVersion ||
                (selectedSource == null ? plan : inlineDetailPlan) != renderPlan ||
                inlineRevision != (selectedSource == null ? 0 : inlineDetailRevision))) ||
                (renderPlan == null && layer.DataContext is not PageTile) ||
                (renderPlan == null && layer.DataContext is PageTile tile && (tile.Pdf != source || tile.Index != page)))
                return;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            var image = new Image { Source = bitmap, Stretch = Stretch.Fill,
                Width = area.Width * layer.ActualWidth, Height = area.Height * layer.ActualHeight };
            Canvas.SetLeft(image, area.X * layer.ActualWidth);
            Canvas.SetTop(image, area.Y * layer.ActualHeight);
            layer.Children.Clear();
            layer.Children.Add(image);
            layer.Tag = patch;
        } finally {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
