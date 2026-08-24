using System.Numerics;

using Puck.Forge.Authoring;
using Puck.SignedDistance;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins <c>partDynamics</c>'s creation-part id resolution (a dangling part id, a dangling row on a
/// resolved part, an empty part id), a root and a part dynamics reference coexisting, and
/// <see cref="WorldLookMotion.Default"/>'s literal shape.</summary>
public sealed class WorldLookDynamicsLawTests {
    private static WorldDynamicsRow Chase => new(Name: "chase", Frequency: 1f, Damping: 1f, Response: 0f);

    private static WorldCreation BuildPartedCreation() {
        var shape = new ShapeDocument(
            Id: 1,
            Name: "head",
            Type: SdfSolidPrimitive.Sphere,
            Position: new Vector3(x: 0f, y: 1.7f, z: 0f),
            Rotation: Quaternion.Identity,
            Scale: new Vector3(value: 0.25f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "parted",
            Palette: null,
            Shapes: [shape],
            Frames: null,
            Parts: [new CreationPartDocument(Id: "head", ShapeId: 1)]
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "parted");

        return new WorldCreation(Id: "parted", Document: canonical.Document, HashRaw: canonical.Hash);
    }

    private static WorldDefinition WithPartedCreation(WorldLookMotion motion) => Fixtures.BuildDocument() with {
        DynamicsRaw = [Chase],
        CreationsRaw = [BuildPartedCreation()],
        LooksRaw = [new WorldLook(Name: "avatar", Source: new WorldLookSource.Creation(CreationId: "parted"), Scale: 1f, Motion: motion)],
    };

    [Fact]
    public void PartDynamicsDanglingPartIdRefusesWhileResolvingPasses() {
        var denied = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["missing-part"] = "chase" },
        });
        var admitted = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "looks[0].motion.partDynamics['missing-part'] names no part of creation 'parted'.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void PartDynamicsDanglingRowRefusesWhileResolvingPasses() {
        var denied = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["head"] = "missing" },
        });
        var admitted = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "looks[0].motion.partDynamics['head'] 'missing' names no dynamics row.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void PartDynamicsEmptyPartIdRefusesWhileNonEmptyPasses() {
        var denied = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { [""] = "chase" },
        });
        var admitted = WithPartedCreation(motion: WorldLookMotion.Default with {
            PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "looks[0].motion.partDynamics has an empty part id.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void RootAndPartDynamicsTogetherPass() {
        var admitted = WithPartedCreation(motion: WorldLookMotion.Default with {
            Dynamics = "chase",
            PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
        });

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var reason), userMessage: reason);
    }

    [Fact]
    public void DefaultMotionCarriesNoDynamics() {
        var motion = WorldLookMotion.Default;

        Assert.Equal(expected: 1f, actual: motion.GaitAmplitude);
        Assert.False(condition: motion.ReplayFrames);
        Assert.Equal(expected: 0f, actual: motion.SecondsPerFrame);
        Assert.Null(@object: motion.Cues);
        Assert.Null(@object: motion.Dynamics);
        Assert.Null(@object: motion.PartDynamics);
    }
}
