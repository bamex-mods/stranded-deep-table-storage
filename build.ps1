param(
    [Alias("GameDir")]
    [string]$GameRoot = $env:STRANDED_DEEP_GAME_ROOT,
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SourceFiles = @(
    (Join-Path $ProjectDir "TableStorage.cs"),
    (Join-Path $ProjectDir "RaftFurnitureClient.cs"),
    (Join-Path $ProjectDir "SDK\ModSettingsClient.cs")
)
$BuildDir = Join-Path $ProjectDir "build"
$OutputDll = Join-Path $BuildDir "StrandedDeepTableStorage.dll"

$GameDir = $GameRoot

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw "GameRoot was not supplied. Pass -GameRoot or set STRANDED_DEEP_GAME_ROOT."
}
$ManagedDir = Join-Path $GameDir "Stranded_Deep_Data\Managed"
$BepInExCore = Join-Path $GameDir "BepInEx\core"

$CscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$Csc = $CscCandidates |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1

if (-not $Csc) {
    throw "csc.exe was not found."
}

$Refs = @(
    (Join-Path $BepInExCore "BepInEx.dll"),
    (Join-Path $BepInExCore "0Harmony.dll"),
    (Join-Path $ManagedDir "Assembly-CSharp.dll"),
    (Join-Path $ManagedDir "bolt.dll"),
    (Join-Path $ManagedDir "bolt.user.dll"),
    (Join-Path $ManagedDir "UnityEngine.dll"),
    (Join-Path $ManagedDir "UnityEngine.CoreModule.dll"),
    (Join-Path $ManagedDir "UnityEngine.AnimationModule.dll"),
    (Join-Path $ManagedDir "UnityEngine.PhysicsModule.dll"),
    (Join-Path $ManagedDir "UnityEngine.InputLegacyModule.dll")
)

foreach ($ref in $Refs) {
    if (-not (Test-Path $ref)) {
        throw "Missing reference: $ref"
    }
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$args = @(
    "/nologo",
    "/target:library",
    "/optimize+",
    "/langversion:5",
    "/out:$OutputDll"
)

foreach ($source in $SourceFiles) {
    if (-not (Test-Path $source)) {
        throw "Missing source: $source"
    }

    $args += $source
}

foreach ($ref in $Refs) {
    $args += "/reference:$ref"
}

Write-Host "Game: $GameDir"
Write-Host "Compiler: $Csc"
Write-Host "Building Table Storage v0.8.2 canonical master..."

& $Csc $args

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $OutputDll"

if ($Deploy) {
    $PluginDir = Join-Path $GameDir "BepInEx\plugins\StrandedDeepTableStorage"
    $LegacyPluginDir = Join-Path $GameDir "BepInEx\plugins\StrandedDeepKitchenTable"
    $LegacyPluginDll = Join-Path $LegacyPluginDir "StrandedDeepKitchenTable.dll"
    $LegacyRootDll = Join-Path (Join-Path $GameDir "BepInEx\plugins") "StrandedDeepKitchenTable.dll"

    New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null

    # Install the new DLL first. The game is expected to be closed during deploy.
    Copy-Item `
        $OutputDll `
        (Join-Path $PluginDir "StrandedDeepTableStorage.dll") `
        -Force

    # Same BepInEx GUID, new product/file name: never leave the old DLL installed too.
    # State/config are intentionally NOT deleted.
    foreach ($legacyDll in @($LegacyPluginDll, $LegacyRootDll)) {
        if (Test-Path -LiteralPath $legacyDll) {
            Remove-Item -LiteralPath $legacyDll -Force
            Write-Host "Removed legacy runtime DLL: $legacyDll"
        }
    }

    # Remove diagnostics/inspectors. The main DLL is overwritten below.
    $old = @(
        "StrandedDeepKitchenTableDiagnostic.dll",
        "StrandedDeepKitchenTableInspector.dll",
        "StrandedDeepKitchenTableSlotStorageInspector.dll",
        "StrandedDeepKitchenTableSaveTrace.dll"
    )

    foreach ($name in $old) {
        $path = Join-Path $PluginDir $name

        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "Removed: $name"
        }
    }

    # Remove obsolete diagnostic reports only; persistent state is untouched.
    $ConfigDirs = @(
        (Join-Path $GameDir "BepInEx\config\StrandedDeepTableStorage"),
        (Join-Path $GameDir "BepInEx\config\StrandedDeepKitchenTable")
    )

    foreach ($ConfigDir in $ConfigDirs) {
        if (Test-Path $ConfigDir) {
            Get-ChildItem -LiteralPath $ConfigDir -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Name -like "KitchenTable-save-trace-*.txt" -or
                    $_.Name -like "*inspection*.txt" -or
                    $_.Name -like "*diagnostic*.txt"
                } |
                Remove-Item -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Deployed to: $PluginDir"
    Write-Host ""
    Write-Host "Table Storage:"
    Write-Host "  WOOD_TABLE       = 7 slots"
    Write-Host "  PLANK_TABLE      = 7 slots"
    Write-Host "  CORRUGATED_TABLE = 7 slots"
    Write-Host "  stackable items  = native stack x configurable 1..10 (default x3)"
    Write-Host "  slots            = fixed at 7 (safe slot migration deferred)"
    Write-Host "  display          = configurable on/off, 1/2/3 visual copies"
    Write-Host "  display labels   = disabled"
    Write-Host "  table physics    = land auto-level; raft-managed physics owned by Raft Furniture"
    Write-Host "  raft integration = explicit attachment registry/API first; parent/raycast fallback"
    Write-Host "  display tools    = flat-orientation override for tools/weapons/long handhelds"
    Write-Host "  display offsets  = configurable in BepInEx config"
    Write-Host ""
    Write-Host "Persistence:"
    Write-Host "  BepInEx\config\StrandedDeepTableStorage\Storage"
    Write-Host "  legacy Kitchen Table storage is copied once on first run and preserved"
    Write-Host "  snapshots are hooked directly to SaveManager.SaveGame"
    Write-Host ""
    Write-Host "Field-test build: diagnostic hotkeys disabled."
}
