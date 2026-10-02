using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Audio;

public sealed class MicrophoneCaptureService(ILogger<MicrophoneCaptureService> logger)
    : WasapiCaptureServiceBase(DataFlow.Capture, logger), IMicrophoneCaptureService
{
    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiCapture(device);
}

public sealed class SystemAudioCaptureService(ILogger<SystemAudioCaptureService> logger)
    : WasapiCaptureServiceBase(DataFlow.Render, logger), ISystemAudioCaptureService
{
    protected override IWaveIn CreateCapture(MMDevice device) => new WasapiLoopbackCapture(device);
}

public abstract class WasapiCaptureServiceBase(DataFlow dataFlow, ILogger logger) : IAudioCaptureService
{
    private readonly object sync = new();
    private IWaveIn? capture;
    private WaveFileWriter? writer;
    private TaskCompletionSource? stopped;
    private MMDevice? device;

    public event EventHandler<float>? LevelChanged;
    public bool IsCapturing { get; private set; }

    public Task StartAsync(string? deviceId, string outputWavePath, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (IsCapturing)
            {
                throw new InvalidOperationException("Audio capture is already active.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputWavePath) ?? throw new ArgumentException("Audio output path has no directory.", nameof(outputWavePath)));
            using var enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            capture = CreateCapture(device);
            writer = new WaveFileWriter(outputWavePath, capture.WaveFormat);
            stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            IsCapturing = true;
            logger.LogInformation("Started {Flow} audio capture from {Device} ({Format})", dataFlow, device.FriendlyName, capture.WaveFormat);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? completion;
        lock (sync)
        {
            if (!IsCapturing || capture is null)
            {
                return;
            }
            completion = stopped?.Task;
            capture.StopRecording();
        }

        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected abstract IWaveIn CreateCapture(MMDevice device);

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (sync)
        {
            writer?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            if (capture is not null)
            {
                LevelChanged?.Invoke(this, CalculatePeak(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded), capture.WaveFormat));
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        lock (sync)
        {
            IsCapturing = false;
            writer?.Dispose();
            writer = null;
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                capture = null;
            }
            device?.Dispose();
            device = null;

            if (eventArgs.Exception is null)
            {
                stopped?.TrySetResult();
            }
            else
            {
                logger.LogError(eventArgs.Exception, "{Flow} audio capture stopped unexpectedly", dataFlow);
                stopped?.TrySetException(new InvalidOperationException($"{dataFlow} audio capture stopped unexpectedly.", eventArgs.Exception));
            }
        }
    }

    private static float CalculatePeak(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        var peak = 0f;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer);
            foreach (var sample in samples)
            {
                peak = Math.Max(peak, Math.Abs(sample / 32768f));
            }
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
