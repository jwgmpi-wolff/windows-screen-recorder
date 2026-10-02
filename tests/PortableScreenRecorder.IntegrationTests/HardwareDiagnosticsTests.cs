using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PortableScreenRecorder.Audio;
using PortableScreenRecorder.Capture;
using PortableScreenRecorder.Core;
using PortableScreenRecorder.Encoding;
using PortableScreenRecorder.Infrastructure;
using PortableScreenRecorder.Transcription;

namespace PortableScreenRecorder.IntegrationTests;

public sealed class HardwareDiagnosticsTests
{
    [Fact]
    public void ActiveDisplays_CanBeEnumerated()
    {
        var displays = new Win32DisplayEnumerator(NullLogger<Win32DisplayEnumerator>.Instance).GetActiveDisplays();
        Assert.NotEmpty(displays);
        Assert.All(displays, display => Assert.True(display.Bounds.Width > 0 && display.Bounds.Height > 0));
    }

    [Fact]
    public void AudioEndpoints_CanBeEnumeratedWithoutFailure()
    {
        var enumerator = new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance);
        _ = enumerator.GetMicrophones();
        _ = enumerator.GetPlaybackDevices();
    }

    [Fact]
    public async Task Ffmpeg_HasRequiredCaptureAndEncodingFeatures()
    {
        var encoder = new FfmpegVideoEncoder(NullLogger<FfmpegVideoEncoder>.Instance);
        await encoder.VerifyAvailableAsync(CancellationToken.None);
    }

    [Fact]
    public void WindowsSpeechRecognizers_CanBeEnumerated()
    {
        var service = new WindowsSpeechTranscriptionService(
            NullLogger<WindowsSpeechTranscriptionService>.Instance);
        var recognizers = service.GetInstalledRecognizers();
        Assert.All(recognizers, recognizer => Assert.False(string.IsNullOrWhiteSpace(recognizer.CultureName)));
    }

    [HardwareRecordingTheory]
    [InlineData(AudioMode.None)]
    [InlineData(AudioMode.Microphone)]
    [InlineData(AudioMode.System)]
    [InlineData(AudioMode.MicrophoneAndSystem)]
    public async Task RecordingSession_AllAudioModesPauseResumeAndFinalize(AudioMode audioMode)
    {
        var displays = new Win32DisplayEnumerator(NullLogger<Win32DisplayEnumerator>.Instance).GetActiveDisplays();
        var display = displays.First(static item => item.IsPrimary);
        var audioDevices = new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance);
        var microphoneDeviceId = audioMode is AudioMode.Microphone or AudioMode.MicrophoneAndSystem
            ? audioDevices.GetMicrophones().First().Id
            : null;
        var systemDeviceId = audioMode is AudioMode.System or AudioMode.MicrophoneAndSystem
            ? audioDevices.GetPlaybackDevices().First().Id
            : null;
        var output = Path.Combine(Path.GetTempPath(), $"psr-session-{Guid.NewGuid():N}.mp4");
        var encoder = new FfmpegVideoEncoder(NullLogger<FfmpegVideoEncoder>.Instance);
        await using var microphone = new MicrophoneCaptureService(NullLogger<MicrophoneCaptureService>.Instance);
        await using var system = new SystemAudioCaptureService(NullLogger<SystemAudioCaptureService>.Instance);
        await using var session = new RecordingSession(
            new FfmpegScreenCaptureService(encoder, NullLogger<FfmpegScreenCaptureService>.Instance),
            microphone,
            system,
            encoder,
            new PortableStorageService(),
            NullLogger<RecordingSession>.Instance);
        var request = new RecordingRequest(
            CaptureMode.SingleDisplay, display, displays, null, audioMode, microphoneDeviceId, systemDeviceId,
            output, 30, 4, 192, true, 1, 1);

        try
        {
            await session.StartAsync(request);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await session.PauseAsync();
            await Task.Delay(TimeSpan.FromSeconds(0.5));
            await session.ResumeAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));
            await session.StopAsync();

            var ffprobe = Path.Combine(Path.GetDirectoryName(encoder.ExecutablePath) ?? string.Empty, "ffprobe.exe");
            var startInfo = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_name,codec_type,width,height", "-of", "json", output })
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var probe = Process.Start(startInfo) ?? throw new InvalidOperationException("ffprobe did not start.");
            var json = await probe.StandardOutput.ReadToEndAsync();
            var error = await probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            Assert.True(probe.ExitCode == 0, error);
            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Equal("h264", video.GetProperty("codec_name").GetString());
            Assert.Equal(display.Bounds.Width, video.GetProperty("width").GetInt32());
            Assert.Equal(display.Bounds.Height, video.GetProperty("height").GetInt32());
            if (audioMode == AudioMode.None)
            {
                Assert.DoesNotContain(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
            }
            else
            {
                var audio = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
                Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
            }
            var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 1.5, 3.5);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [HardwareRecordingTheory]
    [InlineData(SpeechInputSource.SystemAudio)]
    [InlineData(SpeechInputSource.DefaultMicrophone)]
    [InlineData(SpeechInputSource.MicrophoneAndSystem)]
    public async Task SpeechRecognition_AllInputSourcesStartPauseResumeAndStop(SpeechInputSource inputSource)
    {
        var playbackDeviceId = new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance)
            .GetPlaybackDevices()
            .First(static device => device.IsDefault)
            .Id;
        await using var service = new WindowsSpeechTranscriptionService(
            NullLogger<WindowsSpeechTranscriptionService>.Instance);
        var recognizer = service.GetInstalledRecognizers().First();

        await service.StartAsync(recognizer.CultureName, 0, inputSource, playbackDeviceId);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await service.PauseAsync();
        await service.ResumeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await service.StopAsync();

        Assert.False(service.IsListening);
    }

    [HardwareRecordingTheory]
    [InlineData(SpeechInputSource.SystemAudio)]
    [InlineData(SpeechInputSource.MicrophoneAndSystem)]
    public async Task SpeechRecognition_CapturesWordsFromSystemAudio(SpeechInputSource inputSource)
    {
        var playbackDeviceId = new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance)
            .GetPlaybackDevices()
            .First(static device => device.IsDefault)
            .Id;
        await using var service = new WindowsSpeechTranscriptionService(
            NullLogger<WindowsSpeechTranscriptionService>.Instance);
        var recognized = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Updated += (_, update) =>
        {
            var text = string.IsNullOrWhiteSpace(update.FinalText)
                ? update.PartialText
                : update.FinalText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognized.TrySetResult(text);
            }
        };
        var recognizer = service.GetInstalledRecognizers().First();

        await service.StartAsync(recognizer.CultureName, 0, inputSource, playbackDeviceId);
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Speak("Portable recorder captures every spoken word.");
        var text = await recognized.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await service.StopAsync();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(
            "portable recorder captures every spoken word",
            text.ToLowerInvariant().Trim().TrimEnd('.'));
    }

    [HardwareRecordingTheory]
    [InlineData(SpeechInputSource.SystemAudio)]
    [InlineData(SpeechInputSource.MicrophoneAndSystem)]
    public async Task SpeechAudioBridge_CapturesSystemAudioSignal(SpeechInputSource inputSource)
    {
        var playbackDeviceId = new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance)
            .GetPlaybackDevices()
            .First(static device => device.IsDefault)
            .Id;
        await using var capture = new SpeechAudioCapture(inputSource, playbackDeviceId);
        capture.Start();
        var readTask = Task.Run(async () =>
        {
            var bytes = new List<byte>();
            var buffer = new byte[640];
            int count;
            while ((count = await capture.Stream.ReadAsync(buffer)) > 0)
            {
                bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            }
            return bytes.ToArray();
        });

        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Speak("Portable recorder captures every spoken word.");
        await Task.Delay(TimeSpan.FromSeconds(1));
        await capture.StopAsync();
        var audio = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
        var peak = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio)
            .ToArray()
            .Max(static sample => Math.Abs((int)sample));

        Assert.True(peak > 100, $"Expected a loopback speech signal, but peak PCM amplitude was {peak}.");

        using var engine = new SpeechRecognitionEngine(
            SpeechRecognitionEngine.InstalledRecognizers().First());
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToAudioStream(
            new MemoryStream(audio),
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var result = engine.Recognize(TimeSpan.FromSeconds(15));
        Assert.NotNull(result);
    }

    public sealed class HardwareRecordingTheoryAttribute : TheoryAttribute
    {
        public HardwareRecordingTheoryAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("PSR_RUN_HARDWARE_RECORDING"), "1", StringComparison.Ordinal))
            {
                Skip = "Set PSR_RUN_HARDWARE_RECORDING=1 to explicitly authorize a desktop recording.";
            }
        }
    }
}
