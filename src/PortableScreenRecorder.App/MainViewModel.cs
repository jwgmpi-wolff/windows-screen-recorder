using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.App;

public partial class MainViewModel : ObservableObject
{
    private readonly IDisplayEnumerator displayEnumerator;
    private readonly IAudioDeviceEnumerator audioDeviceEnumerator;
    private readonly IRegionSelectionService regionSelection;
    private readonly IRecordingSession recordingSession;
    private readonly ISettingsService settingsService;
    private readonly IStorageService storage;
    private readonly ISpeechTranscriptionServiceFactory transcriptionFactory;
    private readonly ISpeechToTextWindowService speechToTextWindowService;
    private RecorderSettings settings = new();
    private ISpeechTranscriptionService? recordingTranscription;
    private string? transcriptionOutputPath;

    [ObservableProperty] private DisplayInfo? selectedDisplay;
    [ObservableProperty] private AudioDeviceInfo? selectedMicrophone;
    [ObservableProperty] private AudioDeviceInfo? selectedPlaybackDevice;
    [ObservableProperty] private CaptureMode captureMode;
    [ObservableProperty] private AudioMode audioMode;
    [ObservableProperty] private PixelRect? selectedRegion;
    [ObservableProperty] private string outputDirectory = "Recordings";
    [ObservableProperty] private string fileName = FileNames.DefaultRecordingName(DateTimeOffset.Now);
    [ObservableProperty] private int frameRate = 30;
    [ObservableProperty] private int videoBitrateMbps = 8;
    [ObservableProperty] private int audioBitrateKbps = 192;
    [ObservableProperty] private bool includePointer = true;
    [ObservableProperty] private float microphoneVolume = 1;
    [ObservableProperty] private float systemVolume = 1;
    [ObservableProperty] private RecordingState state = RecordingState.Idle;
    [ObservableProperty] private string statusMessage = "Ready.";
    [ObservableProperty] private bool canEditSettings = true;
    [ObservableProperty] private string elapsedText = "00:00:00";
    [ObservableProperty] private string resolutionText = "-";
    [ObservableProperty] private int activeFrameRate;
    [ObservableProperty] private long droppedFrames;
    [ObservableProperty] private string estimatedSizeText = "0 MB";
    [ObservableProperty] private string? activeOutputPath;
    [ObservableProperty] private float microphoneLevel;
    [ObservableProperty] private float systemLevel;
    [ObservableProperty] private TranscriptionMode transcriptionMode;
    [ObservableProperty] private SpeechRecognizerInfo? selectedSpeechRecognizer;
    [ObservableProperty] private float minimumSpeechConfidence;
    [ObservableProperty] private SpeechInputSource speechInputSource = SpeechInputSource.SystemAudio;
    [ObservableProperty] private string liveTranscript = string.Empty;

