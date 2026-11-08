using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace Topdf;

/// <summary>One session, one queue; export directories must be outside the watched directory.</summary>
public sealed class FolderAutomation : IDisposable
{
    readonly FileSystemWatcher watcher;
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> completed = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string, Task<bool>> convert;
    readonly Action<string> notify;
    readonly Task loop;
    public FolderAutomation(string input, string output, Func<string, Task<bool>> convert, Action<string> notify)
    {
        input = Path.GetFullPath(input); output = Path.GetFullPath(output);
        if (Within(input, output) || Within(output, input)) throw new ArgumentException("입력 폴더와 저장 폴더는 서로 분리해서 선택하세요.");
        if (!Directory.Exists(input) || !Directory.Exists(output)) throw new DirectoryNotFoundException("입력·저장 폴더를 확인하세요.");
        this.convert = convert; this.notify = notify;
        watcher = new(input) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Created += (_, e) => Queue(e.FullPath); watcher.Changed += (_, e) => Queue(e.FullPath);
        watcher.Renamed += (_, e) => Queue(e.FullPath);
        watcher.Error += (_, _) => {
            try { notify("폴더 감시 이벤트가 누락되어 폴더를 다시 확인합니다."); foreach (var path in Directory.EnumerateFiles(input)) Queue(path); }
            catch (IOException ex) { notify("감시 폴더를 확인할 수 없습니다: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { notify("감시 폴더 접근 실패: " + ex.Message); }
        };
        // Existing documents are intentionally not queued when the user starts watching.
        watcher.EnableRaisingEvents = true;
        loop = RunAsync();
    }
    static bool Within(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(directory)+Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    void Queue(string path) { if (IndependentConversion.Supports(path)) pending[path] = 0; }
    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested) {
            try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
            foreach (var path in pending.Keys) {
                if (stop.IsCancellationRequested) return;
                if (!pending.TryRemove(path, out _)) continue;
                try {
                    var before = await FingerprintAsync(path, stop.Token);
                    await Task.Delay(1500, stop.Token);
                    var stable = await FingerprintAsync(path, stop.Token);
                    if (before != stable) { Queue(path); continue; }
                    if (completed.TryGetValue(path, out var done) && done == stable) continue;
                    var success = await convert(path);
                    var after = await FingerprintAsync(path, stop.Token);
                    if (after != stable) Queue(path);
                    else if (success) completed[path] = after;
                } catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (IOException) { if (File.Exists(path)) Queue(path); }
                catch (Exception ex) { notify("폴더 자동 변환 실패: " + ex.Message); }
            }
        }
    }
    static async Task<string> FingerprintAsync(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
    }
    public void Dispose() { watcher.Dispose(); stop.Cancel(); _ = loop.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default); }
}
