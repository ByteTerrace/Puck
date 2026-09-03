using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Contract under test: under a constant up (a contact field without
/// <see cref="WorldContactRequirement.GradientDerivedUp"/>), a grounded body's orientation is yaw-only — the
/// quaternion's X and Z lanes stay exactly zero through every contact, an edge lip included — and when the support
/// under its capsule ends it leaves the ground state and falls under the authored gravity; a vertical face pushes
/// but never grounds. Under this fixture's vertical gravity, a lip's tilted contact normal must never become the
/// body's up axis.
/// <para>The fixture mirrors the shipped world where it matters: the walker capsule (endpoint (0,1,0), radius
/// 0.35), a 20x20x1 rounded solid box whose top sits at <c>y = -0.5</c> with edges at ±10, uniform gravity
/// (0, -46, 0), and <c>SmoothUnionContact</c> alone. The body walks toward the -Z edge, releases the stick once its
/// centre is past the lip with the capsule's base still on it, and must fall clean.</para>
/// </summary>
public sealed class ConstantUpLipContactLawTests {
    private const int ForwardOrdinal = 0;
    // Where the stick releases: the body centre past the lip's rounded corner (the compiled box's top reaches
    // z = -10.4, its rounding included), so the foot sphere has already cleared its support. The lip contact itself is
    // crossed under a held stick on the way here — the tilted corner normal the body must not adopt.
    private static readonly FixedQ4816 ReleaseZ = FixedQ4816.FromDouble(value: -10.6);
    // Long enough for a body that left the lip at tick ~50 to fall many units (terminal 40 u/s) — and for a body
    // that has not left it to prove so.
    private const int TotalTicks = 600;
    private const int SettleTicks = 30;
    // After the release, how many ticks the body may take to lose ground contact before the law reads it as pinned.
    private const int AirborneWithinTicks = 120;

    [Fact]
    public void GroundedBodyUnderConstantUp_StaysYawOnlyAcrossTheLip_AndFalls() {
        using var fixture = Fixtures.FreshServer(definition: PlatformDocument());
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        // Facing -Z (yaw 0 under the heading move frame), 0.7 units short of the -Z edge, foot on the platform top.
        body.Pose(x: 0.3f, y: -0.5f, z: -9.3f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
            AssertYawOnly(body: body, tick: tick);
        }

        Assert.True(condition: body.Grounded, userMessage: $"the body did not settle grounded on the platform top; y={body.Position.Y:0.###}");

        var releasedAt = -1;
        var airborneAt = -1;
        var previousY = body.FixedPosition.Y;

        for (var tick = SettleTicks; (tick < TotalTicks); tick++) {
            if (
                (releasedAt < 0) &&
                (body.FixedPosition.Z <= ReleaseZ)
            ) {
                releasedAt = tick;
            }

            body.SubmitIntent(intent: ((releasedAt < 0)
                ? default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One)
                : default
            ));
            fixture.Step();
            AssertYawOnly(body: body, tick: tick);

            if (releasedAt < 0) {
                continue;
            }

            if (airborneAt < 0) {
                if (!body.Grounded) {
                    airborneAt = tick;
                }

                Assert.True(condition: ((tick - releasedAt) <= AirborneWithinTicks), userMessage: $"the body stayed grounded {AirborneWithinTicks} ticks past the lip — pinned at ({body.Position.X:0.##}, {body.Position.Y:0.##}, {body.Position.Z:0.##}) instead of falling");
            } else {
                Assert.False(condition: body.Grounded, userMessage: $"tick {tick}: the body re-grounded mid-fall at ({body.Position.X:0.##}, {body.Position.Y:0.##}, {body.Position.Z:0.##}) — a vertical face may push but never grounds");
                Assert.True(condition: (body.FixedPosition.Y < previousY), userMessage: $"tick {tick}: y={body.Position.Y:0.###} did not descend from {((double)previousY):0.###} while falling");
            }

            previousY = body.FixedPosition.Y;
        }

        Assert.True(condition: (releasedAt >= 0), userMessage: $"the walk never reached the release point z<={((double)ReleaseZ):0.##}; final z={body.Position.Z:0.###}");
        Assert.True(condition: (airborneAt >= 0), userMessage: "the body never left the ground state after the lip");
        Assert.True(condition: (body.Position.Y < -5f), userMessage: $"the body fell only to y={body.Position.Y:0.###} in {(TotalTicks - airborneAt)} airborne ticks");
    }

    // A pure yaw about world +Y has exactly zero X and Z quaternion lanes — the raw fixed lanes, the same values the
    // replay pose hash covers, never a float decomposition with a tolerance.
    private static void AssertYawOnly(WorldBody body, int tick) {
        var orientation = body.FixedOrientation;

        Assert.True(
            condition: ((orientation.X == FixedQ4816.Zero) && (orientation.Z == FixedQ4816.Zero)),
            userMessage: $"tick {tick}: orientation left yaw-only — raw lanes W={orientation.W.Value} X={orientation.X.Value} Y={orientation.Y.Value} Z={orientation.Z.Value} at ({body.Position.X:0.##}, {body.Position.Y:0.##}, {body.Position.Z:0.##})"
        );
    }

    // The shipped world's platform and walker, on the suite's shared capsule-bearing core: the one arm the
    // shipped configuration exercises is SmoothUnionContact without GradientDerivedUp over a uniform gravity field.
    private static WorldDefinition PlatformDocument() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var shape = new ShapeDocument(
            Id: 0,
            Name: "platform",
            Type: SdfSolidPrimitive.Box,
            Position: new Vector3(x: 0f, y: -1f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 10f, y: 0.5f, z: 10f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "platform",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "platform");
        var creation = new WorldPrototype(Id: "platform", Document: canonical.Document, HashRaw: canonical.Hash);

        return source with {
            GravityRaw = new WorldGravity(
                Attractors: [],
                GravitationalConstant: 0f,
                SofteningLength: 0.5f,
                Solver: WorldGravitySolver.Pairwise,
                Uniform: new DocumentVector3(x: 0f, y: -46f, z: 0f)
            ),
            KitRowsRaw = source.Kits.Select(selector: kit => kit with {
                Motion = kit.Motion with {
                    Speed = kit.Motion.Speed with { Value = 4f },
                    Holds = [
                        kit.Motion.Holds![0] with { Gravity = new WorldHoldGravity(Fall: 46f, Rise: 28f, Terminal: 40f) },
                    ],
                },
            }).ToArray(),
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "platform", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f))],
        };
    }
}
