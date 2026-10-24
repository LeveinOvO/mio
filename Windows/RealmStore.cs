using System.Text.Json;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class RealmStore
{
    private static string ConfigPath => Path.Combine(ConfigurationStore.DirectoryPath, "realm-rules.json");
    public static RealmConfiguration Read()
    {
        if (!File.Exists(ConfigPath)) return RealmConfiguration.Empty();
        if ((File.GetAttributes(ConfigPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(ConfigPath).Length > 2 * 1024 * 1024)
            throw new IOException("Realm 配置文件无效。");
        using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var config = JsonSerializer.Deserialize<RealmConfiguration>(stream) ?? throw new IOException("Realm 配置损坏。");
        if (config.Version != 1 || config.Rules is null || config.Rules.Count > 1000 ||
            config.Rules.Select(r => r.Id).Distinct().Count() != config.Rules.Count ||
            config.Rules.Select(r => r.Desired.ListenerKey).Distinct().Count() != config.Rules.Count)
            throw new IOException("Realm 配置内容无效。");
        foreach (var entry in config.Rules)
        {
            RuleValidation.Validate(entry.Desired); RuleValidation.Validate(entry.Applied);
            if (entry.Id == Guid.Empty || !entry.Desired.HasSameListener(entry.Applied) ||
                entry.Desired.ConnectPort != entry.Applied.ConnectPort) throw new IOException("Realm 目标配置不一致。");
            RuleValidation.NormalizeIPv4(entry.Applied.ConnectAddress);
        }
        return config;
    }
    public static void Save(RealmConfiguration config)
    {
        if (File.Exists(ConfigPath) && (File.GetAttributes(ConfigPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Realm 配置不能是链接。");
        var temp = Path.Combine(ConfigurationStore.DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, config); stream.Flush(true); }
            File.Move(temp, ConfigPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
