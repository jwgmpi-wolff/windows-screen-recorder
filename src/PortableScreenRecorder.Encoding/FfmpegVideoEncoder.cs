using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Encoding;

public sealed class FfmpegVideoEncoder(ILogger<FfmpegVideoEncoder> logger) : IVideoEncoder
{
    public string ExecutablePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("PORTABLE_SCREEN_RECORDER_FFMPEG");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : "ffmpeg.exe";
        }
    }

    public async Task VerifyAvailableAsync(CancellationToken cancellationToken)
    {
        var filters = await RunAsync(["-hide_banner", "-filters"], cancellationToken).ConfigureAwait(false);
        var encoders = await RunAsync(["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false);
        if (!filters.Contains("ddagrab", StringComparison.Ordinal) ||
            !encoders.Contains("h264_mf", StringComparison.Ordinal) ||
            !encoders.Contains(" aac ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bundled FFmpeg build must include ddagrab, h264_mf, and AAC support.");
        }
    }

    public async Task MuxSegmentAsync(
        string videoPath,
        string? microphonePath,
        string? systemPath,
        string outputPath,
        RecordingRequest request,
        CancellationToken cancellationToken)
    {
        if (microphonePath is null && systemPath is null)
        {
            File.Move(videoPath, outputPath, overwrite: false);
            return;
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", videoPath };
        if (microphonePath is not null) arguments.AddRange(["-i", microphonePath]);
        if (systemPath is not null) arguments.AddRange(["-i", systemPath]);

        var filters = new List<string>();
        var audioInputs = new List<string>();
        var inputIndex = 1;
        if (microphonePath is not null)
        {
            filters.Add(FormattableString.Invariant($"[{inputIndex++}:a]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={request.MicrophoneVolume:0.###}[mic]"));
            audioInputs.Add("[mic]");
        }
        if (systemPath is not null)
        {
            filters.Add(FormattableString.Invariant($"[{inputIndex}:a]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={request.SystemVolume:0.###}[system]"));
            audioInputs.Add("[system]");
        }
        if (audioInputs.Count == 2)
        {
            filters.Add($"{string.Concat(audioInputs)}amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95[aout]");
        }
        else
        {
            filters.Add($"{audioInputs[0]}anull[aout]");
        }

        arguments.AddRange([
            "-filter_complex", string.Join(";", filters),
            "-map", "0:v:0",
            "-map", "[aout]",
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", $"{request.AudioBitrateKbps}k",
            "-ar", "48000",
            "-ac", "2",
            "-shortest",
            "-movflags", "+faststart",
            outputPath
        ]);
        await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConcatenateAsync(IReadOnlyList<string> segmentPaths, string partialOutputPath, CancellationToken cancellationToken)
    {
        if (segmentPaths.Count == 0)
        {
            throw new InvalidOperationException("The recording contains no completed segments.");
        }
        if (segmentPaths.Count == 1)
        {
            File.Copy(segmentPaths[0], partialOutputPath, overwrite: false);
            return;
        }

        var listPath = Path.Combine(Path.GetDirectoryName(partialOutputPath)!, "segments.txt");
        var lines = segmentPaths.Select(path => $"file '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'");
        await File.WriteAllLinesAsync(listPath, lines, cancellationToken).ConfigureAwait(false);
        await RunAsync([
            "-hide_banner", "-loglevel", "warning", "-y",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy", "-movflags", "+faststart", partialOutputPath
        ], cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"FFmpeg was not found at '{ExecutablePath}'. Publish the portable package or set PORTABLE_SCREEN_RECORDER_FFMPEG.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogError("FFmpeg exited with {ExitCode}: {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"FFmpeg failed with exit code {process.ExitCode}: {error}");
        }
        return output + Environment.NewLine + error;
    }
}
