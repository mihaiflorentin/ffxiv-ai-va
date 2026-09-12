namespace AIVoiceActing.Infrastructure.TestDoubles
{
    /// <summary>
    /// Stand-in for a forbidden Infrastructure reference (real Infrastructure types are absent
    /// from the test assembly; this namespace prefix is what the scan forbids).
    /// </summary>
    public sealed class ForbiddenProbe
    {
    }
}

namespace AIVoiceActing.Ports.TestFixtures
{
    /// <summary>
    /// Scan fixture (test assembly only, excluded from the main scan via IsScanned): a scanned
    /// type whose forbidden reference hides inside a constructed generic argument. Proves the
    /// scan recurses into generic arguments.
    /// </summary>
    public sealed record GenericScanFixture(Task<AIVoiceActing.Infrastructure.TestDoubles.ForbiddenProbe> Probe);
}
