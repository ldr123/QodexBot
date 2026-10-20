using System.Collections.Concurrent;

internal sealed record CodexImage(byte[] Bytes, string ContentType, string Extension);
internal sealed record CodexTurnReply(string Text, IReadOnlyList<CodexImage> Images, string ReasoningSummary = "");

internal sealed record CodexThreadSummary(string Id, string Name, string WorkDirectory, DateTimeOffset UpdatedAt);
internal sealed record CodexThreadPage(IReadOnlyList<CodexThreadSummary> Threads, string? NextCursor);

internal static class WorkspacePath
{
    public static string Normalize(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            path = path.Replace('/', '\\');
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) path = "\\\\" + path[8..];
            else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Matches(string left, string right)
    {
        try { return string.Equals(Normalize(left), Normalize(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}

internal sealed class ReplyContext(string messageId, string userOpenId, string? groupOpenId = null)
{
    private int _sequence = 1;
    private string _messageId = messageId;
    public string MessageId => Volatile.Read(ref _messageId);
    public string UserOpenId { get; } = userOpenId;
    public string? GroupOpenId { get; } = groupOpenId;
    public string ChatOpenId => GroupOpenId ?? UserOpenId;
    public int Sequence => _sequence;
    public ConcurrentDictionary<string, byte> SentImageHashes { get; } = new(StringComparer.Ordinal);
    public int NextSequence() => Interlocked.Increment(ref _sequence) - 1;
    public void StartReplyTo(string newMessageId)
    {
        Volatile.Write(ref _messageId, newMessageId);
        Interlocked.Exchange(ref _sequence, 1);
    }
}

internal sealed class PendingQuestion(ReplyContext context)
{
    private readonly object _sync = new();
    public ReplyContext Context { get; } = context;
    public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool TryAnswer(string messageId, string answer)
    {
        lock (_sync)
        {
            if (Completion.Task.IsCompleted) return false;
            Context.StartReplyTo(messageId);
            return Completion.TrySetResult(answer);
        }
    }
    public void Close() => Completion.TrySetCanceled();
}
