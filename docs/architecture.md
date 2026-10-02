# Architecture

## Environment decision

The implementation was selected after verifying .NET SDK 10.0.401, Windows SDK 10.0.26100, Visual Studio Build Tools 17.14, and FFmpeg support for `ddagrab`, `xstack`, `h264_mf`, native AAC, and `aac_mf`.

## Capture API

DXGI Desktop Duplication via FFmpeg's `ddagrab` Direct3D 11 source filter is used instead of Windows.Graphics.Capture. Desktop Duplication exposes deterministic output-index capture, source-side region offsets, optional pointer drawing, and multiple concurrent output sources without a Store capability or picker. It works on the required Windows versions and permits a self-contained application to keep native Direct3D lifetime and device-loss handling inside the proven FFmpeg process.

Single-display and selected-region modes instantiate one `ddagrab` source. All-display mode instantiates one source per active output and composes frames using `xstack` at normalized virtual-desktop coordinates. Unused layout areas are black. Software download and BGRA-to-NV12 conversion occur in FFmpeg; managed code never copies video frames.

## Audio API

NAudio 3.1 opens Windows Core Audio endpoints. `WasapiCapture` records the selected microphone and `WasapiLoopbackCapture` records the selected playback endpoint. Capture callbacks write directly to streaming WAV writers and calculate bounded peak levels; there is no unbounded managed queue.

## Encoding

An LGPL BtbN static FFmpeg build is shipped as a separate executable. H.264 is encoded by the hardware Windows Media Foundation `h264_mf` encoder from NV12 frames. Capture uses variable-frame-rate timestamps so delayed desktop frames retain their wall-clock presentation time instead of causing the muxer to manufacture extra constant-rate frames. AAC uses FFmpeg's native encoder at 48 kHz stereo.

Each active interval is recorded as one finalized segment. Pause closes the segment and stops both audio devices. Resume opens a new segment. Stop muxes any WAV sources into each video segment, then concatenates segments without re-encoding. This removes paused wall-clock time and preserves monotonic per-segment timestamps.

Audio conversion uses `aresample=48000:async=1:first_pts=0`. Independent gain filters precede `amix`; `alimiter` controls clipping. `async=1` inserts or removes a small number of samples to correct endpoint-clock drift while timestamps stay aligned to the segment.

## Speech transcription

The Transcription project uses local Whisper through Whisper.net/whisper.cpp for system and mixed audio. The bundled `small.en` GGML model runs locally. Default-microphone-only input retains Windows SAPI through the official `System.Speech` package.

Recording transcription runs alongside capture and can listen to the default microphone, selected playback endpoint, or both. Microphone-only input uses SAPI's live device path. System and mixed input use WASAPI capture, downmix and resample to 16 kHz mono PCM, detect phrase-ending silence, and submit completed local phrase buffers to Whisper. Accepted segments are timestamped against active recording duration. Pause stops recognition and resume restarts it, so paused wall time is omitted. Stop writes UTF-8 TXT and SRT sidecars beside the finalized MP4.

The standalone speech-to-text window creates an independent recognizer instance and does not require screen capture. It provides live partial text, final text, confidence filtering, clipboard copy, and TXT/SRT saving. Recognition remains local.

## Process and failure boundaries

The managed process owns state, settings, endpoint selection, temporary files, and final atomic rename. FFmpeg owns Direct3D devices, capture frame pacing, color conversion, H.264/AAC encoding, and MP4 muxing. Stop sends FFmpeg `q` and allows 15 seconds for finalization before terminating only that exact child process.

Output is first created as `<name>.mp4.partial.mp4` and renamed only after successful concatenation. Recoverable segment files remain under `Data\Temp` after failed finalization.
