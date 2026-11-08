using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Topdf;

public static class LocalAiHost
{
    public const string ModelName = "내장 Qwen2.5 0.5B";
    static string Folder => Path.Combine(AppContext.BaseDirectory, "engines", "vendor", "llama");
    public static bool Available => File.Exists(Path.Combine(Folder, "llama-server.exe")) && File.Exists(Path.Combine(Folder, "models", "qwen2.5-0.5b-instruct-q4_k_m.gguf"));
    static readonly SemaphoreSlim Gate = new(1);
    static Process? process;
    static HttpClient? client;
    public static async Task<HttpClient> GetClientAsync(CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation);
        try {
            if (process is { HasExited: false } && client != null) return client;
            if (!Available) throw new FileNotFoundException("내장 로컬 AI 모델이 없습니다. 전체 설치판을 사용하거나 Ollama의 로컬 모델을 선택하세요.");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Guid.NewGuid().ToString("N");
            var info = new ProcessStartInfo(Path.Combine(Folder, "llama-server.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Folder };
            foreach (var arg in new[] { "--model", Path.Combine(Folder, "models", "qwen2.5-0.5b-instruct-q4_k_m.gguf"), "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", key, "--ctx-size", "8192", "--threads", Math.Min(4, Environment.ProcessorCount).ToString(), "--n-gpu-layers", "0", "--offline", "--no-webui" }) info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new IOException("로컬 AI 엔진 실행 실패");
            Directory.CreateDirectory(AppPaths.DataRoot);
            var logPath = Path.Combine(AppPaths.DataRoot, "local-ai.log");
            File.WriteAllText(logPath, "");
            var logLock = new object();
            void Log(object sender, DataReceivedEventArgs e) {
                if (e.Data == null) return;
                lock (logLock) { try { if (new FileInfo(logPath).Length < 500000) File.AppendAllText(logPath, e.Data.Replace(key, "[redacted]") + Environment.NewLine); } catch (IOException) { } }
            }
            process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            client?.Dispose();
            client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            while (true) {
                timeout.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new IOException("로컬 AI 엔진이 시작되지 않았습니다. CPU 지원과 설치 파일을 확인해 주세요.");
                try { using var health = await client.GetAsync("health", timeout.Token); if (health.IsSuccessStatusCode) return client; }
                catch (HttpRequestException) { }
                await Task.Delay(300, timeout.Token);
            }
        } catch { Stop(); throw; }
        finally { Gate.Release(); }
    }
    public static void Stop()
    {
        if (process != null) {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            process.Dispose(); process = null;
        }
        client?.Dispose(); client = null;
    }
}
