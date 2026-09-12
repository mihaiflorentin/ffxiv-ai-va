namespace AIVoiceActing;

using AIVoiceActing.Container;
using AIVoiceActing.Infrastructure.Dalamud;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

public sealed class AIVoiceActingPlugin : IDalamudPlugin, IDisposable
{
    [PluginService]
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    [PluginService]
    public static ICondition Condition { get; private set; } = null!;

    [PluginService]
    public static IPluginLog PluginLog { get; private set; } = null!;

    private readonly ServiceContainer services;

    public AIVoiceActingPlugin()
    {
        this.services = new ServiceContainer(logSinkFactory: () => new DalamudLogSink(PluginLog));

        this.services.LogSink.Info("AIVoiceActing loaded.");
    }

    public void Dispose()
    {
        this.services.Dispose();
    }
}
