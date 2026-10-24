using System.Runtime.InteropServices;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public static class SocketOwners
{
    public static bool HasListeners(ForwardRule rule, int processId) =>
        (!rule.UsesTcp || Read(tcp: true).Any(r => r.Port == rule.ListenPort && r.Pid == processId && r.Address == rule.ListenAddress)) &&
        (!rule.UsesUdp || Read(tcp: false).Any(r => r.Port == rule.ListenPort && r.Pid == processId && r.Address == rule.ListenAddress));

    public static bool IsOccupied(ForwardRule rule) =>
        (rule.UsesTcp && Read(true).Any(r => Overlaps(r, rule))) || (rule.UsesUdp && Read(false).Any(r => Overlaps(r, rule)));

    private static bool Overlaps(SocketRow r, ForwardRule rule) => r.Port == rule.ListenPort &&
        (r.Address == "0.0.0.0" || rule.ListenAddress == "0.0.0.0" || r.Address == rule.ListenAddress);
    private sealed record SocketRow(string Address, int Port, int Pid);

    private static List<SocketRow> Read(bool tcp)
    {
        var size = 0;
        var code = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 3, 0) : GetExtendedUdpTable(IntPtr.Zero, ref size, true, 2, 1, 0);
        if (code != 122 && code != 0) throw new IOException("无法查询 Windows 监听端口。");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            code = tcp ? GetExtendedTcpTable(buffer, ref size, true, 2, 3, 0) : GetExtendedUdpTable(buffer, ref size, true, 2, 1, 0);
            if (code != 0) throw new IOException("无法查询 Windows 监听端口。");
            var count = Marshal.ReadInt32(buffer);
            var result = new List<SocketRow>();
            var stride = tcp ? 24 : 12;
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * stride);
                if (tcp && Marshal.ReadInt32(row) != 2) continue; // LISTEN only, never established/TIME_WAIT.
                var addrOffset = tcp ? 4 : 0;
                var portOffset = tcp ? 8 : 4;
                var bytes = BitConverter.GetBytes(Marshal.ReadInt32(row, addrOffset));
                var address = string.Join('.', bytes);
                var port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                var pid = Marshal.ReadInt32(row, tcp ? 20 : 8);
                result.Add(new SocketRow(address, port, pid));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")] private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
