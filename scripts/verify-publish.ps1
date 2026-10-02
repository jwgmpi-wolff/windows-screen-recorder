[CmdletBinding()]
param(
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $root 'publish'
}
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$required = @(
    'PortableScreenRecorder.App.exe',
    'Tools\ffmpeg.exe',
    'Tools\ffprobe.exe',
    'runtimes\win-x64\native\copilot.exe',
    'runtimes\win-x64\native\copilot-runtime.exe',
    'runtimes\win-x64\native\runtime.node',
    'runtimes\win-arm64\native\copilot.exe',
    'runtimes\win-arm64\native\copilot-runtime.exe',
    'runtimes\win-arm64\native\runtime.node',
    'README.txt',
    'ThirdPartyNotices.txt',
    'version-manifest.json'
)
foreach ($relative in $required) {
    $path = Join-Path $PublishDirectory $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required publish file is missing: $relative" }
}

$ffmpegOutput = & (Join-Path $PublishDirectory 'Tools\ffmpeg.exe') -hide_banner -filters 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $ffmpegOutput -notmatch 'ddagrab') {
    throw 'The packaged FFmpeg executable does not expose the ddagrab filter.'
}

$process = Start-Process -FilePath (Join-Path $PublishDirectory 'PortableScreenRecorder.App.exe') -WorkingDirectory $PublishDirectory -PassThru
Start-Sleep -Seconds 4
if ($process.HasExited) {
    throw "The published application exited during startup with code $($process.ExitCode)."
}
Stop-Process -Id $process.Id
$process.WaitForExit()

$sourceRootText = [IO.Path]::GetFullPath($root)
$settings = Join-Path $PublishDirectory 'Data\settings.json'
if (Test-Path $settings) {
    $settingsText = Get-Content -LiteralPath $settings -Raw
    if ($settingsText.Contains($sourceRootText, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Published settings contain a source-tree path.'
    }
}

$size = (Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host ("Publish verified: {0:N1} MB at {1}" -f ($size / 1MB), $PublishDirectory)
