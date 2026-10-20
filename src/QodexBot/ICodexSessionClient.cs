internal interface ICodexSessionClient
{
    Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(string workDirectory, CancellationToken token);
    Task<CodexThreadSummary?> ReadThreadAsync(string threadId, string workDirectory, CancellationToken token);
    Task<string> CreateThreadAsync(string workDirectory, CancellationToken token);
    Task SetThreadNameAsync(string threadId, string name, CancellationToken token);
}
