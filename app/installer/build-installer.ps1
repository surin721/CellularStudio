# Builds dist\CellularStudio-<version>-x64.msi
#   1. publishes the app (self-contained single file) to publish\
#   2. regenerates installer artwork
#   3. compiles installer\Package.wxs with WiX v5
# Requirements: per-user .NET SDK at %LOCALAPPDATA%\Microsoft\dotnet, and the WiX tool + UI/Util extensions:
#   dotnet tool install --global wix --version 5.0.2
#   wix extension add -g WixToolset.UI.wixext/5.0.2 ; wix extension add -g WixToolset.Util.wixext/5.0.2
#
# Code signing: the exe (before packaging) and the MSI are Authenticode-signed with the certificate whose
# thumbprint is given by -CertThumbprint or $env:CS_SIGN_THUMBPRINT; otherwise the newest code-signing cert in
# Cert:\CurrentUser\My whose subject contains "Cellular Studio code signing" is used. Signatures are timestamped
# (RFC 3161) so they stay valid after the certificate expires. -SkipSign builds unsigned.
param(
    [switch]$SkipPublish,
    [switch]$SkipSign,
    [string]$CertThumbprint = $env:CS_SIGN_THUMBPRINT,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

function Get-SigningCert {
    if ($CertThumbprint) {
        $c = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $CertThumbprint | Select-Object -First 1
        if (-not $c) { throw "Code-signing certificate $CertThumbprint not found (CurrentUser\My or LocalMachine\My)." }
        return $c
    }
    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like '*Cellular Studio code signing*' -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $c) { throw 'No code-signing certificate found. Pass -CertThumbprint, set CS_SIGN_THUMBPRINT, or use -SkipSign.' }
    $c
}

function Invoke-Sign([string]$Path, $Cert) {
    for ($i = 1; $i -le 3; $i++) {
        $r = Set-AuthenticodeSignature -FilePath $Path -Certificate $Cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
        if ($r.SignerCertificate -and $r.TimeStamperCertificate) { break }
        Write-Warning "Signing attempt $i for $(Split-Path $Path -Leaf): $($r.StatusMessage)"
        Start-Sleep 3
    }
    $v = Get-AuthenticodeSignature -FilePath $Path
    if (-not $v.SignerCertificate -or $v.SignerCertificate.Thumbprint -ne $Cert.Thumbprint) { throw "Signing failed for $Path : $($v.StatusMessage)" }
    if (-not $v.TimeStamperCertificate) { Write-Warning "$Path is signed but NOT timestamped." }
    "  signed {0}  [{1}]" -f (Split-Path $Path -Leaf), $v.Status
}

$signCert = if ($SkipSign) { $null } else { Get-SigningCert }
if ($signCert) { "Signing with: $($signCert.Subject)  ($($signCert.Thumbprint))" }
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

# sign the app before it is packaged, so the installed exe carries the signature
if ($signCert) { Invoke-Sign (Join-Path $app 'publish\CellularStudio.exe') $signCert }

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
if ($signCert) { Invoke-Sign $msi $signCert }
$item = Get-Item $msi
"Built {0} ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB)
