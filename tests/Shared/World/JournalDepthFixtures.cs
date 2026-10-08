

namespace Puck.World.Testing;

/// <summary>The fixture world with an authored journal depth.</summary>
internal static class JournalDepthFixtures {
    internal static WorldDefinition WithDepth(int depth) {
        var source = Fixtures.BuildDocument();

        return (source with { HostRaw = (source.Host with { JournalDepth = depth }) });
    }
}
