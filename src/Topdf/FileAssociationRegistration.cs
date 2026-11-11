using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Topdf;

public sealed record AssociationEntry(string Key, string Name, string Value);
public static class FileAssociationRegistration
{
    static readonly string[] Extensions = [".hwp", ".hwpx", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".dwg", ".dxf", ".pdf"];
    public static IReadOnlyList<AssociationEntry> BuildEntries(string executable)
    {
        executable = Path.GetFullPath(executable);
        var command = $"\"{executable}\" \"%1\"";
        var entries = new List<AssociationEntry> {
            new(@"Applications\Topdf.exe", "FriendlyAppName", "TOPDF"),
            new(@"Applications\Topdf.exe\shell\open\command", "", command),
            new(@"TOPDF.Document", "", "TOPDF 문서 미리보기"),
            new(@"TOPDF.Document", "FriendlyTypeName", "TOPDF 문서 미리보기"),
            new(@"TOPDF.Document\DefaultIcon", "", $"\"{executable}\",0"),
            new(@"TOPDF.Document\shell\open\command", "", command)
        };
        foreach (var extension in Extensions) {
            entries.Add(new(@"Applications\Topdf.exe\SupportedTypes", extension, ""));
            entries.Add(new(extension + @"\OpenWithProgids", "TOPDF.Document", ""));
        }
        return entries;
    }
    public static void RegisterCurrentUser(string executable)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("앱 실행 파일을 찾을 수 없습니다.", executable);
        // Opt-in Open With registration only. Never change UserChoice or extension default values.
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        foreach (var entry in BuildEntries(executable)) {
            using var key = classes.CreateSubKey(entry.Key);
            key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
        }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
