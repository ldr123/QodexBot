using System.Collections.Concurrent;
using System.Text.Json;

internal sealed partial class QqBotClient
{
    private const int ListPageSize = 10;
    private readonly ConcurrentDictionary<string, ProjectThreadList> _displayedThreads = new(StringComparer.Ordinal);

    private sealed record ProjectThreadList(string WorkDirectory, string? SelectedThreadId, IReadOnlyList<CodexThreadSummary> Threads);

    private static readonly IReadOnlyDictionary<string, string> CommandLabels = new Dictionary<string, string>
    {
        ["status"] = "运行状态", ["sessions"] = "项目会话", ["switch-session"] = "切换会话",
        ["new-session"] = "新建会话", ["recent"] = "最近记录", ["current"] = "当前会话",
        ["rename"] = "重命名会话", ["workdir"] = "工作目录", ["switch-workspace"] = "切换工作区",
        ["screenshot"] = "发送截图", ["retry"] = "重试上一条", ["stop"] = "停止任务",
        ["models"] = "模型列表", ["switch-model"] = "切换模型", ["quota"] = "Codex 额度", ["update-codex"] = "更新 Codex CLI"
    };

    private static readonly IReadOnlyDictionary<string, string> CommandArguments = new Dictionary<string, string>
    {
        ["sessions"] = " [页码]", ["switch-session"] = " 编号", ["new-session"] = " [名称]",
        ["rename"] = " 新名称", ["switch-workspace"] = " 绝对路径", ["models"] = " [页码]",
        ["switch-model"] = " 名称或编号"
    };

    private bool IsCommandEnabled(string command) => !string.IsNullOrWhiteSpace(getCommandBindings().GetValueOrDefault(command));

    private async Task ShowProjectSessionsAsync(string argument, ReplyContext context, CancellationToken token)
    {
        var bindings = getCommandBindings();
        var command = bindings.GetValueOrDefault("sessions", "/会话");
        if (!TryReadPage(argument, out var pageNumber))
        {
            await SendTextReplyAsync(context, $"用法：{command} [页码]，例如：{command} 2。", token);
            return;
        }
        ProjectThreadList listing;
        try { listing = await LoadWorkspaceSessionsAsync(context, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"读取工作区 Codex 会话失败：{ex.Message}");
            await SendTextReplyAsync(context, $"读取当前工作区的 Codex 会话失败，请稍后重试 {command}。", token);
            return;
        }
        var pageCount = Math.Max(1, (listing.Threads.Count + ListPageSize - 1) / ListPageSize);
        if (pageNumber > pageCount)
        {
            await SendTextReplyAsync(context, $"项目会话只有 {pageCount} 页，请使用 {command} 1～{pageCount}。", token);
            return;
        }
        _displayedThreads[context.UserOpenId] = listing;
        var offset = (pageNumber - 1) * ListPageSize;
        var rows = listing.Threads.Skip(offset).Take(ListPageSize).Select((thread, index) =>
            $"{offset + index + 1}. {(thread.Id == listing.SelectedThreadId ? "▶ " : "")}{CommandLabel(thread.Name)}");
        var text = $"项目会话（第 {pageNumber}/{pageCount} 页，共 {listing.Threads.Count} 项）\n工作区：{CommandLabel(listing.WorkDirectory, 120)}\n\n"
            + (listing.Threads.Count == 0 ? "暂无项目会话。" : string.Join("\n", rows));
        if (listing.Threads.Count > 0 && IsCommandEnabled("switch-session")) text += $"\n\n切换：{bindings["switch-session"]} 编号";
        if (listing.Threads.Count == 0 && IsCommandEnabled("new-session")) text += $"\n新建：{bindings["new-session"]} [名称]";
        if (pageNumber > 1) text += $"\n上一页：{command} {pageNumber - 1}";
        if (pageNumber < pageCount) text += $"\n下一页：{command} {pageNumber + 1}";
        await SendTextReplyAsync(context, text, token);
        await RecordConversationAsync(context.UserOpenId, "command", $"sessions: {pageNumber}", cancellationToken: token);
    }

