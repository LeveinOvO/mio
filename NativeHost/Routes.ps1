param([switch]$LibraryOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-RouteCapabilities {
    if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -lt 1 -or ![Environment]::Is64BitProcess){throw 'Route management requires 64-bit Windows PowerShell 5.1.'}
    Import-Module NetTCPIP -ErrorAction Stop
    foreach($name in @('Get-NetRoute','New-NetRoute','Remove-NetRoute','Get-NetIPInterface','Get-NetIPAddress')){
        $command=Get-Command $name -Module NetTCPIP -ErrorAction Stop
        if($command.ModuleName -ne 'NetTCPIP'){throw "Unexpected command provider: $name"}
    }
    $remove=Get-Command Remove-NetRoute -Module NetTCPIP
    $sets=@($remove.ParameterSets | Where-Object { 'InputObject' -in $_.Parameters.Name -and 'PolicyStore' -notin $_.Parameters.Name })
    if($sets.Count -ne 1){throw 'Unsupported Remove-NetRoute parameter contract; writes refused.'}
    $null=Get-CimClass -Namespace root/StandardCimv2 -ClassName MSFT_NetRoute -ErrorAction Stop
}

function Normalize-RouteDraft($draft) {
    function IPv4([string]$text) {
        if ($text -notmatch '^(\d{1,3}\.){3}\d{1,3}$') { throw 'Invalid IPv4 address.' }
        $parts = @($text.Split('.') | ForEach-Object { [int]$_ })
        if (@($parts | Where-Object { $_ -gt 255 }).Count) { throw 'Invalid IPv4 octet.' }
        return ($parts -join '.')
    }
    $prefix = [string]$draft.DestinationPrefix
    if ($prefix -notmatch '^([^/]+)/([0-9]{1,2})$') { throw 'Destination must be IPv4 CIDR.' }
    $ip = IPv4 $Matches[1]; $length = [int]$Matches[2]
    if ($length -gt 32) { throw 'Prefix length must be 0-32.' }
    $bytes = @($ip.Split('.') | ForEach-Object { [long]$_ })
    $number = $bytes[0]*16777216 + $bytes[1]*65536 + $bytes[2]*256 + $bytes[3]
    $block = [math]::Pow(2, 32-$length)
    if (($number % $block) -ne 0) { throw 'Destination contains host bits.' }
    $hop = IPv4 ([string]$draft.NextHop)
    $first = [int]$hop.Split('.')[0]
    if ($hop -ne '0.0.0.0' -and ($first -eq 0 -or $first -eq 127 -or $first -ge 224)) { throw 'Next hop must be unicast IPv4 or 0.0.0.0.' }
    if ($draft.Persistent -isnot [bool]) { throw 'Persistent must be boolean.' }
    $index = [long]$draft.InterfaceIndex; $metric = [long]$draft.RouteMetric
    if ($index -lt 1 -or $index -gt 4294967295 -or $metric -lt 0 -or $metric -gt 65535 -or $index -ne $draft.InterfaceIndex -or $metric -ne $draft.RouteMetric) { throw 'Invalid interface index or metric.' }
    return [pscustomobject]@{ DestinationPrefix="$ip/$length"; NextHop=$hop; InterfaceIndex=[uint32]$index; RouteMetric=[uint32]$metric; Persistent=$draft.Persistent }
}

function Get-StoreRoutes([string]$store) {
    try { Get-NetRoute -AddressFamily IPv4 -PolicyStore $store -ErrorAction Stop }
    catch { if ($_.CategoryInfo.Category -ne 'ObjectNotFound') { throw } }
}

