using System.Net;
using System.Net.Sockets;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public sealed class SystemTargetResolver : ITargetResolver
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, timeout.Token);
            return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("DNS 查询超时。"); }
        catch (SocketException) { throw new InvalidOperationException("无法解析目标域名。"); }
    }
}
