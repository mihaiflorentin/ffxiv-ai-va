namespace AIVoiceActing.UI.State;

using System.Globalization;
using System.Net;

/// <summary>The remote server address as the player typed it.</summary>
/// <param name="Valid">An absolute http:// or https:// address.</param>
/// <param name="Loopback">The host is this computer (localhost or a loopback address).</param>
/// <param name="Problem">Plain reason the address cannot be used; null when valid.</param>
public sealed record RemoteEndpoint(bool Valid, bool Loopback, string? Problem);

/// <summary>
/// Pure checks for the Remote engine fields under Advanced, mirroring what the remote adapter
/// enforces (it is the one that finally refuses a bad address) so the page can warn while the
/// player types instead of failing on the first spoken line. The keys are those of
/// <c>Engine.PerEngine["remote"].Extra</c>.
/// </summary>
public static class RemoteEndpointModel
{
    public const string EngineId = "remote";
    public const string UrlKey = "url";
    public const string AllowNonLoopbackKey = "allowNonLoopback";
    public const string TimeoutKey = "timeoutSeconds";
    public const string ModelKey = "model";

    public const string DefaultUrl = "http://127.0.0.1:8880";
    public const int DefaultTimeoutSeconds = 30;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 300;

    public static RemoteEndpoint Parse(string? raw)
    {
        var url = string.IsNullOrWhiteSpace(raw) ? DefaultUrl : raw.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new RemoteEndpoint(false, false, "Enter an address that starts with http:// or https://.");
        }

        return new RemoteEndpoint(true, IsLoopbackHost(uri), null);
    }

    public static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.IdnHost;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// The warning to show under the address field, or null. A server on another computer receives
    /// the text of every line it speaks, so it stays refused until the player allows it.
    /// </summary>
    public static string? Warning(RemoteEndpoint endpoint, bool allowNonLoopback)
    {
        if (!endpoint.Valid)
        {
            return endpoint.Problem;
        }

        if (endpoint.Loopback)
        {
            return null;
        }

        return allowNonLoopback
            ? "This server is on another computer. The text of every line you hear is sent to it. Only use a server you trust."
            : "This server is not on your computer, so it is blocked. Turn on \"Allow a server on another computer\" to send it the text of your lines.";
    }

    public static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static int TimeoutSeconds(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds)
            : DefaultTimeoutSeconds;

    public static string FormatSeconds(int seconds) => seconds.ToString(CultureInfo.InvariantCulture);
}
