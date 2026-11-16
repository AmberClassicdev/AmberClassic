[CmdletBinding()]
param(
    # Batch 797: the patch line, 126 or 132. This script is in host/tools; the line's projects and natives are in <emu>/<Patch>.
    [Parameter(Mandatory)][ValidateSet('126', '132')][string]$Patch,
    [string]$OutputDirectory,
    [Parameter(Mandatory)][string]$GameDataRoot,
    [string]$BuildIdentity = ("patch$($Patch)-" +(Get-Date -Format 'yyyyMMdd-HHmmss')),
    # Batch 825: the server stage is all that is left (the detached host package with Amberline.Host.exe is retired); it must be named.
    [switch]$ServerOnly,
    # Batch 611: the server's runtime; its natives are built for it (batch 634: Zig, native/*/build.ps1 -Runtime).
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$emu = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repository = Join-Path $emu $Patch
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $ServerOnly) {
    throw 'The detached host package (Amberline.Host.exe, its host data and guest invitations) was retired in batch 825; use -ServerOnly.'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository (Join-Path 'artifacts' "server-update-$stamp")
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Choose a new output folder; existing packages are never overwritten.' }
$runtimeRoot = Join-Path $destination 'runtime'
# Batch 634: a folder without .git, or a PC without git, builds too; its source commit is unknown (as in build-server.ps1).
$gitCheckout = (Test-Path -LiteralPath (Join-Path $emu '.git')) -and [bool](Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
$commit = if ($gitCheckout) { (& git -C $emu rev-parse HEAD) } else { 'unknown (no git checkout)' }
$localChanges = if ($gitCheckout) { [bool](& git -C $emu status --porcelain | Select-Object -First 1) } else { $null }
$data = (Resolve-Path -LiteralPath $GameDataRoot).Path
$build = Join-Path $repository (Join-Path '.artifacts' ('remote-package-' + [guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
$serverDirectory = Join-Path $runtimeRoot 'server'
# Batch 611: the server is a self-contained folder. Single-file bundling plus the ReadyToRun cross-module flags (batch 573) crashed
# the published server at its first JSON use (System.Buffers.ProbabilisticMapState); the same publish as a folder passes the preflight.
& dotnet publish (Join-Path $repository 'src/Amberline.Server/Amberline.Server.csproj') -c Release -r $Runtime --self-contained true --artifacts-path (Join-Path $build 'Amberline.Server') -o $serverDirectory -p:PublishTrimmed=false "-p:InformationalVersion=$BuildIdentity"
if ($LASTEXITCODE -ne 0) { throw 'Amberline.Server publish failed.' }
$serverExecutable = if ($Runtime -eq 'win-x64') { 'Amberline.Server.exe' } else { 'Amberline.Server' }
# Natives beside the server: the server finds amberline_enet and exehost in its own folder (LeagueEnetTransport.cs, ExeHostProcess.cs).
# Batch 634: Zig builds them for either runtime (native/*/build.ps1 -Runtime), so neither LLVM, the C++ Build Tools nor sh is needed.
# Batch 635: this is build-server.ps1's server stage, so this prints its stage line 3 of 7 (tools/guide.ps1 shows it as a bar).
Write-Host 'AMBERLINE-STAGE 3/7 Building the native parts'
& (Join-Path $repository 'native/enet/build.ps1') -Runtime $Runtime -OutputDirectory $serverDirectory
# Batch 608: the helper that runs the client's own movement code from the player's EXE (without it the server uses its fallback mover).
& (Join-Path $repository 'native/exehost/build.ps1') -Runtime $Runtime -OutputDirectory $serverDirectory
$manifest = [ordered]@{
    component = 'server'
    runtime = $Runtime
    buildIdentity = $BuildIdentity
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $commit
    sourceHasLocalChanges = $localChanges
    files = @(Get-ChildItem -LiteralPath $serverDirectory -File | Sort-Object Name |
        ForEach-Object { [ordered]@{ path = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $serverDirectory 'BUILD.json') -Encoding utf8
# The preflight runs the server on this host, so only a server for this host's runtime can take it.
if ($Runtime -eq 'win-x64' -and $IsWindows) {
    $previousData = $env:GAME_DATA_ROOT
    $previousNative = $env:AMBERLINE_ENET_NATIVE
    try {
        $env:GAME_DATA_ROOT = $data
        $env:AMBERLINE_ENET_NATIVE = Join-Path $serverDirectory 'amberline_enet.dll'
        & (Join-Path $serverDirectory $serverExecutable) --solo --ephemeral --validate-only --data-root $data --log-dir (Join-Path $build 'validation')
        if ($LASTEXITCODE -ne 0) { throw 'Published server preflight failed.' }
    } finally {
        $env:GAME_DATA_ROOT = $previousData
        $env:AMBERLINE_ENET_NATIVE = $previousNative
    }
} else {
    Write-Host "Preflight not run: the $Runtime server runs on that system only (run server --validate-only there)."
}
# Batch 611: the publish intermediates (obj, the preflight log folder) are regenerable; the staged server is in $destination.
Remove-Item -LiteralPath $build -Recurse -Force
Write-Host "Staged server: $serverDirectory"
