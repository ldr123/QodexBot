using System.Text.Json;
using Xunit;

namespace QodexBot.Tests;

public sealed class BotSettingsStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_MigratesOldDefaults_PreservesCustomBindings_AndPersistsOnSave(bool singleBotFormat)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qodex-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bindings = BotSettingsStore.DefaultCommandBindings.ToDictionary(pair => pair.Key, pair => "\\" + pair.Value[1..]);
            bindings["screenshot"] = "";
            bindings["models"] = "!models";
            bindings["rename"] = "\\改名";
            var document = new BotSettingsStore.BotSettingsDocument();
            if (singleBotFormat)
            {
                document.AppId = "id";
                document.AppSecret = "secret";
                document.CommandBindings = bindings;
            }
            else
            {
                document.Bots.Add(new() { Id = "test", AppId = "id", AppSecret = "secret", CommandBindings = bindings });
            }
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, WebJsonContext.Default.BotSettingsDocument));
            var store = new BotSettingsStore(path);
            await store.LoadAsync();
            var bot = Assert.Single(store.GetBots());
            foreach (var (key, command) in BotSettingsStore.DefaultCommandBindings)
                Assert.Equal(key is "screenshot" or "models" or "rename" ? bindings[key] : command, bot.CommandBindings[key]);

            await store.SaveAsync(new(bot.AppId, null, bot.Sandbox, bot.SubscribeC2cMessages,
                bot.SubscribeGroupMessages, bot.AdminPort, BotId: bot.BotId), default);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var persisted = saved.RootElement.GetProperty("bots")[0].GetProperty("commandBindings");
            foreach (var (key, command) in bot.CommandBindings)
                Assert.Equal(command, persisted.GetProperty(key).GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Load_DoesNotMigrateIntoAnotherActionsCustomBinding()
    {
        var path = Path.Combine(Path.GetTempPath(), "qodex-settings-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, """
                { "bots": [{ "id": "test", "commandBindings": { "status": "\\状态", "current": "/状态" } }] }
                """);
            var store = new BotSettingsStore(path);
            await store.LoadAsync();
            var bot = Assert.Single(store.GetBots());
            Assert.Equal("\\状态", bot.CommandBindings["status"]);
            Assert.Equal("/状态", bot.CommandBindings["current"]);
        }
        finally { File.Delete(path); }
    }
}
