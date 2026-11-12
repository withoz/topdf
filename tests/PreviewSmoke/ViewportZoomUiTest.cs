using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;

internal static class ViewportZoomUiTest
{
    public static int Run(string source)
    {
        var app = new Application();
        var result = 1;
        app.Startup += async (_, _) => {
            var window = new MainWindow(preferSharedPageEditor: true) { Width = 1050, Height = 760 };
            window.Show();
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await window.OpenFilesAsync([source]).WaitAsync(timeout.Token);
                var editor = window.ActivePdfEditor ?? throw new Exception("Editor did not open.");
                await editor.Ready.WaitAsync(timeout.Token);
                var single = (Canvas)editor.FindName("EditDetailLayer");
                var scroll = (ScrollViewer)editor.FindName("EditScroll");
                foreach (var zoom in new[] { 2400, 6400 }) {
                    await editor.SetUnifiedZoomAsync(zoom);
                    if (editor.CurrentZoomPercent != zoom) throw new Exception("Zoom was clamped.");
                    var first = await WaitForPatch(() => single, null, timeout.Token);
                    scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset + 1250);
                    window.UpdateLayout();
                    var second = await WaitForPatch(() => single, first, timeout.Token);
                    Console.WriteLine($"PASS {zoom}% single page, new patch after scroll ({Describe(second)})");
                }

                foreach (var columns in new[] { 2, 3 }) {
                    await editor.SetViewColumnsAsync(columns);
                    await editor.SetUnifiedZoomAsync(6400);
                    var spread = (ListBox)editor.FindName("SpreadPages");
                    var image = await WaitForPatch(() => Layers(spread).FirstOrDefault(layer => layer.Children.Count > 0), null, timeout.Token);
                    Console.WriteLine($"PASS {columns}-page view at 6400% ({Describe(image)})");
                }
                await editor.FitUnifiedZoomAsync(true);
                window.UpdateLayout();
                await Task.Delay(500, timeout.Token);
                if (Layers((ListBox)editor.FindName("SpreadPages")).Any(layer => layer.Children.Count > 0))
                    throw new Exception("Fit left a high zoom patch visible.");
                Console.WriteLine("PASS page fit clears high zoom patches");
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

    static async Task<Image> WaitForPatch(Func<Canvas?> layer, Image? previous, CancellationToken token)
    {
        while (true) {
            if (layer()?.Children.OfType<Image>().FirstOrDefault() is { Source: BitmapSource bitmap } image &&
                !ReferenceEquals(image, previous)) {
                if (bitmap.PixelWidth < 250 || bitmap.PixelHeight < 250 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 12_000_000)
                    throw new Exception($"Unexpected viewport bitmap {Describe(image)}");
                return image;
            }
            await Task.Delay(50, token);
        }
    }

    static string Describe(Image image) => image.Source is BitmapSource bitmap ? $"{bitmap.PixelWidth}×{bitmap.PixelHeight}" : "";

    static IEnumerable<Canvas> Layers(DependencyObject root)
    {
        if (root is Canvas { Name: "SpreadDetailLayer", IsVisible: true } layer) yield return layer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Layers(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
