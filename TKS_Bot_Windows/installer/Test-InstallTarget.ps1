[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallDirectory, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
try {
    $target = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $forbidden = @([IO.Path]::GetPathRoot($target).TrimEnd('\'), $env:USERPROFILE, $env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:APPDATA, $env:LOCALAPPDATA, [Environment]::GetFolderPath('Desktop'))
    if ($forbidden -contains $target) { throw 'Choose a dedicated application subdirectory.' }
    $ancestor = $target
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point install paths are not supported.' }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    if (Test-Path -LiteralPath $target) {
        $directories = New-Object 'System.Collections.Generic.Queue[string]'
        $directories.Enqueue($target)
        while ($directories.Count -gt 0) {
            foreach ($entry in Get-ChildItem -LiteralPath $directories.Dequeue() -Force) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points inside installation are not supported.' }
                if ($entry.PSIsContainer) { $directories.Enqueue($entry.FullName) }
            }
        }
    }
    if ((Test-Path -LiteralPath (Join-Path $target 'portable.flag')) -or (Test-Path -LiteralPath (Join-Path $target 'data'))) {
        throw 'Portable/user data directory detected. Choose a separate installer directory.'
    }
    foreach ($process in Get-Process TKSDesktop -ErrorAction SilentlyContinue) {
        # Do not terminate the user process or risk overwriting a running application.
        throw 'Exit TKSDesktop from the tray before installing or uninstalling.'
    }
    if (-not $Uninstall -and (Test-Path -LiteralPath $target)) {
        if ((Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1) -and
            -not (Test-Path -LiteralPath (Join-Path $target 'uninstall.exe'))) {
            throw 'Target is nonempty and is not a previous installation.'
        }
    }
    exit 0
} catch { Write-Error $_; exit 1 }
