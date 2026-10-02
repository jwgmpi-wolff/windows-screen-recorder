using System.Windows;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.App;

public sealed class RegionSelectionService : IRegionSelectionService
{
    public async Task<PixelRect?> SelectAsync(PixelRect bounds, CancellationToken cancellationToken)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("The region-selection bounds are empty.", nameof(bounds));
        }

        var window = new RegionSelectionWindow(bounds);
        using var registration = cancellationToken.Register(() => window.Dispatcher.Invoke(window.Cancel));
        window.Show();
        return await window.Completion.ConfigureAwait(true);
    }
}
