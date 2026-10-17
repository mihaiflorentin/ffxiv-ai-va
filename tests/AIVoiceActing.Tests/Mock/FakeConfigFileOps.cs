namespace AIVoiceActing.Tests.Mock;

using AIVoiceActing;

/// <summary>In-memory <see cref="IConfigFileOps"/>: records copies, never touches disk.</summary>
public sealed class FakeConfigFileOps : IConfigFileOps
{
    public HashSet<string> Files { get; } = new();

    public List<(string Source, string Destination)> Copies { get; } = new();

    public bool Exists(string path) => this.Files.Contains(path);

    public void Copy(string source, string destination)
    {
        this.Copies.Add((source, destination));
        this.Files.Add(destination);
    }
}
