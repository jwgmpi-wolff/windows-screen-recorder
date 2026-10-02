using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.App;

public partial class RegionSelectionWindow : Window
{
    private readonly PixelRect physicalBounds;
    private readonly TaskCompletionSource<PixelRect?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativePoint start;
    private PixelRect selection;
    private bool dragging;
    private double scaleX = 1;
    private double scaleY = 1;

    public RegionSelectionWindow(PixelRect bounds)
    {
        InitializeComponent();
        physicalBounds = bounds;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => completion.TrySetResult(null);
    }

    public Task<PixelRect?> Completion => completion.Task;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowPos(
            handle,
            new nint(-1),
            physicalBounds.X,
            physicalBounds.Y,
            physicalBounds.Width,
            physicalBounds.Height,
            0x0040))
        {
            throw new InvalidOperationException("Windows could not position the region-selection overlay.");
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
            {
                scaleX = source.CompositionTarget.TransformToDevice.M11;
                scaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            UpdateDim();
            Activate();
            Focus();
        });
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HasButtonAncestor(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (!GetCursorPos(out start))
        {
            throw new InvalidOperationException("Windows could not read the mouse position.");
        }
        start = ClampToBounds(start);
        dragging = true;
        selection = default;
        CaptureMouse();
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        UpdateSelection();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!dragging)
        {
            return;
        }
        if (!GetCursorPos(out var current))
        {
            return;
        }
        current = ClampToBounds(current);
        selection = PortableScreenRecorder.Core.Geometry.Clip(
            PortableScreenRecorder.Core.Geometry.Normalize(start.X, start.Y, current.X, current.Y),
            physicalBounds);
        UpdateSelection();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging)
        {
            return;
        }
        dragging = false;
        ReleaseMouseCapture();
        ConfirmationPanel.Visibility = selection.Width >= 16 && selection.Height >= 16
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSelection();
        Focus();
    }

    private void UpdateSelection()
    {
        if (selection.IsEmpty)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            InfoPanel.Visibility = Visibility.Collapsed;
            UpdateDim();
            return;
        }

        SelectionBorder.Visibility = Visibility.Visible;
        var logicalSelection = ToLogicalRect(selection);
        SelectionBorder.Margin = new Thickness(logicalSelection.X, logicalSelection.Y, 0, 0);
        SelectionBorder.Width = logicalSelection.Width;
        SelectionBorder.Height = logicalSelection.Height;
        DimensionsText.Text = $"{selection.Width} x {selection.Height}";
        InfoPanel.Visibility = Visibility.Visible;
        InfoPanel.Margin = new Thickness(
            Math.Min(logicalSelection.X, Math.Max(0, ActualWidth - 210)),
            Math.Min(logicalSelection.Bottom + 8, Math.Max(0, ActualHeight - 90)),
            0,
            0);
        UpdateDim();
    }

    private void UpdateDim()
    {
        var outside = new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
        var logicalSelection = ToLogicalRect(selection);
        DimPath.Data = selection.IsEmpty
            ? outside
            : new CombinedGeometry(GeometryCombineMode.Exclude, outside, new RectangleGeometry(logicalSelection));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
        }
        else if (e.Key == Key.Enter)
        {
            Confirm();
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Confirm();
    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();

    private void Confirm()
    {
        if (selection.Width < 16 || selection.Height < 16)
        {
            return;
        }
        completion.TrySetResult(PortableScreenRecorder.Core.Geometry.EnsureEven(selection));
        Close();
    }

    public void Cancel()
    {
        completion.TrySetResult(null);
        Close();
    }

    private static bool HasButtonAncestor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button)
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private NativePoint ClampToBounds(NativePoint point) => new()
    {
        X = Math.Clamp(point.X, physicalBounds.X, physicalBounds.Right),
        Y = Math.Clamp(point.Y, physicalBounds.Y, physicalBounds.Bottom)
    };

    private Rect ToLogicalRect(PixelRect physical) =>
        physical.IsEmpty
            ? Rect.Empty
            : new Rect(
                (physical.X - physicalBounds.X) / scaleX,
                (physical.Y - physicalBounds.Y) / scaleY,
                physical.Width / scaleX,
                physical.Height / scaleY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
