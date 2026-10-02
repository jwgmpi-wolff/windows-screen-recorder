[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'PortableScreenRecorder.slnx'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found.'
}

$sdkVersion = [Version]((dotnet --version) -replace '-.*$')
if ($sdkVersion.Major -lt 10) {
    throw ".NET SDK 10 or newer is required; found $sdkVersion."
}

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $solution -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet test $solution -c Release --no-build
exit $LASTEXITCODE
