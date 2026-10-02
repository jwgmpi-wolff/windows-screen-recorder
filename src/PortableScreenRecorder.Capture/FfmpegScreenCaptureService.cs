using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Capture;

public sealed class FfmpegScreenCaptureService(
    IVideoEncoder encoder,
    ILogger<FfmpegScreenCaptureService> logger) : IScreenCaptureService
{
    private readonly object sync = new();
    private Process? process;
    private readonly Queue<string> recentErrors = new();

    public Task StartAsync(RecordingRequest request, string videoPath, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (process is not null)
            {
                throw new InvalidOperationException("Screen capture is already active.");
            }

            var startInfo = new ProcessStartInfo(encoder.ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in BuildArguments(request, videoPath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += OnErrorData;
            logger.LogInformation(
                "Starting DXGI capture for {Mode}, display {Display}, region {Region}, {FrameRate} FPS, {Output}",
                request.CaptureMode,
                request.Display?.DeviceName,
                request.Region,
                request.FrameRate,
                videoPath);
            if (!process.Start())
            {
                process.Dispose();
                process = null;
                throw new InvalidOperationException("FFmpeg did not start.");
            }
            process.BeginErrorReadLine();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? active;
        lock (sync)
        {
            active = process;
        }

        if (active is null)
        {
            return;
        }

        try
        {
            if (!active.HasExited)
            {
                await active.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                await active.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("FFmpeg did not stop in time; terminating process {ProcessId}", active.Id);
                    active.Kill(entireProcessTree: true);
                    await active.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (active.ExitCode != 0)
            {
                throw new InvalidOperationException($"Screen capture failed (FFmpeg exit code {active.ExitCode}). {string.Join(Environment.NewLine, recentErrors)}");
            }
        }
        finally
        {
            lock (sync)
            {
                process = null;
            }
            active.ErrorDataReceived -= OnErrorData;
            active.Dispose();
        }
    }

    private void OnErrorData(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            return;
        }

        lock (recentErrors)
        {
            if (recentErrors.Count == 30)
            {
                recentErrors.Dequeue();
            }
            recentErrors.Enqueue(eventArgs.Data);
        }
    }

    public static IReadOnlyList<string> BuildArguments(RecordingRequest request, string outputPath)
    {
        if (request.Displays.Count == 0)
        {
            throw new InvalidOperationException("No active display is available.");
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-y" };
        var pointer = request.IncludePointer ? "1" : "0";
        var frameRate = request.FrameRate.ToString(CultureInfo.InvariantCulture);

        if (request.CaptureMode is CaptureMode.SingleDisplay or CaptureMode.Region)
        {
            var display = request.Display ?? throw new InvalidOperationException("Select a display before recording.");
            var source = new StringBuilder($"ddagrab=output_idx={display.OutputIndex}:draw_mouse={pointer}:framerate={frameRate}");
            if (request.CaptureMode == CaptureMode.Region)
            {
                var region = request.Region ?? throw new InvalidOperationException("Select a recording region before recording.");
                var clipped = Geometry.EnsureEven(Geometry.Clip(region, display.Bounds));
                if (clipped.Width < 16 || clipped.Height < 16)
                {
                    throw new InvalidOperationException("The selected region must be at least 16 x 16 pixels and lie on the selected display.");
                }
                source.Append(CultureInfo.InvariantCulture, $":video_size={clipped.Width}x{clipped.Height}:offset_x={clipped.X - display.Bounds.X}:offset_y={clipped.Y - display.Bounds.Y}");
            }

            arguments.AddRange(["-f", "lavfi", "-i", source.ToString(), "-vf", "hwdownload,format=bgra,format=nv12"]);
        }
        else
        {
            foreach (var display in request.Displays)
            {
                arguments.AddRange(["-f", "lavfi", "-i", $"ddagrab=output_idx={display.OutputIndex}:draw_mouse={pointer}:framerate={frameRate}"]);
            }

            var virtualBounds = Geometry.BoundingRect(request.Displays.Select(static d => d.Bounds));
            var filters = new List<string>();
            for (var index = 0; index < request.Displays.Count; index++)
            {
                filters.Add($"[{index}:v]hwdownload,format=bgra[v{index}]");
            }
            var layout = string.Join("|", request.Displays.Select(d => $"{d.Bounds.X - virtualBounds.X}_{d.Bounds.Y - virtualBounds.Y}"));
            var inputs = string.Concat(Enumerable.Range(0, request.Displays.Count).Select(static index => $"[v{index}]"));
            filters.Add($"{inputs}xstack=inputs={request.Displays.Count}:layout={layout}:fill=black,pad=ceil(iw/2)*2:ceil(ih/2)*2,format=nv12[out]");
            arguments.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[out]"]);
        }

        arguments.AddRange([
            "-an",
            "-c:v", "h264_mf",
            "-hw_encoding", "1",
            "-scenario", "display_remoting",
            "-rate_control", "cbr",
            "-b:v", $"{request.VideoBitrateMbps}M",
            "-fps_mode", "vfr",
            "-pix_fmt", "nv12",
            "-movflags", "+faststart",
            outputPath
        ]);
        return arguments;
    }
}