    private async Task<ProjectThreadList> LoadWorkspaceSessionsAsync(ReplyContext context, CancellationToken token)
    {
        var selection = await stateStore.GetAsync(SessionOwner(context.UserOpenId), codex.WorkDirectory, token);
        IReadOnlyList<CodexThreadSummary> history;
        await _codexControlLock.WaitAsync(token);
        try
        {
            history = await _projectSessions.ListThreadsAsync(selection.WorkDirectory, token);
            if (selection.ThreadId is not null && history.All(thread => thread.Id != selection.ThreadId))
            {
                var current = await _projectSessions.ReadThreadAsync(selection.ThreadId, selection.WorkDirectory, token);
                if (current is not null) history = [..history, current];
            }
        }
        finally { _codexControlLock.Release(); }
        var threads = history.Where(thread => WorkspacePath.Matches(thread.WorkDirectory, selection.WorkDirectory)).DistinctBy(thread => thread.Id)
            .OrderByDescending(thread => thread.UpdatedAt).ThenBy(thread => thread.Id, StringComparer.Ordinal).ToArray();
        return new(selection.WorkDirectory, selection.ThreadId, threads);
    }

    private async Task<CodexThreadSummary?> GetSessionChoiceAsync(ReplyContext context, int index, CancellationToken token)
    {
        if (index <= 0) return null;
        var selection = await stateStore.GetAsync(SessionOwner(context.UserOpenId), codex.WorkDirectory, token);
        if (!_displayedThreads.TryGetValue(context.UserOpenId, out var listing) || !WorkspacePath.Matches(listing.WorkDirectory, selection.WorkDirectory))
            listing = await LoadWorkspaceSessionsAsync(context, token);
        if (index > listing.Threads.Count) return null;
        await _codexControlLock.WaitAsync(token);
        try { return await _projectSessions.ReadThreadAsync(listing.Threads[index - 1].Id, selection.WorkDirectory, token); }
        finally { _codexControlLock.Release(); }
    }

    private async Task<CodexThreadSummary?> ReadCurrentThreadAsync(ReplyContext context, CancellationToken token)
    {
        var selection = await stateStore.GetAsync(SessionOwner(context.UserOpenId), codex.WorkDirectory, token);
        if (selection.ThreadId is null) return null;
        await _codexControlLock.WaitAsync(token);
        try { return await _projectSessions.ReadThreadAsync(selection.ThreadId, selection.WorkDirectory, token); }
        finally { _codexControlLock.Release(); }
    }

    private async Task ShowModelsAsync(string argument, ReplyContext context, CancellationToken token)
    {
        var bindings = getCommandBindings();
        var command = bindings.GetValueOrDefault("models", "/模型列表");
        if (!TryReadPage(argument, out var pageNumber))
        {
            await SendTextReplyAsync(context, $"用法：{command} [页码]，例如：{command} 2。", token);
            return;
        }
        IReadOnlyList<CodexModel> models;
        await _codexControlLock.WaitAsync(token);
        try { models = await codex.ListModelsAsync(token); }
        finally { _codexControlLock.Release(); }
        var selection = await stateStore.GetAsync(SessionOwner(context.UserOpenId), codex.WorkDirectory, token);
        var pageCount = Math.Max(1, (models.Count + ListPageSize - 1) / ListPageSize);
        if (pageNumber > pageCount)
        {
            await SendTextReplyAsync(context, $"模型列表只有 {pageCount} 页，请使用 {command} 1～{pageCount}。", token);
            return;
        }
        var offset = (pageNumber - 1) * ListPageSize;
        var text = $"模型列表（第 {pageNumber}/{pageCount} 页）\n当前：{CommandLabel(selection.Model ?? "沿用会话模型（新会话使用默认模型）")}\n\n"
            + (models.Count == 0 ? "Codex CLI 没有返回可用模型。" : string.Join("\n", models.Skip(offset).Take(ListPageSize).Select((model, index) =>
                $"{offset + index + 1}. {(model.Model == selection.Model ? "▶ " : "")}{CommandLabel(model.DisplayName, 30)}（{CommandLabel(model.Model, 30)}）")));
        if (IsCommandEnabled("switch-model")) text += $"\n\n切换：{bindings["switch-model"]} 名称或编号";
        if (pageNumber > 1) text += $"\n上一页：{command} {pageNumber - 1}";
        if (pageNumber < pageCount) text += $"\n下一页：{command} {pageNumber + 1}";
        await SendTextReplyAsync(context, text, token);
    }

