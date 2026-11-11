using System.IO;
using Topdf;
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/smoke");
Directory.CreateDirectory(root);
var input = Path.Combine(root, "sample.rtf");
await File.WriteAllTextAsync(input, @"{\rtf1\ansi TOPDF smoke test\par Table and Korean quality require real document review.}");
var service = new ConversionService();
var pdf = await service.ConvertAsync(input, root, CancellationToken.None, useCompatibilityEngines: true);
if (!File.Exists(pdf)) throw new Exception("Missing output");
var second = await service.ConvertAsync(input, root, CancellationToken.None, useCompatibilityEngines: true);
if (pdf == second) throw new Exception("Job output collision");
if (await service.ConvertAsync(pdf, root, CancellationToken.None) != pdf) throw new Exception("PDF passthrough failed");
try { await service.ConvertAsync(Path.Combine(root, "missing.docx"), root, CancellationToken.None); throw new Exception("Missing input accepted"); } catch (FileNotFoundException) { }
if (!ConversionService.Supports("test.hwp") || !ConversionService.Supports("test.hwpx") || !ConversionService.Supports("test.dwg") || !ConversionService.Supports("test.dxf") || ConversionService.Supports("test.unsupported")) throw new Exception("Format routing failed");
Console.WriteLine("PASS: real RTF conversion, unique job outputs, PDF passthrough, missing source, unsupported format routing.");
Console.WriteLine(pdf);
