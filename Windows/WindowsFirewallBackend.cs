using System.Globalization;
using System.Runtime.InteropServices;
using PortForwarder.Core;

namespace PortForwarder.Windows;

// Windows Defender Firewall COM API; no shell interpolation, global policy edits or extra runtime.
public sealed class WindowsFirewallBackend : IFirewallBackend
{
    public IReadOnlyList<FirewallSnapshot> Find(string name)
        => FindMany([name]).GetValueOrDefault(name) ?? [];

    public Dictionary<string, IReadOnlyList<FirewallSnapshot>> FindMany(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var found = wanted.ToDictionary(name => name, _ => new List<FirewallSnapshot>(), StringComparer.Ordinal);
        if (wanted.Count == 0) return [];
        dynamic policy = Create("HNetCfg.FwPolicy2");
        object? rulesObject = null;
        try
        {
            rulesObject = policy.Rules;
            foreach (dynamic rule in (dynamic)rulesObject)
            {
                try
                {
                    var name = (string)rule.Name;
                    if (wanted.Contains(name)) found[name].Add(Snapshot(rule));
                }
                finally { Release(rule); }
            }
            return found.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<FirewallSnapshot>)pair.Value, StringComparer.Ordinal);
        }
        finally { Release(rulesObject); Release(policy); }
    }

    public FirewallSnapshot Put(string name, FirewallScope scope, bool enabled)
    {
        FirewallValidation.Validate(scope);
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;
        object? ruleObject = null;
        try
        {
            var exists = Find(name).Count != 0;
            ruleObject = exists ? rules.Item(name) : Create("HNetCfg.FWRule");
            dynamic rule = ruleObject;
            // Never broaden an enabled rule while changing its address/port restrictions.
            ConfigureDraft(ruleObject, name, scope);
            if (!exists)
            {
                try { rules.Add(rule); }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"防火墙规则创建失败（0x{error.HResult:X8}）：{error.Message}", error);
                }
            }
            try { rule.Enabled = enabled; }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Windows 防火墙切换启用状态失败（0x{error.HResult:X8}）：{error.Message}", error);
            }
            try { return Snapshot(rule); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"防火墙规则已保存，但无法读取状态（0x{error.HResult:X8}），请刷新后检查：{error.Message}", error);
            }
        }
        finally { Release(ruleObject); Release(rules); Release(policy); }
    }

    internal static void ConfigureDraft(object ruleObject, string name, FirewallScope scope)
    {
        dynamic rule = ruleObject;
        void Set(string field, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Windows 防火墙属性 {field} 设置失败（0x{error.HResult:X8}）：{error.Message}", error);
            }
        }
        Set("Enabled", () => rule.Enabled = false);
        Set("Name", () => rule.Name = name);
        Set("Grouping", () => rule.Grouping = FirewallOwnership.Group);
        Set("Description", () => rule.Description = $"PortForwarder 的 {scope.ProtocolText} 入站规则。");
        Set("Protocol", () => rule.Protocol = scope.ProtocolNumber);
        Set("Direction", () => rule.Direction = 1);
        Set("Action", () => rule.Action = 1);
        Set("LocalAddresses", () => rule.LocalAddresses = scope.LocalAddresses);
        Set("LocalPorts", () => rule.LocalPorts = scope.ListenPort.ToString(CultureInfo.InvariantCulture));
        Set("RemoteAddresses", () => rule.RemoteAddresses = scope.RemoteAddresses);
        Set("RemotePorts", () => rule.RemotePorts = "*");
        Set("Profiles", () => rule.Profiles = (int)scope.Profiles);
        Set("EdgeTraversal", () => rule.EdgeTraversal = false);
        // These optional restrictions are deliberately left unset on a port rule.
        // Assigning an empty BSTR is not equivalent to omitting an optional field,
        // and may fail the Windows rule collection's validation on Add.
        // Existing rules are already ownership-checked to have no such restrictions.
        Set("InterfaceTypes", () => rule.InterfaceTypes = "All");
    }

    public void Remove(string name)
    {
        dynamic policy = Create("HNetCfg.FwPolicy2");
        dynamic rules = policy.Rules;
        try { rules.Remove(name); }
        finally { Release(rules); Release(policy); }
    }

    internal static FirewallSnapshot Snapshot(dynamic rule)
    {
        object? interfaces = rule.Interfaces;
        var interfaceText = interfaces is Array array ? string.Join(",", array.Cast<object>().Select(Convert.ToString).Order()) : "";
        return new FirewallSnapshot(rule.Name, rule.Grouping ?? "", rule.Description ?? "", rule.Protocol, rule.Direction,
            rule.Action, rule.Enabled, rule.LocalAddresses ?? "", rule.LocalPorts ?? "", rule.RemoteAddresses ?? "",
            rule.RemotePorts ?? "", rule.Profiles, rule.EdgeTraversal, rule.ApplicationName ?? "", rule.ServiceName ?? "",
            rule.InterfaceTypes ?? "", interfaceText, ""); // ICMP fields are not valid on TCP rules.
    }

    private static object Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)!;
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
