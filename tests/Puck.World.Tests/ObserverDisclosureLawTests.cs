using System.Numerics;

using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves per-observer snapshot redaction at the output hub's sink boundary: a redacted sink is delivered only what
/// its policy discloses, while a sink attached with <see cref="WorldSinkDisclosure.Full"/> in the same fan-out sees
/// every body — the control that makes the negative mean something. Also pins the default: a world authoring no
/// policy is disclose-all, so nothing already checked in changes behavior.
/// </summary>
public sealed class ObserverDisclosureLawTests {
    [Fact]
    public void UnauthoredWorld_IsDiscloseAll() {
        var population = Fixtures.BuildDocument().Population;

        Assert.Null(population.Disclosure);
        Assert.Equal(expected: WorldObserverDisclosureMode.All, actual: population.ObserverDisclosure.Mode);
        Assert.True(new WorldSinkDisclosure(Policy: population.ObserverDisclosure, ObserverBodyIndex: -1).IsFull);
    }

    [Fact]
    public void RadiusPolicy_FiltersOneSink_WhileTheControlSinkSeesAll() {
        var hub = new WorldOutputHub();
        var redacted = new RecordingSink();
        var control = new RecordingSink();

        using var redactedLease = hub.Subscribe(sink: redacted, disclosure: new WorldSinkDisclosure(
            Policy: new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius, Radius: 5f),
            ObserverBodyIndex: 0));
        using var controlLease = hub.Subscribe(sink: control);

        var snapshot = new WorldSnapshot(
            Tick: 12UL,
            Revision: 1,
            StepTicks: 1UL,
            Entries: new EntitySnapshot[] {
                Entity(index: 0, x: 0f),
                Entity(index: 1, x: 3f),
                Entity(index: 2, x: 40f),
            },
            Authority: "boot");

        hub.DeliverSnapshot(snapshot: in snapshot);

        Assert.Equal(expected: [0, 1], actual: redacted.LastIndices);
        Assert.Equal(expected: [0, 1, 2], actual: control.LastIndices);
        // The tick's own facts are untouched — redaction removes rows, it never rewrites the frame.
        Assert.Equal(expected: 12UL, actual: redacted.LastTick);
        Assert.Equal(expected: "boot", actual: redacted.LastAuthority);
    }

    [Fact]
    public void SelfOnlyPolicy_DisclosesNothingToAnUnembodiedObserver() {
        var hub = new WorldOutputHub();
        var unembodied = new RecordingSink();
        var embodied = new RecordingSink();
        var policy = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.SelfOnly);

        using var unembodiedLease = hub.Subscribe(sink: unembodied, disclosure: new WorldSinkDisclosure(Policy: policy, ObserverBodyIndex: -1));
        using var embodiedLease = hub.Subscribe(sink: embodied, disclosure: new WorldSinkDisclosure(Policy: policy, ObserverBodyIndex: 1));

        var snapshot = new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: new EntitySnapshot[] { Entity(index: 0, x: 0f), Entity(index: 1, x: 1f) },
            Authority: "boot");

        hub.DeliverSnapshot(snapshot: in snapshot);

        Assert.Empty(unembodied.LastIndices);
        Assert.Equal(expected: [1], actual: embodied.LastIndices);
    }

    [Fact]
    public void ValidatorRefusesAMisauthoredPolicyByName() {
        var document = Fixtures.BuildDocument();

        Assert.False(WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { Population = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius) }) }),
            reason: out var missingRadius));
        Assert.Contains(expectedSubstring: "population.disclosure.radius is required", actualString: missingRadius, comparisonType: StringComparison.Ordinal);

        Assert.False(WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { Population = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.All, Radius: 4f) }) }),
            reason: out var strayRadius));
        Assert.Contains(expectedSubstring: "population.disclosure.radius must be absent", actualString: strayRadius, comparisonType: StringComparison.Ordinal);

        // The control: a well-formed policy validates, so the refusals above are about the shape, not the member.
        Assert.True(WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { Population = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius, Radius: 12f) }) }),
            reason: out var wellFormed), wellFormed);
    }

    private static EntitySnapshot Entity(int index, float x) =>
        new(Index: index,
            Position: new Vector3(x: x, y: 0f, z: 0f),
            Orientation: Quaternion.Identity,
            BodyColor: Vector3.One,
            Active: true,
            Kit: 0,
            Look: 0,
            CatalogRig: 0,
            Continuity: EntityContinuity.Continuous);

    private sealed class RecordingSink : IClientSink {
        public int[] LastIndices { get; private set; } = [];
        public ulong LastTick { get; private set; }
        public string LastAuthority { get; private set; } = string.Empty;

        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            var entries = snapshot.Entries.Span;
            var indices = new int[entries.Length];

            for (var index = 0; (index < entries.Length); index++) {
                indices[index] = entries[index].Index;
            }

            LastIndices = indices;
            LastTick = snapshot.Tick;
            LastAuthority = snapshot.Authority;
        }

        public void DeliverDefinition(WorldDefinition definition) { }
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
    }
}
