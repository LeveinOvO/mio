param([switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$directory = Join-Path $root "artifacts\build\V$version"
$executable = Join-Path $directory 'Mio.Host.exe'
try {
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw '未找到程序文件，请先运行 Build.cmd 生成程序。'
    }
    if ($CheckOnly) {
        Write-Output 'PASS: Launcher parsed and executable found. No application started.'
        exit 0
    }
    Start-Process -FilePath $executable -WorkingDirectory $directory -Verb RunAs
    exit 0
}
catch {
    Write-Host $_.Exception.Message
    exit 1
}
