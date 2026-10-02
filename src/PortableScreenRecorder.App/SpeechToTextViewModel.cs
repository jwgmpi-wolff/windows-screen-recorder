using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PortableScreenRecorder.Core;
using PortableScreenRecorder.Transcription;

namespace PortableScreenRecorder.App;

public partial class SpeechToTextViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISpeechTranscriptionService service;
    private readonly ICopilotResponseService copilotService;
    private readonly ITextToSpeechService textToSpeechService;
    private readonly SpeechInputSource inputSource;
    private readonly AudioDeviceInfo? playbackDevice;
    private IReadOnlyList<TranscriptSegment> segments = [];
    private string lastPartialText = string.Empty;

    [ObservableProperty] private SpeechRecognizerInfo? selectedRecognizer;
    [ObservableProperty] private float minimumConfidence;
    [ObservableProperty] private string transcript = string.Empty;
    [ObservableProperty] private string partialText = string.Empty;
    [ObservableProperty] private string displayedTranscript = string.Empty;
    [ObservableProperty] private string status = "Ready to listen.";
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private string copilotPrompt = string.Empty;
    [ObservableProperty] private string copilotResponse = string.Empty;
    [ObservableProperty] private bool readCopilotResponseAloud;
    [ObservableProperty] private bool isCopilotBusy;

    public SpeechToTextViewModel(
        ISpeechTranscriptionServiceFactory factory,
        ICopilotResponseService copilotService,
        ITextToSpeechService textToSpeechService,
        SpeechRecognizerInfo? preferredRecognizer,
        float confidence,
        SpeechInputSource inputSource,
        AudioDeviceInfo? playbackDevice)
    {
        service = factory.Create();
        this.copilotService = copilotService;
        this.textToSpeechService = textToSpeechService;
        this.inputSource = inputSource;
        this.playbackDevice = playbackDevice;
        service.Updated += OnUpdated;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsListening && SelectedRecognizer is not null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsListening);
        ClearCommand = new RelayCommand(Clear, () => !IsListening);
        CopyCommand = new RelayCommand(Copy, () => !string.IsNullOrWhiteSpace(Transcript));
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !string.IsNullOrWhiteSpace(Transcript));
        UseTranscriptCommand = new RelayCommand(
            () => CopilotPrompt = DisplayedTranscript.Trim(),
            () => !string.IsNullOrWhiteSpace(DisplayedTranscript) && !IsCopilotBusy);
        AskCopilotCommand = new AsyncRelayCommand(
            AskCopilotAsync,
            () => !string.IsNullOrWhiteSpace(CopilotPrompt) && !IsCopilotBusy);
        CopyCopilotResponseCommand = new RelayCommand(
            CopyCopilotResponse,
            () => !string.IsNullOrWhiteSpace(CopilotResponse));

        foreach (var recognizer in service.GetInstalledRecognizers())
        {
            Recognizers.Add(recognizer);
        }
        SelectedRecognizer = Recognizers.FirstOrDefault(
            recognizer => recognizer.CultureName == preferredRecognizer?.CultureName) ?? Recognizers.FirstOrDefault();
        MinimumConfidence = confidence;
    }

    public ObservableCollection<SpeechRecognizerInfo> Recognizers { get; } = [];
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand UseTranscriptCommand { get; }
    public IAsyncRelayCommand AskCopilotCommand { get; }
    public IRelayCommand CopyCopilotResponseCommand { get; }

    partial void OnCopilotPromptChanged(string value) => AskCopilotCommand.NotifyCanExecuteChanged();

    partial void OnDisplayedTranscriptChanged(string value) => UseTranscriptCommand.NotifyCanExecuteChanged();

    partial void OnIsCopilotBusyChanged(bool value)
    {
        AskCopilotCommand.NotifyCanExecuteChanged();
        UseTranscriptCommand.NotifyCanExecuteChanged();
    }

    partial void OnCopilotResponseChanged(string value) => CopyCopilotResponseCommand.NotifyCanExecuteChanged();

    private async Task StartAsync()
    {
        try
        {
            lastPartialText = string.Empty;
            await service.StartAsync(
                SelectedRecognizer!.CultureName,
                MinimumConfidence,
                inputSource,
                playbackDevice?.Id);
            Status = inputSource switch
            {
                SpeechInputSource.SystemAudio =>
                    $"Capturing {playbackDevice?.Name ?? "the default playback device"} with local Whisper; text appears after a short pause...",
                SpeechInputSource.MicrophoneAndSystem =>
                    $"Capturing the microphone and {playbackDevice?.Name ?? "the default playback device"} with local Whisper...",
                _ => "Listening locally through the Windows default microphone..."
            };
        }
        catch (Exception exception)
        {
            Status = $"Error: {exception.Message}";
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await service.StopAsync();
            if (string.IsNullOrWhiteSpace(Transcript) && !string.IsNullOrWhiteSpace(lastPartialText))
            {
                Transcript = lastPartialText.Trim();
                DisplayedTranscript = Transcript;
                segments =
                [
                    new TranscriptSegment(
                        Transcript,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        0f)
                ];
                Status = "No final result met the confidence threshold. The last live text was retained so you can copy or save it.";
                RefreshCommands();
            }
            else
            {
                Status = "Stopped. You can copy or save the transcript.";
            }
        }
        catch (Exception exception)
        {
            Status = $"Error: {exception.Message}";
        }
    }

    private void Clear()
    {
        Transcript = string.Empty;
        PartialText = string.Empty;
        DisplayedTranscript = string.Empty;
        segments = [];
        lastPartialText = string.Empty;
        Status = "Transcript cleared.";
        RefreshCommands();
    }

    private void Copy()
    {
        Clipboard.SetText(Transcript);
        Status = "Transcript copied to the clipboard.";
    }

    private async Task SaveAsync()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save speech transcript",
                Filter = "Text transcript (*.txt)|*.txt",
                FileName = $"Transcript_{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}.txt",
                AddExtension = true,
                DefaultExt = ".txt"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var basePath = Path.Combine(
                Path.GetDirectoryName(dialog.FileName)!,
                Path.GetFileNameWithoutExtension(dialog.FileName));
            await File.WriteAllTextAsync(basePath + ".txt", Transcript);
            await File.WriteAllTextAsync(basePath + ".srt", TranscriptWriter.ToSrt(segments));
            Status = $"Saved {Path.GetFileName(basePath)}.txt and .srt.";
        }
        catch (Exception exception)
        {
            Status = $"Could not save transcript: {exception.Message}";
        }
    }

    private async Task AskCopilotAsync()
    {
        IsCopilotBusy = true;
        CopilotResponse = string.Empty;
        Status = "Waiting for Copilot...";
        try
        {
            CopilotResponse = await copilotService.AskAsync(CopilotPrompt);
            Status = "Copilot response received.";
            if (ReadCopilotResponseAloud)
            {
                Status = "Copilot response received. Reading it aloud...";
                await textToSpeechService.SpeakAsync(CopilotResponse);
                Status = "Copilot response received and read aloud.";
            }
        }
        catch (Exception exception)
        {
            Status = $"Copilot error: {exception.Message}";
        }
        finally
        {
            IsCopilotBusy = false;
        }
    }

    private void CopyCopilotResponse()
    {
        Clipboard.SetText(CopilotResponse);
        Status = "Copilot response copied to the clipboard.";
    }

    private void OnUpdated(object? sender, TranscriptionUpdate update)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Transcript = update.FinalText;
            PartialText = update.PartialText;
            DisplayedTranscript = string.IsNullOrWhiteSpace(update.PartialText)
                ? update.FinalText
                : string.IsNullOrWhiteSpace(update.FinalText)
                    ? update.PartialText
                    : $"{update.FinalText}{Environment.NewLine}{update.PartialText}";
            if (!string.IsNullOrWhiteSpace(update.PartialText))
            {
                lastPartialText = update.PartialText;
            }
            segments = update.Segments;
            IsListening = update.IsListening;
            RefreshCommands();
        });
    }

    partial void OnSelectedRecognizerChanged(SpeechRecognizerInfo? value) => StartCommand.NotifyCanExecuteChanged();

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        service.Updated -= OnUpdated;
        await service.DisposeAsync();
    }
}

public interface ISpeechToTextWindowService
{
    void Show(
        SpeechRecognizerInfo? preferredRecognizer,
        float minimumConfidence,
        SpeechInputSource inputSource,
        AudioDeviceInfo? playbackDevice);
}

public sealed class SpeechToTextWindowService(
    ISpeechTranscriptionServiceFactory factory,
    ICopilotResponseService copilotService,
    ITextToSpeechService textToSpeechService) : ISpeechToTextWindowService
{
    public void Show(
        SpeechRecognizerInfo? preferredRecognizer,
        float minimumConfidence,
        SpeechInputSource inputSource,
        AudioDeviceInfo? playbackDevice)
    {
        var viewModel = new SpeechToTextViewModel(
            factory,
            copilotService,
            textToSpeechService,
            preferredRecognizer,
            minimumConfidence,
            inputSource,
            playbackDevice);
        new SpeechToTextWindow(viewModel).Show();
    }
}