    private async Task ShowQuotaAsync(ReplyContext context, CancellationToken token)
    {
        JsonElement snapshot;
        await _codexControlLock.WaitAsync(token);
        try { snapshot = await codex.ReadRateLimitsAsync(token); }
        finally { _codexControlLock.Release(); }

        var rows = new List<string>();
        if (snapshot.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject())
                AppendQuotaBucket(rows, bucket.Name, bucket.Value);
        }
        if (rows.Count == 0 && snapshot.TryGetProperty("rateLimits", out var fallback) && fallback.ValueKind == JsonValueKind.Object)
            AppendQuotaBucket(rows, "codex", fallback);

        if (rows.Count == 0) rows.Add("Codex 暂未返回可用额度窗口。");
        if (snapshot.TryGetProperty("rateLimitResetCredits", out var resetCredits) &&
            resetCredits.ValueKind == JsonValueKind.Object &&
            resetCredits.TryGetProperty("availableCount", out var count) && count.ValueKind == JsonValueKind.Number)
            rows.Add($"可用额度重置次数：{count.GetInt64()}");

        var plan = snapshot.TryGetProperty("rateLimits", out var planLimits) &&
            planLimits.ValueKind == JsonValueKind.Object && planLimits.TryGetProperty("planType", out var planType) &&
            planType.ValueKind == JsonValueKind.String
            ? planType.GetString() : null;
        var response = "Codex 额度（当前登录账号）" + (string.IsNullOrWhiteSpace(plan) ? "" : $"\n方案：{plan}")
            + "\n" + string.Join("\n", rows);
        await SendTextReplyAsync(context, response, token);
    }

    private static void AppendQuotaBucket(List<string> rows, string id, JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        var name = bucket.TryGetProperty("limitName", out var limitName) && limitName.ValueKind == JsonValueKind.String
            ? limitName.GetString() : null;
        var label = string.IsNullOrWhiteSpace(name) ? id : $"{name}（{id}）";
        var hasWindow = false;
        foreach (var property in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            hasWindow = true;
            var windowName = property == "primary" ? "主要窗口" : "次要窗口";
            if (window.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt64(out var minutes))
                windowName = minutes == 300 ? "5 小时" : minutes == 10080 ? "1 周" : $"{minutes} 分钟";
            var used = window.TryGetProperty("usedPercent", out var usedPercent) && usedPercent.TryGetInt32(out var usedValue)
                ? Math.Clamp(usedValue, 0, 100) : (int?)null;
            var status = used is null ? "使用率未知" : $"剩余 {100 - used.Value}%（已用 {used.Value}%）";
            if (window.TryGetProperty("resetsAt", out var resetsAt) && resetsAt.TryGetInt64(out var timestamp))
            {
                try { status += $"，重置时间 {DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime():yyyy-MM-dd HH:mm}"; }
                catch (ArgumentOutOfRangeException) { }
            }
            rows.Add($"{label} · {windowName}：{status}");
        }
        if (!hasWindow) rows.Add($"{label}：额度窗口暂不可用");
    }

    private static bool TryReadPage(string argument, out int pageNumber)
    {
        pageNumber = 1;
        return string.IsNullOrWhiteSpace(argument) || int.TryParse(argument.Trim(), out pageNumber) && pageNumber > 0;
    }

    private static string CommandLabel(string text, int length = 48) => Truncate(string.Concat(text.Select(character => char.IsControl(character) ? ' ' : character)), length);
}
