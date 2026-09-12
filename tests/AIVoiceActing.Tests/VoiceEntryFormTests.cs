namespace AIVoiceActing.Tests;

using AIVoiceActing.UI.State;
using Xunit;

public sealed class VoiceEntryFormTests
{
    private static HashSet<string> Keys(params string[] keys) => [.. keys];

    [Fact]
    public void PlayerForm_BuildsCanonicalPlayerKey()
    {
        var form = new VoiceEntryForm(player: true) { Name = "Minfilia Warde", World = "66" };
        Assert.Null(form.Validate(Keys()));
        Assert.Equal("pc:Minfilia Warde@66", form.BuildKey());
    }

    [Fact]
    public void PlayerForm_TrimsInputs()
    {
        var form = new VoiceEntryForm(true) { Name = "  Minfilia  ", World = " 66 " };
        Assert.Equal("pc:Minfilia@66", form.BuildKey());
    }

    [Theory]
    [InlineData("", "", "Name is required.")]
    [InlineData("Minfilia", "", "World must be a world id (0-65535).")]
    [InlineData("Minfilia", "abc", "World must be a world id (0-65535).")]
    [InlineData("pc:Minfilia", "66", "Name must not include the pc:/npc: prefix.")]
    [InlineData("npc:Minfilia", "66", "Name must not include the pc:/npc: prefix.")]
    public void PlayerForm_RejectsInvalidInput(string name, string world, string expectedError)
    {
        var form = new VoiceEntryForm(true) { Name = name, World = world };
        Assert.Equal(expectedError, form.Validate(Keys()));
        Assert.Null(form.BuildKey());
    }

    [Fact]
    public void PlayerForm_RejectsSuffixCharacter()
    {
        var form = new VoiceEntryForm(true) { Name = "Minfilia»Jenesis", World = "66" };
        Assert.Contains("world-suffix", form.Validate(Keys()), StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerForm_DetectsDuplicates()
    {
        var form = new VoiceEntryForm(true) { Name = "Minfilia Warde", World = "66" };
        Assert.Equal(
            "A voice is already assigned to this speaker.",
            form.Validate(Keys("pc:Minfilia Warde@66")));
    }

    [Fact]
    public void NpcForm_LowercasesIntoNpcKey()
    {
        var form = new VoiceEntryForm(player: false) { Name = "Sidurgu Orl" };
        Assert.Null(form.Validate(Keys()));
        Assert.Equal("npc:sidurgu orl", form.BuildKey());
    }

    [Fact]
    public void NpcForm_DetectsDuplicatesCaseInsensitivelyViaKey()
    {
        var form = new VoiceEntryForm(false) { Name = "SIDURGU ORL" };
        Assert.Equal(
            "A voice is already assigned to this speaker.",
            form.Validate(Keys("npc:sidurgu orl")));
    }

    [Fact]
    public void Reset_ClearsInputs()
    {
        var form = new VoiceEntryForm(true) { Name = "A", World = "1" };
        form.Reset();
        Assert.Equal(string.Empty, form.Name);
        Assert.Equal(string.Empty, form.World);
    }
}
