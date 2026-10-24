param([Parameter(Mandatory=$true)][string]$Directory,[switch]$Silent)
$ErrorActionPreference='Stop'
$unresolved=[Collections.Generic.List[string]]::new()
try {
    $path=Join-Path $Directory 'data\mio-owned-routes.json'
    if(!(Test-Path -LiteralPath $path)){exit 0}
    . (Join-Path $Directory 'CoreBin\Routes.ps1') -LibraryOnly
    $LibraryOnly=$false
    Test-RouteCapabilities
    $owned=@(Read-RouteOwnership $path)
    foreach($entry in $owned){
        $label="$($entry.DestinationPrefix) -> $($entry.NextHop) | $($entry.InterfaceAlias) (#$($entry.InterfaceIndex)) | $($entry.Store)"
        try {
            $state=Read-RouteInventory
            $current=@($state.Routes | Where-Object { $_.DestinationPrefix -eq $entry.DestinationPrefix -and $_.NextHop -eq $entry.NextHop -and $_.InterfaceIndex -eq $entry.InterfaceIndex })
            if(!$current.Count){
                $remaining=@(Read-RouteOwnership $path | Where-Object Snapshot -CNE $entry.Snapshot)
                Save-RouteOwnership $path $remaining
                continue
            }
            if($current.Count -ne 1 -or !$current[0].Editable -or $current[0].Snapshot -cne $entry.Snapshot){throw 'Route was modified externally; retained.'}
            $null=Invoke-RouteRequest ([pscustomobject]@{Command='RouteOperation';Operation='Delete';Expected=$entry;Updated=$null})
        }catch{$unresolved.Add("$label`r`n  $($_.Exception.Message)")}
    }
}catch{$unresolved.Add('Unable to verify MIO route ownership: '+$_.Exception.Message)}
if(!$unresolved.Count){exit 0}
$details=$unresolved -join "`r`n`r`n"
if($Silent){[Console]::Out.WriteLine($details);exit 0}
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$form=[Windows.Forms.Form]::new();$form.Text='MIO 卸载 · 未清理的系统路由';$form.Size=[Drawing.Size]::new(800,500);$form.StartPosition='CenterScreen'
$text=[Windows.Forms.TextBox]::new();$text.Multiline=$true;$text.ReadOnly=$true;$text.ScrollBars='Both';$text.Dock='Fill';$text.Text=$details
$bar=[Windows.Forms.FlowLayoutPanel]::new();$bar.Dock='Bottom';$bar.Height=48;$bar.FlowDirection='RightToLeft'
$cancel=[Windows.Forms.Button]::new();$cancel.Text='取消';$cancel.DialogResult='Cancel'
$go=[Windows.Forms.Button]::new();$go.Text='保留并继续卸载';$go.Width=150;$go.DialogResult='OK'
$bar.Controls.Add($cancel);$bar.Controls.Add($go);$form.Controls.Add($text);$form.Controls.Add($bar);$form.CancelButton=$cancel
$result=$form.ShowDialog();$form.Dispose();if($result -eq 'OK'){exit 0}else{exit 3}
