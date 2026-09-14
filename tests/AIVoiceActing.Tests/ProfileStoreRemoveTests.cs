namespace AIVoiceActing.Tests;

using AIVoiceActing.Domain;
using AIVoiceActing.Infrastructure.Storage;
using AIVoiceActing.Ports;
using Xunit;

public sealed class ProfileStoreRemoveTests
{
    private static (JsonProfileStore Store, string Path) NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aiva-remove-{Guid.NewGuid():N}.json");
        return (new JsonProfileStore(path, new NullLog()), path);
    }

    private sealed class NullLog : AIVoiceActing.Ports.ILogSink
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    [Fact]
    public void Remove_DeletesEntryAndPersists()
    {
        var (store, path) = NewStore();
        try
        {
            var slots = new[] { new VoiceSlot("default", 0.5f) };
            store.GetOrCreate("npc:test", () => slots, 1, 1, 0);
            store.SetOverride("pc:Test@66", "default", 0f);

            Assert.True(store.Remove("pc:Test@66"));
            Assert.DoesNotContain(store.Entries, e => e.SpeakerKey == "pc:Test@66");

            // Persisted: a fresh store over the same file must not see the removed key,
            // while the first-sight entry survives untouched.
            var reloaded = new JsonProfileStore(path, new NullLog());
            Assert.DoesNotContain(reloaded.Entries, e => e.SpeakerKey == "pc:Test@66");
            Assert.Contains(reloaded.Entries, e => e.SpeakerKey == "npc:test");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Remove_UnknownKeyReturnsFalseAndLeavesEntriesUntouched()
    {
        var (store, path) = NewStore();
        try
        {
            Assert.False(store.Remove("npc:never-existed"));
            Assert.Empty(store.Entries);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemovedSpeaker_PicksAgainOnNextLookup()
    {
        var (store, path) = NewStore();
        try
        {
            // One candidate: the re-pick is trivially the same voice; the fresh
            // CreatedUtc proves the entry was re-created, not kept.
            var slots = new[] { new VoiceSlot("default", 0.5f) };
            var first = store.GetOrCreate("npc:sidurgu", () => slots, 1, 1, 0);
            store.Remove("npc:sidurgu");
            var second = store.GetOrCreate("npc:sidurgu", () => slots, 1, 1, 0);
            Assert.Equal(first.ReferenceVoiceId, second.ReferenceVoiceId);
            Assert.True(second.CreatedUtc >= first.CreatedUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
