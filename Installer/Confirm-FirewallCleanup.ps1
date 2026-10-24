param([Parameter(Mandatory=$true)][string]$Directory,[switch]$Silent)
$ErrorActionPreference='Stop'
$items=@();$problem=''
try {
    $exe=Join-Path $Directory 'Mio.Host.exe'
    $db=Join-Path $Directory 'data\Mio.db'
    if(!(Test-Path -LiteralPath $db)){throw 'Database missing; rule ownership cannot be verified.'}
    if(!(Test-Path -LiteralPath $exe)){throw 'MIO host missing; automatic cleanup unavailable.'}
    $start=[Diagnostics.ProcessStartInfo]::new($exe,'--uninstall-cleanup')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    $p=[Diagnostics.Process]::Start($start)
    $output=$p.StandardOutput.ReadToEndAsync();$errorOutput=$p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    if($p.ExitCode -ne 0){throw $errorOutput.Result}
    if(!$output.Result.Trim()){throw 'Cleanup did not return a verification report.'}
    $report=$output.Result | ConvertFrom-Json
    if($null -eq $report -or !$report.PSObject.Properties['Unresolved']){throw 'Cleanup verification report is incomplete.'}
    $items=@($report.Unresolved)
} catch {
    $problem=$_.Exception.Message
    # Candidates only: a name/group match is not proof of ownership. Never delete here.
    try {
        $policy=New-Object -ComObject HNetCfg.FwPolicy2
        $items=@($policy.Rules | Where-Object { $_.Grouping -eq 'Mio.Managed.TCP.v1' -or $_.Name -like 'Mio.TCP.*' } | ForEach-Object {
            [pscustomobject]@{Name=$_.Name;Protocol=$(if($_.Protocol -eq 6){'TCP'}elseif($_.Protocol -eq 17){'UDP'}else{[string]$_.Protocol});Address=$_.LocalAddresses;Port=$_.LocalPorts;Reason='Unverified ownership; retained.'}
        })
    } catch { $problem+=' Firewall enumeration failed: '+$_.Exception.Message }
}
if(!$items.Count -and !$problem){exit 0}
$lines=@($items | ForEach-Object {
    $line="$($_.Name)`r`n  配置记录：$($_.Protocol)  $($_.Address):$($_.Port)`r`n  原因：$($_.Reason)"
    if($_.PSObject.Properties['Actual']){
        foreach($actual in $_.Actual){
            $line+="`r`n  当前 Windows 属性："+($actual | ConvertTo-Json -Depth 6 -Compress)
        }
    }
    $line
})
$details=($lines -join "`r`n`r`n")
if($problem){$details="检查说明：$problem`r`n`r`n以下仅为疑似关联规则，不代表已确认归属。`r`n`r`n"+$details}
if(!$items.Count){$details+='无法提供可确认的规则清单；不代表系统中没有残留规则。'}
if($Silent){[Console]::Out.WriteLine($details);exit 0}
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$form=[Windows.Forms.Form]::new();$form.Text='MIO 卸载 · 防火墙清理结果';$form.Size=[Drawing.Size]::new(760,520);$form.StartPosition='CenterScreen';$form.MinimizeBox=$false;$form.MaximizeBox=$false
$label=[Windows.Forms.Label]::new();$label.Text="以下 $($items.Count) 条规则未自动清理，将保留在 Windows 中。是否继续卸载？";$label.Dock='Top';$label.Height=50;$label.Padding=[Windows.Forms.Padding]::new(12)
$text=[Windows.Forms.TextBox]::new();$text.Multiline=$true;$text.ReadOnly=$true;$text.ScrollBars='Both';$text.WordWrap=$false;$text.Dock='Fill';$text.Text=$details
$buttons=[Windows.Forms.FlowLayoutPanel]::new();$buttons.Dock='Bottom';$buttons.Height=50;$buttons.FlowDirection='RightToLeft';$buttons.Padding=[Windows.Forms.Padding]::new(8)
$cancel=[Windows.Forms.Button]::new();$cancel.Text='取消';$cancel.DialogResult='Cancel'
$continue=[Windows.Forms.Button]::new();$continue.Text='继续卸载';$continue.Width=110;$continue.DialogResult='OK'
$buttons.Controls.Add($cancel);$buttons.Controls.Add($continue)
$form.Controls.Add($text);$form.Controls.Add($label);$form.Controls.Add($buttons)
$form.CancelButton=$cancel
$answer=$form.ShowDialog();$form.Dispose()
if($answer -eq 'OK'){exit 0}else{exit 3}
