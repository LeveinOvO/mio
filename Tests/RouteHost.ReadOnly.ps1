param([Parameter(Mandatory=$true)][string]$HostPath)
$ErrorActionPreference='Stop'
$folder=Join-Path (Split-Path $HostPath) ('route-read-test-'+[guid]::NewGuid().ToString('N'))
$name='PortForwarder.RouteRead.'+[guid]::NewGuid().ToString('N')
$process=Start-Process -FilePath $HostPath -ArgumentList "--headless --config-directory `"$folder`" --pipe $name" -PassThru
function Request($command) {
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$name,[IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $utf=[Text.UTF8Encoding]::new($false)
        $writer=[IO.StreamWriter]::new($pipe,$utf,1024,$true)
        $reader=[IO.StreamReader]::new($pipe,$utf,$false,1024,$true)
        $writer.WriteLine('{"Command":"'+$command+'"}');$writer.Flush()
        $response=$reader.ReadLine() | ConvertFrom-Json
        $pipe.WriteByte(1);$pipe.Flush()
        if(!$response.Success){throw $response.Message}
        return $response
    }finally{$pipe.Dispose()}
}
try {
    $response=Request 'Routes'
    if($response.Data.Routes.Count -lt 1 -or $response.Data.Interfaces.Count -lt 1){throw 'Empty route read result'}
    Write-Output "PASS: Native pipe -> PowerShell -> Windows: $($response.Data.Routes.Count) routes, $($response.Data.Interfaces.Count) interfaces. Read-only."
    $reply=Request 'State'
    if($reply.Data.Rules.Count -ne 0){throw 'Isolated host unexpectedly contains forwarding rules'}
    Write-Output 'PASS: Forwarding state remains available after route query.'
}finally{
    if(!$process.HasExited){$null=Request 'Shutdown';if(!$process.WaitForExit(15000)){throw 'Host did not exit normally'}}
    if(Test-Path $folder){Remove-Item -LiteralPath $folder -Recurse -Force}
}
