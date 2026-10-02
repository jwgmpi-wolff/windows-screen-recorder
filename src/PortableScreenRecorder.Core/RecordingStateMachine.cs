namespace PortableScreenRecorder.Core;

public sealed class RecordingStateMachine
{
    private static readonly IReadOnlyDictionary<RecordingState, RecordingState[]> Allowed =
        new Dictionary<RecordingState, RecordingState[]>
        {
            [RecordingState.Idle] = [RecordingState.Preparing],
            [RecordingState.Preparing] = [RecordingState.Recording, RecordingState.Faulted, RecordingState.Stopping],
            [RecordingState.Recording] = [RecordingState.Paused, RecordingState.Stopping, RecordingState.Faulted],
            [RecordingState.Paused] = [RecordingState.Recording, RecordingState.Stopping, RecordingState.Faulted],
            [RecordingState.Stopping] = [RecordingState.Completed, RecordingState.Faulted],
            [RecordingState.Completed] = [RecordingState.Preparing, RecordingState.Idle],
            [RecordingState.Faulted] = [RecordingState.Preparing, RecordingState.Idle]
        };

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public bool CanTransitionTo(RecordingState next) =>
        Allowed.TryGetValue(State, out var allowed) && allowed.Contains(next);

    public void TransitionTo(RecordingState next)
    {
        if (!CanTransitionTo(next))
        {
            throw new InvalidOperationException($"Cannot transition recording from {State} to {next}.");
        }

        State = next;
    }
}

public sealed class ActiveDurationTracker
{
    private readonly System.Diagnostics.Stopwatch stopwatch = new();
    private TimeSpan accumulated;

    public TimeSpan Elapsed => accumulated + stopwatch.Elapsed;
    public void Start() => stopwatch.Start();
    public void Pause()
    {
        stopwatch.Stop();
        accumulated += stopwatch.Elapsed;
        stopwatch.Reset();
    }
    public void Reset()
    {
        stopwatch.Reset();
        accumulated = TimeSpan.Zero;
    }
}
