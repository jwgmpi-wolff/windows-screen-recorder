using Microsoft.Extensions.Logging.Abstractions;
using PortableScreenRecorder.Capture;
using PortableScreenRecorder.Core;
using PortableScreenRecorder.Infrastructure;

namespace PortableScreenRecorder.UnitTests;

public sealed class CoreLogicTests
{
    [Fact]
    public void BoundingRect_PreservesNegativeCoordinatesAndLayout()
    {
        var result = Geometry.BoundingRect([
            new PixelRect(-1920, 0, 1920, 1080),
            new PixelRect(0, -200, 2560, 1440)
        ]);

        Assert.Equal(new PixelRect(-1920, -200, 4480, 1440), result);
    }

    [Theory]
    [InlineData(100, 100, 20, 40, 20, 40, 80, 60)]
    [InlineData(20, 40, 100, 100, 20, 40, 80, 60)]
    public void Normalize_WorksInEveryDragDirection(int x1, int y1, int x2, int y2, int x, int y, int width, int height)
    {
        Assert.Equal(new PixelRect(x, y, width, height), Geometry.Normalize(x1, y1, x2, y2));
    }

    [Fact]
    public void Clip_ClampsToDesktopBounds()
    {
        Assert.Equal(
            new PixelRect(-100, 0, 150, 50),
            Geometry.Clip(new PixelRect(-200, -50, 250, 100), new PixelRect(-100, 0, 200, 200)));
    }

    [Fact]
    public void LogicalToPhysical_UsesIndependentDpiScales()
    {
        Assert.Equal(new PixelRect(150, 200, 300, 200), Geometry.LogicalToPhysical(new PixelRect(100, 100, 200, 100), 1.5, 2));
    }

    [Fact]
    public void FileNameSanitization_RemovesWindowsInvalidCharactersAndForcesMp4()
    {
        var sanitized = FileNames.Sanitize(" quarterly:demo?.avi ");
        Assert.Equal("quarterly_demo_.mp4", sanitized);
    }

    [Fact]
    public void RecordingStateMachine_RejectsInvalidTransitions()
    {
        var machine = new RecordingStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(RecordingState.Paused));
        machine.TransitionTo(RecordingState.Preparing);
        machine.TransitionTo(RecordingState.Recording);
        machine.TransitionTo(RecordingState.Paused);
        machine.TransitionTo(RecordingState.Stopping);
        machine.TransitionTo(RecordingState.Completed);
        Assert.Equal(RecordingState.Completed, machine.State);
    }

    [Fact]
    public async Task ActiveDuration_OmitsPausedTime()
    {
        var tracker = new ActiveDurationTracker();
        tracker.Start();
        await Task.Delay(40);
        tracker.Pause();
        var beforePause = tracker.Elapsed;
        await Task.Delay(60);
        Assert.InRange((tracker.Elapsed - beforePause).TotalMilliseconds, -1, 5);
        tracker.Start();
        await Task.Delay(40);
        tracker.Pause();
        Assert.InRange(tracker.Elapsed.TotalMilliseconds, 60, 180);
    }

    [Fact]
    public void AudioMixer_HandlesDifferentBufferLengthsAndClamps()
    {
        var destination = new float[3];
        new AudioMixer().Mix([0.8f, -0.8f], [0.8f, -0.8f, 0.5f], destination, 1, 1);
        Assert.Equal([1f, -1f, 0.5f], destination);
    }

    [Fact]
    public async Task SettingsService_RecoversMalformedJson()
    {
        using var folder = new TemporaryFolder();
        var storage = new TestStorage(folder.Path);
        Directory.CreateDirectory(storage.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(storage.DataDirectory, "settings.json"), "{broken");
        var service = new JsonSettingsService(storage, NullLogger<JsonSettingsService>.Instance);

        var settings = await service.LoadAsync();

        Assert.Equal(30, settings.FrameRate);
        Assert.Single(Directory.GetFiles(storage.DataDirectory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void StorageService_AvoidsOutputCollisions()
    {
        using var folder = new TemporaryFolder();
        var storage = new PortableStorageService();
        File.WriteAllText(Path.Combine(folder.Path, "capture.mp4"), "existing");
        var path = storage.CreateAvailableOutputPath(folder.Path, "capture.mp4");
        Assert.Equal("capture_2.mp4", Path.GetFileName(path));
    }

    [Fact]
    public void DdagrabArguments_UseRelativeOffsetsForNegativeCoordinateMonitor()
    {
        var display = new DisplayInfo(@"\\.\DISPLAY2", "Display 2", 1, new PixelRect(-1920, 0, 1920, 1080), 96, 96, false);
        var request = new RecordingRequest(CaptureMode.Region, display, [display], new PixelRect(-1800, 100, 800, 600), AudioMode.None, null, null, "out.mp4", 30, 8, 192, false, 1, 1);
        var arguments = FfmpegScreenCaptureService.BuildArguments(request, "out.mp4");
        Assert.Contains("ddagrab=output_idx=1:draw_mouse=0:framerate=30:video_size=800x600:offset_x=120:offset_y=100", arguments);
        Assert.Contains("hwdownload,format=bgra,format=nv12", arguments);
        Assert.Contains("-hw_encoding", arguments);
        Assert.Contains("vfr", arguments);
    }

    private sealed class TestStorage(string root) : IStorageService
    {
        public string DataDirectory => root;
        public string LogsDirectory => Path.Combine(root, "Logs");
        public string ResolveOutputDirectory(string configuredPath) => configuredPath;
        public string CreateAvailableOutputPath(string directory, string requestedFileName) => Path.Combine(directory, requestedFileName);
        public void EnsureWritableDirectory(string directory) => Directory.CreateDirectory(directory);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"psr-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
