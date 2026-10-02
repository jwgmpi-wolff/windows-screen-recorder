[CmdletBinding()]
param(
    [string]$InnoCompiler
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$definition = Join-Path $root 'installer\PortableScreenRecorder.iss'
$output = Join-Path $root 'installer\output\install.exe'

& (Join-Path $PSScriptRoot 'verify-publish.ps1') -PublishDirectory (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $InnoCompiler = $command.Source
    }
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $knownPaths = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $InnoCompiler = $knownPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

& $InnoCompiler $definition
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw 'Inno Setup completed without producing installer\output\install.exe.'
}

$installer = Get-Item -LiteralPath $output
Write-Host ("Installer created: {0} ({1:N1} MB)" -f $installer.FullName, ($installer.Length / 1MB))
