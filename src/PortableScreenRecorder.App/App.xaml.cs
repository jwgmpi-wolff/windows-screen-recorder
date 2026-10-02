using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Audio;
using PortableScreenRecorder.Capture;
using PortableScreenRecorder.Core;
using PortableScreenRecorder.Encoding;
using PortableScreenRecorder.Infrastructure;
using PortableScreenRecorder.Transcription;

namespace PortableScreenRecorder.App;

public partial class App : Application
{
    private ServiceProvider? services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var storage = new PortableStorageService();
        var loggerFactory = LoggingConfiguration.Create(storage.LogsDirectory);
        var collection = new ServiceCollection();
        collection.AddSingleton<IStorageService>(storage);
        collection.AddSingleton(loggerFactory);
        collection.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        collection.AddSingleton<ISettingsService, JsonSettingsService>();
        collection.AddSingleton<IDisplayEnumerator, Win32DisplayEnumerator>();
        collection.AddSingleton<IAudioDeviceEnumerator, AudioDeviceEnumerator>();
        collection.AddSingleton<IMicrophoneCaptureService, MicrophoneCaptureService>();
        collection.AddSingleton<ISystemAudioCaptureService, SystemAudioCaptureService>();
        collection.AddSingleton<IAudioMixer, AudioMixer>();
        collection.AddSingleton<IVideoEncoder, FfmpegVideoEncoder>();
        collection.AddSingleton<IScreenCaptureService, FfmpegScreenCaptureService>();
        collection.AddSingleton<IRecordingSession, RecordingSession>();
        collection.AddSingleton<IRegionSelectionService, RegionSelectionService>();
        collection.AddSingleton<IHotkeyService, GlobalHotkeyService>();
        collection.AddSingleton<ISpeechTranscriptionServiceFactory, WindowsSpeechTranscriptionServiceFactory>();
        collection.AddSingleton<ICopilotResponseService, CopilotResponseService>();
        collection.AddSingleton<ITextToSpeechService, WindowsTextToSpeechService>();
        collection.AddSingleton<ISpeechToTextWindowService, SpeechToTextWindowService>();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<MainWindow>();
        services = collection.BuildServiceProvider();

        var logger = loggerFactory.CreateLogger<App>();
        logger.LogInformation("PortableScreenRecorder starting on {OperatingSystem}, .NET {Runtime}", Environment.OSVersion, Environment.Version);
        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogCritical(args.Exception, "Unhandled dispatcher exception");
            MessageBox.Show(args.Exception.Message, "PortableScreenRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            logger.LogCritical(args.ExceptionObject as Exception, "Unhandled application-domain exception");

        services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (services is not null)
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnExit(e);
    }
}
