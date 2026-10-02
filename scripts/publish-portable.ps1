[CmdletBinding()]
param(
    [string]$FfmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish'
$publishStaging = Join-Path $root '.publish-staging'
$toolCache = Join-Path $root '.tools\ffmpeg-win64-lgpl'
$whisperModel = Join-Path $root '.tools\whisper\ggml-small.en.bin'
$whisperRuntime = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages\whisper.net.runtime\1.9.1\build\win-x64'
$archive = Join-Path $root '.tools\ffmpeg-win64-lgpl.zip'
$extract = Join-Path $root '.tools\ffmpeg-extract'
$appProject = Join-Path $root 'src\PortableScreenRecorder.App\PortableScreenRecorder.App.csproj'

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path (Join-Path $toolCache 'ffmpeg.exe'))) {
    New-Item -ItemType Directory -Force -Path (Split-Path $archive) | Out-Null
    Invoke-WebRequest -Uri $FfmpegUrl -OutFile $archive
    if (Test-Path $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
    $ffmpeg = Get-ChildItem -LiteralPath $extract -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
    $ffprobe = Get-ChildItem -LiteralPath $extract -Filter 'ffprobe.exe' -Recurse | Select-Object -First 1
    if (-not $ffmpeg -or -not $ffprobe) { throw 'The FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe.' }
    New-Item -ItemType Directory -Force -Path $toolCache | Out-Null
    Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $toolCache 'ffmpeg.exe')
    Copy-Item -LiteralPath $ffprobe.FullName -Destination (Join-Path $toolCache 'ffprobe.exe')
    $license = Get-ChildItem -LiteralPath $extract -File -Recurse |
        Where-Object { $_.Name -match '^(LICENSE|COPYING)(\..*)?$' } |
        Select-Object -First 1
    if ($license) { Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $toolCache 'FFmpeg-LICENSE.txt') }
}

if (-not (Test-Path $whisperModel) -or (Get-Item -LiteralPath $whisperModel).Length -ne 487614201) {
    New-Item -ItemType Directory -Force -Path (Split-Path $whisperModel) | Out-Null
    Invoke-WebRequest -Uri 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin' -OutFile $whisperModel
}

if (Test-Path $publishStaging) { Remove-Item -LiteralPath $publishStaging -Recurse -Force }
dotnet restore $appProject -r win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish $appProject -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $publishStaging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$copilotBuildOutput = Join-Path $root 'src\PortableScreenRecorder.App\bin\Release'
foreach ($runtimeIdentifier in @('win-x64', 'win-arm64')) {
    $copilotRuntime = Get-ChildItem -LiteralPath $copilotBuildOutput -Filter 'copilot-runtime.exe' -File -Recurse |
        Where-Object { $_.DirectoryName.EndsWith("runtimes\$runtimeIdentifier\native", [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if (-not $copilotRuntime) {
        throw "The GitHub Copilot $runtimeIdentifier runtime was not produced by the build."
    }

    $copilotDestination = Join-Path $publishStaging "runtimes\$runtimeIdentifier\native"
    if (Test-Path $copilotDestination) {
        Remove-Item -LiteralPath $copilotDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $copilotDestination | Out-Null
    Copy-Item -Path (Join-Path $copilotRuntime.DirectoryName '*') -Destination $copilotDestination -Recurse
}

$tools = Join-Path $publishStaging 'Tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
Copy-Item -LiteralPath (Join-Path $toolCache 'ffmpeg.exe') -Destination $tools
Copy-Item -LiteralPath (Join-Path $toolCache 'ffprobe.exe') -Destination $tools
$models = Join-Path $publishStaging 'Models'
New-Item -ItemType Directory -Force -Path $models | Out-Null
Copy-Item -LiteralPath $whisperModel -Destination $models
$whisperRuntimeOutput = Join-Path $publishStaging 'runtimes\win-x64'
New-Item -ItemType Directory -Force -Path $whisperRuntimeOutput | Out-Null
Copy-Item -Path (Join-Path $whisperRuntime '*.dll') -Destination $whisperRuntimeOutput
New-Item -ItemType Directory -Force -Path (Join-Path $publishStaging 'Data\Logs') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $publishStaging 'LICENSES') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $publishStaging 'README.txt')
Copy-Item -LiteralPath (Join-Path $root 'ThirdPartyNotices.txt') -Destination $publishStaging
Copy-Item -Path (Join-Path $root 'LICENSES\*') -Destination (Join-Path $publishStaging 'LICENSES') -Recurse
if (Test-Path (Join-Path $toolCache 'FFmpeg-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $toolCache 'FFmpeg-LICENSE.txt') -Destination (Join-Path $publishStaging 'LICENSES')
}

$ffmpegVersion = (& (Join-Path $tools 'ffmpeg.exe') -version | Select-Object -First 1)
$manifest = [ordered]@{
    application = 'PortableScreenRecorder'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    target = 'win-x64'
    framework = 'net10.0-windows10.0.19041.0'
    selfContained = $true
    singleFile = $true
    ffmpeg = $ffmpegVersion
    executableSha256 = (Get-FileHash (Join-Path $publishStaging 'PortableScreenRecorder.App.exe') -Algorithm SHA256).Hash
    ffmpegSha256 = (Get-FileHash (Join-Path $tools 'ffmpeg.exe') -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishStaging 'version-manifest.json') -Encoding utf8

& (Join-Path $PSScriptRoot 'verify-publish.ps1') -PublishDirectory $publishStaging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force -Path $publish | Out-Null
Copy-Item -Path (Join-Path $publishStaging '*') -Destination $publish -Recurse -Force
Remove-Item -LiteralPath $publishStaging -Recurse -Force

& (Join-Path $PSScriptRoot 'verify-publish.ps1') -PublishDirectory $publish
exit $LASTEXITCODE
