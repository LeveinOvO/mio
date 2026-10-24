param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference='Stop'
try {
    $install=[IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $data=$install
    if(![IO.Directory]::Exists($data)){exit 0}
    if($install -eq [IO.Path]::GetPathRoot($install).TrimEnd('\')){throw 'Refusing drive root removal.'}
    if(!(Test-Path -LiteralPath (Join-Path $install 'Mio.exe')) -or !(Test-Path -LiteralPath (Join-Path $install 'Uninstall.exe'))){throw 'MIO installation identity not found.'}
    # Remove the entire selected installation, not just its data directory.
    # Refuse links anywhere in the tree before deleting any files.
    # Do not recurse through junctions into other applications or user directories.
    $cursor=Get-Item -LiteralPath $data -Force
    while($null -ne $cursor){
        if($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked directory refused: $($cursor.FullName)"}
        $cursor=$cursor.Parent
    }
    $files=[Collections.Generic.List[string]]::new()
    $directories=[Collections.Generic.List[string]]::new()
    $pending=[Collections.Generic.Stack[string]]::new()
    $pending.Push($data)
    while($pending.Count){
        $path=$pending.Pop();$directories.Add($path)
        foreach($item in Get-ChildItem -LiteralPath $path -Force){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked item refused: $($item.FullName)"}
            if($item.PSIsContainer){$pending.Push($item.FullName)}else{$files.Add($item.FullName)}
        }
    }
    # Check write/delete sharing before starting, so a locked extra file does not
    # cause predictable partial deletion. Never forcibly close a user process.
    foreach($file in $files){
        $probe=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $probe.Dispose()
    }
    foreach($file in $files){
        if((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Data changed during removal.'}
        [IO.File]::Delete($file)
    }
    # Nonrecursive removal fails rather than following a newly inserted tree.
    for($i=$directories.Count-1;$i -ge 0;$i--){[IO.Directory]::Delete($directories[$i],$false)}
    exit 0
} catch { [Console]::Out.WriteLine($_.Exception.Message); exit 2 }
