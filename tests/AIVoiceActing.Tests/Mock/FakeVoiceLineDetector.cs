namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing.Ports;

/// <summary>Fake IVoiceLineDetector (census fake pattern): Raise simulates a hook hit.</summary>
public sealed class FakeVoiceLineDetector : IVoiceLineDetector
{
    public event Action? VoiceLinePlayback;

    public int Raised { get; private set; }

    public void Raise()
    {
        this.Raised++;
        this.VoiceLinePlayback?.Invoke();
    }

    public void Start()
    {
    }

    public void Dispose()
    {
    }
}
