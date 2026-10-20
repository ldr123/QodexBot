internal sealed partial class QqBotClient
{
    private async Task SwitchWorkspaceAsync(string argument, ReplyContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            var current = await stateStore.GetAsync(SessionOwner(context.UserOpenId), codex.WorkDirectory, token);
            var command = getCommandBindings().GetValueOrDefault("switch-workspace", "/切换工作区");
            await SendTextReplyAsync(context, $"当前工作区：{current.WorkDirectory}\n用法：{command} 绝对路径\n例如：{command} D:\\OpenCode\\项目名称", token);
            return;
        }
        if (!TryNormalizeWorkspacePath(argument, out var workDirectory, out var error))
        {
            await SendTextReplyAsync(context, error, token);
            return;
        }
        var owner = SessionOwner(context.UserOpenId);
        await stateStore.SetWorkspaceAsync(owner, workDirectory, token);
        await SendTextReplyAsync(context, $"已切换工作区：{workDirectory}\n请选择该项目的 Codex 会话，或新建会话。当前任务继续使用原目录。", token);
        await RecordConversationAsync(context.UserOpenId, "command", $"switch-workspace: {workDirectory}", cancellationToken: token);
        if (IsCommandEnabled("sessions"))
            await ShowProjectSessionsAsync("", context, token);
    }

    internal static bool TryNormalizeWorkspacePath(string input, out string workDirectory, out string error)
    {
        workDirectory = string.Empty;
        error = string.Empty;
        var path = input.Trim();
        if (path.Length >= 2 && ((path[0] == '"' && path[^1] == '"') || (path[0] == '\'' && path[^1] == '\'')))
            path = path[1..^1];
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            error = "请输入已有目录的绝对路径，例如 D:\\OpenCode\\项目名称。";
            return false;
        }
        try
        {
            workDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Directory.Exists(workDirectory)) return true;
            error = "该目录不存在或无法访问，请检查后重新输入。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "目录路径无效或无法访问，请检查后重新输入。";
        }
        workDirectory = string.Empty;
        return false;
    }
}
