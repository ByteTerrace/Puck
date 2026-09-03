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

        Assert.Null(@object: population.Disclosure);
        Assert.Equal(expected: WorldObserverDisclosureMode.All, actual: population.ObserverDisclosure.Mode);
        Assert.Equal(expected: 0.03f, actual: population.ObserverDisclosure.UpdateSeconds);
        Assert.True(condition: new WorldSinkDisclosure(Policy: population.ObserverDisclosure, ObserverBodyIndex: -1).IsFull);
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

        using var unembodiedLease = hub.Subscribe(sink: unembodied, disclosure: new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: policy));
        using var embodiedLease = hub.Subscribe(sink: embodied, disclosure: new WorldSinkDisclosure(ObserverBodyIndex: 1, Policy: policy));

        var snapshot = new WorldSnapshot(
            Tick: 1UL,
            Revision: 0,
            StepTicks: 1UL,
            Entries: new EntitySnapshot[] { Entity(index: 0, x: 0f), Entity(index: 1, x: 1f) },
            Authority: "boot");

        hub.DeliverSnapshot(snapshot: in snapshot);

        Assert.Empty(collection: unembodied.LastIndices);
        Assert.Equal(expected: [1], actual: embodied.LastIndices);
    }
    [Fact]
    public void ValidatorRefusesAMisauthoredPolicyByName() {
        var document = Fixtures.BuildDocument();

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { PopulationRaw = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius) }) }),
            reason: out var missingRadius));
        Assert.Contains(actualString: missingRadius, comparisonType: StringComparison.Ordinal, expectedSubstring: "bodies.disclosure.radius is required");

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { PopulationRaw = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.All, Radius: 4f) }) }),
            reason: out var strayRadius));
        Assert.Contains(actualString: strayRadius, comparisonType: StringComparison.Ordinal, expectedSubstring: "bodies.disclosure.radius must be absent");

        // The control: a well-formed policy validates, so the refusals above are about the shape, not the member.
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { PopulationRaw = (document.Population with { Disclosure = new WorldObserverDisclosure(Mode: WorldObserverDisclosureMode.Radius, Radius: 12f) }) }),
            reason: out var wellFormed), userMessage: wellFormed);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (document with { PopulationRaw = (document.Population with {
                Disclosure = new WorldObserverDisclosure(UpdateSeconds: 1.01f),
            }) }),
            reason: out var badCadence));
        Assert.Contains(actualString: badCadence, comparisonType: StringComparison.Ordinal,
            expectedSubstring: "bodies.disclosure.updateSeconds");
    }

    [Fact]
    public void RemoteProjectionCadenceCoalescesFieldsAndRetainsDiscontinuities() {
        var sampler = new WorldProjectionSampler(updateSeconds: 0.03f);

        WorldSnapshot Snapshot(ulong tick, EntityContinuity continuity = default, long fieldRaw = 0L) => new(
            Authority: "boot",
            Entries: new[] { Entity(index: 7, x: tick) with { Continuity = continuity, Generation = 3 } },
            FieldCells: (fieldRaw == 0L ? [] : new[] { new FieldCellDelta(Cell: 11, Field: 2, Raw: fieldRaw) }),
            Revision: 0,
            StepTicks: Fixtures.StepTicks,
            Tick: tick
        );

        var first = Snapshot(tick: 1UL);
        Assert.True(sampler.TryProject(snapshot: in first, projected: out var primer));
        Assert.Equal(Fixtures.StepTicks, primer.StepTicks);

        for (var tick = 2UL; tick <= 8UL; tick++) {
            var continuity = (tick == 3UL ? EntityContinuity.Teleport : EntityContinuity.Continuous);
            var raw = (tick == 4UL ? 40L : (tick == 6UL ? 60L : 0L));
            var skipped = Snapshot(tick, continuity, raw);
            Assert.False(sampler.TryProject(snapshot: in skipped, projected: out _));
        }

        var due = Snapshot(tick: 9UL, fieldRaw: 90L);
        Assert.True(sampler.TryProject(snapshot: in due, projected: out var projected));
        Assert.Equal(expected: 8UL * Fixtures.StepTicks, actual: projected.StepTicks);
        Assert.Equal(expected: 9UL, actual: projected.Tick);
        Assert.Equal(expected: EntityContinuity.Teleport, actual: Assert.Single(projected.Entries.ToArray()).Continuity);
        Assert.Equal(expected: 90L, actual: Assert.Single(projected.FieldCells.ToArray()).Raw);
    }

    [Fact]
    public void LiveProjectionCadenceChangePreservesSkippedFullFieldImage() {
        var sampler = new WorldProjectionSampler(updateSeconds: 1f);
        var primer = new WorldSnapshot(1UL, 0, Fixtures.StepTicks, new[] { Entity(index: 0, x: 0f) }, "boot");
        Assert.True(sampler.TryProject(snapshot: in primer, projected: out _));

        var full = primer with {
            Tick = 2UL,
            FieldsFull = true,
            FieldCells = new[] {
                new FieldCellDelta(Cell: 3, Field: 0, Raw: 30L),
                new FieldCellDelta(Cell: 4, Field: 0, Raw: 40L),
            },
        };
        Assert.False(sampler.TryProject(snapshot: in full, projected: out _));

        sampler.SetUpdateSeconds(updateSeconds: 0f);
        var latest = primer with {
            Tick = 3UL,
            FieldCells = new[] { new FieldCellDelta(Cell: 3, Field: 0, Raw: 31L) },
        };
        Assert.True(sampler.TryProject(snapshot: in latest, projected: out var projected));
        Assert.True(projected.FieldsFull);
        Assert.Equal(2UL * Fixtures.StepTicks, projected.StepTicks);
        Assert.Equal(new[] { (3, 31L), (4, 40L) },
            projected.FieldCells.Span.ToArray().Select(delta => (delta.Cell, delta.Raw)).ToArray());
    }

    [Fact]
    public void ProjectionSamplerRefusesInvalidCadenceAndNonIncreasingTicks() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldProjectionSampler(updateSeconds: -0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldProjectionSampler(updateSeconds: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldProjectionSampler(
            updateSeconds: WorldObserverDisclosure.MaximumUpdateSeconds + 0.01f));

        var sampler = new WorldProjectionSampler(updateSeconds: 0f);
        var snapshot = new WorldSnapshot(7UL, 0, Fixtures.StepTicks, Array.Empty<EntitySnapshot>(), "boot");
        Assert.True(sampler.TryProject(snapshot: in snapshot, projected: out _));
        Assert.Throws<ArgumentException>(() => sampler.TryProject(snapshot: in snapshot, projected: out _));
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
