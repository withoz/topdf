using System.IO;

namespace Topdf;

public static partial class PdfEditService
{
    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static async Task<int> ExportFormatAsync(string source, string target, string format, string pages, int dpi, bool visual, CancellationToken cancellation)
    {
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target)!;
        // Keep final moves on the destination filesystem for atomic publication.
        var localStage = Path.Combine(parent, $".topdf-export-{Guid.NewGuid():N}");
        var published = new List<string>();
        try {
            Directory.CreateDirectory(localStage);
            var temporaryTarget = Path.Combine(localStage, Path.GetFileName(target));
            var arguments = new List<string> { format, source, temporaryTarget, "--pages", pages, "--dpi", dpi.ToString() };
            if (visual) arguments.Add("--visual");
            await RunAsync(arguments.ToArray(), cancellation, "format_export.py");
            var files = Directory.GetFiles(localStage);
            if (files.Length == 0) throw new IOException("내보낸 파일을 찾을 수 없습니다.");
            if (files.Length == 1 && string.Equals(files[0], temporaryTarget, StringComparison.OrdinalIgnoreCase)) {
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporaryTarget, target, overwrite: true);
                return 1;
            }
            foreach (var file in files) {
                var destination = Path.Combine(parent, Path.GetFileName(file));
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException($"같은 이름의 파일이 있습니다. 다른 이름을 선택해 주세요.\n{Path.GetFileName(destination)}");
            }
            foreach (var file in files) {
                cancellation.ThrowIfCancellationRequested();
                var destination = Path.Combine(parent, Path.GetFileName(file));
                File.Move(file, destination);
                published.Add(destination);
            }
            return published.Count;
        } catch {
            foreach (var file in published) TryDelete(file);
            throw;
        } finally {
            if (Directory.Exists(localStage)) {
                foreach (var file in Directory.GetFiles(localStage)) TryDelete(file);
                try { Directory.Delete(localStage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

        }
    }
}
