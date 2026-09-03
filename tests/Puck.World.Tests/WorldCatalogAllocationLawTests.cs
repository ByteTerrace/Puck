using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldCatalogAllocationLawTests {
    private static SdfProgram Emit(int capacity, int rig, Func<int, bool> active, bool probe = false, int? limit = null, float scale = 1f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        var materials = Enumerable.Repeat(material, capacity).ToArray();
        WorldRigCatalog.Emit(builder, active, materials, materials, probe, 0, _ => rig, _ => scale, probeAvatarLimit: limit);
        return builder.Build();
    }

    [Fact]
    public void ACarriedLookRendersAllItsLeavesRegardlessOfTheDestinationSlot() {
        var rigs = Enumerable.Range(0, WorldLookSource.Catalog.RigCount).ToArray();
        var smallest = rigs.MinBy(WorldRigCatalog.InstructionCount);
        var largest = rigs.MaxBy(WorldRigCatalog.InstructionCount);
        Assert.True(WorldRigCatalog.InstructionCount(largest) > WorldRigCatalog.InstructionCount(smallest));
        var native = Emit(WorldBodiesLimits.CapacityCeiling, largest, index => index == largest);
        var moved = Emit(WorldBodiesLimits.CapacityCeiling, largest, index => index == smallest);
        Assert.Equal(native.Instances.Count, moved.Instances.Count);
        Assert.Equal(native.Instructions.Count, moved.Instructions.Count);
        var smallAtLargeSlot = Emit(WorldBodiesLimits.CapacityCeiling, smallest, index => index == largest);
        Assert.Equal(WorldRigCatalog.InstructionCount(smallest), smallAtLargeSlot.Instructions.Count);
    }

    [Fact]
    public void RenderSlotsAndPartPosesDoNotStopAtTheAppearanceCatalogBoundary() {
        const int lastBody = 4095;
        const int rig = 7;
        Assert.True(WorldRigCatalog.TryPartTransformSlot(lastBody, "pelvis", out var first));
        var transforms = new DynamicTransform[WorldRigCatalog.DynamicTransformCapacity];
        var origin = new Vector3(1, 2, 3);
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
        WorldRigCatalog.PackTransforms(0, origin, orientation, 0.7f, true, transforms, rig, 1.5f);
        Assert.True(WorldRigCatalog.TryPartPose(0, "head", rig, transforms.AsSpan(), out var low, 1.5f));
        WorldRigCatalog.PackTransforms(lastBody, origin, orientation, 0.7f, true, transforms, rig, 1.5f);
        Assert.True(WorldRigCatalog.TryPartPose(lastBody, "head", rig, transforms.AsSpan(), out var high, 1.5f));
        Assert.Equal(low, high);
        Assert.True(WorldRigCatalog.TryPartPose(lastBody, "head", rig, (IReadOnlyList<DynamicTransform>)transforms, out var listPose, 1.5f));
        Assert.Equal(high, listPose);
        var program = Emit(lastBody + 1, rig, index => index == lastBody);
        Assert.Equal(4, program.Instructions.Count);
        Assert.Single(program.Instances);
        Assert.All(program.Instances, instance => Assert.InRange(instance.Slot, first, transforms.Length - 1));
        var work = WorldRigCatalog.ActiveWorkload(index => index == lastBody, lastBody + 1, _ => rig);
        Assert.Equal(program.Instructions.Count, work.Instructions);
        Assert.Equal(program.Instances.Count, work.Instances);
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(128)]
    public void ProbeCoversRepeatedLargestLooksNotJustADistinctCatalogSubset(int count) {
        var largest = Enumerable.Range(0, WorldLookSource.Catalog.RigCount).MaxBy(WorldRigCatalog.InstructionCount);
        var probe = Emit(WorldBodiesLimits.CapacityCeiling, largest, _ => false, true, count);
        var live = Emit(WorldBodiesLimits.CapacityCeiling, largest, index => index >= WorldBodiesLimits.CapacityCeiling - count);
        Assert.True(probe.Instances.Count >= live.Instances.Count);
        Assert.True(probe.Words.Length >= live.Words.Length);
    }

    [Fact]
    public void FreshAppearanceSelectionRemainsValidAcrossThousandsOfDistinctBodySlots() {
        for (var index = 0; index < 4096; index++) {
            var selected = WorldLookSource.Catalog.DefaultIndex(index);
            Assert.InRange(selected, 0, WorldLookSource.Catalog.RigCount - 1);
            Assert.Equal(selected, WorldLookSource.Catalog.DefaultIndex(index));
            if (index < WorldLookSource.Catalog.RigCount) { Assert.Equal(index, selected); }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldLookSource.Catalog.DefaultIndex(-1));
    }

    [Fact]
    public void ReservedPopulationFitsTheInstanceFormatWithoutDroppingParts() {
        const int population = WorldBodiesLimits.CapacityCeiling;
        var largest = Enumerable.Range(0, WorldLookSource.Catalog.RigCount).MaxBy(WorldRigCatalog.InstructionCount);
        var program = Emit(population, largest, _ => true);
        var detailed = Math.Min(population, WorldRigCatalog.DetailedAvatarCapacity);
        var coarse = population - detailed;
        Assert.Equal(detailed * WorldRigCatalog.MaxInstancesPerAvatar + coarse, program.Instances.Count);
        Assert.Equal(WorldRigCatalog.InstructionCapacity, program.Instructions.Count);
        Assert.True(program.Instances.Count <= SdfProgramBuilder.MaxInstances);
        Assert.Equal(WorldRigCatalog.DynamicTransformCapacity, program.RequiredDynamicTransformCapacity);
        for (var index = 0; index < program.Instances.Count; index++) {
            var instance = program.Instances[index];
            Assert.Equal(index < detailed * WorldRigCatalog.MaxInstancesPerAvatar ? 5 : 4, instance.End - instance.First);
        }
    }

    [Fact]
    public void CoarseCullBoundEnclosesItsGroundedCapsuleThroughAnyRootOrientation() {
        var body = WorldBodiesLimits.CapacityCeiling - 1;
        var program = Emit(WorldBodiesLimits.CapacityCeiling, 0, index => index == body, scale: 1.7f);
        var instance = Assert.Single(program.Instances);
        var instructions = program.Instructions
            .Skip(count: instance.First)
            .Take(count: instance.End - instance.First)
            .ToArray();
        var translation = instructions.Single(instruction => instruction.Op == SdfOp.Translate).Data0;
        var capsule = instructions.Single(instruction => instruction.Shape == (byte)SdfShapeType.Capsule);
        var required = new Vector3(translation.X, translation.Y, translation.Z).Length()
            + new Vector3(capsule.Data0.X, capsule.Data0.Y, capsule.Data0.Z).Length()
            + capsule.Data0.W;

        Assert.True(required <= instance.Radius + 0.0001f,
            $"Coarse capsule reaches {required} from its root but its cull bound is only {instance.Radius}.");
        Assert.Equal(0f, translation.Y - capsule.Data0.W, precision: 5);
    }

    [Theory]
    [InlineData(0.001f)] [InlineData(0.1f)] [InlineData(1f)] [InlineData(16f)]
    public void LeafCullBoundsContainEveryAnimatedShapeAcrossTheCatalog(float scale) {
        var transforms = new DynamicTransform[WorldRigCatalog.TransformSlotsPerBody];
        var root = new Vector3(11, -7, 19);
        var orientation = Quaternion.CreateFromYawPitchRoll(0.67f, 0.89f, -1.2f);
        for (var rig = 0; rig < WorldLookSource.Catalog.RigCount; rig++) {
            var program = Emit(1, rig, _ => true, scale: scale);
            for (var phase = 0; phase < 16; phase++) {
                WorldRigCatalog.PackTransforms(0, root, orientation, phase * MathF.Tau / 16, true, transforms, rig, scale);
                foreach (var instance in program.Instances) {
                    var boneCenter = transforms[instance.Slot].Position + instance.Center;
                    var slot = -1;
                    var localOffset = Vector3.Zero;
                    for (var index = instance.First; index < instance.End; index++) {
                        var instruction = program.Instructions[index];
                        switch (instruction.Op) {
                            case SdfOp.ResetPoint: localOffset = Vector3.Zero; break;
                            case SdfOp.TransformDynamic: slot = (int)instruction.Data0.X; break;
                            case SdfOp.Translate: localOffset += new Vector3(instruction.Data0.X, instruction.Data0.Y, instruction.Data0.Z); break;
                            case SdfOp.ShapeBlend:
                                var shapeCenter = transforms[slot].Position + Vector3.Transform(localOffset, transforms[slot].Orientation);
                                var reach = Vector3.Distance(boneCenter, shapeCenter) + ShapeRadius(instruction);
                                Assert.True(reach <= instance.Radius + 0.0001f, $"Rig {rig}, scale {scale}, phase {phase}, shape {instruction.Shape}: reach {reach} exceeds bound {instance.Radius}.");
                                Assert.True(instance.Radius <= ShapeRadius(instruction) + localOffset.Length() + 0.0001f, "A primitive's cull bound must not regress to the largest catalog shape's radius.");
                                break;
                        }
                    }
                }
            }
        }
    }

    // An independent enclosing sphere from the emitted primitive parameters, not the catalog's bound constants.
    private static float ShapeRadius(SdfInstruction instruction) {
        var dimensions = instruction.Data0;
        return (SdfShapeType)instruction.Shape switch {
            SdfShapeType.Box => new Vector3(dimensions.X, dimensions.Y, dimensions.Z).Length() + dimensions.W,
            SdfShapeType.Capsule => new Vector3(dimensions.X, dimensions.Y, dimensions.Z).Length() + dimensions.W,
            SdfShapeType.Cylinder => MathF.Sqrt((dimensions.X * dimensions.X) + (dimensions.Y * dimensions.Y)),
            SdfShapeType.Sphere => dimensions.X,
            _ => throw new InvalidOperationException($"No independent bound for catalog shape {instruction.Shape}."),
        };
    }

    [Fact]
    public void MismatchedMaterialTablesAreRefusedBeforeEmission() {
        var builder = new SdfProgramBuilder();
        builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        Assert.Throws<ArgumentException>(() => WorldRigCatalog.Emit(builder, _ => true, new int[2], new int[1], false, 0));
        WorldRigCatalog.Emit(builder, _ => true, new int[1], new int[1], false, 0);
        Assert.Equal(WorldRigCatalog.InstructionCount(0), builder.Build().Instructions.Count);
    }
}
