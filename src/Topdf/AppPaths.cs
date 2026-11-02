using System.IO;
namespace Topdf;
public static class AppPaths
{
    public static string DataRoot => Path.GetFullPath(Environment.GetEnvironmentVariable("TOPDF_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TOPDF"));
    public static string Cache => Path.Combine(DataRoot, "cache");
    public static string Viewer => Path.Combine(DataRoot, "viewer-data");
    public static string Python => Environment.GetEnvironmentVariable("TOPDF_PYTHON") ?? (File.Exists(BundledPython) ? BundledPython : "python");
    static string BundledPython => Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
}
