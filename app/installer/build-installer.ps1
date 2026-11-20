# Builds dist\CellularStudio-<version>-x64.msi
#   1. publishes the app (self-contained single file) to publish\
#   2. regenerates installer artwork
#   3. compiles installer\Package.wxs with WiX v5
# Requirements: per-user .NET SDK at %LOCALAPPDATA%\Microsoft\dotnet, and the WiX tool + UI/Util extensions:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.UI.wixext/5.0.2 ; wix extension add -g WixToolset.Util.wixext/5.0.2
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$app = Split-Path $here -Parent
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:DOTNET_ROLL_FORWARD = 'Major'        # wix 5 targets an older runtime; only .NET 10 is installed
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'

# version from Directory.Build.props
[xml]$props = Get-Content (Join-Path $app 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version not found in Directory.Build.props' }

if (-not $SkipPublish) {
    Get-Process CellularStudio -ErrorAction SilentlyContinue | Stop-Process -Force
    & $dotnet publish (Join-Path $app 'src\CellularStudio') -c Release -o (Join-Path $app 'publish') --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -v q -nologo
    if ($LASTEXITCODE) { throw 'publish failed' }
}

& (Join-Path $here 'make-artwork.ps1') | Out-Null

$dist = Join-Path $app 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$msi = Join-Path $dist "CellularStudio-$version-x64.msi"

Push-Location $here
try {
    & $wix build Package.wxs -arch x64 `
        -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -d Version=$version -d PublishDir="$app\publish" -d SrcDir="$app\src" -d AppDir="$app" `
        -o $msi
    if ($LASTEXITCODE) { throw 'wix build failed' }
}
finally { Pop-Location }

Remove-Item (Join-Path $dist '*.wixpdb') -ErrorAction SilentlyContinue
$item = Get-Item $msi
"Built {0} ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB)
