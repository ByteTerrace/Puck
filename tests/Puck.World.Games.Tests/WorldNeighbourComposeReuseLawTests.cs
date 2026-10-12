using Puck.World.Server;
using Puck.Testing;
using Xunit;

namespace Puck.World.Games.Tests;
/// <summary>
/// Proves the composed-document reuse behind a quilt boot: a shard whose basis, whose adjacency neighbours and whose
/// derived corners all name one island document merges that document exactly once per process; the reused image is
/// the same value a fresh merge produces, byte for byte, so nothing about reuse can move a state hash; an edit
/// anywhere in a held image's chain is never served from the image; and a held image never stretches the
/// composition-depth rule for a reader standing deeper in a chain than the reader that first composed it.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class WorldNeighbourComposeReuseLawTests {
    [Fact]
    public void ObservingALocalNeighbourLoadsOnceAndReusesItsRunningAuthority() {
        using var files = new TemporaryDirectory();

        files.WriteBytes(bytes: Fixtures.DefaultWorldBytes(), name: "basis.world.json");
        var target = files.WriteText(name: "target.world.json", text: """{"basis":"basis"}""");
        var source = Fixtures.BuildDocument() with {
            References = [new WorldReference(SafeName.Parse(candidate: "target"), WorldDocumentName.OfDocumentFile(path: target))],
            Destinations = [new WorldDestination(SafeName.Parse(candidate: "neighbour"), "target",
                WorldDestinationDurability.Persisted, WorldDestinationScope.Global)],
        };
        using var boot = HostRow.Build(definition: source, name: WorldInstanceHost.BootInstanceName);
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: files.RootPath)
        );

        host.AdmitBoot(row: boot.Instance);
        WorldDefinitionFileSource.ForgetComposedDocuments();

        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);

        Assert.True(condition: host.TryResolveObservedProjection(boot.Instance, "neighbour", out var name,
            out var generation, out var definition, out var attach, out var reason), userMessage: reason);
        Assert.NotNull(@object: definition);
        Assert.NotNull(@object: attach);
        Assert.True(condition: host.TryDescribeDocumentSharing(name: name, shared: out var shared));
        Assert.False(condition: shared);
        // A second load between choosing local hosting and admitting the instance would reuse the held image.
        Assert.Equal(0L, work.Read(kind: WorldBootWork.CompositionsShared));

        File.WriteAllText(contents: "not a document", path: target);
        Assert.True(condition: host.TryResolveObservedProjection(boot.Instance, "neighbour", out var sameName,
            out var sameGeneration, out var sameDefinition, out _, out reason), userMessage: reason);
        Assert.Equal(actual: sameName, expected: name);
        Assert.Equal(actual: sameGeneration, expected: generation);
        Assert.Same(actual: sameDefinition, expected: definition);

        Assert.True(condition: host.TryStop(name: name, reason: out reason), userMessage: reason);
        Assert.False(condition: host.TryResolveObservedProjection(boot.Instance, "neighbour", out _, out _, out _, out _, out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "not a valid");
    }
    [Fact]
    public void AnEditedBasis_IsNeverServedFromAHeldImage() {
        using var files = new TemporaryDirectory();

        var root = files.WriteText(
            name: "root.world.json",
            text: /*lang=json*/ """{ "basis": "basis" }"""
        );

        _ = files.WriteText(
            name: "basis.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.definition.v1", "name": "first" }"""
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                path: root,
                reason: out var firstReason,
                tree: out var first
            ),
            userMessage: firstReason
        );
        Assert.Equal(
            actual: ((string?)first!["name"]),
            expected: "first"
        );

        // Only the basis moves; the document the reader names still holds byte-for-byte what it held. A reuse
        // keyed on the named document's own bytes alone would answer with the stale merge.
        _ = files.WriteText(
            name: "basis.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.definition.v1", "name": "second" }"""
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                path: root,
                reason: out var secondReason,
                tree: out var second
            ),
            userMessage: secondReason
        );
        Assert.Equal(
            actual: ((string?)second!["name"]),
            expected: "second"
        );
    }
    [Fact]
    public void AHeldImage_NeverStretchesTheCompositionDepthRule() {
        using var files = new TemporaryDirectory();

        // A chain exactly as long as the rule admits: MaxChainDepth documents, the last one flat.
        for (var index = 0; (index < (WorldDocumentBasis.MaxChainDepth - 1)); index++) {
            _ = files.WriteText(
                name: $"link{index}.world.json",
                text: $$"""{ "basis": "link{{(index + 1)}}" }"""
            );
        }

        _ = files.WriteText(
            name: $"link{(WorldDocumentBasis.MaxChainDepth - 1)}.world.json",
            text: /*lang=json*/ """{ "schema": "puck.world.definition.v1", "name": "tail" }"""
        );

        var head = Path.Combine(
            path1: files.RootPath,
            path2: "link0.world.json"
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                path: head,
                reason: out var headReason,
                tree: out _
            ),
            userMessage: headReason
        );

        // One document above the head pushes the same subtree one link past the rule. The image the head left
        // behind carries how far it reaches, so it declines to answer here and the walk refuses by name — a reuse
        // that ignored its own reach would compose a chain the rule forbids.
        var above = files.WriteText(
            name: "above.world.json",
            text: /*lang=json*/ """{ "basis": "link0" }"""
        );

        Assert.False(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
            path: above,
            reason: out var aboveReason,
            tree: out _
        ));
        Assert.Contains(
            actualString: aboveReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"exceeds {WorldDocumentBasis.MaxChainDepth} documents"
        );
    }
}
