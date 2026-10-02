namespace PortableScreenRecorder.Core;

public sealed class AudioMixer : IAudioMixer
{
    public void Mix(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, Span<float> destination, float microphoneVolume, float systemVolume)
    {
        if (destination.Length < Math.Max(microphone.Length, system.Length))
        {
            throw new ArgumentException("The destination buffer is too small.", nameof(destination));
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var mic = index < microphone.Length ? microphone[index] * microphoneVolume : 0f;
            var loopback = index < system.Length ? system[index] * systemVolume : 0f;
            destination[index] = Math.Clamp(mic + loopback, -1f, 1f);
        }
    }
}