function Read-RouteInventory {
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [string]$_.AddressState -eq 'Preferred' -and !$_.SkipAsSource })
    $interfaces = @(Get-NetIPInterface -AddressFamily IPv4 -ErrorAction Stop | ForEach-Object {
        $index=$_.InterfaceIndex
        [pscustomobject]@{ Index=[uint32]$index; Name=[string]$_.InterfaceAlias; Metric=[uint32]$_.InterfaceMetric; State=[string]$_.ConnectionState;
            Addresses=@($addresses | Where-Object InterfaceIndex -eq $index | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" }) }
    })
    $raw = @()
    foreach ($store in @('ActiveStore','PersistentStore')) {
        $raw += @(Get-StoreRoutes $store | ForEach-Object {
            [pscustomobject]@{ Store=$store; Route=$_ }
        })
    }
    $groups = @($raw | Group-Object { "$($_.Route.DestinationPrefix)|$($_.Route.NextHop)|$($_.Route.InterfaceIndex)" })
    $rows = @($groups | ForEach-Object {
        $members = @($_.Group | Sort-Object Store)
        $saved = @($members | Where-Object { $_.Store -eq 'PersistentStore' })
        $selected = if ($saved.Count) { $saved[0].Route } else { $members[0].Route }
        $iface = @($interfaces | Where-Object { $_.Index -eq $selected.InterfaceIndex })
        $snapshots = @($members | ForEach-Object {
            [ordered]@{ Store=$_.Store; Prefix=[string]$_.Route.DestinationPrefix; Hop=[string]$_.Route.NextHop; Index=[uint32]$_.Route.InterfaceIndex;
                Alias=[string]$_.Route.InterfaceAlias; Metric=[uint32]$_.Route.RouteMetric; Protocol=[string]$_.Route.Protocol; Publish=[string]$_.Route.Publish }
        })
        $snapshot = ConvertTo-Json -InputObject $snapshots -Depth 6 -Compress
        # System/local/DHCP/dynamic routes and finite-lifetime routes are never edited.
        $editable = @($members | Where-Object { [string]$_.Route.Protocol -ne 'NetMgmt' -or [string]$_.Route.Publish -ne 'No' -or $_.Route.ValidLifetime.TotalSeconds -lt 4294967294 }).Count -eq 0
        if (@($members | Group-Object Store | Where-Object Count -gt 1).Count) { $editable=$false }
        if (@($members | Where-Object { $_.Route.RouteMetric -ne $selected.RouteMetric }).Count) { $editable=$false }
        [pscustomobject]@{ DestinationPrefix=[string]$selected.DestinationPrefix; NextHop=[string]$selected.NextHop;
            InterfaceIndex=[uint32]$selected.InterfaceIndex; InterfaceAlias=[string]$selected.InterfaceAlias;
            RouteMetric=[uint32]$selected.RouteMetric; InterfaceMetric=$(if($iface.Count){$iface[0].Metric}else{0});
            Protocol=[string]$selected.Protocol; Store=$(if($saved.Count){'Persistent'}else{'Active'});
            Snapshot=$snapshot; Editable=$editable }
    })
    return [pscustomobject]@{ Routes=@($rows | Sort-Object DestinationPrefix,InterfaceIndex,NextHop); Interfaces=$interfaces }
}

function Add-RouteDraft($draft) {
    $parameters = @{ DestinationPrefix=$draft.DestinationPrefix; InterfaceIndex=$draft.InterfaceIndex;
        NextHop=$draft.NextHop; RouteMetric=$draft.RouteMetric; AddressFamily='IPv4'; Protocol='NetMgmt'; ErrorAction='Stop' }
    # Microsoft: omitting PolicyStore creates both persistent and active routes.
    if (!$draft.Persistent) { $parameters.PolicyStore='ActiveStore' }
    New-NetRoute @parameters | Out-Null
}

function Resolve-RouteInterface($draft, $interfaces) {
    if ($draft.NextHop -eq '0.0.0.0') { throw 'On-link routes require a manually selected interface.' }
    function Number([string]$ip) { $p=$ip.Split('.'); return [long]$p[0]*16777216+[long]$p[1]*65536+[long]$p[2]*256+[long]$p[3] }
    $hop=Number $draft.NextHop
    $matches=@(foreach($iface in $interfaces) {
        if($iface.State -ne 'Connected'){continue}
        foreach($cidr in $iface.Addresses){
            $parts=$cidr.Split('/');$bits=[int]$parts[1];if($bits -lt 1 -or $bits -gt 32){continue}
            $local=Number $parts[0];$block=[math]::Pow(2,32-$bits);$network=[math]::Floor($local/$block)*$block
            if($local -ne $hop -and $hop -ge $network -and $hop -lt ($network+$block) -and ($bits -ge 31 -or ($hop -ne $network -and $hop -ne ($network+$block-1)))){$iface;break}
        }
    })
    if($matches.Count -ne 1){throw 'Gateway interface is missing or ambiguous; refresh and manually select an interface.'}
    if($matches[0].Index -ne $draft.InterfaceIndex){throw 'Automatic interface changed since preview; refresh before saving.'}
}

function Remove-RouteSnapshot($row) {
    $snapshots = $row.Snapshot | ConvertFrom-Json
    foreach ($snapshot in @($snapshots | Sort-Object Store -Descending)) {
        $matches = @(Get-StoreRoutes $snapshot.Store | Where-Object {
            $_.DestinationPrefix -eq $snapshot.Prefix -and $_.NextHop -eq $snapshot.Hop -and $_.InterfaceIndex -eq $snapshot.Index
        })
        # Removal from one store may remove the corresponding active instance too.
        if (!$matches.Count) { continue }
        if ($matches.Count -ne 1 -or [string]$matches[0].InterfaceAlias -cne $snapshot.Alias -or [string]$matches[0].Protocol -ne $snapshot.Protocol -or $matches[0].RouteMetric -ne $snapshot.Metric -or [string]$matches[0].Publish -ne $snapshot.Publish) {
            throw 'Route changed; refresh before editing.'
        }
        Remove-NetRoute -InputObject $matches[0] -Confirm:$false -ErrorAction Stop
    }
}

function Restore-RouteSnapshot($old) {
    # Restore each original store without overwriting a concurrently changed row.
    $originals = $old.Snapshot | ConvertFrom-Json
    foreach ($snapshot in @($originals | Sort-Object Store -Descending)) {
        $existing = @(Get-StoreRoutes $snapshot.Store | Where-Object {
            $_.DestinationPrefix -eq $snapshot.Prefix -and $_.NextHop -eq $snapshot.Hop -and $_.InterfaceIndex -eq $snapshot.Index
        })
        if ($existing.Count) {
            if ($existing.Count -ne 1 -or $existing[0].RouteMetric -ne $snapshot.Metric -or [string]$existing[0].Protocol -ne $snapshot.Protocol) {
                throw 'Original route was changed externally; restore refused.'
            }
            continue
        }
        $parameters=@{ DestinationPrefix=$snapshot.Prefix; NextHop=$snapshot.Hop; InterfaceIndex=$snapshot.Index; RouteMetric=$snapshot.Metric; AddressFamily='IPv4'; Protocol='NetMgmt'; ErrorAction='Stop' }
        if ($snapshot.Store -eq 'ActiveStore') { $parameters.PolicyStore='ActiveStore' }
        New-NetRoute @parameters | Out-Null
    }
}

function Invoke-RouteRequestCore($request) {
    if ($request.Command -eq 'Routes') { return @{ Success=$true; Data=(Read-RouteInventory) } }
    if ($request.Command -ne 'RouteOperation' -or $request.Operation -notin @('Add','Edit','Delete')) { throw 'Unknown route request.' }
    $state = Read-RouteInventory
    $old = $null
    if ($request.Operation -ne 'Add') {
        $match = @($state.Routes | Where-Object { $_.Snapshot -ceq $request.Expected.Snapshot })
        if ($match.Count -ne 1 -or !$match[0].Editable) { throw 'Route changed or is system-managed; refresh the list.' }
        $old=$match[0]
    }
    $draft=$null
    if ($request.Operation -ne 'Delete') {
        $draft=Normalize-RouteDraft $request.Updated
        if ($request.Updated.PSObject.Properties['AutoInterface']) {
            if($request.Updated.AutoInterface -isnot [bool]){throw 'AutoInterface must be boolean.'}
            if($request.Updated.AutoInterface){Resolve-RouteInterface $draft $state.Interfaces}
        }
        if (!@($state.Interfaces | Where-Object { $_.Index -eq $draft.InterfaceIndex }).Count) { throw 'Network interface no longer exists.' }
        $conflicts=@($state.Routes | Where-Object { $_.DestinationPrefix -eq $draft.DestinationPrefix -and $_.NextHop -eq $draft.NextHop -and $_.InterfaceIndex -eq $draft.InterfaceIndex })
        if ($conflicts.Count -and ($null -eq $old -or $conflicts[0].Snapshot -cne $old.Snapshot)) { throw 'This route already exists.' }
    }
    try {
        if ($null -ne $old) { Remove-RouteSnapshot $old }
        if ($null -ne $draft) { Add-RouteDraft $draft }
    } catch {
        $failure=$_.Exception.Message
        $script:Recovery='NotRequired'
        if ($null -ne $old) {
            try { Restore-RouteSnapshot $old; $script:Recovery='Restored' }
            catch { $script:Recovery='Failed'; throw "Update failed: $failure; restoring old route also failed: $($_.Exception.Message). Refresh the list." }
        }
        throw "Route update failed: $failure. Refresh the list to verify Windows state."
    }
    return @{ Success=$true; Message='Route operation completed.' }
}

function Read-RouteOwnership($path) {
    if(!(Test-Path -LiteralPath $path)){return @()}
    $document=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if($document.Version -ne 1 -or $null -eq $document.Routes){throw 'Invalid MIO route ownership file.'}
    return @($document.Routes)
}
function Save-RouteOwnership($path,$rows) {
    $temporary=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{Version=1;Routes=@($rows)} -Depth 15 -Compress))
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        if(Test-Path -LiteralPath $path){
            $backup=$temporary+'.bak'
            [IO.File]::Replace($temporary,$path,$backup)
            [IO.File]::Delete($backup)
        }else{[IO.File]::Move($temporary,$path)}
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
}

