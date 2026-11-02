using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Topdf;

/// <summary>Normalizes display-only footer color codes that MiniPdf currently prints as text.</summary>
public static class XlsxInputAdapter
{
    public static string Prepare(string source, string jobDirectory)
    {
        var copy = Path.Combine(jobDirectory, "input.xlsx");
        File.Copy(source, copy, overwrite: false);
        using var zip = ZipFile.Open(copy, ZipArchiveMode.Update);
        foreach (var entry in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$")).ToArray()) {
            XDocument xml;
            using (var stream = entry.Open()) xml = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var ns = xml.Root!.Name.Namespace;
            var uncached = xml.Descendants(ns + "c").FirstOrDefault(c => c.Element(ns + "f") != null &&
                (c.Element(ns + "v") == null || (string.IsNullOrWhiteSpace(c.Element(ns + "v")?.Value) && (string?)c.Attribute("t") != "str")));
            if (uncached != null)
                throw new InvalidDataException($"XLSX 수식 셀 {uncached.Attribute("r")?.Value}에 저장된 계산 결과가 없습니다. 수식을 계산해 저장한 파일로 다시 시도해 주세요. 빈 금액으로 출력하지 않도록 변환을 중단했습니다.");
            var changed = false;
            foreach (var item in xml.Descendants().Where(e => new[] { "oddFooter", "evenFooter", "firstFooter", "oddHeader", "evenHeader", "firstHeader" }.Contains(e.Name.LocalName))) {
                var value = Regex.Replace(item.Value, @"(?<!&)&K(?:[0-9A-Fa-f]{6}|\d{2}[+-]\d{3})", "");
                if (value != item.Value) { item.Value = value; changed = true; }
            }
            if (!changed) continue;
            var name = entry.FullName;
            entry.Delete();
            var replacement = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var output = replacement.Open();
            xml.Save(output, SaveOptions.DisableFormatting);
        }
        return copy;
    }
}
