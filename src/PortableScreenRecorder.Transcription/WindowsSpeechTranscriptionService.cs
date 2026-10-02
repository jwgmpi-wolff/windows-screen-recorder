using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;
using Whisper.net;

namespace PortableScreenRecorder.Transcription;

public sealed class WindowsSpeechTranscriptionServiceFactory(
    ILoggerFactory loggerFactory) : ISpeechTranscriptionServiceFactory
{
    public ISpeechTranscriptionService Create() =>
        new WindowsSpeechTranscriptionService(loggerFactory.CreateLogger<WindowsSpeechTranscriptionService>());
}

public sealed class WindowsSpeechTranscriptionService(
    ILogger<WindowsSpeechTranscriptionService> logger) : ISpeechTranscriptionService
{
    private readonly object sync = new();
    private readonly List<TranscriptSegment> segments = [];
    private readonly ActiveDurationTracker duration = new();
    private SpeechRecognitionEngine? engine;
    private SpeechAudioCapture? audioCapture;
    private Task? chunkProcessing;
    private RecognizerInfo? recognizerInfo;
    private WhisperFactory? whisperFactory;
    private WhisperProcessor? whisperProcessor;
    private TaskCompletionSource? recognitionStopped;
    private string partialText = string.Empty;
    private SpeechInputSource inputSource;
    private string? playbackDeviceId;
    private float minimumConfidence;
    private bool paused;

    public event EventHandler<TranscriptionUpdate>? Updated;
    public bool IsListening { get; private set; }

    public IReadOnlyList<SpeechRecognizerInfo> GetInstalledRecognizers() =>
        SpeechRecognitionEngine.InstalledRecognizers()
            .Select(static recognizer => new SpeechRecognizerInfo(
                recognizer.Culture.Name,
                recognizer.Culture.DisplayName))
            .OrderBy(static recognizer => recognizer.DisplayName)
            .ToArray();

    public Task StartAsync(
        string cultureName,
        float confidence,
        SpeechInputSource inputSource,
        string? playbackDeviceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (engine is not null)
            {
                throw new InvalidOperationException("Speech recognition is already active.");
            }

            var recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(item => item.Culture.Name.Equals(cultureName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"The Windows speech language '{cultureName}' is not installed. Add it in Windows Settings > Time & language > Language & region.");

            minimumConfidence = Math.Clamp(confidence, 0f, 1f);
            this.inputSource = inputSource;
            this.playbackDeviceId = playbackDeviceId;
            recognizerInfo = recognizer;
            segments.Clear();
            partialText = string.Empty;
            duration.Reset();
            engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            engine.SpeechRecognized += OnSpeechRecognized;
            engine.SpeechHypothesized += OnSpeechHypothesized;
            engine.SpeechRecognitionRejected += OnSpeechRejected;
            engine.RecognizeCompleted += OnRecognizeCompleted;
            try
            {
                if (inputSource == SpeechInputSource.DefaultMicrophone)
                {
                    engine.SetInputToDefaultAudioDevice();
                    BeginRecognition();
                }
                else
                {
                    StartChunkedRecognition();
                }
                duration.Start();
                logger.LogInformation(
                    "Local speech recognition started with {Culture} from {InputSource}",
                    recognizer.Culture.Name,
                    inputSource);
            }
            catch
            {
                DisposeEngine();
                throw;
            }
        }
        Publish();
        return Task.CompletedTask;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        SpeechRecognitionEngine? active;
        var chunked = false;
        lock (sync)
        {
            if (engine is null || !IsListening)
            {
                return;
            }
            duration.Pause();
            paused = true;
            active = engine;
            chunked = inputSource != SpeechInputSource.DefaultMicrophone;
            if (chunked)
            {
                IsListening = false;
            }
            else
            {
                recognitionStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                active.RecognizeAsyncStop();
            }
        }
        if (chunked)
        {
            await StopChunkedRecognitionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WaitForStopAsync(cancellationToken).ConfigureAwait(false);
        }
        Publish();
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (engine is null)
            {
                throw new InvalidOperationException("Speech recognition has not been started.");
            }
            if (!paused)
            {
                return Task.CompletedTask;
            }
            paused = false;
            if (inputSource == SpeechInputSource.DefaultMicrophone)
            {
                engine.SetInputToDefaultAudioDevice();
                BeginRecognition();
            }
            else
            {
                StartChunkedRecognition();
            }
            duration.Start();
        }
        Publish();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SpeechRecognitionEngine? active;
        var waitForCompletion = false;
        var chunked = false;
        lock (sync)
        {
            active = engine;
            if (active is null)
            {
                return;
            }
            if (IsListening)
            {
                duration.Pause();
                chunked = inputSource != SpeechInputSource.DefaultMicrophone;
                if (chunked)
                {
                    IsListening = false;
                }
                else
                {
                    recognitionStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    active.RecognizeAsyncStop();
                }
                waitForCompletion = true;
            }
        }

        if (waitForCompletion)
        {
            if (chunked)
            {
                await StopChunkedRecognitionAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WaitForStopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        lock (sync)
        {
            if (!string.IsNullOrWhiteSpace(partialText))
            {
                AddPendingPartial();
            }
            DisposeEngine();
            paused = false;
            partialText = string.Empty;
        }
        Publish();
        logger.LogInformation("Local speech recognition stopped with {Count} accepted segments", segments.Count);
    }

    public async Task SaveAsync(string mediaOutputPath, CancellationToken cancellationToken = default)
    {
        TranscriptSegment[] snapshot;
        lock (sync)
        {
            snapshot = [.. segments];
        }
        var basePath = Path.Combine(
            Path.GetDirectoryName(mediaOutputPath) ?? throw new ArgumentException("Output path has no directory.", nameof(mediaOutputPath)),
            Path.GetFileNameWithoutExtension(mediaOutputPath));
        await TranscriptWriter.WriteAsync(basePath, snapshot, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Transcript saved beside {MediaOutput}", mediaOutputPath);
    }

    private void BeginRecognition()
    {
        recognitionStopped = null;
        engine!.RecognizeAsync(RecognizeMode.Multiple);
        IsListening = true;
    }

    private void StartChunkedRecognition()
    {
        if (whisperProcessor is null)
        {
            var modelPath = ResolveWhisperModelPath();
            whisperFactory = WhisperFactory.FromPath(modelPath);
            whisperProcessor = whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithProbabilities()
                .Build();
        }
        audioCapture = new SpeechAudioCapture(inputSource, playbackDeviceId);
        audioCapture.Start();
        chunkProcessing = ProcessAudioChunksAsync(audioCapture, recognizerInfo!);
        IsListening = true;
    }

    private async Task StopChunkedRecognitionAsync(CancellationToken cancellationToken)
    {
        var processing = chunkProcessing;
        chunkProcessing = null;
        await StopAudioCaptureAsync().ConfigureAwait(false);
        if (processing is not null)
        {
            await processing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessAudioChunksAsync(SpeechAudioCapture capture, RecognizerInfo recognizer)
    {
        const int blockSize = 640;
        const int silenceBlockLimit = 35;
        const int maximumPhraseBlocks = 500;
        var buffer = new byte[blockSize];
        var preRoll = new Queue<byte[]>();
        var phrase = new List<byte>();
        var silenceBlocks = 0;
        var phraseBlocks = 0;

        while (true)
        {
            var count = await capture.Stream.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var block = buffer.AsSpan(0, count).ToArray();
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(block);
            var hasSpeech = false;
            foreach (var sample in samples)
            {
                if (Math.Abs((int)sample) > 200)
                {
                    hasSpeech = true;
                    break;
                }
            }
            if (phrase.Count == 0)
            {
                preRoll.Enqueue(block);
                while (preRoll.Count > 10)
                {
                    preRoll.Dequeue();
                }
                if (!hasSpeech)
                {
                    continue;
                }
                foreach (var leadingBlock in preRoll)
                {
                    phrase.AddRange(leadingBlock);
                    phraseBlocks++;
                }
                preRoll.Clear();
            }
            else
            {
                phrase.AddRange(block);
                phraseBlocks++;
            }

            silenceBlocks = hasSpeech ? 0 : silenceBlocks + 1;
            if (silenceBlocks >= silenceBlockLimit || phraseBlocks >= maximumPhraseBlocks)
            {
                await RecognizeChunkAsync(phrase.ToArray()).ConfigureAwait(false);
                phrase.Clear();
                phraseBlocks = 0;
                silenceBlocks = 0;
            }
        }

        if (phrase.Count > 0)
        {
            await RecognizeChunkAsync(phrase.ToArray()).ConfigureAwait(false);
        }
    }

    private async Task RecognizeChunkAsync(byte[] audio)
    {
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio);
        var samples = new float[pcm.Length];
        for (var index = 0; index < pcm.Length; index++)
        {
            samples[index] = pcm[index] / 32768f;
        }

        await foreach (var result in whisperProcessor!.ProcessAsync(samples).ConfigureAwait(false))
        {
            var text = result.Text.Trim();
            if (text.Length == 0 || result.Probability < minimumConfidence)
            {
                continue;
            }
            lock (sync)
            {
                var phraseEnd = duration.Elapsed;
                var segmentDuration = result.End - result.Start;
                var start = phraseEnd > segmentDuration ? phraseEnd - segmentDuration : TimeSpan.Zero;
                segments.Add(new TranscriptSegment(
                    text,
                    start,
                    segmentDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : segmentDuration,
                    result.Probability));
            }
            Publish();
        }
    }

    private static string ResolveWhisperModelPath()
    {
        var configured = Environment.GetEnvironmentVariable("PSR_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-small.en.bin");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var developmentModel = Path.Combine(directory.FullName, ".tools", "whisper", "ggml-small.en.bin");
            if (File.Exists(developmentModel))
            {
                return developmentModel;
            }
        }

        throw new FileNotFoundException(
            "The local Whisper model was not found. Keep Models\\ggml-small.en.bin beside the application.");
    }

    private async Task WaitForStopAsync(CancellationToken cancellationToken)
    {
        Task? task;
        lock (sync)
        {
            task = recognitionStopped?.Task;
        }
        if (task is not null)
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs eventArgs)
    {
        lock (sync)
        {
            var pendingText = partialText;
            partialText = string.Empty;
            if (!AddResult(eventArgs.Result) && minimumConfidence <= 0f)
            {
                AddPendingText(pendingText);
            }
        }
        Publish();
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs eventArgs)
    {
        lock (sync)
        {
            partialText = eventArgs.Result.Text;
        }
        Publish();
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs eventArgs)
    {
        lock (sync)
        {
            var pendingText = partialText;
            partialText = string.Empty;
            if (!AddResult(eventArgs.Result) && minimumConfidence <= 0f)
            {
                AddPendingText(pendingText);
            }
        }
        Publish();
    }

    private bool AddResult(RecognitionResult result)
    {
        if (result.Confidence < minimumConfidence || string.IsNullOrWhiteSpace(result.Text))
        {
            logger.LogDebug(
                "Discarded speech result at {Confidence:P0}; minimum is {MinimumConfidence:P0}",
                result.Confidence,
                minimumConfidence);
            return false;
        }

        var speechDuration = result.Audio?.Duration ?? TimeSpan.Zero;
        var end = duration.Elapsed;
        var start = end > speechDuration ? end - speechDuration : TimeSpan.Zero;
        segments.Add(new TranscriptSegment(
            result.Text.Trim(),
            start,
            speechDuration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : speechDuration,
            result.Confidence));
        return true;
    }

    private void AddPendingPartial()
    {
        AddPendingText(partialText);
    }

    private void AddPendingText(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || segments.LastOrDefault()?.Text.Equals(text, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var segmentDuration = TimeSpan.FromSeconds(1);
        var end = duration.Elapsed;
        var start = end > segmentDuration ? end - segmentDuration : TimeSpan.Zero;
        segments.Add(new TranscriptSegment(text, start, segmentDuration, 0f));
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs eventArgs)
    {
        lock (sync)
        {
            IsListening = false;
            if (eventArgs.Error is not null)
            {
                recognitionStopped?.TrySetException(
                    new InvalidOperationException("Windows speech recognition stopped unexpectedly.", eventArgs.Error));
            }
            else
            {
                recognitionStopped?.TrySetResult();
            }
        }
        Publish();
    }

    private void Publish()
    {
        TranscriptionUpdate update;
        lock (sync)
        {
            update = new TranscriptionUpdate(
                string.Join(Environment.NewLine, segments.Select(static segment => segment.Text)),
                partialText,
                [.. segments],
                IsListening);
        }
        Updated?.Invoke(this, update);
    }

    private void DisposeEngine()
    {
        if (audioCapture is not null)
        {
            audioCapture.DisposeAsync().AsTask().GetAwaiter().GetResult();
            audioCapture = null;
        }
        if (engine is null)
        {
            return;
        }
        engine.SpeechRecognized -= OnSpeechRecognized;
        engine.SpeechHypothesized -= OnSpeechHypothesized;
        engine.SpeechRecognitionRejected -= OnSpeechRejected;
        engine.RecognizeCompleted -= OnRecognizeCompleted;
        engine.Dispose();
        engine = null;
        recognizerInfo = null;
        IsListening = false;
    }

    private async Task StopAudioCaptureAsync()
    {
        SpeechAudioCapture? active;
        lock (sync)
        {
            active = audioCapture;
            audioCapture = null;
        }
        if (active is not null)
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (whisperProcessor is not null)
        {
            await whisperProcessor.DisposeAsync().ConfigureAwait(false);
            whisperProcessor = null;
        }
        whisperFactory?.Dispose();
        whisperFactory = null;
    }
}