function Invoke-RouteRequest($request) {
    if(!$LibraryOnly){Test-RouteCapabilities}
    if ($request.Command -ne 'RouteOperation') {
        $response=Invoke-RouteRequestCore $request
        $pendingPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'data\logs\route-operation.pending.json'
        if($LibraryOnly){$pendingPath=Join-Path $PSScriptRoot 'test-route-audit\route-operation.pending.json'}
        $response.Data | Add-Member -NotePropertyName WriteBlockedReason -NotePropertyValue $(if(Test-Path $pendingPath){'An interrupted route operation needs inspection. Route changes are blocked; current Windows routes are shown.'}else{''})
        return $response
    }
    $id=[guid]::NewGuid().ToString('N')
    $started=[Diagnostics.Stopwatch]::StartNew()
    $script:Recovery='NotAttempted'
    $logs=Join-Path (Split-Path $PSScriptRoot -Parent) 'data\logs'
    # In library tests use a test-local directory, never an installed app's log directory.
    if ($LibraryOnly) { $logs=Join-Path $PSScriptRoot 'test-route-audit' }
    $null=New-Item -ItemType Directory -Path $logs -Force
    $file=Join-Path $logs ('Routes-Audit-'+[DateTime]::UtcNow.ToString('yyyy-MM-dd')+'.jsonl')
    $pending=Join-Path $logs 'route-operation.pending.json'
    if(Test-Path -LiteralPath $pending){throw 'An earlier route operation was interrupted or could not be verified. Writes blocked. Inspect route-operation.pending.json and current Windows routes before recovery.'}
    $before=Read-RouteInventory
    $ownershipPath=Join-Path (Split-Path $logs -Parent) 'mio-owned-routes.json'
    if($LibraryOnly){$ownershipPath=Join-Path $logs 'mio-owned-routes.json'}
    $owned=@(Read-RouteOwnership $ownershipPath)
    $audit=[ordered]@{time=[DateTime]::UtcNow.ToString('o'); requestId=$id; event='route.begin'; operation=$request.Operation;
        expected=$request.Expected; requested=$request.Updated; before=$before.Routes; after=$null; success=$false; recovery=$script:Recovery; message=''; durationMs=0}
    # Fail before writing to Windows if the initial audit record cannot be persisted.
    [IO.File]::AppendAllText($file,((ConvertTo-Json -InputObject $audit -Depth 15 -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
    # Durable marker survives timeout/process termination. Never blindly retry incomplete work.
    $marker=[IO.File]::Open($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $audit -Depth 15 -Compress));$marker.Write($bytes,0,$bytes.Length);$marker.Flush($true)}finally{$marker.Dispose()}
    $result=$null; $failure=$null
    try { $result=Invoke-RouteRequestCore $request; $audit.success=$true }
    catch { $failure=$_.Exception.Message; $audit.message=$failure }
    $audit.event='route.end';$audit.time=[DateTime]::UtcNow.ToString('o');$audit.recovery=$script:Recovery;$audit.durationMs=$started.ElapsedMilliseconds
    $verified=$false
    try { $audit.after=(Read-RouteInventory).Routes; $verified=$true }
    catch { $audit.message+=" After-state query failed: $($_.Exception.Message)"; $audit.success=$false }
    if($verified){
        function RouteKey($row){"$($row.DestinationPrefix)|$($row.NextHop)|$($row.InterfaceIndex)"}
        $allowed=@()
        if($null -ne $request.Expected){$allowed+=RouteKey $request.Expected}
        if($null -ne $request.Updated){
            try{$normalized=Normalize-RouteDraft $request.Updated;$allowed+=RouteKey $normalized}catch{}
        }
        $untouchedBefore=@($before.Routes | Where-Object { (RouteKey $_) -notin $allowed } | ForEach-Object Snapshot | Sort-Object)
        $untouchedAfter=@($audit.after | Where-Object { (RouteKey $_) -notin $allowed } | ForEach-Object Snapshot | Sort-Object)
        if(Compare-Object $untouchedBefore $untouchedAfter){$verified=$false;$audit.message+=' Unrelated route changes detected; inspect before continuing.'}
        if($failure){
            # A failed write is resolved only if the entire original route snapshot is restored.
            if(Compare-Object @($before.Routes | ForEach-Object Snapshot | Sort-Object) @($audit.after | ForEach-Object Snapshot | Sort-Object)){
                $verified=$false;$audit.message+=' Failed operation did not restore the original Windows route state.'
            }
        }else{
            if($request.Operation -eq 'Delete'){
                if(@($audit.after | Where-Object { (RouteKey $_) -eq (RouteKey $request.Expected) }).Count){$verified=$false}
            }else{
                $expectedDraft=Normalize-RouteDraft $request.Updated
                $matches=@($audit.after | Where-Object { (RouteKey $_) -eq (RouteKey $expectedDraft) })
                if($matches.Count -ne 1 -or $matches[0].RouteMetric -ne $expectedDraft.RouteMetric -or ($matches[0].Store -eq 'Persistent') -ne $expectedDraft.Persistent){$verified=$false}
            }
        }
        if(!$verified){$audit.success=$false;$audit.message+=' Postcondition verification failed; further writes blocked.'}
    }
    if($verified -and !$failure){
        try {
            $wasOwned=$false
            if($null -ne $request.Expected){$wasOwned=@($owned | Where-Object { $_.Snapshot -ceq $request.Expected.Snapshot }).Count -eq 1}
            if($wasOwned){$owned=@($owned | Where-Object { $_.Snapshot -cne $request.Expected.Snapshot })}
            if($request.Operation -eq 'Add' -or ($request.Operation -eq 'Edit' -and $wasOwned)){
                $desired=Normalize-RouteDraft $request.Updated
                $owned+=@($audit.after | Where-Object { $_.DestinationPrefix -eq $desired.DestinationPrefix -and $_.NextHop -eq $desired.NextHop -and $_.InterfaceIndex -eq $desired.InterfaceIndex })
            }
            Save-RouteOwnership $ownershipPath $owned
        }catch{$verified=$false;$audit.success=$false;$audit.message+=' Ownership persistence failed: '+$_.Exception.Message}
    }
    try { [IO.File]::AppendAllText($file,((ConvertTo-Json -InputObject $audit -Depth 15 -Compress)+[Environment]::NewLine),[Text.UTF8Encoding]::new($false)) }
    catch { throw "Route operation may have completed, but final audit write failed. Request $id. Refresh before retrying. $failure" }
    if($verified -and $script:Recovery -ne 'Failed'){Remove-Item -LiteralPath $pending -ErrorAction Stop}
    if(!$verified){throw "Operation outcome requires inspection; audit request $id. Further writes blocked."}
    if ($failure) { throw "$failure [requestId=$id]" }
    return $result
}

if (!$LibraryOnly) {
    [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
    try {
        $request=[Console]::In.ReadLine() | ConvertFrom-Json
        $result=Invoke-RouteRequest $request
        [Console]::WriteLine((ConvertTo-Json -InputObject $result -Depth 12 -Compress))
    } catch {
        [Console]::WriteLine((ConvertTo-Json -InputObject @{ Success=$false; Message=$_.Exception.Message } -Compress))
    }
}
