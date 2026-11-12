using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Topdf;

public partial class PdfEditorView
{
    sealed record DocumentPan(ScrollViewer Scroll, Point Start, double Horizontal, double Vertical);
    DocumentPan? documentPan;

    ScrollViewer? DocumentScroll(object sender) =>
        ReferenceEquals(sender, SpreadPages) && IsSpreadViewing ? GetSpreadScrollViewer() :
        ReferenceEquals(sender, EditScroll) && EditSurface.IsVisible ? EditScroll : null;

    void DocumentMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || changingView || info == null ||
            Mouse.Captured != null || DocumentScroll(sender) is not { } scroll ||
            (scroll.ScrollableWidth <= 0 && scroll.ScrollableHeight <= 0)) return;
        if (!scroll.CaptureMouse()) return;
        documentPan = new(scroll, e.GetPosition(scroll), scroll.HorizontalOffset, scroll.VerticalOffset);
        Mouse.UpdateCursor();
        e.Handled = true;
    }

    void DocumentMouseMove(object sender, MouseEventArgs e)
    {
        if (documentPan is not { } pan) return;
        if (e.MiddleButton != MouseButtonState.Pressed) {
            StopDocumentPan();
            return;
        }
        MoveDocumentPan(e.GetPosition(pan.Scroll));
        e.Handled = true;
    }

    void MoveDocumentPan(Point position)
    {
        if (documentPan is not { } pan) return;
        var delta = position - pan.Start;
        pan.Scroll.ScrollToHorizontalOffset(pan.Horizontal - delta.X);
        pan.Scroll.ScrollToVerticalOffset(pan.Vertical - delta.Y);
    }

    void DocumentMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (documentPan == null || e.ChangedButton != MouseButton.Middle) return;
        StopDocumentPan();
        e.Handled = true;
    }

    void DocumentCaptureLost(object sender, MouseEventArgs e)
    {
        if (documentPan is { } pan && ReferenceEquals(e.OriginalSource, pan.Scroll)) StopDocumentPan();
    }

    void StopDocumentPan()
    {
        if (documentPan is not { } pan) return;
        documentPan = null;
        if (ReferenceEquals(Mouse.Captured, pan.Scroll)) pan.Scroll.ReleaseMouseCapture();
        Mouse.UpdateCursor();
    }

    void DocumentPanCursor(object sender, QueryCursorEventArgs e)
    {
        if (documentPan == null) return;
        e.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    static void ScrollDocumentHorizontally(ScrollViewer scroll, int wheelDelta) =>
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - wheelDelta);

    async Task ZoomSpreadAtPointerAsync(double percent, MouseWheelEventArgs e)
    {
        if (GetSpreadScrollViewer() is not { } scroll) {
            await SetUnifiedZoomAsync(percent);
            return;
        }
        var presenter = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement ?? scroll;
        var pointer = e.GetPosition(presenter);
        var surface = FindSpreadPageSurface(e.OriginalSource as DependencyObject);
        var tile = surface?.DataContext;
        var bounds = surface?.TransformToVisual(presenter).TransformBounds(new Rect(surface.RenderSize));
        await SetUnifiedZoomAsync(percent);
        if (surface == null || bounds is not { Width: > 0, Height: > 0 } old ||
            !surface.IsLoaded || !ReferenceEquals(tile, surface.DataContext)) return;
        scroll.UpdateLayout();
        if (!surface.IsLoaded || !ReferenceEquals(tile, surface.DataContext)) return;
        var current = surface.TransformToVisual(presenter).TransformBounds(new Rect(surface.RenderSize));
        var x = current.Left + (pointer.X - old.Left) * current.Width / old.Width;
        var y = current.Top + (pointer.Y - old.Top) * current.Height / old.Height;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + x - pointer.X);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + y - pointer.Y);
    }

    static FrameworkElement? FindSpreadPageSurface(DependencyObject? source)
    {
        for (var current = source; current is Visual; current = VisualTreeHelper.GetParent(current))
            if (current is Grid { Name: "SpreadPageSurface", DataContext: PageTile } grid) return grid;
        return null;
    }
}
