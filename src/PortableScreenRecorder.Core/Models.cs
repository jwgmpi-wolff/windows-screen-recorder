namespace PortableScreenRecorder.Core;

public enum CaptureMode
{
    SingleDisplay,
    AllDisplays,
    Region
}

public enum AudioMode
{
    None,
    Microphone,
    System,
    MicrophoneAndSystem
}

public enum RecordingState
{
    Idle,
    Preparing,
    Recording,
    Paused,
    Stopping,
    Completed,
    Faulted
}

public enum TranscriptionMode
{
    Off,
    RecordAndTranscribe,
    SpeechToTextWindow
}

public enum SpeechInputSource
{
    SystemAudio,
    DefaultMicrophone,
    MicrophoneAndSystem
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"{X}, {Y} - {Width} x {Height}";
}

public sealed record DisplayInfo(
    string DeviceName,
    string FriendlyName,
    int OutputIndex,
    PixelRect Bounds,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public int ScalingPercent => (int)Math.Round(DpiX / 96d * 100);
    public string Label =>
        $"{FriendlyName}: {Bounds.Width} x {Bounds.Height}, {ScalingPercent}%{(IsPrimary ? ", Primary" : string.Empty)} ({Bounds.X}, {Bounds.Y})";
}

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

public sealed record SpeechRecognizerInfo(string CultureName, string DisplayName)
{
    public string Label => $"{DisplayName} ({CultureName})";
}

public sealed record TranscriptSegment(string Text, TimeSpan Start, TimeSpan Duration, float Confidence)
{
    public TimeSpan End => Start + Duration;
}

public sealed record TranscriptionUpdate(
    string FinalText,
    string PartialText,
    IReadOnlyList<TranscriptSegment> Segments,
    bool IsListening);

public sealed record RecordingRequest(
    CaptureMode CaptureMode,
    DisplayInfo? Display,
    IReadOnlyList<DisplayInfo> Displays,
    PixelRect? Region,
    AudioMode AudioMode,
    string? MicrophoneDeviceId,
    string? SystemDeviceId,
    string OutputPath,
    int FrameRate,
    int VideoBitrateMbps,
    int AudioBitrateKbps,
    bool IncludePointer,
    float MicrophoneVolume,
    float SystemVolume);

public sealed record RecordingStatus(
    RecordingState State,
    TimeSpan Elapsed,
    PixelRect CaptureBounds,
    int FrameRate,
    long DroppedFrames,
    long EstimatedBytes,
    float MicrophoneLevel,
    float SystemLevel,
    string? OutputPath,
    string? Error);

public sealed class RecorderSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.SingleDisplay;
    public string? DisplayDeviceName { get; set; }
    public PixelRect? Region { get; set; }
    public string OutputDirectory { get; set; } = "Recordings";
    public int FrameRate { get; set; } = 30;
    public int VideoBitrateMbps { get; set; } = 8;
    public int AudioBitrateKbps { get; set; } = 192;
    public AudioMode AudioMode { get; set; } = AudioMode.System;
    public string? MicrophoneDeviceId { get; set; }
    public string? SystemDeviceId { get; set; }
    public float MicrophoneVolume { get; set; } = 1f;
    public float SystemVolume { get; set; } = 1f;
    public bool IncludePointer { get; set; } = true;
    public TranscriptionMode TranscriptionMode { get; set; }
    public string? SpeechCulture { get; set; }
    public float MinimumSpeechConfidence { get; set; }
    public SpeechInputSource SpeechInputSource { get; set; } = SpeechInputSource.SystemAudio;
    public string StartStopHotkey { get; set; } = "Ctrl+Shift+F9";
    public string PauseResumeHotkey { get; set; } = "Ctrl+Shift+F10";
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 760;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}
