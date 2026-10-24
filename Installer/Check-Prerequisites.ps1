$ErrorActionPreference='Stop'
try {
    if(![Environment]::Is64BitProcess){throw '64-bit prerequisite checker required.'}
    $build=[int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
    if($build -lt 19044){throw 'Windows build 19044 or newer is required.'}
    $installType=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').InstallationType
    if($installType -eq 'Server Core'){throw 'Windows Desktop Experience is required.'}
    if(!(Test-Path "$env:WINDIR\System32\vcruntime140.dll")){throw 'Install Microsoft Visual C++ 2015-2022 Redistributable (x64) from https://aka.ms/vs/17/release/vc_redist.x64.exe then retry.'}
    if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1){throw 'Windows PowerShell 5.1 required.'}
    Import-Module NetTCPIP -ErrorAction Stop
    $null=Get-CimClass -Namespace root/StandardCimv2 -ClassName MSFT_NetRoute
    foreach($service in 'BFE','MpsSvc'){
        if((Get-Service $service).Status -ne 'Running'){throw "Required Windows firewall service is not running: $service"}
    }
    $policy=New-Object -ComObject HNetCfg.FwPolicy2
    $null=$policy.Rules.Count
    exit 0
} catch { [Console]::Out.WriteLine($_.Exception.Message); exit 2 }
