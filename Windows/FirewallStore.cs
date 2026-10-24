using System.Text.Json;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class FirewallStore
{
    private static string ConfigPath => Path.Combine(ConfigurationStore.DirectoryPath, "firewall-rules.json");

    public static FirewallConfiguration Read()
    {
        if (!File.Exists(ConfigPath)) return FirewallConfiguration.Empty();
        if ((File.GetAttributes(ConfigPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(ConfigPath).Length > 2 * 1024 * 1024)
            throw new IOException("防火墙配置文件不安全或过大。");
        using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var config = JsonSerializer.Deserialize<FirewallConfiguration>(stream) ?? throw new IOException("防火墙配置损坏。");
        if (config.Version != 1 || config.Rules is null || config.Rules.Count > 1000 ||
            config.Rules.Select(r => r.Id).Distinct().Count() != config.Rules.Count ||
            config.Rules.Select(r => r.Scope.ListenerKey).Distinct().Count() != config.Rules.Count)
            throw new IOException("防火墙配置版本或内容无效。");
        foreach (var rule in config.Rules)
        {
            FirewallValidation.Validate(rule.Scope);
            if (rule.Id == Guid.Empty || rule.Expected is not null &&
                (rule.Expected.Name != FirewallOwnership.Name(rule.Id) || rule.Expected.Group != FirewallOwnership.Group))
            throw new IOException("防火墙规则记录无效，请检查防火墙配置文件。");
        }
        return config;
    }

    public static void Save(FirewallConfiguration config)
    {
        if (File.Exists(ConfigPath) && (File.GetAttributes(ConfigPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("防火墙配置文件位置无效，请检查配置目录。");
        var temp = Path.Combine(ConfigurationStore.DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config);
                stream.Flush(true);
            }
            File.Move(temp, ConfigPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
