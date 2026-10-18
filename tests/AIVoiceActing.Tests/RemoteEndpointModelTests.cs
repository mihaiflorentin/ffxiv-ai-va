namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class RemoteEndpointModelTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8880")]
    [InlineData("http://localhost:8880")]
    [InlineData("HTTP://LOCALHOST")]
    [InlineData("http://127.5.5.5/api")]
    [InlineData("http://[::1]:8880")]
    [InlineData("")]
    [InlineData(null)]
    public void LoopbackAddresses_AreValidAndLocal(string? raw)
    {
        var endpoint = RemoteEndpointModel.Parse(raw);

        Assert.True(endpoint.Valid);
        Assert.True(endpoint.Loopback);
        Assert.Null(RemoteEndpointModel.Warning(endpoint, allowNonLoopback: false));
    }

    [Theory]
    [InlineData("http://192.168.1.20:8880")]
    [InlineData("https://tts.example.com")]
    public void OtherComputers_AreValidButBlockedUntilAllowed(string raw)
    {
        var endpoint = RemoteEndpointModel.Parse(raw);

        Assert.True(endpoint.Valid);
        Assert.False(endpoint.Loopback);
        Assert.Contains("blocked", RemoteEndpointModel.Warning(endpoint, allowNonLoopback: false));
    }

    [Fact]
    public void AllowingAnotherComputer_StillWarnsThatTextIsSent()
    {
        var endpoint = RemoteEndpointModel.Parse("http://192.168.1.20:8880");

        var warning = RemoteEndpointModel.Warning(endpoint, allowNonLoopback: true);

        Assert.Contains("sent to it", warning);
        Assert.DoesNotContain("blocked", warning);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("127.0.0.1:8880")]
    public void BadAddresses_AreInvalidWithAProblemToShow(string raw)
    {
        var endpoint = RemoteEndpointModel.Parse(raw);

        Assert.False(endpoint.Valid);
        Assert.Contains("http://", endpoint.Problem);
        Assert.Equal(endpoint.Problem, RemoteEndpointModel.Warning(endpoint, allowNonLoopback: true));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("abc", 30)]
    [InlineData("-4", 30)]
    [InlineData("45", 45)]
    [InlineData("2", 5)]
    [InlineData("9999", 300)]
    public void Timeout_FallsBackToTheDefaultAndStaysInRange(string? raw, int expected)
    {
        Assert.Equal(expected, RemoteEndpointModel.TimeoutSeconds(raw));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrue_ReadsTheStoredFlag(string? raw, bool expected)
    {
        Assert.Equal(expected, RemoteEndpointModel.IsTrue(raw));
    }

    [Fact]
    public void TheDefaultsAndKeys_MatchTheRemoteAdapter()
    {
        Assert.Equal(AIVoiceActing.Infrastructure.Net.RemoteSynthesizer.DefaultUrl, RemoteEndpointModel.DefaultUrl);
        Assert.Equal(AIVoiceActing.Infrastructure.Net.RemoteSynthesizer.DefaultTimeoutSeconds, RemoteEndpointModel.DefaultTimeoutSeconds);
        Assert.Equal("remote", RemoteEndpointModel.EngineId);
    }

    [Theory]
    [InlineData("http://localhost:1")]
    [InlineData("http://192.168.0.5")]
    [InlineData("http://[::1]")]
    [InlineData("https://example.org")]
    public void LoopbackCheck_AgreesWithTheAdapter(string url)
    {
        var uri = new Uri(url);

        Assert.Equal(
            AIVoiceActing.Infrastructure.Net.RemoteSynthesizer.IsLoopbackHost(uri),
            RemoteEndpointModel.IsLoopbackHost(uri));
    }
}
