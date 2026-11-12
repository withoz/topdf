using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;

internal static class StampTransparencyTest
{
    public static int Run(string source)
    {
        var folder = Directory.CreateTempSubdirectory("topdf-stamp-").FullName;
        var pixels = Enumerable.Repeat((byte)255, 9 * 9 * 4).ToArray();
        for (var i = 2; i <= 6; i++) {
            Red(i, 2); Red(i, 6); Red(2, i); Red(6, i);
        }
        void Red(int x, int y) {
            var offset = (y * 9 + x) * 4;
            pixels[offset] = pixels[offset + 1] = 0;
        }
        pixels[(4 * 9 + 2) * 4 + 3] = 128;
        var sourceImage = BitmapSource.Create(9, 9, 96, 96, PixelFormats.Bgra32, null, pixels, 36);
        var import = typeof(PdfEditorView).Assembly.GetType("Topdf.StampImportWindow")!;
        var remove = import.GetMethod("RemoveBackgroundColor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var borderOnly = (BitmapSource)remove.Invoke(null, [sourceImage, Colors.White, 30, false])!;
        var all = (BitmapSource)remove.Invoke(null, [sourceImage, Colors.White, 30, true])!;
        if (Pixel(borderOnly, 4, 4).A != 255 || Pixel(all, 4, 4).A != 0 || Pixel(all, 0, 0).A != 0 ||
            Pixel(all, 4, 2) != Colors.Red || Pixel(all, 2, 4).A != 128)
            throw new Exception("The selected mode did not preserve the expected ink and alpha.");

        var asset = Path.Combine(folder, "stamp.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(all));
        using (var stream = File.Create(asset)) png.Save(stream);
        var info = PdfEditService.InspectAsync(source, folder, default).GetAwaiter().GetResult();
        var stamp = new PdfEditElement { Kind = "stamp", X = 60, Y = 60, Width = 180, Height = 180, ImagePath = asset, ImageOpacity = .5 };
        var plan = new PdfEditPlan { SourceSha256 = info.SourceSha256, Pages = [new() { SourceIndex = 0, Elements = [stamp] }] };
        BitmapSource Render(string pdf) => BitmapDecoder.Create(
            new Uri(PdfEditService.RenderAsync(pdf, folder, 0, 0, default).GetAwaiter().GetResult()),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var before = Render(source);
        var after = Render(PdfEditService.ExportAsync(source, folder, plan, default).GetAwaiter().GetResult());
        var scale = before.PixelWidth / 300.0;
        for (var y = 123; y < 176; y++)
            for (var x = 123; x < 176; x++)
                if (Pixel(before, (int)(x * scale), (int)(y * scale)) != Pixel(after, (int)(x * scale), (int)(y * scale)))
                    throw new Exception("The original PDF changed under a transparent interior.");
        var ink = Pixel(after, (int)(150 * scale), (int)(110 * scale));
        if (Math.Abs(ink.R - 128) > 3 || ink.G > 3 || Math.Abs(ink.B - 127) > 3)
            throw new Exception($"The PDF opacity was not applied: {ink}");
        Console.WriteLine("PASS edge-only and interior modes, original alpha, PDF text under transparent stamp, and 50% stamp opacity.");
        return 0;
    }

    static Color Pixel(BitmapSource source, int x, int y)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
