param([string]$ScriptPath = (Join-Path $PSScriptRoot '..\NativeHost\Routes.ps1'))
$ErrorActionPreference='Stop'
. $ScriptPath -LibraryOnly
$script:rows=@(); $script:writes=0; $script:failAdd=$false; $script:checks=0
function Check($condition,$message) { if(!$condition){throw "FAIL: $message"}; $script:checks++; Write-Output "PASS: $message" }
function Reject([scriptblock]$action,$message) { $caught=$false;try{& $action | Out-Null}catch{$caught=$true}; Check $caught $message }
# These functions shadow Windows cmdlets; tests never invoke actual route writes.
function Get-NetIPInterface { param($AddressFamily,$ErrorAction) [pscustomobject]@{ InterfaceIndex=7; InterfaceAlias='Test NIC'; InterfaceMetric=25; ConnectionState='Connected' } }
function Get-NetIPAddress { param($AddressFamily,$ErrorAction) [pscustomobject]@{ InterfaceIndex=7; IPAddress='192.168.2.20'; PrefixLength=24; AddressState='Preferred'; SkipAsSource=$false } }
function Get-NetRoute { param($AddressFamily,$PolicyStore,$ErrorAction) @($script:rows | Where-Object Store -eq $PolicyStore) }
function New-NetRoute {
    param($DestinationPrefix,$InterfaceIndex,$NextHop,$RouteMetric,$AddressFamily,$Protocol,$PolicyStore,$ErrorAction)
    if($script:failAdd){$script:failAdd=$false;throw 'Simulated create failure'}
    $script:writes++
    $stores=if($PolicyStore){@($PolicyStore)}else{@('ActiveStore','PersistentStore')}
    foreach($store in $stores){
        if(@($script:rows | Where-Object { $_.Store -eq $store -and $_.DestinationPrefix -eq $DestinationPrefix -and $_.NextHop -eq $NextHop -and $_.InterfaceIndex -eq $InterfaceIndex }).Count){continue}
        $script:rows += [pscustomobject]@{ Store=$store; DestinationPrefix=$DestinationPrefix; InterfaceIndex=$InterfaceIndex; InterfaceAlias='Test NIC'; NextHop=$NextHop; RouteMetric=$RouteMetric; Protocol=$Protocol; Publish='No'; ValidLifetime=[timespan]::MaxValue }
    }
}
function Remove-NetRoute {
    param([Parameter(Mandatory=$true)]$InputObject,[switch]$Confirm)
    process { $script:writes++; $script:rows=@($script:rows | Where-Object { !($_.Store -eq $InputObject.Store -and $_.DestinationPrefix -eq $InputObject.DestinationPrefix -and $_.InterfaceIndex -eq $InputObject.InterfaceIndex -and $_.NextHop -eq $InputObject.NextHop) }) }
}
function Draft($persistent=$false,$metric=10) { [pscustomobject]@{DestinationPrefix='192.0.2.0/24'; NextHop='192.168.2.1'; InterfaceIndex=7; RouteMetric=$metric; Persistent=$persistent} }
function Request($op,$old,$new) { Invoke-RouteRequest ([pscustomobject]@{Command='RouteOperation';Operation=$op;Expected=$old;Updated=$new}) }
Check ((Read-RouteInventory).Routes.Count -eq 0) 'Empty inventory is valid'
$auto=Draft
$auto | Add-Member AutoInterface $true
Resolve-RouteInterface (Normalize-RouteDraft $auto) (Read-RouteInventory).Interfaces
Check $true 'Backend confirms automatic interface by actual address/prefix'
$auto.InterfaceIndex=8
Reject {Request Add $null $auto} 'Backend rejects stale automatic interface before writing'
$auto.InterfaceIndex=7;$auto.NextHop='10.0.0.1'
Reject {Request Add $null $auto} 'Backend rejects gateway outside local subnets'
Check ($script:writes -eq 0) 'Automatic validation failures do not mutate routes'
$d=Draft
$r=Request Add $null $d
Check ($r.Success -and $script:rows.Count -eq 1 -and $script:rows[0].Store -eq 'ActiveStore') 'Temporary route explicitly uses ActiveStore'
$ownershipPath=Join-Path (Split-Path $ScriptPath) 'test-route-audit\mio-owned-routes.json'
Check (@(Read-RouteOwnership $ownershipPath).Count -ge 1) 'Newly created route ownership persisted'
$state=Read-RouteInventory
Check ($state.Routes[0].Editable -and $state.Routes[0].InterfaceMetric -eq 25) 'Static route editable and interface metric displayed'
$before=$script:writes
Reject {Request Add $null $d} 'Duplicate route rejected'
Check ($script:writes -eq $before) 'Duplicate rejection performs no writes'
$old=$state.Routes[0]
$r=Request Edit $old (Draft $true 30)
$current=(Read-RouteInventory).Routes[0]
Check ($script:rows.Count -eq 2 -and $current.Store -eq 'Persistent' -and $current.RouteMetric -eq 30) 'Persistent edit updates both stores and merges display'
Check (@(Read-RouteOwnership $ownershipPath | Where-Object Snapshot -CEQ $current.Snapshot).Count -eq 1) 'Owned route edit updates ownership snapshot'
Reject {Request Delete $old $null} 'Stale snapshot cannot delete newer route'
$script:failAdd=$true
Reject {Request Edit $current (Draft $false 40)} 'Failed edit returns error'
$restored=(Read-RouteInventory).Routes[0]
Check ($restored.Snapshot -ceq $current.Snapshot) 'Failed edit restores original persistent and active state'
$r=Request Delete $restored $null
Check ($r.Success -and $script:rows.Count -eq 0) 'Delete removes both store instances'
Check (@(Read-RouteOwnership $ownershipPath | Where-Object Snapshot -CEQ $restored.Snapshot).Count -eq 0) 'Delete removes matching ownership metadata'
$r=Request Add $null (Draft)
$script:rows[0].Protocol='Local'
$system=(Read-RouteInventory).Routes[0]
Check (!$system.Editable) 'Automatic local route is read-only'
Reject {Request Delete $system $null} 'System route cannot be deleted'
$script:rows[0].Protocol='NetMgmt';$script:rows[0].ValidLifetime=[timespan]::FromSeconds(300)
Check (!(Read-RouteInventory).Routes[0].Editable) 'Finite-lifetime route is read-only'
$before=$script:writes
foreach($prefix in @('192.0.2.1/24','192.0.2.0/33','::/0','1.2.3/24','0.0.0.0/0;whoami')) {
    $bad=Draft;$bad.DestinationPrefix=$prefix
    Reject {Normalize-RouteDraft $bad} "Reject invalid prefix $prefix"
}
$default=Draft;$default.DestinationPrefix='0.0.0.0/0'
Check ((Normalize-RouteDraft $default).DestinationPrefix -eq '0.0.0.0/0') 'Default route validates'
$bad=Draft;$bad.NextHop='127.0.0.1';Reject {Normalize-RouteDraft $bad} 'Reject loopback gateway'
$bad=Draft;$bad.InterfaceIndex=999;Reject {Request Add $null $bad} 'Missing interface rejected'
Check ($script:writes -eq $before) 'Validation failures perform no writes'
Write-Output "ROUTE BACKEND COMPLETE: $script:checks checks; mocked writes only."
