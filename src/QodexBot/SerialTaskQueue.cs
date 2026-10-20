/// <summary>Runs Codex requests in arrival order without blocking independent bot commands.</summary>
internal sealed class SerialTaskQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _tail = RunAfterAsync(_tail, action, cancellationToken);
            return _tail;
        }
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> action, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try { await previous.ConfigureAwait(false); }
        catch { /* A failed message must not prevent the next message from running. */ }
        cancellationToken.ThrowIfCancellationRequested();
        await action().ConfigureAwait(false);
    }
}
