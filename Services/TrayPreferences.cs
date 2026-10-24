using System.Text.Json;

namespace PortForwarder.Services;

public sealed record TrayPreferences(bool KeepInTray = true)
{
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data");
    private static string FilePath => Path.Combine(DirectoryPath, "tray-preferences.json");

    public static TrayPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 4096) return new();
            return JsonSerializer.Deserialize<TrayPreferences>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0 ||
            (File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("托盘设置保存位置无效，请检查设置目录。");
        var temp = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, this);
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
