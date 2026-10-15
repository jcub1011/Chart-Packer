<#
.SYNOPSIS
    Publishes Chart Packer and Chart Builder as Native AOT builds, without debug symbols, to <repo>/publish/<rid>/,
    with, on Windows, the .sng thumbnail handler (ChartPacker.Thumbnails.dll) the installer registers.

.DESCRIPTION
    The two apps are installed together, so they're published into one folder. Each is published on its
    own first, then the two are merged; a file both have must be identical in each, which
    Directory.Packages.props ensures for the native libraries, and the script stops if one isn't.

    The output holds the executables and every native library they load: SDL3, Avalonia's Skia,
    HarfBuzz and ANGLE libraries, and FFmpeg. The thumbnail handler is a Native AOT library of its own, which
    Explorer loads; only its DLL is kept. FFmpeg isn't in the repo. If it's missing, or is a
    build from before the audio decoders were added, the script offers to build it with
    build/ffmpeg/build.sh (see build/ffmpeg/README.md). If you decline, it stops rather than publish
    a build without video preview or with the slower managed audio decoders.

    Native AOT can't cross-compile to another OS, so -Runtime picks an architecture of this one.

.PARAMETER Yes
    Builds FFmpeg, and installs what that needs, without asking.

.EXAMPLE
    pwsh build/publish.ps1
.EXAMPLE
    pwsh build/publish.ps1 -Runtime win-arm64
.EXAMPLE
    pwsh build/publish.ps1 -Yes
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [switch] $Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Each app's project is <repo>/<name>/<name>.csproj, and its executable is named after it.
$apps = 'ChartPacker', 'ChartBuilder'
# Explorer's thumbnail handler for .sng files; Windows only. Its project is <repo>/<name>/<name>.csproj too.
$thumbnails = 'ChartPacker.Thumbnails'
$isWindowsRuntime = $Runtime -like 'win-*'
$outputDir = Join-Path $repoRoot "publish/$Runtime"
$stagingDir = Join-Path $repoRoot "publish/$Runtime.staging"

# ChartShared.Ffmpeg copies these beside the executables, but only if they're there to copy.
$ffmpegDir = Join-Path $repoRoot "ChartShared.Ffmpeg/runtimes/$Runtime/native"

# Why the FFmpeg in $ffmpegDir can't be published, or $null when it can. Only builds with the audio
# decoders have swresample, which the Opus decoder needs; without it, audio would play on the managed decoders.
function Get-FfmpegProblem {
    if (-not (Test-Path (Join-Path $ffmpegDir '*avcodec*'))) {
        return "FFmpeg isn't in $ffmpegDir."
    }
    if (-not (Test-Path (Join-Path $ffmpegDir '*swresample*'))) {
        return "The FFmpeg in $ffmpegDir has no swresample, so it predates the audio decoders."
    }
    return $null
}

# The bash to run build/ffmpeg/build.sh with, as the command and the arguments that come before the script.
# On Windows that's Git Bash, from which the script moves itself into MSYS2, or MSYS2's own. Not
# System32's bash, which is WSL's and can't see Windows paths as given.
function Get-Bash {
    if (-not $IsWindows) {
        $bash = Get-Command bash -ErrorAction SilentlyContinue
        return $bash ? (, @($bash.Source)) : $null
    }
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        # git.exe is in Git's cmd/ or mingw64/bin/, and bash.exe in its bin/.
        $gitRoot = Split-Path (Split-Path $git.Source)
        foreach ($gitBash in (Join-Path $gitRoot 'bin/bash.exe'), (Join-Path (Split-Path $gitRoot) 'bin/bash.exe')) {
            if (Test-Path $gitBash) {
                return , @($gitBash)
            }
        }
    }
    $msys2 = $env:MSYS2_ROOT ?? 'C:\msys64'
    $shell = $Runtime -eq 'win-arm64' ? 'CLANGARM64' : 'UCRT64'
    if (Test-Path (Join-Path $msys2 'usr/bin/bash.exe')) {
        return , @((Join-Path $msys2 'usr/bin/env.exe'), "MSYSTEM=$shell", 'CHERE_INVOKING=1', (Join-Path $msys2 'usr/bin/bash.exe'), '-l')
    }
    return $null
}

