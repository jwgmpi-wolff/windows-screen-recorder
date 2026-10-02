using System.Text;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Transcription;

public static class TranscriptWriter
{
    public static async Task WriteAsync(
        string outputBasePath,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken = default)
    {
        var text = string.Join(Environment.NewLine, segments.Select(static segment => segment.Text));
        await File.WriteAllTextAsync(outputBasePath + ".txt", text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(outputBasePath + ".srt", ToSrt(segments), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var start = segment.Start < TimeSpan.Zero ? TimeSpan.Zero : segment.Start;
            var end = segment.End <= start ? start + TimeSpan.FromSeconds(1) : segment.End;
            builder.AppendLine((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(FormatTimestamp(start));
            builder.Append(" --> ");
            builder.AppendLine(FormatTimestamp(end));
            builder.AppendLine(segment.Text.ReplaceLineEndings(" "));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatTimestamp(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
}
