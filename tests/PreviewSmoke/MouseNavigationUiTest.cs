using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Topdf;

internal static class MouseNavigationUiTest
{
    public static int Run(string source)
    {
        var app = new Application();
        var result = 1;
        app.Startup += async (_, _) => {
            var window = new MainWindow(preferSharedPageEditor: true) { Width = 1000, Height = 730 };
            window.Show();
            try {
                await window.OpenFilesAsync([source]);
                var editor = window.ActivePdfEditor ?? throw new Exception("Editor did not open.");
                await editor.Ready;
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var panField = typeof(PdfEditorView).GetField("documentPan", flags)!;
                var move = typeof(PdfEditorView).GetMethod("MoveDocumentPan", flags)!;
                var spreadScroll = typeof(PdfEditorView).GetMethod("GetSpreadScrollViewer", flags)!;
                var horizontalWheel = typeof(PdfEditorView).GetMethod("ScrollDocumentHorizontally", BindingFlags.Static | BindingFlags.NonPublic)!;

                foreach (var columns in new[] { 1, 2, 3 }) {
                    if (columns > 1) await editor.SetViewColumnsAsync(columns);
                    await editor.SetUnifiedZoomAsync(2400);
                    window.UpdateLayout();
                    var scroll = columns == 1 ? (ScrollViewer)editor.FindName("EditScroll") : (ScrollViewer)spreadScroll.Invoke(editor, null)!;
                    scroll.ScrollToLeftEnd();
                    scroll.ScrollToTop();
                    window.UpdateLayout();
                    if (scroll.ScrollableWidth < 300 || scroll.ScrollableHeight < 180) throw new Exception($"Test surface cannot scroll in {columns}-page view: {scroll.ScrollableWidth}×{scroll.ScrollableHeight}.");
                    var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) {
                        RoutedEvent = Mouse.PreviewMouseDownEvent
                    };
                    scroll.RaiseEvent(down);
                    var pan = panField.GetValue(editor) ?? throw new Exception($"Middle mouse down not routed in {columns}-page view.");
                    var start = (Point)pan.GetType().GetProperty("Start")!.GetValue(pan)!;
                    move.Invoke(editor, [start - new Vector(240, 130)]);
                    window.UpdateLayout();
                    if (Math.Abs(scroll.HorizontalOffset - 240) > 2 || Math.Abs(scroll.VerticalOffset - 130) > 2)
                        throw new Exception($"Incorrect pan displacement in {columns}-page view.");
                    scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle) {
                        RoutedEvent = Mouse.PreviewMouseUpEvent
                    });
                    if (panField.GetValue(editor) != null || ReferenceEquals(Mouse.Captured, scroll))
                        throw new Exception("Middle mouse release did not clear capture.");
                    horizontalWheel.Invoke(null, [scroll, -120]);
                    window.UpdateLayout();
                    if (Math.Abs(scroll.HorizontalOffset - 360) > 2 || Math.Abs(scroll.VerticalOffset - 130) > 2)
                        throw new Exception("Horizontal wheel changed the wrong offset.");
                    Console.WriteLine($"PASS {columns}-page middle mouse capture, two-axis drag, release and horizontal scroll");
                }
                if (editor.HasUnsavedEdits) throw new Exception("Navigation modified the document.");
                result = 0;
            } catch (Exception error) {
                Console.Error.WriteLine(error);
            } finally {
                window.Close();
                app.Shutdown();
            }
        };
        app.Run();
        return result;
    }
}
