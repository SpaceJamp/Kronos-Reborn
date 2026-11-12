#Requires -Version 7
<#
.SYNOPSIS
    Signs a draft release's installer with the release key, then publishes the release.

.DESCRIPTION
    The app installs an update only if its installer is signed with the private key this script
    keeps on your PC. GitHub never sees that key, so even someone who takes over the repository or
    its workflows cannot ship an update the app will run.

    CI publishes each tagged release as a draft. This script downloads the draft's installer,
    signs "KronosReborn|<version>|<sha256>" (the text UpdateChecker.SignedMessage builds), uploads
    the signature as <installer>.sig, checks it, and only then makes the release public.

    Back up the key file somewhere offline. If it is lost, a new key means one release that users
    must install by hand, since the old app trusts only the old key.

.EXAMPLE
    .\sign-release.ps1 -NewKey
    Creates the key once and prints the public key to put in UpdateChecker.ReleasePublicKey.

.EXAMPLE
    .\sign-release.ps1 v0.1.2
    Signs and publishes the v0.1.2 draft.
#>
param(
    [Parameter(Position = 0)][string]$Tag,
    [switch]$NewKey,
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.kronos-reborn\update-signing-key.pem'),
    [string]$Repo = 'SpaceJamp/Kronos-Reborn'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($NewKey) {
    if (Test-Path $KeyPath) {
        throw "A key already exists at $KeyPath. Replacing it would stop every installed copy accepting updates."
    }

    New-Item -ItemType Directory -Force (Split-Path $KeyPath) | Out-Null
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    Set-Content -Path $KeyPath -Value $key.ExportPkcs8PrivateKeyPem() -NoNewline

    # Only this Windows account may read the private key.
    icacls $KeyPath /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null

    Write-Host "Private key written to $KeyPath. Back it up offline; it is never uploaded."
    Write-Host 'Public key, for UpdateChecker.ReleasePublicKey:'
    Write-Output ([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
    return
}

if (-not $Tag) { throw 'Give the release tag to sign, such as v0.1.2, or -NewKey to create the key.' }
if (-not (Test-Path $KeyPath)) { throw "No release key at $KeyPath. Create one with -NewKey." }

$release = gh release view $Tag --repo $Repo --json isDraft,assets | ConvertFrom-Json
if (-not $release.isDraft) { throw "$Tag is already public. Only drafts are signed, so nothing unsigned is ever offered." }

$installer = @($release.assets | Where-Object name -like '*-installer.exe')
if ($installer.Count -ne 1) { throw "Expected one installer in $Tag, found $($installer.Count)." }
$name = $installer[0].name

$work = Join-Path ([IO.Path]::GetTempPath()) "kronos-sign-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null
try {
    gh release download $Tag --repo $Repo --pattern $name --dir $work
    $file = Join-Path $work $name

    $sha = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $published = $installer[0].digest
    if ($published -ne "sha256:$sha") { throw "The downloaded installer ($sha) does not match GitHub's digest ($published)." }

    $version = $Tag.TrimStart('v', 'V')
    $message = [Text.Encoding]::UTF8.GetBytes("KronosReborn|$version|$sha")

    $key = [System.Security.Cryptography.ECDsa]::Create()
    $key.ImportFromPem((Get-Content $KeyPath -Raw))
    $signature = $key.SignData($message, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

    # Checked with the public key alone, the way the app will check it.
    $public = [System.Security.Cryptography.ECDsa]::Create()
    $public.ImportSubjectPublicKeyInfo($key.ExportSubjectPublicKeyInfo(), [ref]0)
    if (-not $public.VerifyData($message, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
        throw 'The signature did not verify.'
    }

    $sigFile = "$file.sig"
    Set-Content -Path $sigFile -Value ([Convert]::ToBase64String($signature)) -NoNewline
    gh release upload $Tag $sigFile --repo $Repo --clobber
    gh release edit $Tag --repo $Repo --draft=false | Out-Null

    Write-Host "Signed $name ($version, $sha) and published $Tag."
}
finally {
    Remove-Item -Recurse -Force $work
}
