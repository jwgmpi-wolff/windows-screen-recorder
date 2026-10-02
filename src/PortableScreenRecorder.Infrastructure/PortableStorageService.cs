using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Infrastructure;

public sealed class PortableStorageService : IStorageService
{
    public string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public string LogsDirectory => Path.Combine(DataDirectory, "Logs");

    public string ResolveOutputDirectory(string configuredPath)
    {
        var path = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
    }

    public string CreateAvailableOutputPath(string directory, string requestedFileName)
    {
        EnsureWritableDirectory(directory);
        var safeName = FileNames.Sanitize(requestedFileName);
        var path = Path.Combine(directory, safeName);
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(safeName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            path = Path.Combine(directory, $"{stem}_{suffix}.mp4");
            if (!File.Exists(path))
            {
                return path;
            }
        }

        throw new IOException("Could not create a unique output file name.");
    }

    public void EnsureWritableDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if (drive.AvailableFreeSpace < 256L * 1024 * 1024)
        {
            throw new IOException("The output drive has less than 256 MB of free space.");
        }

        var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
        using (File.Create(probe, 1, FileOptions.DeleteOnClose))
        {
        }
    }
}
