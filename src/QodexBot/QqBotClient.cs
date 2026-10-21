using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed partial class QqBotClient(HttpClient http, BotConfig config, CodexAppServer codex,
    LocalToolIpcServer mcp, ConversationStore conversationStore, BotConversationStateStore stateStore,
    Func<IReadOnlyDictionary<string, string>> getCommandBindings, Func<string> getRuntimeStatus,
    Action<string> statusChanged, SerialTaskQueue? codexRequests = null,
    ICodexSessionClient? projectSessions = null)
{
    private static readonly Uri TokenUri = new("https://bots.qq.com/app/getAppAccessToken");
    private static readonly Uri ProductionApi = new("https://api.sgroup.qq.com/");
    private static readonly Uri SandboxApi = new("https://sandbox.api.sgroup.qq.com/");
    private readonly Uri _apiBase = config.Sandbox ? SandboxApi : ProductionApi;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SerialTaskQueue _codexRequests = codexRequests ?? new();
    private readonly SemaphoreSlim _codexControlLock = new(1, 1);
    private readonly ICodexSessionClient _projectSessions = projectSessions ?? codex;
    private readonly ConcurrentDictionary<long, Task> _messageTasks = new();
    private long _nextMessageTaskId;
    private readonly ConcurrentDictionary<string, PendingQuestion> _questions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRequest> _activeRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _handledMessageIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _handledMessageOrder = new();
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        mcp.SetHandler((request, token) => HandleMcpToolAsync(codex, request, token));
        codex.SetMcpPipeName(mcp.PipeName);
        try { await RunGatewayAsync(cancellationToken); }
        finally { await Task.WhenAll(_messageTasks.Values); }
    }

    private async Task RunGatewayAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            statusChanged("正在连接 QQ 网关");
            try
            {
                await ConnectAndListenAsync(cancellationToken);
                retryDelay = TimeSpan.FromSeconds(2);
                statusChanged("QQ 网关已断开，准备重连");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"连接中断：{ex.Message}");
                statusChanged($"连接失败：{ex.Message}");
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"{retryDelay.TotalSeconds:0} 秒后重连…");
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var gatewayRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, "gateway"));
        gatewayRequest.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        using var gatewayResponse = await http.SendAsync(gatewayRequest, cancellationToken);
        var gatewayJson = await gatewayResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!gatewayResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"获取 WebSocket 网关地址失败 ({(int)gatewayResponse.StatusCode})：{gatewayJson}");
        using var gatewayDoc = JsonDocument.Parse(gatewayJson);
        var gatewayUrl = gatewayDoc.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("网关响应中没有 url 字段。");

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(gatewayUrl), cancellationToken);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectionToken = connectionCts.Token;
        Console.WriteLine("WebSocket 已连接，等待 QQ 网关握手…");

        long? sequence = null;
        var heartbeatInterval = TimeSpan.FromSeconds(30);
        using var sendLock = new SemaphoreSlim(1, 1);
        var buffer = new byte[32 * 1024];
        using var fragment = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            fragment.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, connectionToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"网关关闭连接：{result.CloseStatus} {result.CloseStatusDescription}");
                    return;
                }
                fragment.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var doc = JsonDocument.Parse(fragment.ToArray());
            var root = doc.RootElement;
            var op = root.GetProperty("op").GetInt32();
            var data = root.TryGetProperty("d", out var d) ? d : default;
            if (root.TryGetProperty("s", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number)
                sequence = seqElement.GetInt64();

            switch (op)
            {
                case 10:
                    if (data.TryGetProperty("heartbeat_interval", out var interval))
                        heartbeatInterval = TimeSpan.FromMilliseconds(interval.GetInt32());
                    await SendGatewayAsync(socket, sendLock,
                        new QqGatewayIdentifyPayload(2,
                            new QqGatewayIdentifyData($"QQBot {token}", config.Intents, [0, 1])),
                        AppJsonContext.Default.QqGatewayIdentifyPayload, connectionToken);
                    _ = SendHeartbeatsAsync(socket, sendLock, heartbeatInterval, () => sequence, connectionToken);
                    Console.WriteLine("已发送鉴权信息，等待 READY 事件…");
                    break;

                case 0:
                    var eventName = root.TryGetProperty("t", out var eventElement) ? eventElement.GetString() : null;
                    if (eventName is not null && eventName != "READY") Console.WriteLine($"收到事件：{eventName}");
                    if (eventName == "READY")
                    {
                        var name = data.TryGetProperty("user", out var user) && user.TryGetProperty("username", out var username)
                            ? username.GetString() : null;
                        Console.WriteLine($"机器人已就绪：{name ?? "(unknown)"}");
                        statusChanged($"机器人已就绪：{name ?? "(unknown)"}");
                        TrackBackgroundTask(StartCodexAndLogAsync(cancellationToken));
                    }
                    else if (eventName == "C2C_MESSAGE_CREATE")
                    {
                        if (config.SubscribeC2cMessages)
                        {
                            var message = data.Clone();
                            DispatchMessage(message, cancellationToken);
                        }
                    }
                    else if (eventName is "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE")
                    {
                        if (!config.SubscribeGroupMessages)
                        {
                            Console.WriteLine("群聊消息接收已在管理页关闭，忽略该事件。");
                            break;
                        }
                        var message = data.Clone();
                        if (TryMarkGroupMessageHandled(message))
                            DispatchMessage(message, cancellationToken, isGroup: true);
                    }
                    else if (!string.IsNullOrEmpty(eventName) && eventName != "RESUMED")
                    {
                        Console.WriteLine($"当前程序未处理此事件：{eventName}");
                    }
                    break;

                case 7:
                case 9:
                    Console.WriteLine("QQ 网关要求重新连接。");
                    return;
            }
        }
    }

    private void DispatchMessage(JsonElement message, CancellationToken cancellationToken, bool isGroup = false)
        => TrackBackgroundTask(HandleMessageAndLogAsync(message, cancellationToken, isGroup));

    private async Task StartCodexAndLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _codexControlLock.WaitAsync(cancellationToken);
            try { await codex.StartAsync(cancellationToken); }
            finally { _codexControlLock.Release(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"启动常驻 Codex CLI 失败：{ex.Message}"); }
    }

    private void TrackBackgroundTask(Task task)
    {
        var id = Interlocked.Increment(ref _nextMessageTaskId);
        _messageTasks[id] = task;
        _ = RemoveMessageTaskAsync(id, task);
    }

    private async Task RemoveMessageTaskAsync(long id, Task task)
    {
        try { await task; }
        finally { _messageTasks.TryRemove(id, out _); }
    }

    private async Task HandleMessageAndLogAsync(JsonElement message, CancellationToken cancellationToken, bool isGroup = false)
    {
        try { await HandleMessageAsync(message, cancellationToken, isGroup); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"处理 QQ 消息失败：{ex.Message}"); }
    }

    private async Task SendHeartbeatsAsync(ClientWebSocket socket, SemaphoreSlim sendLock, TimeSpan interval,
        Func<long?> getSequence, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(interval * 0.8);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await SendGatewayAsync(socket, sendLock, new QqGatewayHeartbeatPayload(1, getSequence()),
                    AppJsonContext.Default.QqGatewayHeartbeatPayload, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task SendGatewayAsync<TPayload>(ClientWebSocket socket, SemaphoreSlim sendLock,
        TPayload payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TPayload> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonTypeInfo);
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { sendLock.Release(); }
    }

    internal async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken, bool isGroup = false)
    {
        var messageId = GetString(message, "id");
        var content = GetString(message, "content") ?? string.Empty;
        var author = message.TryGetProperty("author", out var authorElement) ? authorElement : default;
        var groupOpenId = isGroup ? GetString(message, "group_openid") : null;
        var memberOpenId = isGroup ? GetString(author, "member_openid") : null;
        var userOpenId = isGroup ? $"group:{groupOpenId}:{memberOpenId}" : GetString(author, "user_openid");
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(userOpenId) ||
            (isGroup && (string.IsNullOrWhiteSpace(groupOpenId) || string.IsNullOrWhiteSpace(memberOpenId))))
        {
            Console.WriteLine(isGroup ? "群聊消息缺少消息 ID、群 OpenID 或成员 OpenID，跳过处理。" : "C2C 消息缺少消息 ID 或用户 OpenID，跳过回复。");
            return;
        }
        if (author.ValueKind == JsonValueKind.Object && author.TryGetProperty("bot", out var botElement) &&
            botElement.ValueKind == JsonValueKind.True)
            return;

        if (isGroup)
            content = Regex.Replace(content, "<@!?[^>]+>", string.Empty).Trim();
        var context = new ReplyContext(messageId, userOpenId, groupOpenId);

        if (TryMatchCommand(content, getCommandBindings(), out var command, out var argument))
        {
            await HandleCommandAsync(command, argument, context, cancellationToken);
            return;
        }

        if (_questions.TryGetValue(userOpenId, out var pendingQuestion))
        {
            var answer = string.IsNullOrWhiteSpace(content) ? "（用户发送了空回复）" : content;
            if (pendingQuestion.TryAnswer(messageId, answer))
            {
                await this.RecordConversationAsync(userOpenId, "answer", answer, cancellationToken: cancellationToken);
                WriteLogBlock("QQ 提问回答", answer);
                Console.WriteLine("已收到 QQ 用户对 Codex 提问的回答。");
                return;
            }
        }

        if (content.TrimStart().StartsWith('/'))
        {
            var help = getCommandBindings().GetValueOrDefault("help");
            await SendTextReplyAsync(context, "未识别的指令。" + (string.IsNullOrWhiteSpace(help) ? "" : $"发送 {help} 查看可用指令。"), cancellationToken);
            return;
        }

        await _codexRequests.RunAsync(() => ProcessCodexRequestAsync(codex, context, content, cancellationToken), cancellationToken);
    }

    private async Task HandleCommandAsync(string command, string argument, ReplyContext context, CancellationToken cancellationToken)
    {
        if (command == "sessions")
        {
            await ShowProjectSessionsAsync(argument, context, cancellationToken);
            return;
        }
        if (command == "models")
        {
            await ShowModelsAsync(argument, context, cancellationToken);
            return;
        }
        if (command == "quota")
        {
            try { await ShowQuotaAsync(context, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"读取 Codex 额度失败：{ex.Message}");
                await SendTextReplyAsync(context, $"读取 Codex 额度失败：{Truncate(ex.Message, 300)}。请确认 Codex CLI 已登录并稍后重试。", cancellationToken);
            }
            return;
        }
        if (command == "switch-workspace")
        {
            await SwitchWorkspaceAsync(argument, context, cancellationToken);
            return;
        }
        if (command == "stop")
        {
            var stopped = false;
            if (_activeRequests.TryGetValue(context.UserOpenId, out var active))
            {
                try { active.Cancellation.Cancel(); stopped = true; } catch (ObjectDisposedException) { }
            }
            await SendTextReplyAsync(context, stopped ? "已请求停止当前任务。" : "当前没有正在运行的任务。", cancellationToken);
            return;
        }
        if (command == "update-codex")
        {
            await SendTextReplyAsync(context, "正在等待当前任务完成并更新 Codex CLI，完成后会自动重启 app-server。", cancellationToken);
            await _codexRequests.RunAsync(async () =>
            {
                await _codexControlLock.WaitAsync(cancellationToken);
                try
                {
                    string response;
                    try { response = await codex.UpdateCliAsync(cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { response = $"更新 Codex CLI 失败：{Truncate(ex.Message, 700)}"; }
                    await SendTextReplyAsync(context, response, cancellationToken);
                    await RecordConversationAsync(context.UserOpenId, "command", command, cancellationToken: cancellationToken);
                }
                finally { _codexControlLock.Release(); }
            }, cancellationToken);
            return;
        }
        if (command == "retry")
            await _codexRequests.RunAsync(() => ExecuteCommandAsync(codex, command, argument, context, cancellationToken), cancellationToken);
        else if (command is "new-session" or "switch-model")
        {
            await _codexControlLock.WaitAsync(cancellationToken);
            try { await ExecuteCommandAsync(codex, command, argument, context, cancellationToken); }
            finally { _codexControlLock.Release(); }
        }
        else
            await ExecuteCommandAsync(codex, command, argument, context, cancellationToken);
    }

    private bool TryMarkGroupMessageHandled(JsonElement message)
    {
        var messageId = GetString(message, "id");
        var groupOpenId = GetString(message, "group_openid");
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(groupOpenId)) return false;
        var key = $"{groupOpenId}:{messageId}";
        if (!_handledMessageIds.TryAdd(key, 0))
        {
            Console.WriteLine("跳过重复的群聊消息事件。");
            return false;
        }
        _handledMessageOrder.Enqueue(key);
        while (_handledMessageIds.Count > 4096 && _handledMessageOrder.TryDequeue(out var oldest))
            _handledMessageIds.TryRemove(oldest, out _);
        return true;
    }

    private async Task ExecuteCommandAsync(CodexAppServer codex, string command, string argument, ReplyContext context,
        CancellationToken cancellationToken)
    {
        var bindings = getCommandBindings();
        var userId = SessionOwner(context.UserOpenId);
        string response;
        switch (command)
        {
            case "help":
                response = "可用指令（参数直接跟在命令后）：\n" + string.Join("\n", bindings
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && pair.Key != "help")
                    .Select(pair => $"{pair.Value}{CommandArguments.GetValueOrDefault(pair.Key, "")} — {CommandLabels.GetValueOrDefault(pair.Key, pair.Key)}"));
                break;
            case "status":
                response = $"机器人：{getRuntimeStatus()}\nCodex：{(codex.IsStarted ? "已连接" : "未启动")}";
                break;
            case "switch-session":
            {
                if (!int.TryParse(argument, out var index)) { response = "请在切换命令后加列表编号，例如：" + bindings.GetValueOrDefault(command, "/切换") + " 2"; break; }
                var session = await GetSessionChoiceAsync(context, index, cancellationToken);
                if (session is null) { response = "没有这个编号。先查看会话列表，再输入切换命令和编号。"; break; }
                await stateStore.SelectThreadAsync(userId, session, cancellationToken);
                response = $"已切换到会话：{session.Name}";
                break;
            }
            case "new-session":
            {
                var previousSelection = await stateStore.GetAsync(userId, codex.WorkDirectory, cancellationToken);
                var workDirectory = previousSelection.WorkDirectory;
                var threadId = await _projectSessions.CreateThreadAsync(workDirectory, cancellationToken);
                var name = string.IsNullOrWhiteSpace(argument) ? $"新会话 {DateTimeOffset.Now:MM-dd HH:mm}" : argument.Trim();
                await _projectSessions.SetThreadNameAsync(threadId, name, cancellationToken);
                await stateStore.SelectThreadAsync(userId, new(threadId, name, workDirectory, DateTimeOffset.UtcNow), cancellationToken);
                response = $"已创建并切换到会话：{name}";
                break;
            }
            case "current":
            {
                var session = await ReadCurrentThreadAsync(context, cancellationToken);
                response = session is null ? $"尚未选择项目会话。发送 {bindings.GetValueOrDefault("sessions", "/会话")} 查看，或发送 {bindings.GetValueOrDefault("new-session", "/新建")} 新建会话。" : $"当前会话：{session.Name}";
                break;
            }
            case "rename":
                if (string.IsNullOrWhiteSpace(argument)) { response = $"用法：{bindings.GetValueOrDefault("rename", "/标题")} 新名称"; break; }
                if (argument.Trim().Length > 64) { response = "会话名称不能超过 64 个字符。"; break; }
                var active = await ReadCurrentThreadAsync(context, cancellationToken);
                if (active is null) { response = "当前没有会话。"; break; }
                await _codexControlLock.WaitAsync(cancellationToken);
                try { await _projectSessions.SetThreadNameAsync(active.Id, argument.Trim(), cancellationToken); }
                finally { _codexControlLock.Release(); }
                response = $"会话已命名为：{argument.Trim()}";
                break;
            case "workdir":
                var current = await stateStore.GetAsync(userId, codex.WorkDirectory, cancellationToken);
                response = $"当前工作目录：{current.WorkDirectory}";
                break;
            case "switch-model":
            {
                var session = await stateStore.GetAsync(userId, codex.WorkDirectory, cancellationToken);
                if (string.IsNullOrWhiteSpace(argument))
                {
                    response = $"当前模型：{session.Model ?? CodexAppServer.DefaultModel}。查看模型请发送 {bindings.GetValueOrDefault("models", "/模型列表")}；切换时可输入模型名称或列表编号。";
                    break;
                }
                var models = await codex.ListModelsAsync(cancellationToken);
                var requested = argument.Trim();
                CodexModel? selected = int.TryParse(requested, out var modelIndex) && modelIndex > 0 && modelIndex <= models.Count
                    ? models[modelIndex - 1]
                    : models.FirstOrDefault(model => string.Equals(model.Model, requested, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(model.DisplayName, requested, StringComparison.OrdinalIgnoreCase));
                if (selected is null)
                {
                    response = $"找不到模型“{requested}”。先发送 {bindings.GetValueOrDefault("models", "/模型列表")} 查看可用模型。";
                    break;
                }
                await stateStore.SetModelAsync(userId, codex.WorkDirectory, selected.Model, cancellationToken);
                response = $"当前会话已切换到模型：{selected.DisplayName}（{selected.Model}）";
                break;
            }
            case "recent":
            {
                var page = conversationStore.GetPage(null, 12, context.UserOpenId, exactUserMatch: true, botId: config.BotId);
                var recent = page.Items.Where(item => item.Kind is "user" or "assistant").Take(8).ToArray();
                response = recent.Length == 0 ? "暂无对话记录。" : string.Join("\n", recent.Select(item =>
                    $"{(item.Kind == "user" ? "你" : "Codex")}: {Truncate(item.Content, 100)}"));
                break;
            }
            case "screenshot":
                await HandleMcpToolAsync(codex, new McpToolRequest(McpToolKind.CaptureScreen, null), cancellationToken, context);
                response = "截图已发送。";
                break;
            case "retry":
                if (!_lastRequests.TryGetValue(userId, out var previous))
                    previous = conversationStore.GetLatestContent(context.UserOpenId, "user", config.BotId) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(previous)) { response = "没有可重试的请求。"; break; }
                await ProcessCodexRequestAsync(codex, context, previous, cancellationToken);
                await this.RecordConversationAsync(context.UserOpenId, "command", "retry", cancellationToken: cancellationToken);
                return;
            default:
                response = "该动作尚未实现。";
                break;
        }
        await SendTextReplyAsync(context, response, cancellationToken);
        await this.RecordConversationAsync(context.UserOpenId, "command", $"{command}: {argument}".TrimEnd(), cancellationToken: cancellationToken);
    }

    private async Task ProcessCodexRequestAsync(CodexAppServer codex, ReplyContext context, string content, CancellationToken cancellationToken)
    {
        await codex.StartAsync(cancellationToken);
        var userId = SessionOwner(context.UserOpenId);
        WriteLogBlock($"QQ 输入 ({userId})", content);
        await this.RecordConversationAsync(context.UserOpenId, "user", content, cancellationToken: cancellationToken);
        var session = await stateStore.GetAsync(userId, codex.WorkDirectory, cancellationToken);
        var workDirectory = session.WorkDirectory;
        if (!Directory.Exists(workDirectory))
        {
            await SendTextReplyAsync(context, $"工作区目录已不存在：{workDirectory}\n请使用 {getCommandBindings().GetValueOrDefault("switch-workspace", "/切换工作区")} 绝对路径 切换工作区。", cancellationToken);
            return;
        }
        if (session.ThreadId is null)
        {
            var threadId = await _projectSessions.CreateThreadAsync(workDirectory, cancellationToken);
            await stateStore.SelectIfUnchangedAsync(userId, session, threadId, cancellationToken);
            session = session with { ThreadId = threadId };
        }
        try { await codex.ResumeThreadAsync(session.ThreadId, cancellationToken, workDirectory); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase))
        {
            await SendTextReplyAsync(context, $"选中的 Codex 会话已无法恢复，请发送 {getCommandBindings().GetValueOrDefault("sessions", "/会话")} 重新查看并选择，或新建会话。", cancellationToken);
            return;
        }
        codex.SetActiveToolContext(context);
        _lastRequests[userId] = content;
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeRequests[context.UserOpenId] = new ActiveRequest(requestCts);
        try
        {
            var turnReply = await codex.AskAsync(session.ThreadId, content, session.Model, requestCts.Token, workDirectory);
            var reply = turnReply.Text;
            Console.WriteLine($"Codex 已返回回复（{reply.Length} 字符，{turnReply.Images.Count} 张图片）。");
            WriteLogBlock("Codex 推理摘要", string.IsNullOrWhiteSpace(turnReply.ReasoningSummary)
                ? "（app-server 此轮未提供推理摘要）" : turnReply.ReasoningSummary);
            WriteLogBlock("Codex 完整输出", reply);
            if (!string.IsNullOrWhiteSpace(turnReply.ReasoningSummary))
                await this.RecordConversationAsync(context.UserOpenId, "reasoning", turnReply.ReasoningSummary, cancellationToken: cancellationToken);
            await this.RecordConversationAsync(context.UserOpenId, "assistant", reply, turnReply.Images.Count, cancellationToken);
            await SendTurnReplyAsync(context, reply, turnReply.Images, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SendTextReplyAsync(context, "任务已停止。", cancellationToken);
        }
        finally
        {
            requestCts.Cancel();
            codex.SetActiveToolContext(null);
            _activeRequests.TryRemove(context.UserOpenId, out _);
        }
    }

    private static bool TryMatchCommand(string content, IReadOnlyDictionary<string, string> bindings,
        out string action, out string argument)
    {
        var trimmed = content.Trim();
        foreach (var pair in bindings.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                     .OrderByDescending(pair => pair.Value.Length))
        {
            var trigger = pair.Value.Trim();
            if (string.Equals(trimmed, trigger, StringComparison.OrdinalIgnoreCase))
            { action = pair.Key; argument = string.Empty; return true; }
            if ((pair.Key is "sessions" or "models" or "switch-session" or "rename" or "new-session" or "switch-model" or "switch-workspace") &&
                trimmed.StartsWith(trigger + " ", StringComparison.OrdinalIgnoreCase))
            { action = pair.Key; argument = trimmed[(trigger.Length + 1)..].Trim(); return true; }
        }
        action = string.Empty; argument = string.Empty; return false;
    }

    private string SessionOwner(string userOpenId) => $"{config.BotId}:{userOpenId}";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static void WriteLogBlock(string title, string content)
    {
        Console.WriteLine($"===== {title} =====");
        Console.WriteLine(content);
        Console.WriteLine($"===== {title} 结束 =====");
    }

    private async Task SendTurnReplyAsync(ReplyContext context, string reply, IReadOnlyList<CodexImage> images,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var endpoint = GetMessagesEndpoint(context);

        var uploadedImages = new List<JsonElement>();
        if (images.Count > 4)
            reply = AppendReplyNote(reply, "（单条 QQ 回复最多发送 4 张图片，其余图片未发送。）");
        var availableImageSlots = Math.Max(0, 5 - context.Sequence);
        foreach (var image in images.Take(Math.Min(4, availableImageSlots)))
        {
            if (context.SentImageHashes.ContainsKey(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes))))
                continue;
            JsonElement fileInfo;
            try
            {
                fileInfo = await UploadImageAsync(context, image, token, cancellationToken);
            }
            catch (Exception directUploadError) when (directUploadError is not OperationCanceledException)
            {
                Console.Error.WriteLine($"QQ 直接图片上传失败：{directUploadError.Message}");
                reply = AppendReplyNote(reply, "（图片已生成，但 QQ 图片发送失败。）");
                continue;
            }
            uploadedImages.Add(fileInfo);
        }

        var parts = SplitReply(reply, maxParts: Math.Max(0, 6 - context.Sequence - uploadedImages.Count), maxCharsPerPart: 1000);
        foreach (var part in parts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            request.Content = new StringContent(JsonSerializer.Serialize(
                new QqMessagePayload(null, 2, context.MessageId, context.NextSequence(), Markdown: new QqMarkdownPayload(part)),
                AppJsonContext.Default.QqMessagePayload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"回复失败 ({(int)response.StatusCode})：{responseText}");
                return;
            }
        }

        foreach (var fileInfo in uploadedImages)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
            request.Content = new StringContent(JsonSerializer.Serialize(
                new QqMessagePayload(" ", 7, context.MessageId, context.NextSequence(), new QqMediaPayload(fileInfo)),
                AppJsonContext.Default.QqMessagePayload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"QQ 图片消息发送失败 ({(int)response.StatusCode})：{responseText}");
                return;
            }
        }
        Console.WriteLine($"已将 Codex 的 Markdown 回复发送回 QQ（{parts.Count} 条消息，{uploadedImages.Count} 张图片）。");
    }

    private sealed record ActiveRequest(CancellationTokenSource Cancellation);

    internal async Task<string> HandleMcpToolAsync(CodexAppServer codex, McpToolRequest request, CancellationToken cancellationToken,
        ReplyContext? explicitContext = null)
    {
        var context = explicitContext ?? codex.ActiveToolContext ?? throw new InvalidOperationException("当前没有活动的 QQ 对话。");
        using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            explicitContext is null && _activeRequests.TryGetValue(context.UserOpenId, out var active)
                ? active.Cancellation.Token : cancellationToken);
        cancellationToken = toolCts.Token;
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Kind == McpToolKind.CaptureScreen)
        {
            if (context.Sequence >= 5)
                throw new InvalidOperationException("当前 QQ 回复次数已接近上限，不能再发送截图。");
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                throw new PlatformNotSupportedException("桌面截图需要 Windows 7 或更高版本。");

            var processName = request.Argument?.Trim();
            byte[] bytes;
            string imageDescription;
            if (string.IsNullOrWhiteSpace(processName))
            {
                bytes = WindowsScreenCapture.CapturePng();
                imageDescription = "当前桌面";
            }
            else
            {
                var capture = WindowsScreenCapture.CaptureProcessWindowPng(processName);
                bytes = capture.Bytes;
                imageDescription = $"进程 {processName} 的窗口“{capture.WindowTitle}”";
            }
            if (bytes.Length > 10 * 1024 * 1024)
                throw new InvalidOperationException($"{imageDescription}截图为 {bytes.Length / 1024 / 1024} MiB，超过 QQ 10 MiB 的图片大小限制。");
            var image = new CodexImage(bytes, "image/png", ".png");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes));
            if (!context.SentImageHashes.TryAdd(hash, 0)) return "当前截图与本轮已发送图片相同，已忽略重复请求。";
            try
            {
                await SendImageReplyAsync(context, image, cancellationToken);
                await this.RecordConversationAsync(context.UserOpenId, "image", $"通过 MCP 工具捕获并发送了{imageDescription}", 1, cancellationToken);
                Console.WriteLine($"已直接捕获并发送{imageDescription}截图（{bytes.Length:N0} 字节）。");
                return $"已将{imageDescription}截图直接发送到 QQ。";
            }
            catch
            {
                context.SentImageHashes.TryRemove(hash, out _);
                throw;
            }
        }
        if (request.Kind == McpToolKind.SendImage)
        {
            var path = request.Argument;
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 参数不能为空。");
            var outbox = Path.GetFullPath(Path.Combine(codex.WorkDirectory, "qq-outbox"));
            Directory.CreateDirectory(outbox);
            if ((File.GetAttributes(outbox) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("图片输出目录不能是符号链接或重解析点。");
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), outbox, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"图片必须直接放在 {outbox} 目录中。");
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不允许发送符号链接或重解析点文件。");
            var file = new FileInfo(fullPath);
            if (!file.Exists || file.Length is 0 or > 10 * 1024 * 1024)
                throw new InvalidOperationException("图片文件不存在、为空或超过 10 MiB。");
            var image = CodexAppServer.GetSupportedImage(await File.ReadAllBytesAsync(fullPath, cancellationToken))
                ?? throw new InvalidOperationException("仅支持 PNG 和 JPEG 图片。");
            if (context.Sequence >= 5) throw new InvalidOperationException("当前 QQ 回复次数已接近上限，需保留一次最终回复，不能再发送图片。");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bytes));
            if (!context.SentImageHashes.TryAdd(hash, 0)) return "图片已发送过，已忽略重复请求。";
            try
            {
                await SendImageReplyAsync(context, image, cancellationToken);
                await this.RecordConversationAsync(context.UserOpenId, "image", "通过 MCP 工具发送了图片", 1, cancellationToken);
                return "图片已发送到当前 QQ 对话。";
            }
            catch
            {
                context.SentImageHashes.TryRemove(hash, out _);
                throw;
            }
        }
        if (request.Kind == McpToolKind.AskUser)
        {
            var question = request.Argument?.Trim();
            if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("question 参数不能为空。");
            if (context.Sequence >= 5) throw new InvalidOperationException("QQ 回复次数即将用尽，需保留一次最终回复，不能再发起提问。");
            var pending = new PendingQuestion(context);
            if (!_questions.TryAdd(context.UserOpenId, pending))
                throw new InvalidOperationException("该用户已有一个待回答的问题。");
            try
            {
                await SendTextReplyAsync(context, "Codex 需要你的补充：\n" + question, cancellationToken, markdown: true);
                await this.RecordConversationAsync(context.UserOpenId, "question", question, cancellationToken: cancellationToken);
                return await pending.Completion.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
            }
            catch (TimeoutException) { throw new TimeoutException("等待 QQ 用户回答超时（5 分钟）。"); }
            finally
            {
                pending.Close();
                _questions.TryRemove(context.UserOpenId, out _);
            }
        }
        if (request.Kind == McpToolKind.Input)
        {
            var input = request.Input ?? throw new ArgumentException("键鼠输入参数不能为空。");
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                throw new PlatformNotSupportedException("模拟键盘和鼠标需要 Windows 7 或更高版本。");
            var result = WindowsInputAutomation.Execute(input);
            var summary = input.Action switch
            {
                "type_text" => $"模拟键盘输入（{input.Text?.Length ?? 0} 个字符）",
                "press_key" => $"模拟按键 {input.Key}",
                "click" => $"模拟鼠标 {input.Button ?? "left"} 键单击 ({input.X},{input.Y})",
                "move_mouse" => $"模拟鼠标移动到 ({input.X},{input.Y})",
                "scroll" => $"模拟滚轮 {input.ScrollDelta}",
                _ => "模拟键鼠输入"
            };
            await this.RecordConversationAsync(context.UserOpenId, "input", summary, cancellationToken: cancellationToken);
            Console.WriteLine($"已执行桌面输入：{summary}。");
            return result;
        }
        throw new InvalidOperationException($"未知工具编号：{request.Kind}");
    }

    private async Task SendImageReplyAsync(ReplyContext context, CodexImage image, CancellationToken cancellationToken)
    {
        if (context.Sequence > 5) throw new InvalidOperationException("此 QQ 消息的回复次数已用完。");
        var token = await GetAccessTokenAsync(cancellationToken);
        var fileInfo = await UploadImageAsync(context, image, token, cancellationToken);
        var endpoint = GetMessagesEndpoint(context);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Content = new StringContent(JsonSerializer.Serialize(
            new QqMessagePayload(" ", 7, context.MessageId, context.NextSequence(), new QqMediaPayload(fileInfo)),
            AppJsonContext.Default.QqMessagePayload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"QQ 图片消息发送失败 ({(int)response.StatusCode})：{body}");
    }

    private async Task SendTextReplyAsync(ReplyContext context, string text, CancellationToken cancellationToken,
        bool markdown = false)
    {
        if (context.Sequence > 5) throw new InvalidOperationException("此 QQ 消息的回复次数已用完。");
        var token = await GetAccessTokenAsync(cancellationToken);
        var endpoint = GetMessagesEndpoint(context);
        var content = markdown ? SplitReply(text, 1, 1000)[0] : text.Length > 1000 ? text[..1000] : text;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Content = new StringContent(JsonSerializer.Serialize(
            markdown
                ? new QqMessagePayload(null, 2, context.MessageId, context.NextSequence(), Markdown: new QqMarkdownPayload(content))
                : new QqMessagePayload(content, 0, context.MessageId, context.NextSequence()),
            AppJsonContext.Default.QqMessagePayload),
            Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"QQ 提问发送失败 ({(int)response.StatusCode})：{body}");
    }

    private async Task<JsonElement> UploadImageAsync(ReplyContext context, CodexImage image, string token,
        CancellationToken cancellationToken)
    {
        var endpoint = context.GroupOpenId is null
            ? new Uri(_apiBase, $"v2/users/{Uri.EscapeDataString(context.ChatOpenId)}/files")
            : new Uri(_apiBase, $"v2/groups/{Uri.EscapeDataString(context.ChatOpenId)}/files");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("QQBot", token);
        request.Content = new StringContent(JsonSerializer.Serialize(
            new QqUploadPayload(1, Convert.ToBase64String(image.Bytes), false),
            AppJsonContext.Default.QqUploadPayload), Encoding.UTF8, "application/json");
        return await SendMediaUploadAsync(request, cancellationToken);
    }

    private async Task RecordConversationAsync(string userOpenId, string kind, string content, int imageCount = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await conversationStore.AppendAsync(userOpenId, kind, content, imageCount, cancellationToken, config.BotId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"保存会话记录失败：{ex.Message}");
        }
    }

    private async Task<JsonElement> SendMediaUploadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"上传富媒体失败 ({(int)response.StatusCode})：{body}");
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("file_info", out var fileInfo) ||
            fileInfo.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException("QQ 富媒体上传响应中没有 file_info。");
        return fileInfo.Clone();
    }

    private Uri GetMessagesEndpoint(ReplyContext context) => context.GroupOpenId is null
        ? new Uri(_apiBase, $"v2/users/{Uri.EscapeDataString(context.ChatOpenId)}/messages")
        : new Uri(_apiBase, $"v2/groups/{Uri.EscapeDataString(context.ChatOpenId)}/messages");

    private static string AppendReplyNote(string reply, string note) =>
        string.IsNullOrWhiteSpace(reply) ? note : $"{reply.TrimEnd()}\n{note}";

    private static List<string> SplitReply(string reply, int maxParts, int maxCharsPerPart)
    {
        if (maxParts <= 0) return [];
        var maxLength = maxParts * maxCharsPerPart;
        if (reply.Length > maxLength) reply = reply[..(maxLength - 15)] + "\n…（回复过长，已截断）";
        var parts = new List<string>();
        for (var start = 0; start < reply.Length && parts.Count < maxParts;)
        {
            var end = Math.Min(start + maxCharsPerPart, reply.Length);
            if (end < reply.Length)
            {
                var preferredBreak = reply.LastIndexOf('\n', end - 1, end - start);
                if (preferredBreak >= start + maxCharsPerPart / 2) end = preferredBreak + 1;
                else
                {
                    preferredBreak = reply.LastIndexOf(' ', end - 1, end - start);
                    if (preferredBreak >= start + maxCharsPerPart / 2) end = preferredBreak + 1;
                }
            }
            var length = end - start;
            if (start + length < reply.Length && char.IsHighSurrogate(reply[start + length - 1])) length--;
            parts.Add(reply.Substring(start, length));
            start += length;
        }
        return parts.Count == 0 && maxParts > 0 ? ["（Codex 没有返回文本）"] : parts;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddMinutes(-5)) return _accessToken;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt.AddMinutes(-5)) return _accessToken;
            using var response = await http.PostAsync(TokenUri,
                new StringContent(JsonSerializer.Serialize(new QqTokenPayload(config.AppId, config.AppSecret),
                        AppJsonContext.Default.QqTokenPayload),
                    Encoding.UTF8, "application/json"), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"获取 access_token 失败 ({(int)response.StatusCode})：{body}");
            using var doc = JsonDocument.Parse(body);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("获取到的 access_token 为空。");
            var expiresIn = doc.RootElement.GetProperty("expires_in");
            var seconds = expiresIn.ValueKind == JsonValueKind.String
                ? int.Parse(expiresIn.GetString()!) : expiresIn.GetInt32();
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return _accessToken;
        }
        finally { _tokenLock.Release(); }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
