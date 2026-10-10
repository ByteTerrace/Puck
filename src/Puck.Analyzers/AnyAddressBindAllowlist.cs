namespace Puck.Analyzers;

/// <summary>
/// The one list of sites that may name the any-address (<see cref="AnyAddressBindAnalyzer"/>, NET001): deployment
/// configuration that binds a container's or a virtual machine's own interface, and the few tests that assert on that
/// configuration or hand it to a container. An entry names the assembly, the repository-relative source file and the
/// member holding the site, and states why the site cannot listen on loopback.
/// </summary>
public static class AnyAddressBindAllowlist {
    /// <summary>Gets every allowlisted site.</summary>
    public static IReadOnlyList<AnyAddressBindEntry> Entries { get; } = [
        new(
            Assembly: "Puck.Cli.Release",
            Member: "DeploymentListenAddress",
            Path: "src/Puck.Cli.Release/Azure/AzureWorld.cs",
            Reason: "The address an Azure world deployment and its container smoke test write into the world's host.listen and the silo's lifecycle.healthAddress: the load balancer probes and forwards to the virtual machine's interface, and Docker forwards a published port to the container's interface, so neither reaches a loopback listener."
        ),
        new(
            Assembly: "Puck.Mcp.Tests",
            Member: "UnsafeDeploymentConfigurationFailsBeforeListening",
            Path: "tests/Puck.Mcp.Tests/RemoteMcpTests.cs",
            Reason: "Asserts that a plaintext MCP listenUrl naming the any-address is refused before anything listens; nothing binds."
        ),
        new(
            Assembly: "Puck.Cli.Release.Tests",
            Member: "BlobLeaseExcludesCompetitorsAndAStaleOwnerCannotReleaseItsSuccessor",
            Path: "tests/Puck.Cli.Release.Tests/WorldReleaseAzureLeaseTests.cs",
            Reason: "Azurite binds every interface inside its own container so Docker can forward to it; the test publishes that port on the host's loopback only."
        ),
    ];

    /// <summary>Returns whether a site may name the any-address.</summary>
    /// <param name="assembly">The compiling assembly's name.</param>
    /// <param name="path">The source file's path as the compiler was given it, spelled with either separator.</param>
    /// <param name="member">The name of the member holding the site.</param>
    /// <returns><see langword="true"/> when an entry names the assembly, the file and the member.</returns>
    public static bool Admits(string assembly, string path, string member) {
        var normalized = path.Replace(
            newChar: '/',
            oldChar: '\\'
        );

        foreach (var entry in Entries) {
            if (
                string.Equals(
                    a: entry.Assembly,
                    b: assembly,
                    comparisonType: StringComparison.Ordinal
                ) &&
                string.Equals(
                    a: entry.Member,
                    b: member,
                    comparisonType: StringComparison.Ordinal
                ) &&
                (string.Equals(
                    a: normalized,
                    b: entry.Path,
                    comparisonType: StringComparison.Ordinal
                ) || normalized.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: ("/" + entry.Path)
                ))
            ) {
                return true;
            }
        }

        return false;
    }
}
/// <summary>One allowlisted any-address site.</summary>
/// <param name="Assembly">The assembly that compiles the site.</param>
/// <param name="Path">The source file's repository-relative path, spelled with <c>/</c>.</param>
/// <param name="Member">The name of the member holding the site.</param>
/// <param name="Reason">Why the site binds, or names, every interface rather than loopback.</param>
public sealed record AnyAddressBindEntry(string Assembly, string Path, string Member, string Reason);
