using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Topdf;

public sealed record PdfEditPageInfo(int SourceIndex, double Width, double Height);
public sealed record PdfEditInfo(string SourceSha256, PdfEditPageInfo[] Pages);
public sealed record PdfSourceText(string Id, string Text, double X, double Y, double Width, double Height, double EditX, double EditY, double EditWidth, double EditHeight, double FontSize, string Color, int Rotation, double BaselineX=0, double BaselineY=0, string FontFamily="Malgun Gothic", double[][]? CaretStops=null, double TextStrokeWidth=0, int TextRenderMode=0);
public sealed class PdfEditElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public string? ImagePath { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double FontSize { get; set; } = 14;
    public string Color { get; set; } = "#111111";
    public int Rotation { get; set; }
    public double StrokeWidth { get; set; } = 2;
    public bool Dashed { get; set; }
    public string? LineStyle { get; set; }
    public bool AutoWidth { get; set; }
    public string Fill { get; set; } = "none";
    public string Alignment { get; set; } = "left";
    public string FontFamily { get; set; } = "Malgun Gothic";
    public List<double[]> Points { get; set; } = [];
    public bool PreserveSourceStyle { get; set; }
    public string? SourceTextId { get; set; }
    public string? SourceText { get; set; }
}
public sealed class PdfEditPage
{
    public int SourceIndex { get; set; }
    public int Rotation { get; set; }
    public List<PdfEditElement> Elements { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public int DisplayNumber {get;set;}
    public string Label => $"원본 {SourceIndex+1}쪽";
}
public sealed class PdfEditPlan
{
    public int FormatVersion { get; set; } = 1;
    public string SourceSha256 { get; set; } = "";
    public List<PdfEditPage> Pages { get; set; } = [];
}

public static class PdfEditService
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static PdfEditPlan Clone(PdfEditPlan plan) => JsonSerializer.Deserialize<PdfEditPlan>(JsonSerializer.Serialize(plan, Json), Json)!;
    public static async Task<PdfEditInfo> InspectAsync(string source, string folder, CancellationToken cancellation)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        await RunAsync(["inspect", source, output], cancellation);
        return JsonSerializer.Deserialize<PdfEditInfo>(await File.ReadAllTextAsync(output, cancellation), Json)!;
    }
    public static async Task<PdfSourceText[]> TextAsync(string source, string folder, int page, CancellationToken cancellation)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        await RunAsync(["text", source, output, "--page", page.ToString()], cancellation);
        return JsonSerializer.Deserialize<PdfSourceText[]>(await File.ReadAllTextAsync(output, cancellation), Json)!;
    }
    public static async Task<string> RenderAsync(string source, string folder, int page, int rotation, CancellationToken cancellation, PdfEditPlan? plan = null, int maxSize=1800)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".png");
        var arguments = new List<string> { "render", source, output, "--page", page.ToString(), "--rotation", rotation.ToString(),"--max-size",Math.Clamp(maxSize,160,3600).ToString() };
        if (plan != null) {
            var specification = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
            await File.WriteAllTextAsync(specification, JsonSerializer.Serialize(plan, Json), cancellation);
            arguments.AddRange(["--plan", specification]);
        }
        await RunAsync(arguments.ToArray(), cancellation);
        return output;
    }
    public static async Task<string> ExportAsync(string source, string folder, PdfEditPlan plan, CancellationToken cancellation)
    {
        var specification = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".pdf");
        await File.WriteAllTextAsync(specification, JsonSerializer.Serialize(plan, Json), cancellation);
        await RunAsync(["export", source, output, "--plan", specification], cancellation);
        return output;
    }
    public static async Task SaveNewAsync(string source, string destination, CancellationToken cancellation)
    {
        // CreateNew rejects source aliases/hardlinks and all existing output files.
        await using var input = File.OpenRead(source);
        var created = false;
        try {
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                created = true;
                await input.CopyToAsync(output, cancellation);
                await output.FlushAsync(cancellation);
            }
        } catch { if (created) File.Delete(destination); throw; }
    }
    static async Task RunAsync(string[] arguments, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(AppPaths.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding=System.Text.Encoding.UTF8, StandardErrorEncoding=System.Text.Encoding.UTF8 };
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "engines", "pdf_edit.py"));
        foreach (var value in arguments) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new IOException("PDF 편집 엔진 실행 실패");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } await process.WaitForExitAsync(); if (cancellation.IsCancellationRequested) throw; throw new TimeoutException("PDF 편집 처리 시간이 초과됐습니다."); }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) { var text = (await stderr).Trim(); throw new IOException(text.Length == 0 ? "PDF 편집 실패" : text[..Math.Min(1200, text.Length)]); }
    }
}
