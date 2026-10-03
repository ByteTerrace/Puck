using System.Reflection;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: one assembly carries the commit it was built at, and only as a resource. No assembly
/// carries the commit in its informational version, because that suffix changes every project's generated assembly info
/// on every commit and recompiles the whole tree; <see cref="WorldSchema.SourceRevision"/> embeds it in
/// Puck.World.Schema alone, so the schema bundle's <c>x-puck.commit</c> still names the real commit.</summary>
public sealed class SourceRevisionLawTests {
    [Fact]
    public void TheSchemaNamesTheCommitItWasBuiltAt() {
        var head = CliGit.Run(RepositoryPaths.RequireRoot(), "rev-parse", "HEAD");

        Assert.Equal(expected: 0, actual: head.ExitCode);
        Assert.Equal(actual: WorldSchema.SourceRevision, expected: head.Stdout.Trim());
    }
    [Fact]
    public void NoAssemblyCarriesTheCommitInItsInformationalVersion() {
        Assembly[] assemblies = [typeof(WorldSchema).Assembly, typeof(CliGit).Assembly, typeof(Puck.Maths.FixedQ4816).Assembly, typeof(SourceRevisionLawTests).Assembly];

        foreach (var assembly in assemblies) {
            var informational = (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty);

            Assert.False(condition: informational.Contains(value: '+'), userMessage: $"{assembly.GetName().Name} carries {informational}");
        }
    }
}
