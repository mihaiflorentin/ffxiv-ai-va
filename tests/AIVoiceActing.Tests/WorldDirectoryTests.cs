namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class WorldDirectoryTests
{
    private static readonly IReadOnlyList<(uint Id, string Name)> Worlds = [(80, "Cerberus"), (33, "Twintania"), (21, "Ravana")];

    [Fact]
    public void Label_ResolvesNames_FallsBackToId_AndDashForNone()
    {
        var dir = new WorldDirectory(() => Worlds);
        Assert.Equal("Twintania", dir.Label(33));
        Assert.Equal("999", dir.Label(999));
        Assert.Equal("—", dir.Label(null));
    }

    [Fact]
    public void Sorted_OrdersByName()
    {
        var dir = new WorldDirectory(() => Worlds);
        Assert.Equal(["Cerberus", "Ravana", "Twintania"], dir.Sorted().Select(w => w.Name));
    }

    [Fact]
    public void Source_IsReadOnce()
    {
        var calls = 0;
        var dir = new WorldDirectory(() => { calls++; return Worlds; });
        _ = dir.Label(1);
        _ = dir.Label(2);
        _ = dir.Sorted();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MissingOrFailingSource_ShowsRawIds()
    {
        var none = new WorldDirectory();
        Assert.Equal("5", none.Label(5));
        Assert.Empty(none.Sorted());

        var broken = new WorldDirectory(() => throw new InvalidOperationException("sheet"));
        Assert.Equal("5", broken.Label(5));
        Assert.Empty(broken.Sorted());
    }
}
