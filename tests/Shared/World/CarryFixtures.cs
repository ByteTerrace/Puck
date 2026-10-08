using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;


namespace Puck.World.Testing;

/// <summary>The carry laws' wall-and-carrier document, shared by the suites that step it.</summary>
internal static class CarryFixtures {
    // A carrier (Carry facet only) and a rigid "ball" target, plus — unlike WorldCarryCommandLawTests' own fixture —
    // a solid field requirement and a wall placement the ball's own witness-sweep can actually collide with. The
    // carrier starts at the origin; its own carry offset (0, 1, -0.6) is what the ball rides at with no wall in the
    // way, so a wall placed further along -Z is what the sweep is exercised against.
    internal static WorldDefinition WallCarryDocument(bool includeWall, bool rigidCarrier = false, bool includeOtherBody = false) {
        var source = Fixtures.BuildDocument();
        var carrierKit = source.Kits[0] with {
            Carry = new WorldCarry(
            Offset: new Vector3(
                x: 0f,
                y: 1f,
                z: -0.6f
            ),
            MassEquivalent: 60f,
            MaxCarryFraction: 1f,
            MaxReach: 1.5f
        ),
            Collider = (rigidCarrier
            ? new WorldCollider.Sphere(Radius: 0.3f)
            : source.Kits[0].Collider),
            BodyContact = (rigidCarrier
            ? WorldBodyContactMode.Solid
            : source.Kits[0].BodyContact),
            Rigid = (rigidCarrier
            ? new WorldRigid(
                AngularDamping: 0f,
                Friction: 0f,
                LinearDamping: 0f,
                Mass: 1f,
                Restitution: 0f,
                RollingFriction: 0f
            )
            : source.Kits[0].Rigid),
        };
        var ballKit = source.Kits[0] with {
            Name = "ball",
            Collider = new WorldCollider.Sphere(Radius: 0.15f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(
            AngularDamping: 0f,
            Friction: 0.4f,
            LinearDamping: 0f,
            Mass: 0.3f,
            Restitution: 0.2f,
            RollingFriction: 0.1f
        ),
            Carry = null,
        };
        var ballShape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.15f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var ballDocument = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "carry-ball",
            Palette: null,
            Shapes: [ballShape],
            Frames: null
        );
        var ballCanonical = CreationCanonicalizer.Canonicalize(
            document: ballDocument,
            source: "carry-ball"
        );
        var ballCreation = new WorldPrototype(
            Id: "carry-ball",
            Document: ballCanonical.Document,
            HashRaw: ballCanonical.Hash
        );

        // The wall's own near face sits at Z = -1.5, well past the unobstructed carry offset (Z = -0.6) but well
        // short of where the carrier's own walk (below) would otherwise carry it.
        var wallShape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 3f,
                y: 3f,
                z: 0.3f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var wallDocument = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "wall",
            Palette: null,
            Shapes: [wallShape],
            Frames: null
        );
        var wallCanonical = CreationCanonicalizer.Canonicalize(
            document: wallDocument,
            source: "wall"
        );
        var wallCreation = new WorldPrototype(
            Id: "wall",
            Document: wallCanonical.Document,
            HashRaw: wallCanonical.Hash
        );

        return source with {
            KitRowsRaw = [carrierKit, ballKit],
            DefaultSeatKitRaw = carrierKit.Name,
            PopulationRaw = source.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + (includeOtherBody
            ? 2
            : 1)),
            },
            CollisionRaw = source.Collision with { Requirements = [WorldContactRequirement.SmoothUnionContact] },
            CreationsRaw = (includeWall
            ? [ballCreation, wallCreation]
            : [ballCreation]),
            PlacementRowsRaw = [
                new WorldPlacement(
                Id: "carry-ball-placement",
                PrototypeId: ballCreation.Id,
                Position: new DocumentVector3(value: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: -0.6f
                )),
                YawDegrees: 0f,
                Scale: 1f,
                Inhabit: new WorldPlacementInhabit(
                    Kit: "ball",
                    Look: null,
                    Source: IntentSource.Idle,
                    Distribution: WorldDistribution.Default
                )
            ),
                .. (includeOtherBody
            ? new[] {
                        new WorldPlacement(
                    Id: "other-ball-placement",
                    PrototypeId: ballCreation.Id,
                    Position: new DocumentVector3(value: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: -0.6f
                    )),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Inhabit: new WorldPlacementInhabit(
                        Kit: "ball",
                        Look: null,
                        Source: IntentSource.Idle,
                        Distribution: WorldDistribution.Default
                    )
                ),
                    }
            : []),
                .. (includeWall
            ? new[] {
                        new WorldPlacement(
                    Id: "wall-placement",
                    PrototypeId: wallCreation.Id,
                    Position: new DocumentVector3(value: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: -1.8f
                    )),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Solid: new WorldSolid(Margin: 0f)
                ),
                    }
            : []),
            ],
        };
    }
}
