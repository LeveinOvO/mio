using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PortForwarder.Core;

namespace PortForwarder.Windows;

public sealed class RealmProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly IntPtr _job;
    private readonly string _config;
    public int Id => _process.Id;
    public bool Running => !_process.HasExited;
    private RealmProcess(Process process, IntPtr job, string config) { _process = process; _job = job; _config = config; }

    public static async Task<RealmProcess> StartAsync(ForwardRule applied, string? executable = null, string? configDirectory = null)
    {
        executable ??= Path.Combine(AppContext.BaseDirectory, "CoreBin", "realm.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 Realm 核心程序，请使用完整发布目录。", executable);
        using (var stream = File.OpenRead(executable))
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals("7c7030c8e40a14dbd11263c22932171ed2170c9e349b76de9351c495f698af6c", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Realm 核心文件校验失败，请使用随软件提供的版本。");
        }
        if (SocketOwners.IsOccupied(applied)) throw new InvalidOperationException("监听地址和端口已被其他程序占用。");
        var file = Path.Combine(configDirectory ?? ConfigurationStore.DirectoryPath, "realm-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, RealmSettings.Serialize(applied));
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
          WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(file);
        // Prevent inherited Realm-specific settings from overriding our generated configuration.
        start.Environment.Remove("REALM_CONF");
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { File.Delete(file); throw new IOException("无法创建 Realm 进程管理对象。"); }
        var info = new JobExtended { Basic = new JobBasic { LimitFlags = 0x2000 } };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<JobExtended>());
        Process? process = null;
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (!SetInformationJobObject(job, 9, memory, (uint)Marshal.SizeOf<JobExtended>())) throw new IOException("无法设置 Realm 退出清理。");
            process = Process.Start(start) ?? throw new IOException("无法启动 Realm。");
            if (!AssignProcessToJobObject(job, process.Handle)) throw new IOException("无法关联 Realm 进程，转发未启动。");
            _ = DrainAsync(process.StandardOutput); _ = DrainAsync(process.StandardError);
            for (var i = 0; i < 30; i++)
            {
                if (process.HasExited) throw new IOException("Realm 启动后退出，请检查监听地址和配置。");
                if (SocketOwners.HasListeners(applied, process.Id)) return new RealmProcess(process, job, file);
                await Task.Delay(100);
            }
            throw new IOException("Realm 未能建立监听端口，转发未启动。");
        }
        catch
        {
            if (process is not null) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch { } process.Dispose(); }
            CloseHandle(job); File.Delete(file); throw;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        CloseHandle(_job); // KILL_ON_JOB_CLOSE also cleans children after GUI crash/termination.
        if (!_process.HasExited) await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // IP Helper tables can lag process termination briefly; permit socket teardown before reuse.
        await Task.Delay(100);
        _process.Dispose();
        if (File.Exists(_config)) File.Delete(_config);
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasic
    { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtended
    { public JobBasic Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
