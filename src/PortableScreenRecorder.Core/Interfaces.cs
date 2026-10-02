namespace PortableScreenRecorder.Core;

public interface IDisplayEnumerator
{
    IReadOnlyList<DisplayInfo> GetActiveDisplays();
}

public interface IScreenCaptureService
{
    Task StartAsync(RecordingRequest request, string videoPath, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IRegionSelectionService
{
    Task<PixelRect?> SelectAsync(PixelRect bounds, CancellationToken cancellationToken);
}

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetMicrophones();
    IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices();
}

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<float>? LevelChanged;
    bool IsCapturing { get; }
    Task StartAsync(string? deviceId, string outputWavePath, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IMicrophoneCaptureService : IAudioCaptureService;
public interface ISystemAudioCaptureService : IAudioCaptureService;

public interface IAudioMixer
{
    void Mix(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, Span<float> destination, float microphoneVolume, float systemVolume);
}

public interface ISpeechTranscriptionService : IAsyncDisposable
{
    event EventHandler<TranscriptionUpdate>? Updated;
    bool IsListening { get; }
    IReadOnlyList<SpeechRecognizerInfo> GetInstalledRecognizers();
    Task StartAsync(
        string cultureName,
        float minimumConfidence,
        SpeechInputSource inputSource,
        string? playbackDeviceId,
        CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string mediaOutputPath, CancellationToken cancellationToken = default);
}

public interface ISpeechTranscriptionServiceFactory
{
    ISpeechTranscriptionService Create();
}

public interface IVideoEncoder
{
    string ExecutablePath { get; }
    Task VerifyAvailableAsync(CancellationToken cancellationToken);
    Task MuxSegmentAsync(string videoPath, string? microphonePath, string? systemPath, string outputPath, RecordingRequest request, CancellationToken cancellationToken);
    Task ConcatenateAsync(IReadOnlyList<string> segmentPaths, string partialOutputPath, CancellationToken cancellationToken);
}

public interface IRecordingSession : IAsyncDisposable
{
    RecordingState State { get; }
    RecordingStatus Status { get; }
    event EventHandler<RecordingStatus>? StatusChanged;
    Task StartAsync(RecordingRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<RecorderSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RecorderSettings settings, CancellationToken cancellationToken = default);
}

public interface IHotkeyService : IDisposable
{
    event EventHandler? StartStopPressed;
    event EventHandler? PauseResumePressed;
    void Attach(nint windowHandle, string startStop, string pauseResume);
    bool ProcessMessage(int message, nint wParam);
}

public interface IStorageService
{
    string DataDirectory { get; }
    string LogsDirectory { get; }
    string ResolveOutputDirectory(string configuredPath);
    string CreateAvailableOutputPath(string directory, string requestedFileName);
    void EnsureWritableDirectory(string directory);
}
