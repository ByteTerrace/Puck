using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a driver's layer sets only shares, never the order turns compose in on a shape, and the seam bound
/// (<see cref="CreationMotion.Envelope"/>) holds every pose the rig can play. A stance clip and a sway swing on one
/// shoulder give the same pose whichever driver is declared first, at equal shares; and over random rigs of clips on
/// adding and replacing drivers, swings and keyed slides, no sampled weight and phase turns a seam or moves its box
/// past the envelope it was admitted under.
/// </summary>
public sealed class CreationLayerLawTests {
    private const float Degree = (MathF.PI / 180f);

    [Fact]
    public void ATurnsOrderOnAShapeIsTheShapesWhicheverDriverIsDeclaredFirst() {
        var shoulder = Shape(id: 0, name: "shoulder", joint: new Vector3(x: 0.2f, y: 1.4f, z: 0f)) with {
            Swings = [new ShapeSwingDocument(Amplitude: 0.3f, Axis: Vector3.UnitY, Driver: "sway", Pivot: new Vector3(x: 0.2f, y: 1.4f, z: 0f))],
        };
        var stance = new CreationClipDocument(Driver: "stance", Keys: [new CreationClipKeyDocument(At: 0f, Poses: [new CreationClipPoseDocument(Shape: "shoulder", Turn: [new CreationClipTurnDocument(Angle: (40f * Degree), Axis: Vector3.UnitX)])])], Name: "stance");
        var sway = new CreationDriverDocument(Cadence: 1f, Name: "sway", Signal: CreationDriverDocument.SignalTime);
        var hold = new CreationDriverDocument(Cadence: 1f, Mode: CreationDriverDocument.ModeReplace, Name: "stance", Parts: ["arm"], Signal: CreationDriverDocument.SignalSpeed);
        var parts = new[] { new CreationPartDocument(Id: "arm", ShapeId: 0) };
        var swayFirst = CreationRig.Compile(document: Document(clips: [stance], drivers: [sway, hold], shapes: [shoulder]) with { Parts = parts });
        var stanceFirst = CreationRig.Compile(document: Document(clips: [stance], drivers: [hold, sway], shapes: [shoulder]) with { Parts = parts });
        var generator = Pcg32XshRr.Create(state: 1913, stream: 5);

        float Unit() => (generator.NextUInt32() / 4294967296f);

        for (var sample = 0; (sample < 2000); sample++) {
            var stanceWeight = Unit();
            var swayLevel = Unit();
            var swayPhase = (Unit() * MathF.Tau);
            // Declared first, the sway is reduced by the stance; declared last, it is not. Its weight there is the share
            // it has when declared first, formed by the same multiplication, so the two shares are the same bits.
            var swayShare = (swayLevel * (1f - stanceWeight));

            WorldGaitDrivers.ComposeDelta(phases: [swayPhase, 0f], rig: swayFirst, rotation: out var firstRotation, shape: 0, translation: out var firstTranslation, weights: [swayLevel, stanceWeight]);
            WorldGaitDrivers.ComposeDelta(phases: [0f, swayPhase], rig: stanceFirst, rotation: out var lastRotation, shape: 0, translation: out var lastTranslation, weights: [stanceWeight, swayShare]);

            Assert.Equal(expected: swayShare, actual: swayFirst.Share(driver: 0, shape: 0, weights: [swayLevel, stanceWeight]));
            Assert.Equal(expected: firstRotation, actual: lastRotation);
            Assert.Equal(expected: firstTranslation, actual: lastTranslation);
        }

        // The control: the two turns do not commute, so an order taken from the drivers would change the pose.
        var swing = Quaternion.CreateFromAxisAngle(angle: 0.3f, axis: Vector3.UnitY);
        var key = Quaternion.CreateFromAxisAngle(angle: (40f * Degree), axis: Vector3.UnitX);

        Assert.True(condition: (VectorFunctions.TurnAngle(turn: (Quaternion.Conjugate(value: (key * swing)) * (swing * key))) > 0.01f));
    }
    [Fact]
    public void NoSampledPoseOfARandomRigPassesItsSeamBound() {
        var generator = Pcg32XshRr.Create(state: 2026, stream: 11);
        var checkedPoses = 0;

        float Unit() => (generator.NextUInt32() / 4294967296f);
        float Signed() => ((2f * Unit()) - 1f);
        Vector3 Axis() => VectorFunctions.NormalizeOr(fallback: Vector3.UnitX, value: new Vector3(x: Signed(), y: Signed(), z: Signed()));

        for (var rigIndex = 0; (rigIndex < 300); rigIndex++) {
            var driverCount = (1 + (int)(Unit() * 4f));
            var drivers = new List<CreationDriverDocument>();

            for (var driver = 0; (driver < driverCount); driver++) {
                drivers.Add(item: new CreationDriverDocument(Cadence: 1f, Mode: ((Unit() < 0.5f) ? CreationDriverDocument.ModeReplace : null), Name: $"d{driver}", Parts: ["limb"], Signal: CreationDriverDocument.SignalSpeed));
            }

            var joint = new Vector3(x: (0.02f * Signed()), y: 0.1f, z: (0.02f * Signed()));
            var carrier = (Unit() < 0.5f);
            var shapes = new List<ShapeDocument> {
                Shape(id: 0, name: "anchor", type: SdfSolidPrimitive.Box, scale: new Vector3(value: 0.2f), group: 1),
            };

            if (carrier) {
                shapes.Add(item: Shape(id: 1, name: "carrier", joint: new Vector3(x: 0f, y: 0.05f, z: 0f), parent: "anchor", position: new Vector3(x: 0f, y: 0.05f, z: 0f), scale: new Vector3(value: 0.01f)));
            }
            shapes.Add(item: Shape(blend: SdfBlendOp.SmoothUnion, group: 1, id: shapes.Count, joint: joint, name: "limb", parent: (carrier ? "carrier" : "anchor"), position: (joint + new Vector3(x: 0f, y: -0.01f, z: 0f)), scale: new Vector3(value: (0.002f + (0.01f * Unit())))) with {
                Smooth = (0.01f + (0.03f * Unit())),
                Swings = ((Unit() < 0.4f) ? [new ShapeSwingDocument(Amplitude: (0.3f * Unit()), Axis: Axis(), Driver: "d0", Pivot: (joint + new Vector3(x: 0f, y: 0.02f, z: 0f)))] : null),
            });

            var clips = new List<CreationClipDocument>();
            var clipCount = (1 + (int)(Unit() * 5f));
            var posed = (carrier ? new[] { "limb", "carrier" } : ["limb"]);

            for (var clip = 0; (clip < clipCount); clip++) {
                var keys = new List<CreationClipKeyDocument>();
                var keyCount = (1 + (int)(Unit() * 3f));
                var shape = posed[(int)(Unit() * posed.Length)];

                for (var key = 0; (key < keyCount); key++) {
                    keys.Add(item: new CreationClipKeyDocument(At: key, Ease: ((Unit() < 0.5f) ? CreationClipDocument.EaseSmooth : null), Poses: [new CreationClipPoseDocument(Shape: shape, Slide: ((Unit() < 0.3f) ? (Axis() * (0.004f * Unit())) : null), Turn: [new CreationClipTurnDocument(Angle: (Signed() * (35f * Degree)), Axis: Axis())])]));
                }
                clips.Add(item: new CreationClipDocument(Driver: $"d{(int)(Unit() * driverCount)}", Keys: keys, Name: $"c{clip}"));
            }

            var document = Document(clips: clips, drivers: drivers, shapes: shapes) with {
                Parts = [new CreationPartDocument(Id: "limb", ShapeId: shapes[^1].Id, Shapes: posed)],
            };

            Assert.Empty(collection: CreationCanonicalizer.Validate(document: document));

            var rig = CreationRig.Compile(document: document);
            var blend = Assert.Single(collection: CreationMotion.MovingBlends(rig: rig));
            var envelope = CreationMotion.Envelope(blend: blend, rig: rig);

            Assert.Null(@object: envelope.Refusal);
            Assert.True(condition: (envelope.Rotation > 0f), userMessage: $"rig {rigIndex}'s seam has no motion to bound");

            var k = (shapes[^1].Smooth ?? 0f);
            var identity = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null);
            var a = CreationStampEmitter.ShapeStampBox(document: rig.Document, shapeIndex: blend.Shape, transform: identity)!.Value;
            var b = CreationStampEmitter.ShapeStampBox(document: rig.Document, shapeIndex: blend.Other, transform: identity)!.Value;
            var low = (Vector3.Max(value1: a.Low, value2: b.Low) - new Vector3(value: k));
            var high = (Vector3.Min(value1: a.High, value2: b.High) + new Vector3(value: k));
            var phases = new float[driverCount];
            var weights = new float[driverCount];

            for (var sample = 0; (sample < 64); sample++) {
                for (var driver = 0; (driver < driverCount); driver++) {
                    phases[driver] = (Unit() * 2.5f) - 0.25f;
                    weights[driver] = ((Unit() < 0.2f) ? 1f : Unit());
                }

                var limb = (shapes.Count - 1);

                WorldGaitDrivers.ComposeDelta(phases: phases, rig: rig, rotation: out var rotation, shape: limb, translation: out var translation, weights: weights);
                if (carrier) {
                    WorldGaitDrivers.ComposeDelta(phases: phases, rig: rig, rotation: out var carrierRotation, shape: 1, translation: out var carrierTranslation, weights: weights);
                    WorldGaitDrivers.Chain(parentRotation: carrierRotation, parentTranslation: carrierTranslation, rotation: ref rotation, translation: ref translation);
                }

                var moved = 0f;

                for (var corner = 0; (corner < 8); corner++) {
                    var point = new Vector3(x: (((corner & 1) == 0) ? low.X : high.X), y: (((corner & 2) == 0) ? low.Y : high.Y), z: (((corner & 4) == 0) ? low.Z : high.Z));

                    moved = MathF.Max(x: moved, y: Vector3.Distance(value1: (Vector3.Transform(rotation: rotation, value: point) + translation), value2: point));
                }

                Assert.True(condition: (VectorFunctions.TurnAngle(turn: rotation) <= (envelope.Rotation + 1e-4f)), userMessage: $"rig {rigIndex} sample {sample} turns {VectorFunctions.TurnAngle(turn: rotation)} past its bound {envelope.Rotation}");
                Assert.True(condition: (moved <= ((envelope.Displacement * (1f + 1e-4f)) + 1e-6f)), userMessage: $"rig {rigIndex} sample {sample} moves its seam box {moved} past its bound {envelope.Displacement}");
                checkedPoses++;
            }
        }

        Assert.Equal(expected: (300 * 64), actual: checkedPoses);
    }

    private static CreationDocument Document(IReadOnlyList<ShapeDocument> shapes, IReadOnlyList<CreationDriverDocument> drivers, IReadOnlyList<CreationClipDocument> clips) =>
        new(Clips: clips, Drivers: drivers, Frames: null, Name: "layers", Palette: [new PaletteEntryDocument(Color: "#808080", Emissive: null, Roughness: null, Specular: null)], Schema: CreationDocument.CurrentSchema, Shapes: shapes);
    private static ShapeDocument Shape(int id, string name, Vector3? joint = null, string? parent = null, Vector3? position = null, Vector3? scale = null, SdfSolidPrimitive type = SdfSolidPrimitive.Sphere, SdfBlendOp blend = SdfBlendOp.Union, int? group = null) =>
        new(Blend: blend, Group: group, Id: id, Joint: joint, Material: 0, Name: name, Parent: parent, Position: (position ?? Vector3.Zero), Rotation: Quaternion.Identity, Scale: (scale ?? new Vector3(value: 0.05f)), Smooth: null, Type: type);
}