    public MainViewModel(
        IDisplayEnumerator displayEnumerator,
        IAudioDeviceEnumerator audioDeviceEnumerator,
        IRegionSelectionService regionSelection,
        IRecordingSession recordingSession,
        ISettingsService settingsService,
        IStorageService storage,
        ISpeechTranscriptionServiceFactory transcriptionFactory,
        ISpeechToTextWindowService speechToTextWindowService)
    {
        this.displayEnumerator = displayEnumerator;
        this.audioDeviceEnumerator = audioDeviceEnumerator;
        this.regionSelection = regionSelection;
        this.recordingSession = recordingSession;
        this.settingsService = settingsService;
        this.storage = storage;
        this.transcriptionFactory = transcriptionFactory;
        this.speechToTextWindowService = speechToTextWindowService;
        recordingSession.StatusChanged += OnStatusChanged;

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        PauseCommand = new AsyncRelayCommand(() => ExecuteAsync(PauseRecordingAsync), () => State == RecordingState.Recording);
        ResumeCommand = new AsyncRelayCommand(() => ExecuteAsync(ResumeRecordingAsync), () => State == RecordingState.Paused);
        StopCommand = new AsyncRelayCommand(() => ExecuteAsync(StopRecordingAsync), () => State is RecordingState.Recording or RecordingState.Paused);
        RefreshDisplaysCommand = new RelayCommand(RefreshDevices, () => CanEditSettings);
        SelectRegionCommand = new AsyncRelayCommand(SelectRegionAsync, () => CanEditSettings && Displays.Count > 0);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => CanEditSettings);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        OpenRecordingCommand = new RelayCommand(OpenRecording, () => ActiveOutputPath is not null && File.Exists(ActiveOutputPath));
        OpenLogsCommand = new RelayCommand(() => OpenPath(storage.LogsDirectory));
        OpenSpeechToTextCommand = new RelayCommand(OpenSpeechToTextWindow);
    }

    public ObservableCollection<DisplayInfo> Displays { get; } = [];
    public ObservableCollection<AudioDeviceInfo> Microphones { get; } = [];
    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];
    public ObservableCollection<SpeechRecognizerInfo> SpeechRecognizers { get; } = [];
    public IReadOnlyList<CaptureMode> CaptureModes { get; } = Enum.GetValues<CaptureMode>();
    public IReadOnlyList<AudioMode> AudioModes { get; } = Enum.GetValues<AudioMode>();
    public IReadOnlyList<int> FrameRates { get; } = [30, 60];
    public IReadOnlyList<int> AudioBitrates { get; } = [128, 160, 192, 256, 320];
    public IReadOnlyList<TranscriptionModeOption> TranscriptionModes { get; } =
    [
        new(TranscriptionMode.Off, "Off - record normally"),
        new(TranscriptionMode.RecordAndTranscribe, "Record + transcript (.txt and .srt)"),
        new(TranscriptionMode.SpeechToTextWindow, "Speech-to-text only (separate window)")
    ];
    public IReadOnlyList<SpeechInputSource> SpeechInputSources { get; } = Enum.GetValues<SpeechInputSource>();
    public string RegionDescription => SelectedRegion is null ? "No region selected." : $"Selected: {SelectedRegion}";
    public string StartStopHotkey => settings.StartStopHotkey;
    public string PauseResumeHotkey => settings.PauseResumeHotkey;
    public double WindowWidth => settings.WindowWidth;
    public double WindowHeight => settings.WindowHeight;
    public double? WindowLeft => settings.WindowLeft;
    public double? WindowTop => settings.WindowTop;

    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand RefreshDisplaysCommand { get; }
    public IAsyncRelayCommand SelectRegionCommand { get; }
    public IRelayCommand BrowseOutputCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand OpenRecordingCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }
    public IRelayCommand OpenSpeechToTextCommand { get; }

    public async Task InitializeAsync()
    {
        try
        {
            settings = await settingsService.LoadAsync();
            CaptureMode = settings.CaptureMode;
            AudioMode = settings.AudioMode;
            OutputDirectory = settings.OutputDirectory;
            FrameRate = settings.FrameRate is 30 or 60 ? settings.FrameRate : 30;
            VideoBitrateMbps = Math.Clamp(settings.VideoBitrateMbps, 1, 100);
            AudioBitrateKbps = AudioBitrates.Contains(settings.AudioBitrateKbps) ? settings.AudioBitrateKbps : 192;
            IncludePointer = settings.IncludePointer;
            MicrophoneVolume = settings.MicrophoneVolume;
            SystemVolume = settings.SystemVolume;
            TranscriptionMode = settings.TranscriptionMode;
            MinimumSpeechConfidence = Math.Clamp(settings.MinimumSpeechConfidence, 0f, 1f);
            SpeechInputSource = settings.SpeechInputSource;
            SelectedRegion = CaptureMode == CaptureMode.Region ? settings.Region : null;
            RefreshDevices();
            await using (var recognizerService = transcriptionFactory.Create())
            {
                Replace(SpeechRecognizers, recognizerService.GetInstalledRecognizers());
            }
            SelectedSpeechRecognizer = SpeechRecognizers.FirstOrDefault(
                recognizer => recognizer.CultureName == settings.SpeechCulture) ?? SpeechRecognizers.FirstOrDefault();
            SelectedDisplay = Displays.FirstOrDefault(display => display.DeviceName == settings.DisplayDeviceName) ?? Displays.FirstOrDefault();
            SelectedMicrophone = Microphones.FirstOrDefault(device => device.Id == settings.MicrophoneDeviceId) ?? Microphones.FirstOrDefault();
            SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(device => device.Id == settings.SystemDeviceId) ?? PlaybackDevices.FirstOrDefault();
            StatusMessage = Displays.Count == 0 ? "No active displays were found." : "Ready.";
            OnPropertyChanged(nameof(StartStopHotkey));
            OnPropertyChanged(nameof(PauseResumeHotkey));
        }
        catch (Exception exception)
        {
            ReportError(exception.Message);
        }
    }

    private void RefreshDevices()
    {
        var previousDisplay = SelectedDisplay?.DeviceName;
        var previousMicrophone = SelectedMicrophone?.Id ?? settings.MicrophoneDeviceId;
        var previousPlaybackDevice = SelectedPlaybackDevice?.Id ?? settings.SystemDeviceId;
        Replace(Displays, displayEnumerator.GetActiveDisplays());
        SelectedDisplay = Displays.FirstOrDefault(display => display.DeviceName == previousDisplay) ?? Displays.FirstOrDefault();
        Replace(Microphones, audioDeviceEnumerator.GetMicrophones());
        SelectedMicrophone = Microphones.FirstOrDefault(device => device.Id == previousMicrophone)
            ?? Microphones.FirstOrDefault(static device => device.IsDefault)
            ?? Microphones.FirstOrDefault();
        Replace(PlaybackDevices, audioDeviceEnumerator.GetPlaybackDevices());
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(device => device.Id == previousPlaybackDevice)
            ?? PlaybackDevices.FirstOrDefault(static device => device.IsDefault)
            ?? PlaybackDevices.FirstOrDefault();
        StatusMessage = $"Found {Displays.Count} display(s), {Microphones.Count} microphone(s), and {PlaybackDevices.Count} playback device(s).";
        SelectRegionCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }

    private async Task SelectRegionAsync()
    {
        if (Displays.Count == 0)
        {
            ReportError("No active display is available for region selection.");
            return;
        }

        var bounds = SelectedDisplay?.Bounds ?? Geometry.BoundingRect(Displays.Select(static display => display.Bounds));
        var result = await regionSelection.SelectAsync(bounds, CancellationToken.None);
        if (result is not null)
        {
            SelectedRegion = result;
            CaptureMode = CaptureMode.Region;
            OnPropertyChanged(nameof(RegionDescription));
            StatusMessage = $"Region selected: {result}.";
        }
    }

    private async Task StartAsync()
    {
        try
        {
            if (TranscriptionMode == TranscriptionMode.SpeechToTextWindow)
            {
                OpenSpeechToTextWindow();
                return;
            }

            ValidateSettings();
            var directory = storage.ResolveOutputDirectory(OutputDirectory);
            var output = storage.CreateAvailableOutputPath(directory, FileName);
            FileName = Path.GetFileName(output);
            var effectiveCaptureMode = SelectedRegion is not null
                ? CaptureMode.Region
                : CaptureMode;
            var request = new RecordingRequest(
                effectiveCaptureMode,
                SelectedDisplay,
                Displays.ToArray(),
                SelectedRegion,
                AudioMode,
                SelectedMicrophone?.Id,
                SelectedPlaybackDevice?.Id,
                output,
                FrameRate,
                VideoBitrateMbps,
                AudioBitrateKbps,
                IncludePointer,
                MicrophoneVolume,
                SystemVolume);
            await SaveSettingsAsync();
            await recordingSession.StartAsync(request);
            if (TranscriptionMode == TranscriptionMode.RecordAndTranscribe)
            {
                recordingTranscription = transcriptionFactory.Create();
                recordingTranscription.Updated += OnTranscriptionUpdated;
                transcriptionOutputPath = output;
                try
                {
                    await recordingTranscription.StartAsync(
                        SelectedSpeechRecognizer!.CultureName,
                        MinimumSpeechConfidence,
                        SpeechInputSource,
                        SelectedPlaybackDevice?.Id);
                    StatusMessage = "Recording and local transcription are active.";
                }
                catch
                {
                    await recordingSession.StopAsync();
                    await DisposeRecordingTranscriptionAsync();
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            ReportError(exception.Message);
        }
    }

    private void ValidateSettings()
    {
        if (Displays.Count == 0) throw new InvalidOperationException("No active display is available.");
        if (CaptureMode != CaptureMode.AllDisplays && SelectedDisplay is null) throw new InvalidOperationException("Select a display.");
        if (CaptureMode == CaptureMode.Region && SelectedRegion is null) throw new InvalidOperationException("Select a region.");
        if (AudioMode is AudioMode.Microphone or AudioMode.MicrophoneAndSystem && SelectedMicrophone is null) throw new InvalidOperationException("Select an active microphone.");
        if (AudioMode is AudioMode.System or AudioMode.MicrophoneAndSystem && SelectedPlaybackDevice is null) throw new InvalidOperationException("Select an active playback device.");
        if (VideoBitrateMbps is < 1 or > 100) throw new InvalidOperationException("Video bitrate must be between 1 and 100 Mbps.");
        if (TranscriptionMode == TranscriptionMode.RecordAndTranscribe && SelectedSpeechRecognizer is null)
        {
            throw new InvalidOperationException("No Windows speech recognizer is installed. Add a speech language in Windows Settings.");
        }
    }

    private async Task PauseRecordingAsync(CancellationToken cancellationToken)
    {
        if (recordingTranscription is not null)
        {
            await recordingTranscription.PauseAsync(cancellationToken);
        }
        await recordingSession.PauseAsync(cancellationToken);
    }

    private async Task ResumeRecordingAsync(CancellationToken cancellationToken)
    {
        await recordingSession.ResumeAsync(cancellationToken);
        if (recordingTranscription is not null)
        {
            await recordingTranscription.ResumeAsync(cancellationToken);
        }
    }

    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        var transcription = recordingTranscription;
        var outputPath = transcriptionOutputPath;
        Exception? transcriptionError = null;
        try
        {
            if (transcription is not null)
            {
                await transcription.StopAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            transcriptionError = exception;
        }

        await recordingSession.StopAsync(cancellationToken);
        try
        {
            if (transcription is not null && outputPath is not null)
            {
                await transcription.SaveAsync(outputPath, cancellationToken);
                StatusMessage = "Recording finalized. Transcript saved as TXT and SRT beside the MP4.";
            }
        }
        finally
        {
            await DisposeRecordingTranscriptionAsync();
        }

        if (transcriptionError is not null)
        {
            throw new InvalidOperationException(
                "The recording was finalized, but Windows speech recognition did not stop cleanly.",
                transcriptionError);
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ReportError(exception.Message);
        }
    }

    public async Task ToggleStartStopAsync()
    {
        if (State is RecordingState.Idle or RecordingState.Completed or RecordingState.Faulted)
        {
            if (StartCommand.CanExecute(null)) await StartCommand.ExecuteAsync(null);
        }
        else if (StopCommand.CanExecute(null))
        {
            await StopCommand.ExecuteAsync(null);
        }
    }

    public async Task TogglePauseResumeAsync()
    {
        if (PauseCommand.CanExecute(null)) await PauseCommand.ExecuteAsync(null);
        else if (ResumeCommand.CanExecute(null)) await ResumeCommand.ExecuteAsync(null);
    }

    private bool CanStart() =>
        (State is RecordingState.Idle or RecordingState.Completed or RecordingState.Faulted) &&
        (TranscriptionMode == TranscriptionMode.SpeechToTextWindow || Displays.Count > 0);

    private void OnTranscriptionUpdated(object? sender, TranscriptionUpdate update)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LiveTranscript = string.IsNullOrWhiteSpace(update.PartialText)
                ? update.FinalText
                : string.IsNullOrWhiteSpace(update.FinalText)
                    ? update.PartialText
                    : $"{update.FinalText}{Environment.NewLine}{update.PartialText}";
        });
    }

    private void OpenSpeechToTextWindow()
    {
        speechToTextWindowService.Show(
            SelectedSpeechRecognizer,
            MinimumSpeechConfidence,
            SpeechInputSource,
            SelectedPlaybackDevice);
        StatusMessage = "Speech-to-text window opened. Screen recording is not required.";
    }

    private void OnStatusChanged(object? sender, RecordingStatus status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            State = status.State;
            CanEditSettings = status.State is RecordingState.Idle or RecordingState.Completed or RecordingState.Faulted;
            ElapsedText = status.Elapsed.ToString(@"hh\:mm\:ss");
            ResolutionText = status.CaptureBounds.IsEmpty ? "-" : $"{status.CaptureBounds.Width} x {status.CaptureBounds.Height}";
            ActiveFrameRate = status.FrameRate;
            DroppedFrames = status.DroppedFrames;
            EstimatedSizeText = $"{status.EstimatedBytes / 1024d / 1024d:0.0} MB";
            ActiveOutputPath = status.OutputPath;
            MicrophoneLevel = status.MicrophoneLevel;
            SystemLevel = status.SystemLevel;
            StatusMessage = status.Error ?? status.State switch
            {
                RecordingState.Recording => "Recording is active. A visible indicator remains on screen.",
                RecordingState.Paused => "Recording is paused; paused time will be omitted.",
                RecordingState.Completed => "Recording finalized successfully.",
                RecordingState.Stopping => "Finalizing the MP4 file...",
                _ => StatusMessage
            };
            RefreshCommandStates();
            OpenRecordingCommand.NotifyCanExecuteChanged();
        });
    }

    private void RefreshCommandStates()
    {
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RefreshDisplaysCommand.NotifyCanExecuteChanged();
        SelectRegionCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose recording output folder",
            InitialDirectory = Directory.Exists(storage.ResolveOutputDirectory(OutputDirectory))
                ? storage.ResolveOutputDirectory(OutputDirectory)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
        }
    }

    private void OpenOutputFolder() => OpenPath(storage.ResolveOutputDirectory(OutputDirectory));
    private void OpenRecording()
    {
        var path = ActiveOutputPath;
        if (path is not null && File.Exists(path)) OpenPath(path);
    }
    private static void OpenPath(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    public void ReportError(string message) => StatusMessage = $"Error: {message}";

    partial void OnSelectedRegionChanged(PixelRect? value) => OnPropertyChanged(nameof(RegionDescription));

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        if (value != CaptureMode.Region && SelectedRegion is not null)
        {
            SelectedRegion = null;
        }
    }

    public async Task CloseAsync(double left, double top, double width, double height)
    {
        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            await ExecuteAsync(StopRecordingAsync);
        }
        await DisposeRecordingTranscriptionAsync();
        settings.WindowLeft = left;
        settings.WindowTop = top;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        await SaveSettingsAsync();
    }

    private Task SaveSettingsAsync()
    {
        settings.CaptureMode = CaptureMode;
        settings.DisplayDeviceName = SelectedDisplay?.DeviceName;
        settings.Region = SelectedRegion;
        settings.OutputDirectory = OutputDirectory;
        settings.FrameRate = FrameRate;
        settings.VideoBitrateMbps = VideoBitrateMbps;
        settings.AudioBitrateKbps = AudioBitrateKbps;
        settings.AudioMode = AudioMode;
        settings.MicrophoneDeviceId = SelectedMicrophone?.Id;
        settings.SystemDeviceId = SelectedPlaybackDevice?.Id;
        settings.MicrophoneVolume = MicrophoneVolume;
        settings.SystemVolume = SystemVolume;
        settings.IncludePointer = IncludePointer;
        settings.TranscriptionMode = TranscriptionMode;
        settings.SpeechCulture = SelectedSpeechRecognizer?.CultureName;
        settings.MinimumSpeechConfidence = MinimumSpeechConfidence;
        settings.SpeechInputSource = SpeechInputSource;
        return settingsService.SaveAsync(settings);
    }

    private async Task DisposeRecordingTranscriptionAsync()
    {
        if (recordingTranscription is null)
        {
            return;
        }
        recordingTranscription.Updated -= OnTranscriptionUpdated;
        await recordingTranscription.DisposeAsync();
        recordingTranscription = null;
        transcriptionOutputPath = null;
    }

    partial void OnTranscriptionModeChanged(TranscriptionMode value)
    {
        StartCommand.NotifyCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    public sealed record TranscriptionModeOption(TranscriptionMode Mode, string Label);
}
