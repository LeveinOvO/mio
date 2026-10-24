using System.Runtime.InteropServices;
using PortForwarder.Core;
using PortForwarder.Windows;

internal static class FirewallDraftHelper
{
    public static FirewallSnapshot Verify(FirewallScope scope)
    {
        FirewallValidation.Validate(scope);
        var draft = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
        try
        {
            WindowsFirewallBackend.ConfigureDraft(draft, "PortForwarder.UnsavedTest", scope);
            return WindowsFirewallBackend.Snapshot(draft);
        }
        finally { Marshal.FinalReleaseComObject(draft); }
    }
}
