internal sealed class FakeCodexSessionClient : ICodexSessionClient
{
    public List<CodexThreadSummary> Threads { get; } = [];
    public List<string> Requests { get; } = [];
    public bool FailListing { get; set; }

    public Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(string workDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add(workDirectory);
        if (FailListing) throw new InvalidOperationException("test: unavailable");
        // Deliberately return unrelated workspaces too, to exercise the client's filtering.
        return Task.FromResult<IReadOnlyList<CodexThreadSummary>>(Threads.ToArray());
    }

    public Task<CodexThreadSummary?> ReadThreadAsync(string threadId, string workDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Threads.FirstOrDefault(thread => thread.Id == threadId && WorkspacePath.Matches(thread.WorkDirectory, workDirectory)));
    }

    public Task<string> CreateThreadAsync(string workDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString();
        Threads.Add(new(id, "新会话", workDirectory, DateTimeOffset.UtcNow));
        return Task.FromResult(id);
    }

    public Task SetThreadNameAsync(string threadId, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var index = Threads.FindIndex(thread => thread.Id == threadId);
        Threads[index] = Threads[index] with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }
}
