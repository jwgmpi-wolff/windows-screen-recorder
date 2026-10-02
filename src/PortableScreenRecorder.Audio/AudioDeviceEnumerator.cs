using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Audio;

public sealed class AudioDeviceEnumerator(ILogger<AudioDeviceEnumerator> logger) : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> GetMicrophones() => Enumerate(DataFlow.Capture);

    public IReadOnlyList<AudioDeviceInfo> GetPlaybackDevices() => Enumerate(DataFlow.Render);

    private IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "No default {Flow} audio endpoint is available", flow);
        }

        var devices = enumerator
            .EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId))
            .OrderByDescending(static device => device.IsDefault)
            .ThenBy(static device => device.Name)
            .ToArray();
        logger.LogInformation("Enumerated {Count} active {Flow} audio endpoints", devices.Length, flow);
        return devices;
    }
}
