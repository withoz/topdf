using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;

internal static class FormatRoundTripUiTest
{
    public static int Run(string source)
    {
        var app = new Application();
        var result = 1;
        app.Startup += async (_, _) => {
            var window = new MainWindow(preferSharedPageEditor: true);
            window.Show();
            try {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var token = timeout.Token;
                var output = Directory.CreateTempSubdirectory("topdf-formats-").FullName;
                await window.OpenFilesAsync([source]).WaitAsync(token);
                var menu = (MenuItem)window.FindName("MenuExportOther");
                var editor = window.ActivePdfEditor ?? throw new Exception("Source editor was not opened.");
                await editor.Ready.WaitAsync(token);
                var formats = new[] { "docx", "hwpx", "xlsx", "pptx", "dxf", "png", "jpg" };
                if (!menu.IsEnabled || !menu.Items.OfType<MenuItem>().Select(item => item.Tag as string).SequenceEqual(formats))
                    throw new Exception("Export menu is not wired.");
                foreach (var columns in new[] { 2, 3 }) {
                    await editor.SetViewColumnsAsync(columns);
                    await editor.SetUnifiedZoomAsync(200);
                    var spreads = (ListBox)editor.FindName("SpreadPages");
                    while (!Images(spreads).Any(image => image.Source is BitmapSource bitmap && bitmap.PixelWidth > 1100)) {
                        window.UpdateLayout();
                        await Task.Delay(40, token);
                    }
                    Console.WriteLine($"PASS {columns}-page preview renders above 1100 pixels");
                }
                await editor.SetViewColumnsAsync(1);
                var snapshot = await editor.GetViewingPdfAsync();
                var text = Path.Combine(output, "글자.docx");
                var visual = Path.Combine(output, "모양.docx");
                var png = Path.Combine(output, "쪽.png");
                var jpg = Path.Combine(output, "쪽.jpg");
                if (await PdfEditService.ExportFormatAsync(snapshot, text, "docx", "1-2", 150, false, token) != 1 ||
                    await PdfEditService.ExportFormatAsync(snapshot, visual, "docx", "1-2", 150, true, token) != 1 ||
                    await PdfEditService.ExportFormatAsync(snapshot, png, "png", "1,3", 300, false, token) != 2 ||
                    await PdfEditService.ExportFormatAsync(snapshot, jpg, "jpg", "2", 150, false, token) != 1)
                    throw new Exception("Unexpected export file count.");

                var converted = new List<(string Path, int Pages)>();
                foreach (var format in new[] { "hwpx", "xlsx", "pptx", "dxf" }) {
                    var target = Path.Combine(output, $"문서.{format}");
                    var count = await PdfEditService.ExportFormatAsync(snapshot, target, format, "1-2", 150, false, token);
                    if (count != (format == "dxf" ? 2 : 1)) throw new Exception($"Unexpected {format} file count.");
                    if (format == "dxf") {
                        converted.Add((Path.Combine(output, "문서-0001.dxf"), 1));
                        converted.Add((Path.Combine(output, "문서-0002.dxf"), 1));
                    } else {
                        converted.Add((target, 2));
                    }
                }

                var list = (DataGrid)window.FindName("Files");
                foreach (var (path, pages) in new[] {
                    (text, 2), (visual, 2), (Path.Combine(output, "쪽-0001.png"), 1),
                    (Path.Combine(output, "쪽-0003.png"), 1), (jpg, 1)
                }.Concat(converted)) {
                    await window.OpenFilesAsync([path]).WaitAsync(token);
                    var job = list.Items.Cast<FileJob>().Single(item => item.Source == path);
                    if (job.Pdf is not { } pdf) throw new Exception($"Open failed: {path} ({job.Detail})");
                    var inspection = await PdfEditService.InspectAsync(pdf, output, token);
                    if (inspection.Pages.Length != pages) throw new Exception($"Wrong page count: {path}");
                    var reopened = window.ActivePdfEditor ?? throw new Exception($"Editor not shown: {path}");
                    await reopened.Ready.WaitAsync(token);
                    if (reopened.CurrentPlan.Pages.Count != pages) throw new Exception($"Wrong editor page count: {path}");
                    Console.WriteLine($"PASS {Path.GetFileName(path)} ({pages} pages)");
                }
                Console.WriteLine(output);
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

    static IEnumerable<Image> Images(DependencyObject element)
    {
        if (element is Image image) yield return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            foreach (var child in Images(VisualTreeHelper.GetChild(element, i)))
                yield return child;
    }
}
