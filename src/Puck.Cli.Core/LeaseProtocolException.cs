namespace Puck.Cli;

/// <summary>A lease acquisition refused because the shared lease directory records a newer lease protocol than this CLI speaks
/// (<see cref="CliLease.Protocol"/>): the CLI was built from older sources than another copy that queued there, and the two
/// would disagree on what the lease files mean. The acquisition withdraws its ticket and grants nothing.</summary>
/// <param name="root">The shared lease directory.</param>
/// <param name="recorded">The protocol the directory records.</param>
/// <param name="spoken">The protocol this CLI speaks.</param>
public sealed class LeaseProtocolException(string root, int recorded, int spoken) : InvalidOperationException(message: $"the shared lease directory {CliPaths.ToDisplay(fullPath: root)} records lease protocol {recorded}, and this CLI speaks lease protocol {spoken}: it was built from older sources than another CLI on this machine. Merge the integration tip, rebuild the CLI (dotnet build src/Puck.Cli -c Release -nodeReuse:false) and refresh your copy of its output before queuing again; no ticket of this CLI remains queued.") {
    /// <summary>Gets the protocol the shared lease directory records.</summary>
    public int Recorded { get; } = recorded;

    /// <summary>Gets the protocol this CLI speaks.</summary>
    public int Spoken { get; } = spoken;
}
