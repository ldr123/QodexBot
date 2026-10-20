using System.Text.Json;

/// <summary>Stores only each QQ user's current selection; Codex owns the project session history.</summary>
internal sealed class BotConversationStateStore(string filePath)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath = Path.GetFullPath(filePath);
    private Dictionary<string, BotConversationState> _states = new(StringComparer.Ordinal);

    public async Task LoadAsync(CancellationToken token)
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            _states = JsonSerializer.Deserialize(await File.ReadAllTextAsync(_filePath, token),
                AppJsonContext.Default.DictionaryStringBotConversationState) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine($"读取用户当前工作区配置失败：{ex.Message}"); }
    }

    public async Task<BotConversationState> GetAsync(string owner, string defaultDirectory, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try { return _states.GetValueOrDefault(owner) ?? new(defaultDirectory); }
        finally { _lock.Release(); }
    }

    public Task<BotConversationState> SetWorkspaceAsync(string owner, string workDirectory, CancellationToken token) =>
        UpdateAsync(owner, workDirectory, current => WorkspacePath.Matches(current.WorkDirectory, workDirectory)
            ? current with { WorkDirectory = workDirectory } : new(workDirectory), token);

    public Task<BotConversationState> SelectThreadAsync(string owner, CodexThreadSummary thread, CancellationToken token) =>
        UpdateAsync(owner, thread.WorkDirectory, current => new(thread.WorkDirectory, thread.Id,
            current.ThreadId == thread.Id ? current.Model : null), token);

    public Task<BotConversationState> SetModelAsync(string owner, string defaultDirectory, string model, CancellationToken token) =>
        UpdateAsync(owner, defaultDirectory, current => current with { Model = model }, token);

    public Task<BotConversationState> SelectIfUnchangedAsync(string owner, BotConversationState expected, string threadId, CancellationToken token) =>
        UpdateAsync(owner, expected.WorkDirectory, current => current == expected ? current with { ThreadId = threadId } : current, token);

    private async Task<BotConversationState> UpdateAsync(string owner, string defaultDirectory,
        Func<BotConversationState, BotConversationState> update, CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            var next = update(_states.GetValueOrDefault(owner) ?? new(defaultDirectory));
            var updated = new Dictionary<string, BotConversationState>(_states, StringComparer.Ordinal) { [owner] = next };
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temporaryPath = _filePath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updated,
                    AppJsonContext.Default.DictionaryStringBotConversationState), token);
                File.Move(temporaryPath, _filePath, overwrite: true);
                _states = updated;
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            return next;
        }
        finally { _lock.Release(); }
    }
}

internal sealed record BotConversationState(string WorkDirectory, string? ThreadId = null, string? Model = null);
