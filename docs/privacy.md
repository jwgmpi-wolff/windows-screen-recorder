# Privacy

PortableScreenRecorder records only after an explicit Start action or registered start hotkey. The main window remains visible with a red recording indicator and current state. It contains no service, startup registration, remote control, stealth mode, upload, telemetry, or cloud integration.

Video, temporary audio, settings, and logs stay under user-selected local storage or the portable application directory. Logs contain versions, device display names, state transitions, and errors; they do not contain media samples, captured frames, usernames, or unrelated user data.

When the user explicitly enables transcription or starts the speech-to-text window, the Windows default microphone is processed by the locally installed Windows speech recognizer. Audio is not uploaded. The application stores only accepted text in user-requested TXT/SRT files and does not log transcript content.
