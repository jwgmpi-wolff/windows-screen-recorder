# Testing

`scripts\build.ps1` runs the Release build and all tests.

Unit tests cover virtual bounds, negative coordinates, drag normalization, clipping, DPI conversion, filename safety, state transitions, pause-time correction, bounded audio mixing, malformed settings recovery, output collisions, and DXGI region arguments.

Transcription tests cover SRT timestamps, line normalization, UTF-8 TXT/SRT sidecar creation, and safe installed-recognizer enumeration. The UI diagnostic opens the standalone window and verifies that the local recognizer can open the default microphone, start listening, stop, and return to ready state.

Integration tests exercise live display enumeration, live microphone/playback endpoint enumeration, and the installed FFmpeg capture/encoder feature set.

`scripts\smoke-test.ps1 -Record` records five seconds from DXGI output 0 using the packaged executable and validates the MP4 with ffprobe. It checks container duration, H.264 video, dimensions, and frame rate.

Set `PSR_RUN_HARDWARE_RECORDING=1` and `PORTABLE_SCREEN_RECORDER_FFMPEG` to an FFmpeg executable before running `RecordingSession_AllAudioModesPauseResumeAndFinalize`. This opt-in theory exercises no-audio, microphone, system-loopback, and combined audio through managed start, pause, resume, stop, segment concatenation, final rename, and ffprobe validation. It validates stream structure and finalization; assessing audible quality still requires a known non-silent test signal.
