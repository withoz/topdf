using System.IO;
using System.Windows.Media.Imaging;

namespace Topdf;

public partial class PdfEditorView
{
    async void EditStampBackground(PdfEditElement item)
    {
        if (importingStamp || IsBusy || item.ImagePath is not { } source) return;
        importingStamp = true;
        try {
            var image = LoadImage(source);
            var settings = new StampImportWindow(image, DialogOwner, editExisting: true);
            if (settings.ShowDialog() != true || !settings.RemoveBackground) return;
            var candidate = Path.Combine(work, Guid.NewGuid().ToString("N") + ".png");
            await SaveStampCutoutAsync(image, settings, candidate);
            if (lifetime.IsCancellationRequested) return;
            var items = Objects();
            if (IsBusy || items.Count != 1 || !ReferenceEquals(items[0], item) || item.ImagePath != source) return;
            ApplyDockedProperty(x => x.ImagePath, (x, path) => x.ImagePath = path, candidate);
        } catch (Exception error) {
            ShowPageOperationError(error.Message);
        } finally {
            importingStamp = false;
        }
    }

    Task SaveStampCutoutAsync(BitmapSource image, StampImportWindow settings, string candidate)
    {
        var background = settings.BackgroundColor;
        var tolerance = settings.Tolerance;
        var includeInterior = settings.IncludeInterior;
        var token = lifetime.Token;
        return Task.Run(() => {
            var output = StampImportWindow.RemoveBackgroundColor(image, background, tolerance, includeInterior);
            token.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            try {
                using var stream = File.Create(candidate);
                encoder.Save(stream);
                if (stream.Length > 20_000_000) throw new IOException("변환된 도장 이미지가 20MB를 초과했습니다.");
                token.ThrowIfCancellationRequested();
            } catch {
                try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }, token);
    }
}
