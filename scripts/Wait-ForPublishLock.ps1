param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$files = @(
    (Join-Path $PublishDirectory 'Mio.exe'),
    (Join-Path $PublishDirectory 'Mio.Host.exe'),
    (Join-Path $PublishDirectory 'CoreBin\realm.exe')
)

while ($true) {
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and ($files -contains $_.ExecutablePath)
    })
    if ($running.Count -eq 0) { exit 0 }

    $names = ($running | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }) -join "`n"
    $message = "The following process(es) still use files in the publish directory:`n`n$names`n`nClose them, then click OK to check again."
    [System.Windows.Forms.MessageBox]::Show(
        $message,
        'PortForwarder - Files in use',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    ) | Out-Null
}
