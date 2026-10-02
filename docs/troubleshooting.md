# Troubleshooting

## FFmpeg is missing

Run `scripts\publish-portable.ps1`; do not copy only the main executable from the publish folder. `Tools\ffmpeg.exe` must stay beside it. Developers may set `PORTABLE_SCREEN_RECORDER_FFMPEG` to an explicit executable.

## Encoder initialization fails

Install current Windows media components. Windows N editions may require the Microsoft Media Feature Pack. Confirm `Tools\ffmpeg.exe -hide_banner -encoders` lists `h264_mf` and `aac`.

## A monitor is black

Protected content cannot be recorded. For an ordinary desktop, refresh displays after docking or GPU changes and retry. Review `Data\Logs`.

## Audio cannot open

Confirm that the endpoint is active and not disabled in Windows Settings. Refresh by restarting the app after changing the default endpoint. Exclusive-mode use by another application can prevent shared capture on some drivers.

## Finalization fails

Check free disk space and write permission. The application retains potentially recoverable segments under `Data\Temp\<session-id>` and logs the FFmpeg error.

## Global hotkey conflict

The application reports registration failure rather than silently disabling a shortcut. Change the hotkey values in `Data\settings.json` while the application is closed.

## Speech language is missing

Install a speech-capable language in **Windows Settings > Time & language > Language & region > Language options**, then restart the application. The language must include the Speech feature, not only a display language.

## Speech recognition does not hear the selected recorder microphone

Windows speech recognition uses the system default microphone. Set the desired device as the default input in Windows Sound Settings. The recorder's microphone selector controls the audio track independently.

## Transcript is empty

Confirm that transcription mode is **Record + transcript**, the default microphone has permission and input activity, and the selected speech language matches the speaker. Lower the confidence threshold cautiously if audible speech is rejected.
