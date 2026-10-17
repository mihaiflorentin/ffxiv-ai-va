namespace AIVoiceActing;

using AIVoiceActing.UI;
using AIVoiceActing.UI.State;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

/// <summary>Shows toasts through Dalamud's notification manager.</summary>
public sealed class DalamudToastSink : IToastSink
{
    private readonly INotificationManager notifications;

    public DalamudToastSink(INotificationManager notifications)
    {
        this.notifications = notifications;
    }

    public void Show(Severity severity, string title, string? message = null)
    {
        this.notifications.AddNotification(new Notification
        {
            Title = title,
            Content = message ?? title,
            Type = severity switch
            {
                Severity.Success => NotificationType.Success,
                Severity.Warning => NotificationType.Warning,
                Severity.Error => NotificationType.Error,
                _ => NotificationType.Info,
            },
        });
    }
}
