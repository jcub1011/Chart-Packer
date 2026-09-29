<#
.SYNOPSIS
    Publishes ChartPacker as a Native AOT build, without debug symbols, to <repo>/publish/<rid>/.

.DESCRIPTION
    The output holds the executable and every native library it loads: SDL3, Avalonia's Skia,
    HarfBuzz and ANGLE libraries, and FFmpeg. FFmpeg isn't in the repo, so build or fetch it first
    (see build/ffmpeg/README.md); the script stops if it's missing, or is a build from before the
    audio decoders were added, rather than publish a build without video preview or with the
    slower managed audio decoders.

    Native AOT can't cross-compile to another OS, so -Runtime picks an architecture of this one.

.EXAMPLE
    pwsh build/publish.ps1
.EXAMPLE
    pwsh build/publish.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'ChartPacker/ChartPacker.csproj'
$outputDir = Join-Path $repoRoot "publish/$Runtime"

# ChartPacker.Ffmpeg copies these beside the executable, but only if they're there to copy.
$ffmpegDir = Join-Path $repoRoot "ChartPacker.Ffmpeg/runtimes/$Runtime/native"
if (-not (Test-Path (Join-Path $ffmpegDir '*avcodec*'))) {
    throw "FFmpeg isn't in $ffmpegDir. Build or fetch it for $Runtime first; see build/ffmpeg/README.md."
}

# Only builds with the audio decoders have swresample, which the Opus decoder needs. Without it, audio
# still plays, on the managed decoders.
if (-not (Test-Path (Join-Path $ffmpegDir '*swresample*'))) {
    throw "The FFmpeg in $ffmpegDir has no swresample, so it predates the audio decoders. Rebuild it for $Runtime with build/ffmpeg/build.sh."
}

if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --output $outputDir `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:CopyOutputSymbolsToPublishDirectory=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# The properties above cover the app's own symbols; this catches any a package ships.
Get-ChildItem $outputDir -Recurse -Include '*.pdb', '*.dbg' | Remove-Item -Force

$files = Get-ChildItem $outputDir -Recurse -File | Sort-Object FullName
$files | Format-Table @{ Label = 'File'; Expression = { [IO.Path]::GetRelativePath($outputDir, $_.FullName) } },
    @{ Label = 'Size (KB)'; Expression = { '{0:N0}' -f ($_.Length / 1KB) }; Align = 'Right' } -AutoSize
$totalMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Published $($files.Count) files ($totalMb MB) to $outputDir"
