# Audio pipeline

Microphone and playback endpoints are enumerated from Windows Core Audio on startup and refresh. Recording opens only the selected sources:

- Microphone: shared-mode WASAPI capture endpoint
- System: shared-mode WASAPI loopback on the playback endpoint

Native endpoint samples are streamed to temporary WAV files. Peak samples update the UI meter. Segment finalization performs:

1. asynchronous resampling to 48 kHz,
2. stereo float conversion,
3. independent microphone/system gain,
4. `amix` with silence retained when a source is temporarily quiet,
5. limiting to 0.95 full scale,
6. AAC encoding at the selected bitrate.

`aresample` clock compensation is reset at each resume boundary. Video and audio both stop on pause, and the final output joins only active segments.
