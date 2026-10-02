using System.ComponentModel;
using System.Windows;

namespace PortableScreenRecorder.App;

public partial class SpeechToTextWindow : Window
{
    private readonly SpeechToTextViewModel viewModel;
    private bool closing;
    private bool disposed;

    public SpeechToTextWindow(SpeechToTextViewModel viewModel)
    {
        InitializeComponent();
        DataContext = this.viewModel = viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (disposed)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (closing)
        {
            return;
        }

        closing = true;
        IsEnabled = false;
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Speech recognition could not stop cleanly: {exception.Message}",
                "PortableScreenRecorder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            disposed = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
