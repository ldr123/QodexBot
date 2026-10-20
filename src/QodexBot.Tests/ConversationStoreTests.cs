using System.Text.Json;
using Xunit;

namespace QodexBot.Tests;

public sealed class ConversationStoreTests
{
    [Fact]
    public async Task Pages_AreNewestFirst_WithoutSkippingOrRepeatingOutOfOrderTimestamps()
    {
        await using var history = await History.CreateAsync(
            Record(1, 3), Record(2, 1), Record(3, 2), Record(4, 3));

        var first = history.Store.GetPage(null, 2, null);
        Assert.Equal([4L, 1L], first.Items.Select(record => record.Id));
        Assert.True(first.HasMore);
        Assert.Equal(1L, first.NextBeforeId);

        var second = history.Store.GetPage(first.NextBeforeId, 2, null);
        Assert.Equal([3L, 2L], second.Items.Select(record => record.Id));
        Assert.False(second.HasMore);
        Assert.Equal(2L, second.NextBeforeId);

        var end = history.Store.GetPage(second.NextBeforeId, 2, null);
        Assert.Empty(end.Items);
        Assert.False(end.HasMore);
        Assert.Null(end.NextBeforeId);
    }

    [Fact]
    public async Task Pagination_AppliesBotAndUserFilters_WhilePreservingTimestampOrder()
    {
        await using var history = await History.CreateAsync(
            Record(1, 5), Record(2, 6, "bob"), Record(3, 7, bot: "other"),
            Record(4, 8, "alice-extra"), Record(5, 3), Record(6, 4));

        var first = history.Store.GetPage(null, 1, "alice", exactUserMatch: true, botId: "default");
        Assert.Equal(1L, Assert.Single(first.Items).Id);
        Assert.True(first.HasMore);
        var older = history.Store.GetPage(first.NextBeforeId, 10, "alice", exactUserMatch: true, botId: "default");
        Assert.Equal([6L, 5L], older.Items.Select(record => record.Id));
        Assert.False(older.HasMore);
    }

    [Fact]
    public async Task AppendedRecords_AppearFirst_AfterReloading()
    {
        await using var history = await History.CreateAsync(Record(1, 3), Record(2, 1));
        await history.Store.AppendAsync("alice", "assistant", "newest");
        var reloaded = new ConversationStore(history.FilePath);
        await reloaded.LoadAsync();
        var page = reloaded.GetPage(null, 10, null);
        Assert.Equal([3L, 1L, 2L], page.Items.Select(record => record.Id));
        Assert.Equal("newest", page.Items[0].Content);
    }

    private static ConversationRecord Record(long id, int time, string user = "alice", string bot = "default") =>
        new(id, DateTimeOffset.UnixEpoch.AddSeconds(time), user, "user", $"message-{id}", 0, bot);

    private sealed class History : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "qodex-history-test-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(_directory, "conversations.jsonl");
        public ConversationStore Store { get; }
        private History()
        {
            Directory.CreateDirectory(_directory);
            Store = new ConversationStore(FilePath);
        }
        public static async Task<History> CreateAsync(params ConversationRecord[] records)
        {
            var history = new History();
            await File.WriteAllLinesAsync(history.FilePath, records.Select(record => JsonSerializer.Serialize(record, AppJsonContext.Default.ConversationRecord)));
            await history.Store.LoadAsync();
            return history;
        }
        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
