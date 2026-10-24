param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference='Stop'
try {
    $Directory=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $guiPath=Join-Path $Directory 'Mio.exe'
    $hostPath=Join-Path $Directory 'Mio.Host.exe'
    $realmPath=Join-Path $Directory 'CoreBin\realm.exe'
    function Instances {
        @(Get-CimInstance Win32_Process | Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath -in @($guiPath,$hostPath,$realmPath)
        })
    }
    $running=@(Instances)
    if(!$running.Count){exit 0}
    $session=(Get-Process -Id $PID).SessionId
    if(@($running | Where-Object SessionId -ne $session).Count){throw 'Application runs in another session; close it there first.'}
    $hadGui=@($running | Where-Object ExecutablePath -eq $guiPath).Count -gt 0
    $hosts=@($running | Where-Object ExecutablePath -eq $hostPath)
    if($hosts.Count -gt 1){throw 'Multiple host instances; close them first.'}
    if($hosts.Count){
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class UpgradePipe {
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint pid);
}
'@
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','Mio.Native.'+$sid,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(5000)
            [uint32]$server=0
            if(![UpgradePipe]::GetNamedPipeServerProcessId($pipe.SafePipeHandle,[ref]$server) -or $server -ne $hosts[0].ProcessId){throw 'Host identity mismatch; shutdown refused.'}
            $utf=[Text.UTF8Encoding]::new($false)
            $writer=[IO.StreamWriter]::new($pipe,$utf,1024,$true)
            $reader=[IO.StreamReader]::new($pipe,$utf,$false,1024,$true)
            $writer.WriteLine('{"Command":"Shutdown"}');$writer.Flush()
            $read=$reader.ReadLineAsync()
            if(!$read.Wait(20000)){throw 'Host shutdown response timed out.'}
            $response=$read.Result | ConvertFrom-Json
            $pipe.WriteByte(1);$pipe.Flush()
            if(!$response.Success){throw $response.Message}
        } finally { $pipe.Dispose() }
    } else {
        foreach($gui in @($running | Where-Object ExecutablePath -eq $guiPath)){
            $p=Get-Process -Id $gui.ProcessId
            $null=$p.CloseMainWindow()
        }
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        if(!@(Instances).Count){if($hadGui){exit 11}else{exit 10}}
        Start-Sleep -Milliseconds 300
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'Application did not exit normally; update cancelled. No process was forcibly terminated.'
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    try { [IO.File]::WriteAllText((Join-Path $env:TEMP 'Mio-Upgrade-error.txt'), $_.Exception.ToString()) } catch {}
    exit 2
}
