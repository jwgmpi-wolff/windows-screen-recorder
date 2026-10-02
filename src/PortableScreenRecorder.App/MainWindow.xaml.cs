using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly IHotkeyService hotkeys;
    private bool closing;
    private bool disposed;

    public MainWindow(MainViewModel viewModel, IHotkeyService hotkeys)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
        this.hotkeys = hotkeys;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
        Width = viewModel.WindowWidth;
        Height = viewModel.WindowHeight;
        if (viewModel.WindowLeft.HasValue && viewModel.WindowTop.HasValue)
        {
            Left = viewModel.WindowLeft.Value;
            Top = viewModel.WindowTop.Value;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source.AddHook(WindowProcedure);
        try
        {
            hotkeys.Attach(source.Handle, viewModel.StartStopHotkey, viewModel.PauseResumeHotkey);
            hotkeys.StartStopPressed += OnStartStopPressed;
            hotkeys.PauseResumePressed += OnPauseResumePressed;
        }
        catch (Exception exception)
        {
            viewModel.ReportError(exception.Message);
        }
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        handled = hotkeys.ProcessMessage(message, wParam);
        return 0;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        e.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        IsEnabled = false;
        try
        {
            await viewModel.CloseAsync(Left, Top, ActualWidth, ActualHeight);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"The recorder could not close cleanly: {exception.Message}",
                "PortableScreenRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            disposed = true;
            _ = Dispatcher.BeginInvoke(() =>
            {
                Close();
                Application.Current.Shutdown();
            });
        }
    }

    private async void OnStartStopPressed(object? sender, EventArgs e) => await viewModel.ToggleStartStopAsync();
    private async void OnPauseResumePressed(object? sender, EventArgs e) => await viewModel.TogglePauseResumeAsync();
}
