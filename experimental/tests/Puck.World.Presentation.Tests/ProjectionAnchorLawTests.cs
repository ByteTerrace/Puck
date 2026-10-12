using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a presentation-tier recipient presents a state clock from anchors. The authority and the
/// recipient call one prediction (<see cref="WorldClockAnchor.Predict"/>), the authority sends an anchor exactly at the
/// authoritative ticks the recipient's prediction misses its own phase, so the recipient's phase equals the
/// authority's, bit for bit, at every tick of a mixed trace with no spurious anchor; a steady sky sends nothing; a late
/// join hydrates the exact current phase, or seeds a clock that reads none from its load-validated phase; a clock
/// whose row the recipient may not read refuses before any derived value is emitted; and the last anchor per recipient
/// per clock is a counted row, released when the recipient leaves or loses disclosure.
/// </summary>
public sealed partial class ProjectionAnchorLawTests(ITestOutputHelper output) {
    // The records the kernels read: the light table, sky block and packed layers.
    private static (SdfLight[] Lights, SdfSkyBlock Block, SdfSkyLayer[] Stops) Packed(WorldResolvedEnvironment environment) {
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        environment.Sky.Pack(
            block: out var block,
            details: new SdfSkyDetails(),
            farDistance: 40f,
            layers: layers,
            lights: environment.Lights
        );

        return (environment.Lights.Records.ToArray(), block, layers);
    }
    [InlineData("src/Puck.World/Assets/worlds/moth-courtyard.puck")]
    [InlineData("tests/Puck.Parity/parity.puck")]
    [Theory]
    public void A_presentation_recipient_of_a_shipped_sky_keyed_on_a_state_clock_presents_it_as_the_authority(string path) {
        var world = AuthoredGameFixtures.Load(relativePath: path);

        foreach (var value in new[] { 0d, 0.25d, 0.5d, 0.625d, 0.75d }) {
            var host = world.WithWorldState(rows: [
                .. world.State.Where(predicate: static row => (row.Name.Value != "skyMode")),
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value))],
                    Kind: CellKind.Fixed,
                    Name: CellName.Parse(candidate: "skyMode")
                ),
            ]);
            var projection = Fixtures.Project(authority: "boot", definition: host, revision: 1, tier: WorldDisclosureTier.Presentation)!;

            Assert.True(condition: WorldProjection.TryToDefinition(definition: out var hydrated, projection: projection, reason: out var reason), userMessage: reason);

            var expected = Packed(environment: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: host, mirror: ClientFixtures.StateMirror(definition: host), revision: 0));
            var actual = Packed(environment: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: hydrated, mirror: ClientFixtures.StateMirror(definition: hydrated), revision: 0));

            Assert.Equal(actual: actual.Lights, expected: expected.Lights);
            Assert.Equal(actual: actual.Block, expected: expected.Block);
            Assert.Equal(actual: actual.Stops, expected: expected.Stops);
        }
    }
    [Fact]
    public void Bytes_per_recipient_per_second_are_counted_for_a_steady_sky_a_busy_sky_a_nonlinear_clock_and_a_late_join() {
        var seconds = 2;
        var ticks = (seconds * RateHz);

        long Measure(WorldStateRow row, Action<WorldFixture, int>? perTick) {
            using var fixture = Fixtures.FreshServer(definition: Document(row: row));

            var (observation, _) = Observe(fixture: fixture);

            fixture.Step();

            var work = new WorldProjectionWork();

            using (WorldProjectionWork.Attribute(work: work)) {
                for (var index = 0; (index < ticks); index++) {
                    perTick?.Invoke(arg1: fixture, arg2: index);
                    fixture.Step();
                }
            }

            observation.Dispose();

            // The wire carries each delta behind its leaf header.
            return (work.Read(kind: WorldProjectionWork.Bytes) + (work.Read(kind: WorldProjectionWork.Deltas) * WorldFederationCodec.ProjectionDeltaHeaderBytes));
        }

        var steady = Measure(perTick: null, row: Row(advance: PerTick(raw: 1024L), raw: 0L));
        // A busy sky: its clock sought ten times a second, a new phase each time.
        var busy = Measure(
            perTick: static (fixture, index) => {
                if ((index % 3) == 0) {
                    fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: Principal.Console, Row: Clock, Key: WorldStateRow.SlotKey.Value, Value: (index * 4099L) & 0xFFFFL, Kind: WorldDocumentWriteKind.Set));
                }
            },
            row: Row(raw: 0L)
        );
        // A nonlinear clock: an eased follower re-anchors at every tick it moves.
        var nonlinear = Measure(perTick: null, row: Row(dynamics: new StateDynamics(Row: "chase"), raw: FixedQ4816.FromDouble(value: 0.875d).Value) with {
            Cells = [new StateCell(
                Clock: new StateCellClock(EpochTick: 0L, Y0: 0L, V0: 0L),
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: 0.875d).Value)
            )],
        });
        var work = new WorldProjectionWork();

        using (WorldProjectionWork.Attribute(work: work)) {
            using var fixture = Fixtures.FreshServer(definition: Document(row: Row(raw: 0L)));

            var (observation, _) = Observe(fixture: fixture);

            observation.Dispose();
        }

        var hydration = (work.Read(kind: WorldProjectionWork.Bytes) + WorldFederationCodec.DocumentHeaderBytes);
        var courtyardProjection = Fixtures.Project(
            authority: "boot",
            definition: AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/moth-courtyard.puck"),
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        )!;
        var courtyardWire = WorldProjection.SerializeCompact(projection: courtyardProjection);
        var courtyard = (courtyardWire.Length + WorldFederationCodec.DocumentHeaderBytes);
        var courtyardCanonical = WorldProjection.Serialize(projection: courtyardProjection).Length;
        var prototypes = Puck.Abstractions.Documents.CanonicalJsonDocument.SerializeCompact(node: WorldProjectionDelta.Tree(utf8Json: courtyardWire)["prototypes"]!).Length;

        output.WriteLine(message: $"steady sky: {(steady / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"busy sky: {(busy / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"nonlinear clock: {(nonlinear / seconds)} bytes per recipient per second");
        output.WriteLine(message: $"late-join hydration: {hydration} bytes (fixture world), {courtyard} bytes (courtyard; {prototypes} of them its prototypes; {courtyardCanonical} indented)");

        Assert.Equal(actual: steady, expected: 0L);
        Assert.InRange(actual: busy, high: (nonlinear - 1L), low: 1L);
        Assert.InRange(actual: nonlinear, high: (hydration * ticks), low: (ticks * 50L));
        // The wire carries the compact form: no indentation.
        Assert.True(condition: (courtyardWire.Length < courtyardCanonical));
        Assert.DoesNotContain(collection: courtyardWire, expected: ((byte)'\n'));
    }
}
