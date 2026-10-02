using System.Text;
using PortableScreenRecorder.Core;
using PortableScreenRecorder.Transcription;

namespace PortableScreenRecorder.UnitTests;

public sealed class TranscriptTests
{
    [Fact]
    public void Settings_DefaultToBestEffortSpeechCapture()
    {
        Assert.Equal(0f, new RecorderSettings().MinimumSpeechConfidence);
    }

    [Fact]
    public void SrtFormatting_UsesMonotonicTimestampsAndFlattensLineBreaks()
    {
        var result = TranscriptWriter.ToSrt([
            new TranscriptSegment("Hello world.", TimeSpan.Zero, TimeSpan.FromSeconds(1.25), 0.9f),
            new TranscriptSegment("Second\r\nline.", TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), 0.8f)
        ]);

        Assert.Equal(
            """
            1
            00:00:00,000 --> 00:00:01,250
            Hello world.

            2
            00:00:02,000 --> 00:00:02,500
            Second line.


            """.ReplaceLineEndings(),
            result);
    }

    [Fact]
    public async Task Writer_CreatesUtf8TextAndSubtitleSidecars()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"psr-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var basePath = Path.Combine(directory, "recording");
        try
        {
            await TranscriptWriter.WriteAsync(
                basePath,
                [new TranscriptSegment("Portable transcript.", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0.95f)]);

            Assert.Equal("Portable transcript.", await File.ReadAllTextAsync(basePath + ".txt", System.Text.Encoding.UTF8));
            Assert.Contains("Portable transcript.", await File.ReadAllTextAsync(basePath + ".srt", System.Text.Encoding.UTF8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
