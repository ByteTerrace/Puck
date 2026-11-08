using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.Testing;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class WorldAdjacencyCorrectionLawTests {
    [Fact]
    public void AuthoredLinkKeepsItsFirstAuthorityAcrossObservationReplacement() {
        using var directory = new TemporaryDirectory(prefix: "puck-seam-link-");
        using var state = new TemporaryDirectory(prefix: "puck-seam-link-host-");
        var sourcePath = directory.PathOf("source.world.json");
        var remotePath = directory.PathOf("remote.world.json");
        var source = Document() with {
            References = [new WorldReference(SafeName.Parse("beyond-ref"), WorldDocumentName.OfDocumentFile(remotePath))],
        };
        var remote = Document() with {
            HostRaw = Document().Host with { AuthorityNamespace = "first-authority" },
            References = [new WorldReference(SafeName.Parse("beyond-ref"), WorldDocumentName.OfDocumentFile(sourcePath))],
            Adjacencies = [Document().Adjacencies![0] with {
                Boundary = Document().Adjacencies![0].Boundary with { OutwardYawDegrees = 180 },
            }],
        };
        FileBackedRows.Write((sourcePath, source), (remotePath, remote));
        using var host = new WorldInstanceHost(applicationStopping: CancellationToken.None, admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory, machineId: Guid.NewGuid(), resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None, stateRoot: new WorldStateRoot(state.RootPath));
        var localRow = FileBackedRows.Build("source", sourcePath, source);
        var firstRow = FileBackedRows.Build("remote-first", remotePath, remote);
        try {
            host.Admit(localRow.Instance);
            host.Admit(firstRow.Instance);
            using var fields = new WorldAdjacencyFields(host, "source");
            Assert.Equal("first-authority", fields.Visuals().Single().Neighbour.Authority);
            Assert.True(host.TryRetire("remote-first", out var reason), reason);
            var changed = remote with { HostRaw = remote.Host with { AuthorityNamespace = "changed-authority" } };
            FileBackedRows.Write((remotePath, changed));
            var replacement = FileBackedRows.Build("remote-second", remotePath, changed);
            try {
                host.Admit(replacement.Instance);
                fields.BeginTick(1);
                Assert.Equal("first-authority", fields.Visuals().Single().Neighbour.Authority);
            } finally {
                replacement.Instance.Dispose();
                replacement.StateDirectory.Dispose();
            }
        } finally {
            localRow.Instance.Dispose();
            firstRow.Instance.Dispose();
            localRow.StateDirectory.Dispose();
            firstRow.StateDirectory.Dispose();
        }
    }
    private sealed class FieldStep(WorldAdjacencyContactField field, WorldBody body) : IFixedContactStep {
        public List<string> Trace { get; } = [];
        public ContactResolution Sweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity) {
            var result = field.ResolveEntitySweep(0, previousPosition, ref position, ref velocity, FixedQuaternion.Identity,
                body.ScaledColliderVolumes(), FixedVector3.UnitY);
            Trace.Add($"sweep {previousPosition} -> {position}: {result}");
            return result;
        }
        public ContactResolution SweepSupport(in FixedVector3 previousPosition, in FixedVector3 responseOrigin,
            ref FixedVector3 position, ref FixedVector3 velocity) {
            var result = field.ResolveEntitySupportSweep(0, previousPosition, responseOrigin, ref position, ref velocity,
                FixedQuaternion.Identity, body.ScaledColliderVolumes(), FixedVector3.UnitY);
            Trace.Add($"support {responseOrigin} via {previousPosition} -> {position}: {result}");
            return result;
        }
        public bool TrySupportDistance(in FixedVector3 start, FixedQ4816 reach, out FixedQ4816 descent) {
            var met = field.TrySupportDistance(start, FixedQuaternion.Identity, body.ScaledColliderVolumes(), FixedVector3.UnitY, reach, out descent);
            Trace.Add($"distance {start}, reach={reach}, met={met}, descent={descent}");
            return met;
        }
    }
    private sealed class RiserField : IContactField {
        public bool Enabled { get; set; }
        private static FixedQ4816 Height => FixedQ4816.FromDouble(0.3);
        public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity,
            in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
            if (!Enabled) { return default; }
            if (position.Y <= FixedQ4816.Zero) {
                position -= FixedVector3.UnitX * FixedQ4816.Epsilon;
                return new(false, -FixedVector3.UnitX);
            }
            if (position.Y < Height) {
                position = position with { Y = Height };
                return new(true, FixedVector3.Zero, GroundNormal: FixedVector3.UnitY);
            }
            return default;
        }
        public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation,
            ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) {
            descent = FixedQ4816.Max(FixedQ4816.Zero, position.Y - Height);
            return Enabled && descent <= reach;
        }
        public bool TryUp(in FixedVector3 position, out FixedVector3 up) { up = FixedVector3.UnitY; return true; }
    }

    [Theory]
    [InlineData(0.01f, false, false)]
    [InlineData(1f, true, false)]
    [InlineData(1f, false, true)]
    public void NeighbourStepUpAdmitsTheEntireLift(float depth, bool honest, bool ceilingFace) {
        var document = Document();
        if (ceilingFace) {
            var seam = document.Adjacencies![0];
            document = document with { Adjacencies = [seam with {
                Boundary = seam.Boundary with { OutwardPitchDegrees = 90 },
            }] };
            var threshold = WorldAdjacencyPolicy.OwnershipFaces(document).Single().Threshold;
            var pitched = document.Adjacencies![0];
            document = document with { Adjacencies = [pitched with {
                Boundary = pitched.Boundary with { Center = new Vector3(0, 0.1f - (float)(double)threshold, 0) },
            }] };
        }
        using var fixture = Fixtures.FreshServer(definition: document);
        var body = Seat(fixture, 0.01f);
        var riser = new RiserField();
        var reports = new Reports(body, new ReportedField(riser), depth, 0, FixedVector3.UnitX);
        fixture.Server.Adjacencies = reports;
        fixture.Step();
        riser.Enabled = true;
        var field = new WorldAdjacencyContactField(new ResponseField(), reports, fixture.Server.Population);
        var step = new FieldStep(field, body);
        var start = body.FixedPosition;
        Assert.True(Face(fixture).Owns(start));
        var intended = start + FixedVector3.UnitX;
        var blocked = intended;
        var velocity = FixedVector3.UnitX;
        _ = step.Sweep(start, ref blocked, ref velocity);
        Assert.True(blocked.X < intended.X);
        var accepted = FixedStepUp.TryStep(ref step, start, intended, FixedVector3.UnitX, FixedVector3.UnitY,
            FixedQ4816.FromDouble(0.3), blocked, out var result);
        Assert.True(accepted == honest, $"accepted={accepted}, honest={honest}: {string.Join('\n', step.Trace)}");
        if (honest) { Assert.True(result.Position.Y > start.Y + FixedQ4816.FromDouble(0.29)); }
    }
    private sealed class ResponseField : IContactField {
        public bool Enabled { get; set; }
        public int Calls { get; private set; }
        public FixedVector3 Push { get; set; }
        public FixedVector3? Velocity { get; set; }
        public ContactResolution Answer { get; set; }
        public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity,
            in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
            if (!Enabled) { return default; }
            Calls++;
            position += Push;
            velocity = Velocity ?? velocity;
            return Answer;
        }
        public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation,
            ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) {
            descent = default;
            return false;
        }
        public bool TryUp(in FixedVector3 position, out FixedVector3 up) { up = FixedVector3.UnitY; return true; }
    }
    private static WorldAdjacencyContactField Ready(WorldFixture fixture, WorldBody body, ResponseField field,
        out Reports reports, float depth = 0.5f, float yaw = 0f, IContactField? inner = null) {
        reports = new Reports(body, new ReportedField(field), depth, yaw, FixedVector3.UnitX);
        fixture.Server.Adjacencies = reports;
        fixture.Step();
        field.Enabled = true;
        return new WorldAdjacencyContactField(inner ?? new ResponseField(), reports, fixture.Server.Population);
    }
    private static ContactResolution Resolve(WorldAdjacencyContactField field, WorldBody body,
        ref FixedVector3 position, ref FixedVector3 velocity) =>
        field.ResolveEntitySweep(0, position, ref position, ref velocity, FixedQuaternion.Identity,
            body.ScaledColliderVolumes(), FixedVector3.UnitY);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedDisplacementCannotDeflectVelocity(bool exhausted) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, -0.1f);
        var response = new ResponseField { Push = FixedVector3.UnitZ, Velocity = FixedVector3.UnitY * FixedQ4816.FromInteger(40) };
        var field = Ready(fixture, body, response, out _);
        var position = body.FixedPosition;
        var velocity = FixedVector3.Zero;
        if (exhausted) {
            response.Push = FixedVector3.UnitX;
            _ = Resolve(field, body, ref position, ref velocity);
        }
        velocity = -FixedVector3.UnitZ;
        var before = position;
        _ = Resolve(field, body, ref position, ref velocity);
        Assert.Equal(before, position);
        Assert.Equal(-FixedVector3.UnitZ, velocity);
    }

    [Fact]
    public void EveryCornerProjectionCanSupplyGroundAfterAnotherReportsContact() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, 0.01f);
        var first = new ResponseField { Push = FixedVector3.UnitX * FixedQ4816.FromDouble(0.01),
            Answer = new(false, FixedVector3.UnitX) };
        var field = Ready(fixture, body, first, out var reports);
        var floor = new ResponseField { Enabled = true, Push = FixedVector3.UnitY * FixedQ4816.FromDouble(0.02),
            Answer = new(true, FixedVector3.Zero, GroundNormal: FixedVector3.UnitY) };
        reports.Extra = [reports.Projection with { Name = "corner", Neighbour = new ReportedField(floor), Direct = false }];
        var position = body.FixedPosition;
        var velocity = FixedVector3.Zero;
        var result = Resolve(field, body, ref position, ref velocity);
        Assert.True(result.Grounded);
        Assert.True(position.X > body.FixedPosition.X);
        Assert.True(position.Y > body.FixedPosition.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroundNormalSurvivesNeighbourComposition(bool localGround) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, 0.01f);
        var normal = new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(0.8), FixedQ4816.Zero).Normalize();
        var response = new ResponseField { Push = FixedVector3.UnitX * FixedQ4816.FromDouble(0.01),
            Answer = new(!localGround, FixedVector3.UnitX, GroundNormal: normal) };
        var inner = new ResponseField { Enabled = true, Answer = new(localGround, FixedVector3.Zero, GroundNormal: normal) };
        var field = Ready(fixture, body, response, out _, inner: inner);
        var position = body.FixedPosition;
        var velocity = FixedVector3.Zero;
        var result = Resolve(field, body, ref position, ref velocity);
        Assert.True(result.Grounded);
        Assert.Equal(normal, result.GroundNormal);
    }

    [Fact]
    public void RejectedGroundPushDoesNotLatchGrounding() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, -0.1f);
        var response = new ResponseField { Push = -FixedVector3.UnitZ,
            Answer = new(true, FixedVector3.Zero, GroundNormal: FixedVector3.UnitY) };
        var field = Ready(fixture, body, response, out _);
        var position = body.FixedPosition;
        var velocity = FixedVector3.UnitZ;
        Assert.False(Resolve(field, body, ref position, ref velocity).Grounded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RotatedHonestResponseAtTheBudgetRemainsGroundedWithoutAClamp(int stages) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, 0.01f);
        var response = new ResponseField { Answer = new(true, FixedVector3.Zero, GroundNormal: FixedVector3.UnitY) };
        var field = Ready(fixture, body, response, out var reports);
        var radius = FixedQ4816.FromDouble(0.5);
        var push = FixedVector3.UnitX * radius;
        var found = false;
        for (var yaw = 1; yaw < 90 && !found; yaw++) {
            var pair = reports.Projection.Path[0] with {
                Source = new WorldAdjacencyBoundary(Vector3.Zero, yaw, 0, 128, 128).CompileFrame(),
            };
            var path = Enumerable.Repeat(pair, stages).ToArray();
            if (WorldAdjacencyPath.MapVectorIntoSource(push, path).IsWithin(radius)) { continue; }
            reports.Projection = reports.Projection with { Path = path };
            found = true;
        }
        Assert.True(found, "the control must exhibit frame-quantization growth");
        response.Push = push;
        var position = body.FixedPosition;
        var velocity = FixedVector3.Zero;
        var before = WorldBodyWork.Process.Read(WorldBodyWork.AdjacencyCorrectionClamps);
        var result = field.ResolveEntitySupportSweep(0, position, position, ref position, ref velocity,
            FixedQuaternion.Identity, body.ScaledColliderVolumes(), FixedVector3.UnitY);
        Assert.True(result.Grounded);
        Assert.True((position - body.FixedPosition).IsWithin(radius));
        Assert.Equal(before, WorldBodyWork.Process.Read(WorldBodyWork.AdjacencyCorrectionClamps));
    }

    [Fact]
    public void MappedArrivalAndOrdinaryAdvanceShareOnePinnedTickBudget() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Seat(fixture, 0.01f);
        var response = new ResponseField { Enabled = true, Push = FixedVector3.UnitX };
        var reports = new Reports(body, new ReportedField(response), 0.25f, 0, FixedVector3.UnitX);
        fixture.Server.Adjacencies = reports;
        var start = body.FixedPosition;
        for (var arrival = 0; arrival < 2; arrival++) {
            var at = body.FixedPosition;
            Assert.True(fixture.Server.Population.ApplyMappedArrival(0, "grounded", at, FixedQ4816.Zero,
                FixedVector3.Zero, FixedQ4816.Zero, fixture.Server.CompletedEngineTicks,
                continuum: new WorldContinuumTrajectory(at, 1, 0, 210, 210, 0)));
        }
        Assert.NotEqual(start, body.FixedPosition);
        Assert.True((body.FixedPosition - start).IsWithin(FixedQ4816.FromDouble(0.25)));
        fixture.Step();
        Assert.Equal(1, reports.Pins);
        Assert.True((body.FixedPosition - start).IsWithin(FixedQ4816.FromDouble(0.25)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CarriedNeighbourBlocksUseTheCarriersOwnRemainingBudget(bool retreat, bool ownershipFace) {
        var basis = Puck.World.Testing.CarryFixtures.WallCarryDocument(includeWall: false);
        var seam = Document();
        var carrierKit = basis.Kits[0] with { Collider = new WorldCollider.Sphere(0.1f),
            Carry = basis.Kits[0].Carry! with { Offset = new Vector3(0, 1, ownershipFace ? -0.6f : 0.6f) } };
        using var fixture = Fixtures.FreshServer(definition: basis with {
            KitRowsRaw = [carrierKit, basis.Kits[1]], GravityRaw = WorldGravity.Default,
            CollisionRaw = seam.Collision,
            References = seam.References, Destinations = seam.Destinations, Adjacencies = seam.Adjacencies,
        });
        var carrier = Seat(fixture, ownershipFace ? (float)(double)Face(fixture).Threshold - 0.01f : -0.1f);
        var ballIndex = WorldBodiesLimits.LocalSeatCount;
        var ball = fixture.Server.Body(ballIndex)!;
        ball.Pose(carrier.FixedPosition + FixedVector3.UnitY, FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        Assert.True(fixture.Server.Population.TryBeginCarry(0, ballIndex, out var reason), reason);
        var response = new ResponseField { Push = (retreat || ownershipFace) ? FixedVector3.UnitZ : FixedVector3.UnitX };
        var field = Ready(fixture, carrier, response, out _, depth: 1f);
        if (retreat) { carrier.SetArrivalVelocity(-FixedVector3.UnitZ, FixedQ4816.Zero); }
        else if (!ownershipFace) {
            var position = carrier.FixedPosition;
            var velocity = FixedVector3.Zero;
            _ = Resolve(field, carrier, ref position, ref velocity);
            // Spend the carrier's allowance, without the test itself moving the body.
        }
        var before = carrier.FixedPosition;
        var calls = response.Calls;
        fixture.Server.Population.UpdateCarriedBodies();
        Assert.True(response.Calls > calls, "the carried sweep must consult the neighbour");
        Assert.NotEqual(before + FixedVector3.FromVector3(carrierKit.Carry!.Offset), ball.FixedPosition);
        if (ownershipFace) { Assert.True(Face(fixture).Owns(carrier.FixedPosition)); }
        else { Assert.Equal(before, carrier.FixedPosition); }
        if (retreat) { Assert.Equal(-FixedVector3.UnitZ, carrier.ApproximateWorldVelocity()); }
    }
}
