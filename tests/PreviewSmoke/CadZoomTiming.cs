using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;

internal static class CadZoomTiming
{
    public static int Run(string source, string report)
    {
        var app = new Application();
        var result = 1;
        app.Startup += async (_, _) => {
            var window = new MainWindow(preferSharedPageEditor: true) { Width = 1050, Height = 760 };
            window.Show();
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                var token = timeout.Token;
                await window.OpenFilesAsync([source]).WaitAsync(token);
                var editor = window.ActivePdfEditor ?? throw new Exception("Editor did not open.");
                await editor.Ready.WaitAsync(token);
                var layer = (Canvas)editor.FindName("EditDetailLayer");
                var scroll = (ScrollViewer)editor.FindName("EditScroll");
                var timings = new List<object>();
                for (var trial = 0; trial < 6; trial++) {
                    foreach (var zoom in new[] { 2400, 6400 }) {
                        await editor.SetUnifiedZoomAsync(100, new Point(200, 220));
                        scroll.ScrollToTop();
                        scroll.ScrollToLeftEnd();
                        window.UpdateLayout();
                        await Task.Delay(400, token);
                        var watch = Stopwatch.StartNew();
                        await editor.SetUnifiedZoomAsync(zoom, new Point(200, 220));
                        var (bitmap, time) = await WaitForFrame(layer, watch, token);
                        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                        var buffer = new byte[pixels.PixelWidth * pixels.PixelHeight * 4];
                        pixels.CopyPixels(buffer, pixels.PixelWidth * 4, 0);
                        timings.Add(new { trial, zoom, milliseconds = time, width = pixels.PixelWidth,
                            height = pixels.PixelHeight, hash = Convert.ToHexString(SHA256.HashData(buffer)) });
                        Console.WriteLine($"{trial} {zoom}% {time:F0} ms {pixels.PixelWidth}×{pixels.PixelHeight}");
                    }
                }
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }), token);
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

    static async Task<(BitmapSource Bitmap, double Milliseconds)> WaitForFrame(Canvas layer, Stopwatch watch, CancellationToken token)
    {
        var frame = new TaskCompletionSource<(BitmapSource, double)>();
        void Rendered(object? sender, EventArgs args)
        {
            if (layer.Children.OfType<Image>().FirstOrDefault()?.Source is BitmapSource bitmap)
                frame.TrySetResult((bitmap, watch.Elapsed.TotalMilliseconds));
        }
        CompositionTarget.Rendering += Rendered;
        try { return await frame.Task.WaitAsync(token); }
        finally { CompositionTarget.Rendering -= Rendered; }
    }
}
