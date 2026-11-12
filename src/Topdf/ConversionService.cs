using System.Diagnostics;
using System.IO;
namespace Topdf;

public sealed class ConversionService
{
    static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf" };
    static readonly HashSet<string> Native = new(StringComparer.OrdinalIgnoreCase) { ".hwp", ".hwpx", ".dwg", ".dxf" };
    public static bool Supports(string path) => IndependentConversion.IsImage(path) || Office.Contains(Path.GetExtension(path)) || Native.Contains(Path.GetExtension(path)) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static string? FindOda() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ODA");
        return Directory.Exists(root) ? Directory.GetFiles(root, "ODAFileConverter.exe", SearchOption.AllDirectories).OrderDescending().FirstOrDefault() : null;
    }
    public static string? FindLibreOffice() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.com")
    }.FirstOrDefault(File.Exists);

    public async Task<string> ConvertAsync(string input, string root, CancellationToken cancellation, bool useCompatibilityEngines = false)
    {
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("원본 파일을 찾을 수 없습니다.", input);
        if (!Supports(input)) throw new NotSupportedException("이 형식은 아직 연결된 변환 엔진이 없습니다.");
        if (Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return input;
        if (IndependentConversion.IsImage(input) || (!useCompatibilityEngines && !Path.GetExtension(input).Equals(".ppt", StringComparison.OrdinalIgnoreCase))) return await IndependentConversion.ConvertAsync(input, root, cancellation);
        if (Native.Contains(Path.GetExtension(input))) return await ConvertNativeAsync(input, root, cancellation);
        var engine = FindLibreOffice() ?? throw new InvalidOperationException("LibreOffice 설치가 필요합니다.");
        var job = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(job, "profile");
        var output = Path.Combine(job, "output");
        Directory.CreateDirectory(Path.Combine(profile, "user"));
        Directory.CreateDirectory(output);
        // Per-job profile avoids attaching to an already running user session.
        // Imported macros are disabled explicitly, including legacy DOC/XLS files.
        await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"),
            "<?xml version=\"1.0\"?><oor:items xmlns:oor=\"http://openoffice.org/2001/registry\"><item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\"><prop oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value></prop><prop oor:name=\"DisableMacrosExecution\" oor:op=\"fuse\"><value>true</value></prop></item></oor:items>", cancellation);
        var info = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--norestore", "--convert-to", "pdf", "--outdir", output, input }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("변환 엔진 실행에 실패했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("변환 제한 시간 2분을 초과했습니다. 암호·손상·문서 크기를 확인하세요.");
        }
        await Task.WhenAll(stdout, stderr);
        var pdf = Path.Combine(output, Path.GetFileNameWithoutExtension(input) + ".pdf");
        if (process.ExitCode != 0 || !File.Exists(pdf)) throw new InvalidOperationException("PDF 생성 실패: 암호·손상·지원 형식을 확인하세요.");
        using var stream = File.OpenRead(pdf);
        var header = new byte[5];
        if (stream.Read(header) != 5 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-") throw new InvalidDataException("유효한 PDF 헤더가 없습니다.");
        return pdf;
    }

    static async Task<string> ConvertNativeAsync(string input, string root, CancellationToken cancellation)
    {
        var job = Path.Combine(Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var pdf = Path.Combine(job, Path.GetFileNameWithoutExtension(input) + ".pdf");
        var worker = Path.Combine(AppContext.BaseDirectory, "engines", "convert.py");
        if (!File.Exists(worker)) throw new FileNotFoundException("변환 worker 파일이 없습니다.", worker);
        var python = AppPaths.Python;
        var info = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in new[] { worker, input, pdf, "--oda", FindOda() ?? "" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Python 변환 엔진을 실행하지 못했습니다.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("변환 제한 시간 3분을 초과했습니다. 한글 접근 승인창 또는 변환 엔진 상태를 확인하세요.");
        }
        await Task.WhenAll(stdout, stderr);
        await File.WriteAllTextAsync(Path.Combine(job, "engine.log"), await stdout + Environment.NewLine + await stderr);
        if (process.ExitCode != 0 || !File.Exists(pdf)) throw new InvalidOperationException("전용 엔진 변환 실패. " + Path.Combine(job, "engine.log"));
        using var stream = File.OpenRead(pdf);
        var header = new byte[5];
        if (stream.Read(header) != 5 || System.Text.Encoding.ASCII.GetString(header) != "%PDF-") throw new InvalidDataException("유효한 PDF 헤더가 없습니다.");
        return pdf;
    }
}
