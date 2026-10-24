using System.Net.NetworkInformation;
using System.Net.Sockets;
using PortForwarder.Core;

namespace PortForwarder.Services;

public sealed record ListenAddressOption(string Address, string Name)
{
    public string DisplayName => $"{Name} · {Address}";
    public override string ToString() => DisplayName;
}

internal static class NetworkAddresses
{
    public static string ResolveEditorAddress(string? text, IReadOnlyList<ListenAddressOption> options)
    {
        var input = text?.Trim() ?? "";
        var option = options.FirstOrDefault(o => o.DisplayName == input);
        // Resolve only a known full label. Otherwise validate the visible input as an IP;
        // never fall back to SelectedItem after the user has manually changed the text.
        return RuleValidation.NormalizeIPv4(option?.Address ?? input);
    }

    public static IReadOnlyList<ListenAddressOption> PrepareForEdit(IReadOnlyList<ListenAddressOption> addresses,
        string? originalAddress)
    {
        var options = addresses.ToList();
        if (!string.IsNullOrEmpty(originalAddress) && !options.Any(o => o.Address == originalAddress))
            options.Insert(0, new ListenAddressOption(originalAddress, "当前规则地址"));
        return options;
    }

    public static IReadOnlyList<ListenAddressOption> Read()
    {
        var options = new List<ListenAddressOption>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderByDescending(a => a.OperationalStatus == OperationalStatus.Up).ThenBy(a => a.Name))
        {
            foreach (var address in adapter.GetIPProperties().UnicastAddresses
                         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                options.Add(new ListenAddressOption(address.Address.ToString(),
                    adapter.Name + (adapter.OperationalStatus == OperationalStatus.Up ? "" : "（未连接）")));
        }
        options.Add(new ListenAddressOption("0.0.0.0", "所有 IPv4 接口"));
        return options;
    }
}
