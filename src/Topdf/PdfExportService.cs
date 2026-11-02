using System.IO;

namespace Topdf;

public sealed record PdfExportItem(string Source, string? Pdf);
public sealed record PdfExportResult(IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors);

public static class PdfExportService
{
    public static PdfExportResult Export(IEnumerable<PdfExportItem> items, string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var saved = new List<string>(); var skipped = new List<string>(); var errors = new List<string>();
        foreach (var item in items) {
            if (item.Pdf == null) { skipped.Add(Path.GetFileName(item.Source)); continue; }
            // Keep original extensions: report.docx.pdf and report.xlsx.pdf cannot collide.
            var name = Path.GetFileName(item.Source);
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
            string? created = null;
            try {
                using var input = File.OpenRead(item.Pdf);
                for (var suffix = 1; ; suffix++) {
                    var fileName = suffix == 1 ? name : Path.GetFileNameWithoutExtension(name) + $" ({suffix})" + Path.GetExtension(name);
                    var destination = Path.Combine(folder, fileName);
                    FileStream output;
                    try { output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write); }
                    catch (IOException) when (File.Exists(destination)) { continue; }
                    created = destination;
                    using (output) input.CopyTo(output);
                    saved.Add(destination);
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
                // Only remove an incomplete file that this operation created with CreateNew.
                if (created != null) { try { File.Delete(created); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                errors.Add($"{Path.GetFileName(item.Source)}: {ex.Message}");
            }
        }
        return new(saved, skipped, errors);
    }
}
