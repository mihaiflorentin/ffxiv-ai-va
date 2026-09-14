namespace AIVoiceActing.Infrastructure.Dalamud;

using System.Text;
using AIVoiceActing.Ports;

/// <summary>
/// Tee sink for the beast-tribe mapping workflow: every <c>Conversation:</c> identity
/// line is appended to a harvest file next to the other plugin data, so the model-id
/// mapping survives regardless of Dalamud's log level (INFO lines are muted in some
/// setups). Everything else forwards to the inner sink untouched. Appends are best
/// effort — a locked or missing file never breaks speech.
/// </summary>
public sealed class HarvestLogSink : ILogSink
{
    public const string LinePrefix = "Conversation:";
    private const int MaxBytes = 512 * 1024;

    private readonly object gate = new();
    private readonly ILogSink inner;
    private readonly string filePath;

    public HarvestLogSink(ILogSink inner, string filePath)
    {
        this.inner = inner;
        this.filePath = filePath;
    }

    public void Info(string message)
    {
        if (message.StartsWith(LinePrefix, StringComparison.Ordinal))
        {
            this.Append(message);
        }

        this.inner.Info(message);
    }

    public void Warn(string message) => this.inner.Warn(message);

    public void Error(string message, Exception? exception = null) => this.inner.Error(message, exception);

    private void Append(string message)
    {
        try
        {
            lock (this.gate)
            {
                // Cap the file: the harvest is read and trimmed between mapping
                // sessions; half a megabyte is far more than a session needs.
                var existing = File.Exists(this.filePath) ? new FileInfo(this.filePath).Length : 0;
                if (existing > MaxBytes)
                {
                    File.Move(this.filePath, this.filePath + ".old", overwrite: true);
                }

                var directory = Path.GetDirectoryName(this.filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(this.filePath, message + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Harvesting must never take speech down with it.
        }
    }
}
