using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Transcription;

internal sealed class SpeechAudioCapture : IAsyncDisposable
{
    private const int OutputSampleRate = 16000;
    private const int SamplesPerTick = OutputSampleRate / 50;
    private readonly List<CaptureSource> sources = [];
    private readonly BlockingAudioStream stream = new();
    private Timer? mixerTimer;

    public SpeechAudioCapture(SpeechInputSource inputSource, string? playbackDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (inputSource is SpeechInputSource.DefaultMicrophone or SpeechInputSource.MicrophoneAndSystem)
        {
            var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            sources.Add(new CaptureSource(new WasapiCapture(microphone), microphone));
        }
        if (inputSource is SpeechInputSource.SystemAudio or SpeechInputSource.MicrophoneAndSystem)
        {
            var playback = string.IsNullOrWhiteSpace(playbackDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(playbackDeviceId);
            sources.Add(new CaptureSource(new WasapiLoopbackCapture(playback), playback));
        }
    }

    public Stream Stream => stream;

    public void Start()
    {
        foreach (var source in sources)
        {
            source.Start();
        }
        mixerTimer = new Timer(_ => MixNextBlock(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20));
    }

    private void MixNextBlock()
    {
        Span<float> mixed = stackalloc float[SamplesPerTick];
        foreach (var source in sources)
        {
            source.MixInto(mixed);
        }

        var divisor = Math.Max(1, sources.Count);
        var bytes = new byte[SamplesPerTick * sizeof(short)];
        Span<short> samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var index = 0; index < mixed.Length; index++)
        {
            samples[index] = (short)Math.Round(Math.Clamp(mixed[index] / divisor, -1f, 1f) * short.MaxValue);
        }
        stream.WriteCaptured(bytes);
    }

    public async Task StopAsync()
    {
        mixerTimer?.Dispose();
        mixerTimer = null;
        await Task.WhenAll(sources.Select(static source => source.StopAsync())).ConfigureAwait(false);
        while (sources.Any(static source => source.HasSamples))
        {
            MixNextBlock();
        }
        stream.CompleteWriting();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var source in sources)
        {
            source.Dispose();
        }
        stream.Dispose();
    }

    private sealed class CaptureSource(IWaveIn capture, MMDevice device) : IDisposable
    {
        private readonly ConcurrentQueue<float> samples = new();
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double resamplePosition;
        private bool started;

        public bool HasSamples => !samples.IsEmpty;

        public void Start()
        {
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            started = true;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            var format = capture.WaveFormat;
            var bytesPerSample = format.BitsPerSample / 8;
            var frameSize = bytesPerSample * format.Channels;
            var frameCount = eventArgs.BytesRecorded / frameSize;
            var step = format.SampleRate / (double)OutputSampleRate;

            while (resamplePosition < frameCount)
            {
                var frame = Math.Min((int)resamplePosition, frameCount - 1);
                var mono = 0f;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var offset = (frame * frameSize) + (channel * bytesPerSample);
                    mono += ReadSample(eventArgs.Buffer, offset, format);
                }
                samples.Enqueue(mono / format.Channels);
                resamplePosition += step;
            }
            resamplePosition -= frameCount;
        }

        public void MixInto(Span<float> destination)
        {
            for (var index = 0; index < destination.Length && samples.TryDequeue(out var sample); index++)
            {
                destination[index] += sample;
            }
        }

        public async Task StopAsync()
        {
            if (!started)
            {
                return;
            }
            started = false;
            capture.StopRecording();
            await stopped.Task.ConfigureAwait(false);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            if (eventArgs.Exception is null)
            {
                stopped.TrySetResult();
            }
            else
            {
                stopped.TrySetException(eventArgs.Exception);
            }
        }

        private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
        {
            if (format.BitsPerSample == 32)
            {
                return BitConverter.ToSingle(buffer, offset);
            }
            if (format.BitsPerSample == 16)
            {
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            }
            throw new NotSupportedException(
                $"Speech input format {format} is not supported. Select a 16-bit PCM or 32-bit float Windows audio format.");
        }

        public void Dispose()
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            device.Dispose();
        }
    }
}

internal sealed class BlockingAudioStream : Stream
{
    private readonly Queue<byte[]> buffers = new();
    private byte[]? current;
    private int currentOffset;
    private bool completed;
    private bool disposed;

    public void WriteCaptured(byte[] buffer)
    {
        lock (buffers)
        {
            if (completed || disposed)
            {
                return;
            }
            buffers.Enqueue(buffer);
            Monitor.PulseAll(buffers);
        }
    }

    public void CompleteWriting()
    {
        lock (buffers)
        {
            completed = true;
            Monitor.PulseAll(buffers);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (buffers)
        {
            while (!disposed && current is null && buffers.Count == 0 && !completed)
            {
                Monitor.Wait(buffers);
            }
            if (disposed || current is null && buffers.Count == 0)
            {
                return 0;
            }

            current ??= buffers.Dequeue();
            var copied = Math.Min(count, current.Length - currentOffset);
            Buffer.BlockCopy(current, currentOffset, buffer, offset, copied);
            currentOffset += copied;
            if (currentOffset == current.Length)
            {
                current = null;
                currentOffset = 0;
            }
            return copied;
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (buffers)
        {
            disposed = true;
            buffers.Clear();
            current = null;
            Monitor.PulseAll(buffers);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 0;
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
