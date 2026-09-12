namespace AIVoiceActing.Infrastructure.Dalamud;

using AIVoiceActing.Ports;
using global::Dalamud.Plugin.Services;

/// <summary>ILogSink adapter over Dalamud's IPluginLog.</summary>
public sealed class DalamudLogSink : ILogSink
{
    private readonly IPluginLog pluginLog;

    public DalamudLogSink(IPluginLog pluginLog) => this.pluginLog = pluginLog;

    public void Info(string msg) => this.pluginLog.Info(msg);

    public void Warn(string msg) => this.pluginLog.Warning(msg);

    public void Error(string msg, Exception? ex = null)
    {
        if (ex is null)
        {
            this.pluginLog.Error("{Message}", msg);
        }
        else
        {
            this.pluginLog.Error(ex, "{Message}", msg);
        }
    }
}
