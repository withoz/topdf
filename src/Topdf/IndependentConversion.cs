using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Topdf;

public static class IndependentConversion
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".hwp" or ".hwpx" or ".docx" or ".xls" or ".xlsx" or ".dwg" or ".dxf" or ".pdf";
    public static string EngineName(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".hwp" or ".hwpx" => "rhwp 0.8.7 · 머리글·꼬리글 보정",
        ".docx" => "office2pdf 0.10.0",
        ".xlsx" => "MiniPdf 0.43.2",
        ".xls" => "Ream 1.33.1 · 병합 셀 보정",
        ".dwg" => "LibreDWG 0.14 + ezdxf",
        ".dxf" => "ezdxf / matplotlib",
        ".pdf" => "PDF 바로 보기",
        _ => "독립 변환 검증 보류 · 호환 모드 필요"
    };

    public static async Task<string> ConvertAsync(string input, string root, CancellationToken cancellation)
    {
        if (!Supports(input)) throw new NotSupportedException("이 형식은 독립 변환 품질 검증을 통과하지 못했습니다. 설치형 호환 모드를 선택하면 기존 엔진으로 변환할 수 있습니다.");
        cancellation.ThrowIfCancellationRequested();
        var job = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var pdf = Path.Combine(job, Path.GetFileNameWithoutExtension(input) + ".pdf");
        var extension = Path.GetExtension(input).ToLowerInvariant();
        var vendor = Path.Combine(AppContext.BaseDirectory, "engines", "vendor");
        string executable;
        string[] arguments;
        switch (extension) {
            case ".hwp": case ".hwpx":
                executable = Path.Combine(vendor, "rhwp", "rhwp.exe");
                arguments = ["export-pdf", input, "-o", pdf, "--font-path", Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "--fallback-mono", "Malgun Gothic", "--profile", "print", "--json"];
                break;
            case ".docx":
                executable = Path.Combine(vendor, "office2pdf", "office2pdf.exe");
                arguments = [input, "-o", pdf, "--font-path", Environment.GetFolderPath(Environment.SpecialFolder.Fonts)];
                break;
            case ".xlsx":
                executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
                arguments = ["--convert-xlsx", input, pdf];
                break;
            case ".xls":
                executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
                arguments = ["--convert-xls", input, pdf];
                break;
            case ".dwg": case ".dxf":
                executable = AppPaths.Python;
                var worker = Path.Combine(AppContext.BaseDirectory, "engines", "convert.py");
                arguments = [worker, input, pdf, "--libredwg", Path.Combine(vendor, "libredwg", "dwg2dxf.exe")];
                break;
            default: return input;
        }
        var log = Path.Combine(job, "engine.log");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = job };
        info.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("독립 변환 엔진을 실행하지 못했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("독립 변환 제한 시간 3분을 초과했습니다.");
        }
        await Task.WhenAll(stdout, stderr);
        await File.WriteAllTextAsync(log, await stdout + Environment.NewLine + await stderr);
        await File.WriteAllTextAsync(Path.Combine(job, "engine.json"), JsonSerializer.Serialize(new { engine = EngineName(input), mode = "independent", executable, arguments, exitCode = process.ExitCode }));
        if (process.ExitCode != 0 || !File.Exists(pdf)) {
            var errorFile = pdf + ".error.txt";
            var reason = File.Exists(errorFile) ? File.ReadLines(errorFile).FirstOrDefault() : null;
            throw new InvalidOperationException(reason == null ? $"{EngineName(input)} 변환 실패. 로그: {log}" : reason[..Math.Min(reason.Length, 600)]);
        }
        using var stream = File.OpenRead(pdf);
        var header = new byte[5];
        if (stream.Read(header) != 5 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-") throw new InvalidDataException("유효한 PDF가 생성되지 않았습니다.");
        return pdf;
    }
}
