namespace Puck.Scripting;

/// <summary>
/// Resolves a declared channel NAME against a host-owned channel table this core deliberately does not reference
/// — the table itself is Simulation-lane (today, World-lane) knowledge, supplied by whatever consumer constructs
/// an <see cref="AddonHost"/>. Unlike the source-id resolver this replaces, resolution failure here is never a
/// mount fault: an unresolvable name still decodes, carrying a sentinel (see <see cref="AddonChannelBinding"/>),
/// and is report-and-inert rather than refused.
/// </summary>
public interface IAddonChannelResolver {
    /// <summary>Attempts to resolve <paramref name="name"/> against the host's channel table.</summary>
    /// <param name="name">The declared channel name text.</param>
    /// <param name="ordinal">When this returns <see langword="true"/>, the host-owned ordinal <paramref name="name"/> resolved to.</param>
    /// <param name="shape">When this returns <see langword="true"/>, the value shape the host table declares for <paramref name="ordinal"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="name"/> names a channel in the host's table; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string name, out int ordinal, out AddonChannelValueShape shape);
}
