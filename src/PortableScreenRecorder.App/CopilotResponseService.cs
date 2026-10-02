using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using System.Speech.Synthesis;

namespace PortableScreenRecorder.App;

public interface ICopilotResponseService
{
    Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed class CopilotResponseService(
    ILogger<CopilotResponseService> logger) : ICopilotResponseService, IAsyncDisposable
{
    private readonly SemaphoreSlim requestLock = new(1, 1);
    private CopilotClient? client;
    private bool clientStarted;

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        await requestLock.WaitAsync(cancellationToken);
        try
        {
            client ??= new CopilotClient();
            if (!clientStarted)
            {
                await client.StartAsync(cancellationToken);
                clientStarted = true;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await SendAsync(prompt.Trim(), cancellationToken);
                }
                catch (Exception exception) when (attempt == 0 && IsAuthenticationTransition(exception))
                {
                    logger.LogWarning(exception, "Copilot authentication changed while creating a session; retrying once");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (Exception exception) when (IsAuthenticationFailure(exception))
                {
                    throw new InvalidOperationException(
                        "Copilot could not authenticate. Verify that GitHub Copilot is signed in, close other Copilot sign-in prompts, and try again.",
                        exception);
                }
            }
        }
        finally
        {
            requestLock.Release();
        }
    }

    private async Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
    {
        await using var session = await client!.CreateSessionAsync(
                new SessionConfig
                {
                    AvailableTools = [],
                    EnableSessionStore = false,
                    WorkingDirectory = AppContext.BaseDirectory
                },
                cancellationToken);
        var response = await session.SendAndWaitAsync(
            new MessageOptions { Prompt = prompt },
            TimeSpan.FromMinutes(2),
            cancellationToken);
        return response?.Data.Content?.Trim()
            ?? throw new InvalidOperationException("Copilot completed the request without returning a text response.");
    }

    private static bool IsAuthenticationTransition(Exception exception) =>
        GetExceptionMessages(exception).Any(message =>
            message.Contains("authentication changed", StringComparison.OrdinalIgnoreCase));

    private static bool IsAuthenticationFailure(Exception exception) =>
        GetExceptionMessages(exception).Any(message =>
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> GetExceptionMessages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            try
            {
                await client.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Copilot runtime could not be stopped within the shutdown timeout");
            }
        }
        requestLock.Dispose();
    }
}

public interface ITextToSpeechService
{
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
}

public sealed class WindowsTextToSpeechService : ITextToSpeechService
{
    public Task SpeakAsync(string text, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var synthesizer = new SpeechSynthesizer();
                synthesizer.Speak(text);
            },
            cancellationToken);
}
