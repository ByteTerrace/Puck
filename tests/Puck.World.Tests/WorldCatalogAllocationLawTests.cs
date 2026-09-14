using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldCatalogAllocationLawTests {
    private static SdfProgram Emit(int capacity, int rig, Func<int, bool> active, bool probe = false, int? limit = null, float scale = 1f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var materials = Enumerable.Repeat(
            count: capacity,
            element: material
        ).ToArray();

        WorldRigCatalog.Emit(
            builder,
            active,
            materials,
            materials,
            probe,
            0,
            _ => rig,
            _ => scale,
            probeAvatarLimit: limit
        );
        return builder.Build();
    }
    // An independent enclosing sphere from the emitted primitive parameters, not the catalog's bound constants.
    private static float ShapeRadius(SdfInstruction instruction) {
        var dimensions = instruction.Data0;

        return ((SdfShapeType)instruction.Shape) switch {
            SdfShapeType.Box => (new Vector3(
            x: dimensions.X,
            y: dimensions.Y,
            z: dimensions.Z
        ).Length() + dimensions.W),
            SdfShapeType.Capsule => (new Vector3(
            x: dimensions.X,
            y: dimensions.Y,
            z: dimensions.Z
        ).Length() + dimensions.W),
            SdfShapeType.Cylinder => MathF.Sqrt(x: ((dimensions.X * dimensions.X) + (dimensions.Y * dimensions.Y))),
            SdfShapeType.Sphere => dimensions.X,
            _ => throw new InvalidOperationException(message: $"No independent bound for catalog shape {instruction.Shape}."),
        };
    }

    [Fact]
    public void ACarriedLookRendersAllItsLeavesRegardlessOfTheDestinationSlot() {
        var rigs = Enumerable.Range(
            count: WorldLookSource.Catalog.RigCount,
            start: 0
        ).ToArray();
        var smallest = rigs.MinBy(keySelector: WorldRigCatalog.InstructionCount);
        var largest = rigs.MaxBy(keySelector: WorldRigCatalog.InstructionCount);

        Assert.True(condition: (WorldRigCatalog.InstructionCount(avatar: largest) > WorldRigCatalog.InstructionCount(avatar: smallest)));
        var native = Emit(
            WorldBodiesLimits.CapacityCeiling,
            largest,
            index => (index == largest)
        );
        var moved = Emit(
            WorldBodiesLimits.CapacityCeiling,
            largest,
            index => (index == smallest)
        );

        Assert.Equal(
            native.Instances.Count,
            moved.Instances.Count
        );
        Assert.Equal(
            native.Instructions.Count,
            moved.Instructions.Count
        );
        var smallAtLargeSlot = Emit(
            WorldBodiesLimits.CapacityCeiling,
            smallest,
            index => (index == largest)
        );

        Assert.Equal(
            WorldRigCatalog.InstructionCount(avatar: smallest),
            smallAtLargeSlot.Instructions.Count
        );
    }
    [Fact]
    public void CoarseCullBoundEnclosesItsGroundedCapsuleThroughAnyRootOrientation() {
        var body = (WorldBodiesLimits.CapacityCeiling - 1);
        var program = Emit(
            WorldBodiesLimits.CapacityCeiling,
            0,
            index => (index == body),
            scale: 1.7f
        );
        var instance = Assert.Single(collection: program.Instances);
        var instructions = program.Instructions
            .Skip(count: instance.First)
            .Take(count: (instance.End - instance.First))
            .ToArray();
        var translation = instructions.Single(predicate: instruction => (instruction.Op == SdfOp.Translate)).Data0;
        var capsule = instructions.Single(predicate: instruction => (instruction.Shape == ((byte)SdfShapeType.Capsule)));
        var required = ((new Vector3(
            x: translation.X,
            y: translation.Y,
            z: translation.Z
        ).Length()
            + new Vector3(
            x: capsule.Data0.X,
            y: capsule.Data0.Y,
            z: capsule.Data0.Z
        ).Length())
            + capsule.Data0.W);

        Assert.True(
            condition: (required <= (instance.Radius + 0.0001f)),
            userMessage: $"Coarse capsule reaches {required} from its root but its cull bound is only {instance.Radius}."
        );
        Assert.Equal(
            0f,
            (translation.Y - capsule.Data0.W),
            precision: 5
        );
    }
    [Fact]
    public void FreshAppearanceSelectionRemainsValidAcrossThousandsOfDistinctBodySlots() {
        for (var index = 0; (index < 4096); index++) {
            var selected = WorldLookSource.Catalog.DefaultIndex(entityIndex: index);

            Assert.InRange(
                actual: selected,
                high: (WorldLookSource.Catalog.RigCount - 1),
                low: 0
            );
            Assert.Equal(
                selected,
                WorldLookSource.Catalog.DefaultIndex(entityIndex: index)
            );
            if (index < WorldLookSource.Catalog.RigCount) { Assert.Equal(
                actual: selected,
                expected: index
            ); }
        }
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => WorldLookSource.Catalog.DefaultIndex(entityIndex: -1));
    }
    [InlineData(0.001f)]
    [InlineData(0.1f)]
    [InlineData(1f)]
    [InlineData(16f)]
    [Theory]
    public void LeafCullBoundsContainEveryAnimatedShapeAcrossTheCatalog(float scale) {
        var transforms = new DynamicTransform[WorldRigCatalog.TransformSlotsPerBody];
        var root = new Vector3(
            x: 11,
            y: -7,
            z: 19
        );
        var orientation = Quaternion.CreateFromYawPitchRoll(
            pitch: 0.89f,
            roll: -1.2f,
            yaw: 0.67f
        );

        for (var rig = 0; (rig < WorldLookSource.Catalog.RigCount); rig++) {
            var program = Emit(
                1,
                rig,
                _ => true,
                scale: scale
            );

            for (var phase = 0; (phase < 16); phase++) {
                WorldRigCatalog.PackTransforms(
                    avatar: 0,
                    castsSoftShadow: true,
                    gaitPhase: ((phase * MathF.Tau) / 16),
                    rig: rig,
                    rootOrientation: orientation,
                    rootPosition: root,
                    scale: scale,
                    transforms: transforms
                );
                foreach (var instance in program.Instances) {
                    var boneCenter = (transforms[instance.Slot].Position + instance.Center);
                    var slot = -1;
                    var localOffset = Vector3.Zero;

                    for (var index = instance.First; (index < instance.End); index++) {
                        var instruction = program.Instructions[index];

                        switch (instruction.Op) {
                            case SdfOp.ResetPoint: localOffset = Vector3.Zero; break;
                            case SdfOp.TransformDynamic: slot = ((int)instruction.Data0.X); break;
                            case SdfOp.Translate: localOffset += new Vector3(
                                x: instruction.Data0.X,
                                y: instruction.Data0.Y,
                                z: instruction.Data0.Z
                            ); break;
                            case SdfOp.ShapeBlend:
                                var shapeCenter = (transforms[slot].Position + Vector3.Transform(
                                    localOffset,
                                    transforms[slot].Orientation
                                ));
                                var reach = (Vector3.Distance(
                                    value1: boneCenter,
                                    value2: shapeCenter
                                ) + ShapeRadius(instruction: instruction));
                                Assert.True(
                                    condition: (reach <= (instance.Radius + 0.0001f)),
                                    userMessage: $"Rig {rig}, scale {scale}, phase {phase}, shape {instruction.Shape}: reach {reach} exceeds bound {instance.Radius}."
                                );
                                Assert.True(
                                    condition: (instance.Radius <= ((ShapeRadius(instruction: instruction) + localOffset.Length()) + 0.0001f)),
                                    userMessage: "A primitive's cull bound must not regress to the largest catalog shape's radius."
                                );
                                break;
                        }
                    }
                }
            }
        }
    }
    [Fact]
    public void MismatchedMaterialTablesAreRefusedBeforeEmission() {
        var builder = new SdfProgramBuilder();

        builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        Assert.Throws<ArgumentException>(testCode: () => WorldRigCatalog.Emit(
            builder,
            _ => true,
            new int[2],
            new int[1],
            false,
            0
        ));
        WorldRigCatalog.Emit(
            builder,
            _ => true,
            new int[1],
            new int[1],
            false,
            0
        );
        Assert.Equal(
            WorldRigCatalog.InstructionCount(avatar: 0),
            builder.Build().Instructions.Count
        );
    }
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(128)]
    [Theory]
    public void ProbeCoversRepeatedLargestLooksNotJustADistinctCatalogSubset(int count) {
        var largest = Enumerable.Range(
            count: WorldLookSource.Catalog.RigCount,
            start: 0
        ).MaxBy(keySelector: WorldRigCatalog.InstructionCount);
        var probe = Emit(
            WorldBodiesLimits.CapacityCeiling,
            largest,
            _ => false,
            true,
            count
        );
        var live = Emit(
            WorldBodiesLimits.CapacityCeiling,
            largest,
            index => (index >= (WorldBodiesLimits.CapacityCeiling - count))
        );

        Assert.True(condition: (probe.Instances.Count >= live.Instances.Count));
        Assert.True(condition: (probe.Words.Length >= live.Words.Length));
    }
    [Fact]
    public void RenderSlotsAndPartPosesDoNotStopAtTheAppearanceCatalogBoundary() {
        const int LastBody = 4095;
        const int Rig = 7;

        Assert.True(condition: WorldRigCatalog.TryPartTransformSlot(
            avatar: LastBody,
            partId: "pelvis",
            transformSlot: out var first
        ));
        var transforms = new DynamicTransform[WorldRigCatalog.DynamicTransformCapacity];
        var origin = new Vector3(
            x: 1,
            y: 2,
            z: 3
        );
        var orientation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitY,
            0.4f
        );

        WorldRigCatalog.PackTransforms(
            avatar: 0,
            castsSoftShadow: true,
            gaitPhase: 0.7f,
            rig: Rig,
            rootOrientation: orientation,
            rootPosition: origin,
            scale: 1.5f,
            transforms: transforms
        );
        Assert.True(condition: WorldRigCatalog.TryPartPose(
            0,
            "head",
            Rig,
            transforms.AsSpan(),
            out var low,
            1.5f
        ));
        WorldRigCatalog.PackTransforms(
            avatar: LastBody,
            castsSoftShadow: true,
            gaitPhase: 0.7f,
            rig: Rig,
            rootOrientation: orientation,
            rootPosition: origin,
            scale: 1.5f,
            transforms: transforms
        );
        Assert.True(condition: WorldRigCatalog.TryPartPose(
            LastBody,
            "head",
            Rig,
            transforms.AsSpan(),
            out var high,
            1.5f
        ));
        Assert.Equal(
            actual: high,
            expected: low
        );
        var program = Emit(
            (LastBody + 1),
            Rig,
            index => (index == LastBody)
        );

        Assert.Equal(
            4,
            program.Instructions.Count
        );
        Assert.Single(collection: program.Instances);
        Assert.All(
            program.Instances,
            instance => Assert.InRange(
                instance.Slot,
                first,
                (transforms.Length - 1)
            )
        );
        var work = WorldRigCatalog.ActiveWorkload(
            capacity: (LastBody + 1),
            isActive: index => (index == LastBody),
            rigFor: _ => Rig
        );

        Assert.Equal(
            program.Instructions.Count,
            work.Instructions
        );
        Assert.Equal(
            program.Instances.Count,
            work.Instances
        );
    }
    [Fact]
    public void ReservedPopulationFitsTheInstanceFormatWithoutDroppingParts() {
        const int Population = WorldBodiesLimits.CapacityCeiling;
        var largest = Enumerable.Range(
            count: WorldLookSource.Catalog.RigCount,
            start: 0
        ).MaxBy(keySelector: WorldRigCatalog.InstructionCount);
        var program = Emit(
            Population,
            largest,
            _ => true
        );
        var detailed = Math.Min(
            val1: Population,
            val2: WorldRigCatalog.DetailedAvatarCapacity
        );
        var coarse = (Population - detailed);

        Assert.Equal(
            ((detailed * WorldRigCatalog.MaxInstancesPerAvatar) + coarse),
            program.Instances.Count
        );
        Assert.Equal(
            WorldRigCatalog.InstructionCapacity,
            program.Instructions.Count
        );
        Assert.True(condition: (program.Instances.Count <= SdfProgramBuilder.MaxInstances));
        Assert.Equal(
            WorldRigCatalog.DynamicTransformCapacity,
            program.RequiredDynamicTransformCapacity
        );
        for (var index = 0; (index < program.Instances.Count); index++) {
            var instance = program.Instances[index];

            Assert.Equal(
                ((index < (detailed * WorldRigCatalog.MaxInstancesPerAvatar))
                ? 5
                : 4),
                (instance.End - instance.First)
            );
        }
    }
}
