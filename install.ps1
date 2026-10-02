[CmdletBinding()]
param(
    [string]$InstallerPath,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$repository = 'jwgmpi-wolff/windows-screen-recorder'
$tag = 'v1.1.0'
$expectedHash = '87579e43d106d19b5859b6dce7f8d90dc1a5aedbc9cebdea2802dfa48bcaabfe'

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $downloadDirectory = Join-Path $env:TEMP 'PortableScreenRecorderInstaller'
    New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
    $InstallerPath = Join-Path $downloadDirectory 'install.exe'

    $github = Get-Command 'gh.exe' -ErrorAction SilentlyContinue
    if ($github) {
        & $github.Source release download $tag `
            --repo $repository `
            --pattern 'install.exe' `
            --dir $downloadDirectory `
            --clobber
        if ($LASTEXITCODE -ne 0) {
            throw 'GitHub CLI could not download the installer. Run gh auth login, then try again.'
        }
    }
    else {
        $downloadUrl = "https://github.com/$repository/releases/download/$tag/install.exe"
        try {
            Invoke-WebRequest -Uri $downloadUrl -OutFile $InstallerPath
        }
        catch {
            throw "The installer could not be downloaded. This repository is private, so install GitHub CLI, run 'gh auth login', and run install.cmd again."
        }
    }
}

$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
    throw "Installer not found: $InstallerPath"
}

$actualHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "Installer verification failed. Expected SHA-256 $expectedHash but received $actualHash."
}

if ($VerifyOnly) {
    Write-Host "Installer verified: $InstallerPath"
    return
}

Write-Host 'Installer verified. Starting Portable Screen Recorder setup...'
$installer = Start-Process -FilePath $InstallerPath -PassThru -Wait
if ($installer.ExitCode -ne 0) {
    throw "Installer exited with code $($installer.ExitCode)."
}
