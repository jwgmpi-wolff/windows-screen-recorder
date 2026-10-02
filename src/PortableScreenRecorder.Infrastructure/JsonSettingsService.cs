using System.Text.Json;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Infrastructure;

public sealed class JsonSettingsService(IStorageService storage, ILogger<JsonSettingsService> logger) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string SettingsPath => Path.Combine(storage.DataDirectory, "settings.json");

    public async Task<RecorderSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storage.DataDirectory);
        if (!File.Exists(SettingsPath))
        {
            var defaults = new RecorderSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<RecorderSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The settings document was empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var corruptPath = Path.Combine(storage.DataDirectory, $"settings.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}.json");
            File.Move(SettingsPath, corruptPath, overwrite: false);
            logger.LogWarning(exception, "Invalid settings were moved to {CorruptPath}; defaults will be used", corruptPath);
            var defaults = new RecorderSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
    }

    public async Task SaveAsync(RecorderSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storage.DataDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
