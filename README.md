# PortableScreenRecorder

PortableScreenRecorder is a local-only Windows desktop recorder for a single display, all attached displays, or a selected display region. It captures the pointer optionally, records microphone and/or Windows playback audio, supports pause/resume, and finalizes H.264/AAC MP4 files.

<!-- project-downloads:start -->
## APK and installer downloads

Every available project package is indexed below, including older versions. Prefer the newest stable version for your platform and architecture. Debug APKs and prereleases are labeled; archives are not necessarily standalone installers.

### Release downloads: all published versions

| Version / source | Package | Download |
| --- | --- | --- |
| v1.1.0 | Windows installer | [install.exe](https://github.com/jwgmpi-wolff/windows-screen-recorder/releases/download/v1.1.0/install.exe) |

### Setup scripts and source

- [Setup script: install.cmd](./install.cmd)
- [Setup script: install.ps1](./install.ps1)

- [Download the complete source ZIP](https://github.com/jwgmpi-wolff/windows-screen-recorder/archive/refs/heads/main.zip). Extract the complete checkout before running setup scripts unless the project instructions explicitly support standalone installation.
- [Browse all releases and their assets](https://github.com/jwgmpi-wolff/windows-screen-recorder/releases).

Android may require approval to install apps from the download source. Follow the project-specific installation, signing, checksum, and prerequisite instructions elsewhere in this README.
<!-- project-downloads:end -->

## Install on Windows

Download and run **[install.exe](https://github.com/jwgmpi-wolff/windows-screen-recorder/releases/download/v1.1.0/install.exe)**.

If you cloned this private repository, double-click `install.cmd`. It downloads
the release installer with GitHub CLI, verifies its SHA-256 checksum, and starts
setup. If needed, authenticate first:

```powershell
gh auth login
.\install.ps1
```

The installer is also listed on the
**[v1.1.0 release page](https://github.com/jwgmpi-wolff/windows-screen-recorder/releases/tag/v1.1.0)**.

## Supported systems

- Windows 10 version 1903 or later and Windows 11
- Windows x64 for the portable release
- Direct3D 11 Desktop Duplication support

Normal recording does not require administrator rights, installation, registry setup, an account, or a network connection. Recordings remain local.

## Features

- Single-monitor DXGI capture, including monitors at negative desktop coordinates
- Physical-layout all-monitor composition with black fill in unused areas
- Per-monitor, DPI-aware rectangular region selector
- Optional pointer capture
- No audio, microphone, system-loopback, or mixed microphone/system audio
- Optional local speech recognition during recording with TXT and SRT sidecars
- Standalone speech-to-text window with live text, copy, save, and Copilot prompt/response actions
- 30 or 60 FPS; configurable video and AAC bitrates
- Safe `.partial.mp4` finalization and collision-free file naming
- Pause/resume that omits paused time by joining independently finalized segments
- Global start/stop and pause/resume hotkeys with conflict reporting
- Portable JSON settings and rotating logs under `Data`

## Build

Prerequisites: .NET 10 SDK and Windows SDK 10.0.19041 or newer.

```powershell
.\scripts\build.ps1
```

Direct commands:

```powershell
dotnet restore .\PortableScreenRecorder.slnx
dotnet build .\PortableScreenRecorder.slnx -c Release --no-restore
dotnet test .\PortableScreenRecorder.slnx -c Release --no-build
```

## Publish

```powershell
.\scripts\publish-portable.ps1
```

The self-contained x64 application is written to `publish`. The script downloads the latest BtbN LGPL x64 static FFmpeg build, copies `ffmpeg.exe` and `ffprobe.exe` into `publish\Tools`, copies its license, and creates `version-manifest.json`.

To create the per-user Windows installer after publishing:

```powershell
.\scripts\build-installer.ps1
```

This writes `installer\output\install.exe`. The installer adds a Start menu shortcut, offers an optional desktop shortcut, and does not require administrator rights.

Verify it with:

```powershell
.\scripts\verify-publish.ps1
.\scripts\smoke-test.ps1
```

## Audio

NAudio opens the selected Core Audio endpoint in shared WASAPI mode. System audio uses render-endpoint loopback; it is not obtained from a microphone. Native endpoint formats are written to temporary WAV files. FFmpeg converts each source to 48 kHz stereo float audio, applies the independent gain settings, mixes with a limiter, uses asynchronous resampling for drift correction, and encodes AAC.

## Speech recognition

The **Speech recognition** section offers three user-selected modes:

- **Off - record normally:** no speech recognition.
- **Record + transcript:** records normally while Windows recognizes the selected speech input. Live text appears in the main window, and final `<recording>.txt` and `<recording>.srt` files are saved beside the MP4.
- **Speech-to-text only:** opens a separate dictation window without starting screen recording. It can copy text or save TXT and SRT files.

Select **SystemAudio**, **DefaultMicrophone**, or **MicrophoneAndSystem**, then select a confidence threshold. System and mixed input use WASAPI loopback, convert the incoming sources to 16 kHz mono, and transcribe each phrase after a short silence with the bundled English Whisper small model. Default-microphone-only input uses the installed Windows speech language. All recognition is on-device; audio is never uploaded.

The standalone window uses expandable sections for speech controls, transcript,
prompt, and response. Each text area scrolls independently, so long transcripts
and Copilot answers remain readable on smaller or high-DPI screens. Select
**Use Transcript** to place the current transcript in the prompt box, then
select **Send to Copilot**. The response remains silent unless **Read response
aloud** is checked. Copilot requests require a GitHub account with Copilot
access and use the authentication managed by the bundled GitHub Copilot CLI
runtime. Prompt text is sent to GitHub Copilot only when **Send to Copilot** is
selected.

If Copilot authentication changes while a session is starting, the app retries
once automatically. If sign-in still fails, verify that GitHub Copilot is
signed in, close any other pending Copilot sign-in prompt, and try again.

## Multi-monitor behavior

Each active DXGI output is captured independently. FFmpeg `xstack` places frames at their physical offsets inside the virtual-desktop bounding rectangle. It neither stretches monitors nor removes gaps. H.264 requires even frame dimensions, so a one-pixel black pad may be added on the right or bottom.

## Privacy

Recording starts only from an explicit button or registered global hotkey. The application shows its state and a red indicator throughout recording, has no auto-start or hidden mode, and never uploads media. See [privacy.md](docs/privacy.md).

## Known limitations

- DRM/protected surfaces may appear black because Windows blocks their capture.
- A display or graphics-device topology change ends the current FFmpeg segment with an explicit error; refresh displays and start again.
- Display capture depends on a hardware Media Foundation H.264 encoder being available. Supported Windows 10/11 systems with current graphics drivers normally provide it.
- If captured motion freezes while audio continues, check GPU utilization and close GPU-heavy applications. The recorder uses variable-frame-rate timestamps to preserve synchronization when a desktop frame is delayed, but it cannot recover screen updates that Windows did not deliver under sustained GPU saturation.
- Audio level meters are shown while recording. Pre-recording monitoring is intentionally not opened continuously to avoid holding devices when idle.
- Whisper significantly improves broadcast-dialogue accuracy, but music, overlapping speakers, and simultaneous microphone/system speech can still reduce accuracy. The bundled model is English-only.
- SRT timing follows active recording time and omits paused intervals. Set minimum confidence to 0% for best-effort dictation; higher values intentionally discard uncertain Windows recognition results.
- Automated hardware tests enumerate endpoints and verify encoder features. Microphone/system-audio quality requires a non-silent physical endpoint and is covered by the diagnostic workflow rather than unattended CI.

## Troubleshooting

See [troubleshooting.md](docs/troubleshooting.md). Logs are under `Data\Logs` beside the executable and can be opened from the UI.

## Third-party software

The application uses CommunityToolkit.Mvvm, Microsoft.Extensions, System.Speech, NAudio, Whisper.net/whisper.cpp, the OpenAI Whisper model, the GitHub Copilot SDK/CLI, Serilog, and a separately executed LGPL FFmpeg build. See [ThirdPartyNotices.txt](ThirdPartyNotices.txt) and [LICENSES](LICENSES).

## Copyright

Copyright (c) 2026 Jerry Wolff. See [COPYRIGHT](COPYRIGHT) for the scope of this
notice. Existing licenses and third-party attribution remain unchanged; this
notice does not grant additional permissions.
