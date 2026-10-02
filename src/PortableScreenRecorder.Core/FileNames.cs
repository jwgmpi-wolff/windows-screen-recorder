using System.Text;

namespace PortableScreenRecorder.Core;

public static class FileNames
{
    private static readonly HashSet<char> Invalid = [.. Path.GetInvalidFileNameChars()];

    public static string DefaultRecordingName(DateTimeOffset now) =>
        $"Recording_{now:yyyy-MM-dd_HH-mm-ss}.mp4";

    public static string Sanitize(string value)
    {
        var name = Path.GetFileName(value.Trim());
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Invalid.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ArgumentException("The file name does not contain any valid characters.", nameof(value));
        }

        return Path.ChangeExtension(result, ".mp4");
    }
}
