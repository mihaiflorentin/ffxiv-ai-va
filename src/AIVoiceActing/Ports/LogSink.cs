namespace AIVoiceActing.Ports;

/// <summary>Driven port for plugin logging; implemented over IPluginLog in-game, console in tools/tests.</summary>
public interface ILogSink
{
    void Info(string msg);

    void Warn(string msg);

    void Error(string msg, Exception? ex = null);
}
