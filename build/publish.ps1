<#
.SYNOPSIS
    Publishes Slate as a single .exe.

.DESCRIPTION
    Two flavours:

      Standalone   (default) - one .exe that runs on any Windows 10 1809+ / 11 x64
                               machine with no .NET installed. Around 150 MB because
                               the runtime travels with it.

      Slim         (-Slim)   - one small .exe (a few MB) that needs the
                               .NET 10 Desktop Runtime already installed.

    Both need the WebView2 runtime, which ships with Windows 11 and with Edge on
    Windows 10, so in practice it is already there.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -Slim -Output C:\Tools
#>
[CmdletBinding()]
param(
    [switch]$Slim,
    [string]$Runtime = 'win-x64',
    [string]$Output = "$PSScriptRoot\..\dist",
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\Slate\Slate.csproj'
$flavour = if ($Slim) { 'slim' } else { 'standalone' }
$target = Join-Path $Output $flavour

Write-Host "Publishing $flavour build for $Runtime -> $target" -ForegroundColor Cyan

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $target,
    '--nologo',
    "/p:SelfContained=$(if ($Slim) { 'false' } else { 'true' })",
    '/p:PublishSingleFile=true',
    '/p:IncludeNativeLibrariesForSelfExtract=true',
    # Bundle compression is only legal for self-contained publishes.
    "/p:EnableCompressionInSingleFile=$(if ($Slim) { 'false' } else { 'true' })",
    '/p:DebugType=embedded'
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $target 'Slate.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$loose = @(Get-ChildItem $target -File | Where-Object { $_.Name -ne 'Slate.exe' })

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $exe  ($sizeMb MB)"
if ($loose.Count -gt 0) {
    Write-Host "  plus $($loose.Count) supporting file(s): $(($loose | Select-Object -First 6 -ExpandProperty Name) -join ', ')"
}
Write-Host ""
Write-Host "Copy the .exe anywhere and run it. Settings live in %LOCALAPPDATA%\Slate."