$problem = Get-FfmpegProblem
if ($problem) {
    $buildScript = (Join-Path $repoRoot 'build/ffmpeg/build.sh') -replace '\\', '/'
    $bash = Get-Bash
    if (-not $bash) {
        throw "$problem Build it for $Runtime with build/ffmpeg/build.sh, which needs bash (on Windows, Git Bash or MSYS2); see build/ffmpeg/README.md."
    }
    if (-not $Yes) {
        if ([Console]::IsInputRedirected) {
            throw "$problem Build it for $Runtime with build/ffmpeg/build.sh, or pass -Yes to have this script run it."
        }
        Write-Host $problem
        $answer = Read-Host "Build it now with build/ffmpeg/build.sh $Runtime? This takes several minutes. [y/N]"
        if ($answer -notmatch '^[Yy]') {
            throw "$problem Build it for $Runtime with build/ffmpeg/build.sh; see build/ffmpeg/README.md."
        }
    }
    $buildArgs = @($bash | Select-Object -Skip 1) + $buildScript + ($Yes ? @('--yes') : @()) + $Runtime
    & $bash[0] @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "build/ffmpeg/build.sh $Runtime failed with exit code $LASTEXITCODE."
    }
    $problem = Get-FfmpegProblem
    if ($problem) {
        throw "$problem build/ffmpeg/build.sh $Runtime finished, but didn't put it there."
    }
}

foreach ($dir in $outputDir, $stagingDir) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
    }
}

foreach ($app in $apps) {
    $appDir = Join-Path $stagingDir $app
    dotnet publish (Join-Path $repoRoot "$app/$app.csproj") `
        --configuration $Configuration `
        --runtime $Runtime `
        --output $appDir `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:CopyOutputSymbolsToPublishDirectory=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish of $app failed with exit code $LASTEXITCODE."
    }

    # The properties above cover the app's own symbols; this catches any a package ships.
    Get-ChildItem $appDir -Recurse -Include '*.pdb', '*.dbg' | Remove-Item -Force
}

$parts = $apps
if ($isWindowsRuntime) {
    $thumbnailsDir = Join-Path $stagingDir $thumbnails
    dotnet publish (Join-Path $repoRoot "$thumbnails/$thumbnails.csproj") `
        --configuration $Configuration `
        --runtime $Runtime `
        --output $thumbnailsDir `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:CopyOutputSymbolsToPublishDirectory=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish of $thumbnails failed with exit code $LASTEXITCODE."
    }

    # A native library's publish also leaves its import library and the like; only the DLL is loaded.
    Get-ChildItem $thumbnailsDir -Recurse -File | Where-Object Name -ne "$thumbnails.dll" | Remove-Item -Force
    $parts += $thumbnails
}

# Merges the apps, and the thumbnail handler, into one folder. A file two have is copied once, and must be
# byte-for-byte the same in each: two different copies of a native library can't share a folder.
New-Item -ItemType Directory $outputDir | Out-Null
foreach ($app in $parts) {
    $appDir = Join-Path $stagingDir $app
    foreach ($file in Get-ChildItem $appDir -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($appDir, $file.FullName)
        $target = Join-Path $outputDir $relative
        if (Test-Path $target) {
            if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) {
                throw "$relative differs between the apps, so they can't share $outputDir. Both must build against the same packages (Directory.Packages.props)."
            }
            continue
        }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}
Remove-Item $stagingDir -Recurse -Force

$files = Get-ChildItem $outputDir -Recurse -File | Sort-Object FullName
$files | Format-Table @{ Label = 'File'; Expression = { [IO.Path]::GetRelativePath($outputDir, $_.FullName) } },
    @{ Label = 'Size (KB)'; Expression = { '{0:N0}' -f ($_.Length / 1KB) }; Align = 'Right' } -AutoSize
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Published $($files.Count) files ($totalMb MB) to $outputDir"
