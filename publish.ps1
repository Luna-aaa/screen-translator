<#
    Produces the single self-contained ScreenTranslator.exe in .\dist.

    Self-contained: the .NET runtime is bundled, so the target machine needs nothing
    installed. Single-file + compression trades ~200ms of startup for roughly half the
    size. Trimming is deliberately NOT enabled - WPF does not survive it.

    Usage:  .\publish.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ScreenTranslator\ScreenTranslator.csproj'

Write-Host "publishing $Configuration/$Runtime -> $OutDir" -ForegroundColor Cyan

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:GenerateDocumentationFile=false `
    -o $OutDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $OutDir 'ScreenTranslator.exe'
if (-not (Test-Path $exe)) { throw "expected $exe to exist after publish" }

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "done: $exe  ($sizeMb MB)" -ForegroundColor Green
