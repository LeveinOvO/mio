using System.Text.Json;

namespace PortForwarder.Core;

public sealed record RealmRule(Guid Id, ForwardRule Desired, ForwardRule Applied, bool Enabled,
    string Message = "", DateTimeOffset? LastChecked = null);
public sealed record RealmConfiguration(int Version, List<RealmRule> Rules)
{
    public static RealmConfiguration Empty() => new(1, []);
}

public sealed record RealmRuntimeState(RealmRule Rule, bool Running, string Error = "");

public static class RealmSettings
{
    public static string Serialize(ForwardRule applied)
    {
        RuleValidation.Validate(applied);
        RuleValidation.NormalizeIPv4(applied.ConnectAddress);
        return JsonSerializer.Serialize(new
        {
            log = new { level = "warn" },
            network = new { no_tcp = !applied.UsesTcp, use_udp = applied.UsesUdp, udp_timeout = 30 },
            endpoints = new[] { new { listen = applied.ListenEndpoint, remote = applied.ConnectEndpoint } }
        });
    }
}
