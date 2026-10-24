using System.Security.AccessControl;
using System.Security.Principal;

namespace PortForwarder.Windows;

public static class ConfigurationStore
{
    internal static string? TestDirectory { get; set; }
    public static string DirectoryPath => TestDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");

    // Creates a machine-wide administrator-owned store; ordinary users may only read it.
    public static void EnsureProtectedDirectory()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("配置写入需要管理员权限。");
        if (Directory.Exists(DirectoryPath))
        {
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("配置目录位置无效，请使用普通文件夹。");
            // Never adopt an ordinary-user-owned directory into an elevated configuration store.
            var owner = new DirectoryInfo(DirectoryPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is not ("S-1-5-32-544" or "S-1-5-18"))
                throw new UnauthorizedAccessException("配置目录权限不正确，请由管理员修复目录权限。");
        }
        var security = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.SetOwner(admins);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { admins, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(DirectoryPath)) FileSystemAclExtensions.CreateDirectory(security, DirectoryPath);
        else new DirectoryInfo(DirectoryPath).SetAccessControl(security);
        foreach (var path in new[] { Path.Combine(DirectoryPath, "write.lock"), Path.Combine(DirectoryPath, "firewall-rules.json"),
                     Path.Combine(DirectoryPath, "realm-rules.json") })
        {
            if (!File.Exists(path)) continue;
            RejectLink(path);
            var fileAcl = new FileInfo(path).GetAccessControl();
            var owner = fileAcl.GetOwner(typeof(SecurityIdentifier))?.Value;
            if (owner is not ("S-1-5-32-544" or "S-1-5-18"))
                throw new UnauthorizedAccessException("配置文件权限不正确，请由管理员修复文件权限。");
            foreach (FileSystemAccessRule rule in fileAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var mutating = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutating) != 0 &&
                    rule.IdentityReference.Value is not ("S-1-5-32-544" or "S-1-5-18"))
                    throw new UnauthorizedAccessException("配置文件写权限过宽，请由管理员设为仅管理员可写。");
            }
        }
    }

    public static async Task<FileStream> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        EnsureProtectedDirectory();
        var path = Path.Combine(DirectoryPath, "write.lock");
        RejectLink(path);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { await Task.Delay(100, cancellationToken); }
        }
        throw new IOException("配置正在保存，请稍后重试。");
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("配置文件位置无效，请使用普通文件。");
    }
}
