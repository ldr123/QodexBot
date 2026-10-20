using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace QodexBot.Tests;

public sealed class MessageConcurrencyTests
{
    [Fact]
    public async Task Queue_PreservesOrderAcrossAllUsers()
    {
        var queue = new SerialTaskQueue();
        var entered = NewSignal();
        var release = NewSignal();
        var order = new List<string>();
        var first = queue.RunAsync(async () =>
        {
            order.Add("alice-1");
            entered.SetResult();
            await release.Task;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.RunAsync(() => { order.Add("bob-1"); return Task.CompletedTask; }, default);
        var third = queue.RunAsync(() => { order.Add("alice-2"); return Task.CompletedTask; }, default);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.False(third.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["alice-1", "bob-1", "alice-2"], order);
    }

    [Fact]
    public async Task Queue_FailureAndCancellation_DoNotBlockLaterMessages()
    {
        var queue = new SerialTaskQueue();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var failed = queue.RunAsync(() => Task.FromException(new InvalidOperationException("test")), default);
        var skipped = queue.RunAsync(() => throw new Exception("must not run"), cancelled.Token);
        var last = queue.RunAsync(() => Task.CompletedTask, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        await last.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("/", "可用指令")]
    [InlineData("/状态", "机器人：")]
    [InlineData("/会话", "项目会话")]
    [InlineData("/最近", "暂无对话记录")]
    [InlineData("/工作目录", "当前工作目录")]
    [InlineData("/标题 新名称", "会话已命名为：新名称")]
    [InlineData("/切换 1", "已切换到会话")]
    [InlineData("/停止", "当前没有正在运行的任务")]
    [InlineData("/截图", "截图已发送")]
    public async Task LocalCommand_RespondsWhileMessagesAndRetryAreQueued_WithoutReplacingActiveContext(string command, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qodex-queue-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var queue = new SerialTaskQueue();
        var entered = NewSignal();
        var release = NewSignal();
        var blocking = queue.RunAsync(async () => { entered.SetResult(); await release.Task; }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var shutdown = new CancellationTokenSource();
        using var handler = new FakeQqHandler();
        using var http = new HttpClient(handler);
        await using var codex = new CodexAppServer("queue-test");
        await using var mcp = new LocalToolIpcServer();
        var originalContext = new ReplyContext("running-message", "alice");
        codex.SetActiveToolContext(originalContext);
        var state = new BotConversationStateStore(Path.Combine(directory, "conversation-state.json"));
        var sessions = new FakeCodexSessionClient();
        var thread = new CodexThreadSummary("test-thread", "测试会话", codex.WorkDirectory, DateTimeOffset.UtcNow);
        sessions.Threads.Add(thread);
        await state.SelectThreadAsync("test:alice", thread, default);
        var client = new QqBotClient(http, new BotConfig("test", "test", "id", "secret", true, 0, true, true),
            codex, mcp, new ConversationStore(Path.Combine(directory, "conversations.jsonl")), state,
            () => BotSettingsStore.DefaultCommandBindings, () => "test", _ => { }, queue,
            projectSessions: sessions);
        var message = client.HandleMessageAsync(Message("normal", "普通消息"), shutdown.Token);
        var retry = client.HandleMessageAsync(Message("retry", "/重试"), shutdown.Token);
        try
        {
            await client.HandleMessageAsync(Message("command", command), shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(message.IsCompleted);
            Assert.False(retry.IsCompleted);
            Assert.Same(originalContext, codex.ActiveToolContext);
            Assert.Equal("running-message", originalContext.MessageId);
            Assert.Equal(1, originalContext.Sequence);
            Assert.Contains(handler.Replies, body => body.GetProperty("msg_id").GetString() == "command"
                && body.TryGetProperty("content", out var content) && content.GetString()!.Contains(expected, StringComparison.Ordinal));
        }
        finally
        {
            shutdown.Cancel();
            release.TrySetResult();
            await blocking;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => message);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement Message(string id, string text)
    {
        var json = new JsonObject
        {
            ["id"] = id, ["content"] = text,
            ["author"] = new JsonObject { ["user_openid"] = "alice" }
        };
        using var document = JsonDocument.Parse(json.ToJsonString());
        return document.RootElement.Clone();
    }

    private sealed class FakeQqHandler : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Replies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/getAppAccessToken", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test\",\"expires_in\":7200}") };
            if (path.EndsWith("/messages", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Replies.Enqueue(body.RootElement.Clone());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"file_info\":\"test-image\"}") };
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
