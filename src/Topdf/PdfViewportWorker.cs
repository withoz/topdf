using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Topdf;

internal sealed class PdfViewportWorker : IDisposable
{
    readonly SemaphoreSlim requests = new(1);
    Process? process;
    bool disposed;

    public void WarmUp()
    {
        if (disposed || process != null) return;
        try { Start(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { }
    }

    Process Start()
    {
        var info = new ProcessStartInfo(AppPaths.Python) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "engines", "pdf_viewport.py"));
        info.ArgumentList.Add("--serve");
        var worker = Process.Start(info) ?? throw new IOException("PDF 화면 렌더링 엔진 실행 실패");
        worker.ErrorDataReceived += (_, _) => { };
        worker.BeginErrorReadLine();
        return process = worker;
    }

    public async Task RenderAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        await requests.WaitAsync(cancellation);
        try {
            cancellation.ThrowIfCancellationRequested();
            if (disposed) throw new OperationCanceledException();
            if (process is { HasExited: true }) Stop();
            var worker = process ?? Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try {
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(arguments).AsMemory(), timeout.Token);
                await worker.StandardInput.FlushAsync(timeout.Token);
                // Drain the response even if the viewport moves, so the next request stays in sync.
                var response = await worker.StandardOutput.ReadLineAsync(timeout.Token);
                if (response == null) throw new IOException("PDF 화면 렌더링 엔진 연결이 종료됐습니다.");
                cancellation.ThrowIfCancellationRequested();
                if (response != "ok") throw new IOException(response);
            } catch (Exception error) when (disposed) {
                throw new OperationCanceledException("PDF editor closed.", error);
            } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
                Stop();
                throw new TimeoutException("PDF 화면 렌더링 시간이 초과됐습니다.");
            } catch (IOException) {
                Stop();
                throw;
            }
        } finally {
            requests.Release();
        }
    }

    void Stop()
    {
        var worker = process;
        process = null;
        if (worker == null) return;
        try { if (!worker.HasExited) worker.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { worker.Dispose(); }
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
    }
}
