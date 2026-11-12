using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Topdf;

public sealed record PdfEditPageInfo(int SourceIndex, double Width, double Height)
{
    public PdfPageNote[] Notes { get; init; } = [];
}
public sealed record PdfPageNote(string Text,string Author);
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
    public bool FillUsesStrokeColor { get; set; }
    public double FillOpacity { get; set; } = 1;
    public string TextBoxFill { get; set; } = "none";
    public string TextBoxBorderColor { get; set; } = "none";
    public double TextBoxBorderWidth { get; set; } = 1;
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

public static partial class PdfEditService
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static PdfEditPlan Clone(PdfEditPlan plan) => JsonSerializer.Deserialize<PdfEditPlan>(JsonSerializer.Serialize(plan, Json), Json)!;
    public static async Task<PdfEditInfo> InspectAsync(string source, string folder, CancellationToken cancellation)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        try {
            await RunAsync(["inspect", source, output], cancellation);
            return JsonSerializer.Deserialize<PdfEditInfo>(await File.ReadAllTextAsync(output, cancellation), Json)
                ?? throw new InvalidDataException("PDF 페이지 정보를 읽을 수 없습니다.");
        } finally { TryRemoveTemporaryFile(output); }
    }
    public static async Task<PdfSourceText[]> TextAsync(string source, string folder, int page, CancellationToken cancellation, bool includeInvisible=false)
    {
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        var arguments=new List<string>{"text",source,output,"--page",page.ToString()};
        if(includeInvisible)arguments.Add("--include-invisible");
        try {
            await RunAsync(arguments.ToArray(),cancellation);
            return JsonSerializer.Deserialize<PdfSourceText[]>(await File.ReadAllTextAsync(output, cancellation), Json)
                ?? throw new InvalidDataException("PDF 텍스트 정보를 읽을 수 없습니다.");
        } finally { TryRemoveTemporaryFile(output); }
    }
    public static Task<string> RenderAsync(string source, string folder, int page, int rotation, CancellationToken cancellation, PdfEditPlan? plan = null, int maxSize=1800) =>
        RenderCoreAsync(source,folder,page,rotation,cancellation,plan,maxSize,null,null);

    internal static Task<string> RenderViewportAsync(string source, string folder, int page, int rotation, CancellationToken cancellation, PdfEditPlan? plan, (double Pixels, System.Windows.Rect Bounds) region, PdfViewportWorker worker) =>
        RenderCoreAsync(source,folder,page,rotation,cancellation,plan,1800,region,worker);

    static async Task<string> RenderCoreAsync(string source, string folder, int page, int rotation, CancellationToken cancellation, PdfEditPlan? plan, int maxSize, (double Pixels, System.Windows.Rect Bounds)? region, PdfViewportWorker? worker)
    {
        var replacements=plan?.Pages.FirstOrDefault(item=>item.SourceIndex==page)?.Elements.Where(item=>item.Kind=="replace").ToList();
        var direct=region.HasValue && replacements is not {Count:>0};
        var output=Path.Combine(folder,Guid.NewGuid().ToString("N")+(direct?".bmp":".png"));
        var arguments = new List<string> { "render", source, output, "--page", page.ToString(), "--rotation", rotation.ToString(),"--max-size",Math.Clamp(maxSize,160,4200).ToString() };
        if(region is {} patch) {
            var bounds=patch.Bounds;
            var coordinates=new[] { patch.Pixels, bounds.X, bounds.Y, bounds.Width, bounds.Height }
                .Select(value=>value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            if(direct)arguments=[source,output,page.ToString(),rotation.ToString(),.. coordinates];
            else arguments.AddRange(["--region",.. coordinates]);
        }
        string? specification=null;
        try {
            if(replacements is {Count:>0}) {
                specification=Path.Combine(folder,Guid.NewGuid()+".json");
                var renderPlan=new PdfEditPlan{SourceSha256=plan!.SourceSha256,Pages=[new(){SourceIndex=page,Elements=replacements}]};
                await File.WriteAllTextAsync(specification,JsonSerializer.Serialize(renderPlan,Json),cancellation);
                arguments.AddRange(["--plan",specification]);
            }
            if(direct && worker!=null) {
                try { await worker.RenderAsync(arguments,cancellation); }
                catch(Exception error) when(error is IOException or TimeoutException or System.ComponentModel.Win32Exception) {
                    cancellation.ThrowIfCancellationRequested();
                    TryRemoveTemporaryFile(output);
                    await RunAsync(arguments.ToArray(),cancellation,"pdf_viewport.py");
                }
            } else await RunAsync(arguments.ToArray(),cancellation,direct?"pdf_viewport.py":"pdf_edit.py");
        } catch {
            TryRemoveTemporaryFile(output);
            throw;
        } finally {
            if(specification!=null)TryRemoveTemporaryFile(specification);
        }
        return output;
    }
    public static async Task<string> ExportAsync(string source, string folder, PdfEditPlan plan, CancellationToken cancellation)
    {
        var specification = Path.Combine(folder, Guid.NewGuid().ToString("N")+".json");
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N")+".pdf");
        try {
            await File.WriteAllTextAsync(specification, JsonSerializer.Serialize(plan, Json), cancellation);
            await RunAsync(["export", source, output, "--plan", specification], cancellation);
            return output;
        } catch {
            TryRemoveTemporaryFile(output);
            throw;
        } finally { TryRemoveTemporaryFile(specification); }
    }
    public static async Task<string> AppendPagesAsync(string source,string extra,string folder,CancellationToken cancellation)
    {
        var output=Path.Combine(folder,Guid.NewGuid()+".pdf");
        try {
            await RunAsync(["append",source,output,"--append",extra],cancellation);
            return output;
        } catch {
            if(File.Exists(output))File.Delete(output);
            throw;
        }
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
    static void TryRemoveTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch(IOException) {}
        catch(UnauthorizedAccessException) {}
    }

    static async Task RunAsync(string[] arguments, CancellationToken cancellation, string worker = "pdf_edit.py")
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(AppPaths.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding=System.Text.Encoding.UTF8, StandardErrorEncoding=System.Text.Encoding.UTF8 };
        info.Environment["PYTHONUTF8"] = "1";
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "engines", worker));
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
