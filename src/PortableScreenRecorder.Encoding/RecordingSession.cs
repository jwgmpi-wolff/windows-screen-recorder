using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Encoding;

public sealed class RecordingSession(
    IScreenCaptureService screenCapture,
    IMicrophoneCaptureService microphoneCapture,
    ISystemAudioCaptureService systemCapture,
    IVideoEncoder encoder,
    IStorageService storage,
    ILogger<RecordingSession> logger) : IRecordingSession
{
    private readonly RecordingStateMachine stateMachine = new();
    private readonly ActiveDurationTracker duration = new();
    private readonly SemaphoreSlim operationLock = new(1, 1);
    private readonly List<string> segments = [];
    private RecordingRequest? request;
    private string? temporaryDirectory;
    private string? currentVideoPath;
    private string? currentMicrophonePath;
    private string? currentSystemPath;
    private Timer? statusTimer;
    private float microphoneLevel;
    private float systemLevel;

    public RecordingState State => stateMachine.State;
    public RecordingStatus Status { get; private set; } =
        new(RecordingState.Idle, TimeSpan.Zero, default, 0, 0, 0, 0, 0, null, null);
    public event EventHandler<RecordingStatus>? StatusChanged;

    public async Task StartAsync(RecordingRequest recordingRequest, CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            stateMachine.TransitionTo(RecordingState.Preparing);
            request = recordingRequest;
            duration.Reset();
            segments.Clear();
            storage.EnsureWritableDirectory(Path.GetDirectoryName(recordingRequest.OutputPath)!);
            await encoder.VerifyAvailableAsync(cancellationToken).ConfigureAwait(false);
            temporaryDirectory = Path.Combine(storage.DataDirectory, "Temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            microphoneCapture.LevelChanged += OnMicrophoneLevelChanged;
            systemCapture.LevelChanged += OnSystemLevelChanged;
            await StartSegmentAsync(cancellationToken).ConfigureAwait(false);
            duration.Start();
            stateMachine.TransitionTo(RecordingState.Recording);
            statusTimer = new Timer(_ => PublishStatus(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
            PublishStatus();
            logger.LogInformation("Recording started: {Output}", recordingRequest.OutputPath);
        }
        catch (Exception exception)
        {
            SetFault(exception);
            await BestEffortStopSourcesAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Recording)
            {
                throw new InvalidOperationException("Only an active recording can be paused.");
            }
            duration.Pause();
            await FinishSegmentAsync(cancellationToken).ConfigureAwait(false);
            stateMachine.TransitionTo(RecordingState.Paused);
            PublishStatus();
            logger.LogInformation("Recording paused");
        }
        catch (Exception exception)
        {
            SetFault(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Paused)
            {
                throw new InvalidOperationException("Only a paused recording can be resumed.");
            }
            await StartSegmentAsync(cancellationToken).ConfigureAwait(false);
            duration.Start();
            stateMachine.TransitionTo(RecordingState.Recording);
            PublishStatus();
            logger.LogInformation("Recording resumed");
        }
        catch (Exception exception)
        {
            SetFault(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (RecordingState.Recording or RecordingState.Paused or RecordingState.Preparing))
            {
                return;
            }
            if (State == RecordingState.Recording)
            {
                duration.Pause();
            }
            stateMachine.TransitionTo(RecordingState.Stopping);
            PublishStatus();
            statusTimer?.Dispose();
            statusTimer = null;

            if (currentVideoPath is not null)
            {
                await FinishSegmentAsync(cancellationToken).ConfigureAwait(false);
            }

            var activeRequest = request ?? throw new InvalidOperationException("No recording request is active.");
            var partialPath = activeRequest.OutputPath + ".partial.mp4";
            await encoder.ConcatenateAsync(segments, partialPath, cancellationToken).ConfigureAwait(false);
            File.Move(partialPath, activeRequest.OutputPath, overwrite: false);
            stateMachine.TransitionTo(RecordingState.Completed);
            PublishStatus();
            logger.LogInformation("Recording finalized successfully: {Output}", activeRequest.OutputPath);
            CleanupTemporaryFiles();
        }
        catch (Exception exception)
        {
            SetFault(exception);
            throw;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private async Task StartSegmentAsync(CancellationToken cancellationToken)
    {
        var activeRequest = request ?? throw new InvalidOperationException("No recording request is active.");
        var directory = temporaryDirectory ?? throw new InvalidOperationException("No temporary directory is active.");
        var index = segments.Count;
        currentVideoPath = Path.Combine(directory, $"segment-{index:D4}.video.mp4");
        currentMicrophonePath = UsesMicrophone(activeRequest.AudioMode) ? Path.Combine(directory, $"segment-{index:D4}.microphone.wav") : null;
        currentSystemPath = UsesSystem(activeRequest.AudioMode) ? Path.Combine(directory, $"segment-{index:D4}.system.wav") : null;

        if (currentMicrophonePath is not null)
        {
            await microphoneCapture.StartAsync(activeRequest.MicrophoneDeviceId, currentMicrophonePath, cancellationToken).ConfigureAwait(false);
        }
        if (currentSystemPath is not null)
        {
            await systemCapture.StartAsync(activeRequest.SystemDeviceId, currentSystemPath, cancellationToken).ConfigureAwait(false);
        }
        await screenCapture.StartAsync(activeRequest, currentVideoPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishSegmentAsync(CancellationToken cancellationToken)
    {
        await screenCapture.StopAsync(cancellationToken).ConfigureAwait(false);
        if (microphoneCapture.IsCapturing) await microphoneCapture.StopAsync(cancellationToken).ConfigureAwait(false);
        if (systemCapture.IsCapturing) await systemCapture.StopAsync(cancellationToken).ConfigureAwait(false);

        var activeRequest = request!;
        var output = Path.Combine(temporaryDirectory!, $"segment-{segments.Count:D4}.mp4");
        await encoder.MuxSegmentAsync(currentVideoPath!, currentMicrophonePath, currentSystemPath, output, activeRequest, cancellationToken).ConfigureAwait(false);
        segments.Add(output);
        currentVideoPath = null;
        currentMicrophonePath = null;
        currentSystemPath = null;
    }

    private void PublishStatus()
    {
        var activeRequest = request;
        var bounds = activeRequest is null
            ? default
            : activeRequest.CaptureMode == CaptureMode.AllDisplays
                ? Geometry.BoundingRect(activeRequest.Displays.Select(static display => display.Bounds))
                : activeRequest.CaptureMode == CaptureMode.Region
                    ? activeRequest.Region ?? default
                    : activeRequest.Display?.Bounds ?? default;
        var bytes = temporaryDirectory is null || !Directory.Exists(temporaryDirectory)
            ? 0
            : Directory.EnumerateFiles(temporaryDirectory).Sum(path => new FileInfo(path).Length);
        Status = new RecordingStatus(State, duration.Elapsed, bounds, activeRequest?.FrameRate ?? 0, 0, bytes, microphoneLevel, systemLevel, activeRequest?.OutputPath, Status.Error);
        StatusChanged?.Invoke(this, Status);
    }

    private async Task BestEffortStopSourcesAsync()
    {
        try { await screenCapture.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception exception) { logger.LogWarning(exception, "Failed to stop screen capture during cleanup"); }
        try { await microphoneCapture.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception exception) { logger.LogWarning(exception, "Failed to stop microphone during cleanup"); }
        try { await systemCapture.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception exception) { logger.LogWarning(exception, "Failed to stop system audio during cleanup"); }
    }

    private void SetFault(Exception exception)
    {
        logger.LogError(exception, "Recording session failed");
        if (stateMachine.CanTransitionTo(RecordingState.Faulted))
        {
            stateMachine.TransitionTo(RecordingState.Faulted);
        }
        Status = Status with { State = State, Error = exception.Message };
        StatusChanged?.Invoke(this, Status);
    }

    private void CleanupTemporaryFiles()
    {
        if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        temporaryDirectory = null;
    }

    private void OnMicrophoneLevelChanged(object? sender, float level) => microphoneLevel = level;
    private void OnSystemLevelChanged(object? sender, float level) => systemLevel = level;
    private static bool UsesMicrophone(AudioMode mode) => mode is AudioMode.Microphone or AudioMode.MicrophoneAndSystem;
    private static bool UsesSystem(AudioMode mode) => mode is AudioMode.System or AudioMode.MicrophoneAndSystem;

    public async ValueTask DisposeAsync()
    {
        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            await StopAsync().ConfigureAwait(false);
        }
        statusTimer?.Dispose();
        microphoneCapture.LevelChanged -= OnMicrophoneLevelChanged;
        systemCapture.LevelChanged -= OnSystemLevelChanged;
        operationLock.Dispose();
    }
}
