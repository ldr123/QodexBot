using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace QodexBot.Tests;

public sealed class QqCommandTests
{
    [Fact]
    public async Task HelpAndMissingArguments_DoNotCreateFollowupInputStates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("\\");
        Assert.Contains("可用指令", fixture.LastText);
        Assert.Contains("\\标题 新名称", fixture.LastText);
        Assert.DoesNotContain("回复数字", fixture.LastText);
        await fixture.SendAsync("\\切换");
        Assert.Contains("切换命令后加列表编号", fixture.LastText);
        await fixture.SendAsync("\\标题");
        Assert.Contains("用法：\\标题 新名称", fixture.LastText);
        Assert.False(fixture.Send("新的标题").IsCompleted);
        Assert.Equal("会话 A", fixture.Sessions.Threads[0].Name);
        await fixture.SendAsync("\\切换工作区");
        Assert.Contains("用法：\\切换工作区 绝对路径", fixture.LastText);
    }

    [Fact]
    public async Task ProjectHistory_IsTheOnlyListSource_AndSupportsManyPages()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Sessions.Threads.Clear();
        for (var index = 0; index < 62; index++)
            fixture.Sessions.Threads.Add(new($"external-{index}", $"项目会话 {index}", fixture.Codex.WorkDirectory, DateTimeOffset.UtcNow.AddMinutes(-index)));
        fixture.Sessions.Threads.Add(new("other", "其它项目", fixture.DirectoryPath, DateTimeOffset.UtcNow));
        await fixture.SendAsync("\\会话");
        Assert.Contains("1/7 页，共 62 项", fixture.LastText);
        Assert.DoesNotContain("其它项目", fixture.LastText);
        Assert.Contains(fixture.Codex.WorkDirectory, fixture.Sessions.Requests);
        await fixture.SendAsync("\\会话 2");
        Assert.Contains("2/7 页", fixture.LastText);
        Assert.Contains("11. 项目会话 10", fixture.LastText);
        await fixture.SendAsync("\\切换 11");
        Assert.Equal("external-10", (await fixture.SelectionAsync()).ThreadId);
        Assert.Contains("已切换到会话：项目会话 10", fixture.LastText);
        await fixture.SendAsync("\\切换 2");
        Assert.Equal("external-1", (await fixture.SelectionAsync()).ThreadId);
    }

    [Fact]
    public async Task ProjectChoice_UsesThreadId_WhenHistoryOrderChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var thread = new CodexThreadSummary("thread-b", "会话 B", fixture.Codex.WorkDirectory, DateTimeOffset.UtcNow.AddMinutes(1));
        fixture.Sessions.Threads.Add(thread);
        await fixture.SendAsync("\\会话");
        Assert.Contains("1. 会话 B", fixture.LastText);
        fixture.Sessions.Threads[0] = fixture.Sessions.Threads[0] with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        await fixture.SendAsync("\\切换 1");
        Assert.Equal("thread-b", (await fixture.SelectionAsync()).ThreadId);
    }

    [Fact]
    public async Task RenameAndNewSession_UpdateCodexHistoryDirectly()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("\\标题 " + new string('a', 65));
        Assert.Contains("不能超过 64 个字符", fixture.LastText);
        await fixture.SendAsync("\\标题 新的工作会话");
        Assert.Equal("新的工作会话", fixture.Sessions.Threads.Single(thread => thread.Id == "thread-a").Name);
        await fixture.SendAsync("\\新建 编译问题");
        var selected = await fixture.SelectionAsync();
        Assert.Equal("编译问题", fixture.Sessions.Threads.Single(thread => thread.Id == selected.ThreadId).Name);
        Assert.Equal(fixture.Codex.WorkDirectory, selected.WorkDirectory);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "sessions.json")));
        var json = await File.ReadAllTextAsync(Path.Combine(fixture.DirectoryPath, "conversation-state.json"));
        Assert.DoesNotContain("编译问题", json);
        Assert.DoesNotContain("新的工作会话", json);
    }

    [Fact]
    public async Task BareNumbersAndMenuWords_RemainOrdinaryMessages()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("\\会话");
        foreach (var text in new[] { "1", "0", "菜单", "/菜单", "取消", "帮我分析这段代码" })
            Assert.False(fixture.Send(text).IsCompleted);
    }

    [Fact]
    public async Task Commands_RespectGroupScopeAndDisabledBindings()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("\\会话", "alice", "group-a");
        Assert.False(fixture.Send("1", "bob", "group-a").IsCompleted);
        Assert.False(fixture.Send("1", "alice", "group-b").IsCompleted);
        Assert.False(fixture.Send("1", "alice").IsCompleted);
        await fixture.SendAsync("\\状态", "alice", "group-a");
        Assert.Contains("机器人：test", fixture.LastText);
        fixture.Bindings["screenshot"] = "";
        await fixture.SendAsync("\\");
        Assert.DoesNotContain("\\截图", fixture.LastText);
        fixture.Bindings["status"] = "";
        await fixture.SendAsync("\\状态");
        Assert.Contains("未识别的指令", fixture.LastText);
    }

    [Fact]
    public async Task NumericAnswersToCodex_DoNotBecomeCommands()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync("\\");
        var context = new ReplyContext("question", "alice");
        fixture.Codex.SetActiveToolContext(context);
        var question = fixture.Client.HandleMcpToolAsync(fixture.Codex, new McpToolRequest(McpToolKind.AskUser, "请选择 1 或 2"), fixture.Shutdown.Token);
        await fixture.Handler.QuestionSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.SendAsync("\\状态");
        Assert.Contains("机器人：test", fixture.LastText);
        await fixture.SendAsync("2");
        Assert.Equal("2", await question.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(context, fixture.Codex.ActiveToolContext);
        Assert.False(fixture.Send("1").IsCompleted);
    }

    [Fact]
    public async Task WorkspaceSwitch_ValidatesDirectory_LoadsProjectHistory_AndPersistsOnlySelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var directory = Path.Combine(fixture.DirectoryPath, new string('a', 70));
        Directory.CreateDirectory(directory);
        fixture.Sessions.Threads.Add(new("project", "已有项目会话", directory, DateTimeOffset.UtcNow));
        var context = new ReplyContext("running", "alice");
        fixture.Codex.SetActiveToolContext(context);
        await fixture.SendAsync("\\切换工作区 relative/path");
        Assert.Contains("绝对路径", fixture.LastText);
        await fixture.SendAsync("\\切换工作区 " + Path.Combine(fixture.DirectoryPath, "missing"));
        Assert.Contains("目录不存在", fixture.LastText);
        await fixture.SendAsync("\\切换工作区 " + '"' + directory + Path.DirectorySeparatorChar + '"');
        Assert.Contains("已有项目会话", fixture.LastText);
        Assert.Contains("共 1 项", fixture.LastText);
        Assert.DoesNotContain("会话 A", fixture.LastText);
        Assert.Null((await fixture.SelectionAsync()).ThreadId);
        await fixture.SendAsync("\\切换 1");
        var selection = await fixture.SelectionAsync();
        Assert.Equal("project", selection.ThreadId);
        Assert.Equal(directory, selection.WorkDirectory);
        Assert.Same(context, fixture.Codex.ActiveToolContext);
        await fixture.SendAsync("\\工作目录");
        Assert.Contains(directory, fixture.LastText);
        var reloaded = new BotConversationStateStore(Path.Combine(fixture.DirectoryPath, "conversation-state.json"));
        await reloaded.LoadAsync(default);
        Assert.Equal(selection, await reloaded.GetAsync("test:alice", fixture.Codex.WorkDirectory, default));
        Assert.Equal(fixture.Codex.WorkDirectory, (await reloaded.GetAsync("test:bob", fixture.Codex.WorkDirectory, default)).WorkDirectory);
    }

    [Fact]
    public async Task EmptyHistoryAndFailedHistory_AreDistinct()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Sessions.Threads.Clear();
        await fixture.SendAsync("\\会话");
        Assert.Contains("共 0 项", fixture.LastText);
        fixture.Sessions.FailListing = true;
        await fixture.SendAsync("\\会话");
        Assert.Contains("读取当前工作区的 Codex 会话失败", fixture.LastText);
    }

    [Theory]
    [InlineData("\\会话 0", "用法：\\会话 [页码]")]
    [InlineData("\\会话 abc", "用法：\\会话 [页码]")]
    [InlineData("\\会话 99", "项目会话只有 1 页")]
    [InlineData("\\模型列表 0", "用法：\\模型列表 [页码]")]
    public async Task InvalidListPage_ReturnsUsageWithoutWaitingForTasks(string command, string expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SendAsync(command);
        Assert.Contains(expected, fixture.LastText);
    }

    [Fact]
    public async Task CodexParameters_UseWorkspaceAndOpaqueCursor_WithoutProviderRestrictions()
    {
        await using var codex = new CodexAppServer("workspace-params-test");
        var directory = WorkspacePath.Normalize(Path.GetTempPath());
        Assert.Equal(directory, codex.BuildThreadStartParams(directory)["cwd"]!.GetValue<string>());
        var turn = CodexAppServer.BuildTurnStartParams("thread", "message", "model", directory);
        Assert.Equal(directory, turn["cwd"]!.GetValue<string>());
        Assert.Equal("message", turn["input"]![0]!["text"]!.GetValue<string>());
        var list = CodexAppServer.BuildThreadListParams(directory, "opaque-cursor");
        Assert.Equal(directory, list["cwd"]!.GetValue<string>());
        Assert.Equal("opaque-cursor", list["cursor"]!.GetValue<string>());
        Assert.Empty(list["modelProviders"]!.AsArray());
        Assert.Contains("appServer", list["sourceKinds"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Contains("vscode", list["sourceKinds"]!.AsArray().Select(value => value!.GetValue<string>()));
        var resume = codex.BuildThreadResumeParams("existing", directory);
        Assert.Equal("existing", resume["threadId"]!.GetValue<string>());
        Assert.Equal(directory, resume["cwd"]!.GetValue<string>());
        Assert.Equal("never", resume["approvalPolicy"]!.GetValue<string>());
        Assert.Contains("qq_ask_user", resume["developerInstructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task LateThreadCreation_DoesNotOverrideANewerWorkspaceSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.State.SetWorkspaceAsync("test:alice", fixture.DirectoryPath, default);
        var beforeCreation = await fixture.SelectionAsync();
        var other = Path.Combine(fixture.DirectoryPath, "other");
        await fixture.State.SetWorkspaceAsync("test:alice", other, default);
        await fixture.State.SelectIfUnchangedAsync("test:alice", beforeCreation, "late-thread", default);
        var current = await fixture.SelectionAsync();
        Assert.Equal(other, current.WorkDirectory);
        Assert.Null(current.ThreadId);
    }

    [Fact]
    public void ThreadParser_UsesActualTitles_AndFiltersUnrelatedOrEphemeralThreads()
    {
        var directory = WorkspacePath.Normalize(Path.GetTempPath());
        var payload = new JsonObject
        {
            ["data"] = new JsonArray(
                new JsonObject { ["id"] = "named", ["name"] = "实际标题", ["preview"] = "首条消息", ["cwd"] = directory, ["updatedAt"] = 100 },
                new JsonObject { ["id"] = "preview", ["preview"] = "历史预览", ["cwd"] = directory, ["updatedAt"] = 99 },
                new JsonObject { ["id"] = "other", ["cwd"] = Path.Combine(directory, "different") },
                new JsonObject { ["id"] = "ephemeral", ["cwd"] = directory, ["ephemeral"] = true }),
            ["nextCursor"] = "next"
        };
        using var document = JsonDocument.Parse(payload.ToJsonString());
        var result = CodexAppServer.ParseThreadList(document.RootElement, directory);
        Assert.Equal(2, result.Threads.Count);
        Assert.Equal("实际标题", result.Threads[0].Name);
        Assert.Equal("历史预览", result.Threads[1].Name);
        Assert.Equal("next", result.NextCursor);
        Assert.True(WorkspacePath.Matches(directory, "\\\\?\\" + directory));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "qodex-menu-test-" + Guid.NewGuid().ToString("N"));
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _tasks = [];
        private readonly HttpClient _http;
        private readonly LocalToolIpcServer _mcp = new();
        private int _messageId;
        public CancellationTokenSource Shutdown { get; } = new();
        public FakeQqHandler Handler { get; } = new();
        public CodexAppServer Codex { get; } = new("menu-test");
        public BotConversationStateStore State { get; }
        public FakeCodexSessionClient Sessions { get; } = new();
        public QqBotClient Client { get; }
        public Dictionary<string, string> Bindings { get; } = BotSettingsStore.DefaultCommandBindings.ToDictionary();
        public string LastText => Handler.Replies.Last().GetProperty("content").GetString()!;
        private Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            _http = new HttpClient(Handler);
            State = new BotConversationStateStore(Path.Combine(DirectoryPath, "conversation-state.json"));
            var queue = new SerialTaskQueue();
            _tasks.Add(queue.RunAsync(() => _release.Task, default));
            Client = new QqBotClient(_http, new BotConfig("test", "test", "id", "secret", true, 0, true, true),
                Codex, _mcp, new ConversationStore(Path.Combine(DirectoryPath, "history.jsonl")), State,
                () => Bindings, () => "test", _ => { }, queue, Sessions);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var thread = new CodexThreadSummary("thread-a", "会话 A", fixture.Codex.WorkDirectory, DateTimeOffset.UtcNow);
            fixture.Sessions.Threads.Add(thread);
            await fixture.State.SelectThreadAsync("test:alice", thread, default);
            return fixture;
        }
        public Task<BotConversationState> SelectionAsync() => State.GetAsync("test:alice", Codex.WorkDirectory, default);
        public Task Send(string text, string user = "alice", string? group = null)
        {
            var json = new JsonObject { ["id"] = (++_messageId).ToString(), ["content"] = text,
                ["author"] = new JsonObject { [group is null ? "user_openid" : "member_openid"] = user } };
            if (group is not null) json["group_openid"] = group;
            using var document = JsonDocument.Parse(json.ToJsonString());
            var task = Client.HandleMessageAsync(document.RootElement.Clone(), Shutdown.Token, group is not null);
            _tasks.Add(task);
            return task;
        }
        public Task SendAsync(string text, string user = "alice", string? group = null) => Send(text, user, group).WaitAsync(TimeSpan.FromSeconds(5));
        public async ValueTask DisposeAsync()
        {
            Shutdown.Cancel();
            _release.TrySetResult();
            try { await Task.WhenAll(_tasks); }
            catch (OperationCanceledException) { }
            await Codex.DisposeAsync();
            await _mcp.DisposeAsync();
            _http.Dispose();
            Shutdown.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
    private sealed class FakeQqHandler : HttpMessageHandler
    {
        public ConcurrentQueue<JsonElement> Replies { get; } = new();
        public TaskCompletionSource QuestionSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/getAppAccessToken", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"test\",\"expires_in\":7200}") };
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement.Clone();
            Replies.Enqueue(body);
            if (body.TryGetProperty("markdown", out var markdown) && markdown.GetProperty("content").GetString()!.Contains("Codex 需要你的补充", StringComparison.Ordinal))
                QuestionSent.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
