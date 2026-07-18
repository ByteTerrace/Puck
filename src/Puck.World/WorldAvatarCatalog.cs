using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The deterministic authored-avatar catalog used by the real World population. Avatar <c>i</c> owns a distinct
/// 12..20-leaf humanoid rig (60..100 VM instructions), chosen through Puck.Maths low-discrepancy sequences so counts,
/// shapes, dimensions, and poses cover their ranges without clones, clumps, RNG state, or modulo bands. Slot ranges
/// are frozen across population rebuilds: activating/deactivating an avatar changes which ranges are emitted, never
/// the dynamic-transform identity of an existing avatar.
/// </summary>
internal static class WorldAvatarCatalog {
    private const float LeafBoundRadius = 0.42f;
    private const int MinLeafCount = 12;
    private const int MaxLeafCount = 20;
    private const int InstructionsPerLeaf = 5;
    private static readonly AvatarRange[] s_ranges;
    private static readonly AvatarLeaf[] s_leaves;
    private static readonly ulong[] s_identityHashes;

    static WorldAvatarCatalog() {
        s_ranges = new AvatarRange[WorldPopulation.MaxPopulation];
        s_identityHashes = new ulong[WorldPopulation.MaxPopulation];
        var leaves = new List<AvatarLeaf>(capacity: (WorldPopulation.MaxPopulation * 24));
        var identities = new HashSet<ulong>();

        for (var avatar = 0; (avatar < WorldPopulation.MaxPopulation); avatar++) {
            var first = leaves.Count;
            var count = LeafCountFor(avatar: avatar);

            for (var bone = 0; (bone < count); bone++) {
                leaves.Add(item: BuildLeaf(avatar: avatar, bone: bone));
            }

            s_ranges[avatar] = new AvatarRange(First: first, Count: count);
            var identity = IdentityHashFor(avatar: avatar, leafCount: count);

            if (!identities.Add(item: identity)) {
                throw new InvalidOperationException(message: $"Avatar {avatar} generated a duplicate deterministic identity {identity:x16}.");
            }

            s_identityHashes[avatar] = identity;
        }

        s_leaves = leaves.ToArray();
    }

    /// <summary>The all-128 rig's frozen dynamic-transform capacity (and leaf-instance count).</summary>
    public static int DynamicTransformCapacity => s_leaves.Length;

    /// <summary>The all-128 authored VM instruction total, excluding the static world.</summary>
    public static int InstructionCapacity => (s_leaves.Length * InstructionsPerLeaf);

    /// <summary>The minimum and maximum authored instruction counts of any catalog avatar.</summary>
    public static int MinInstructionCount => (MinLeafCount * InstructionsPerLeaf);
    public static int MaxInstructionCount => (MaxLeafCount * InstructionsPerLeaf);

    /// <summary>Returns the avatar's stable descriptor identity. The digest folds the Puck.Maths R1/R2 source samples
    /// that author the rig, and catalog construction rejects a collision across the built-in 128.</summary>
    public static ulong IdentityHash(int avatar) => s_identityHashes[avatar];

    /// <summary>Returns the literal avatar-local eyeball attachment point for a first-person camera. It follows the
    /// catalog's authored head leaf, so camera and morphology cannot silently drift apart.</summary>
    public static Vector3 EyeOffset(int avatar) {
        var head = s_leaves[s_ranges[avatar].First + 3];

        return (head.Anchor + head.AuthoredOffset + new Vector3(x: 0f, y: 0.015f, z: -0.12f));
    }

    /// <summary>Counts the active catalog leaves and their authored VM instructions for diagnostics.</summary>
    /// <param name="isActive">Whether the avatar at an index is active (a server-table or client-view read).</param>
    public static (int Leaves, int Instructions) ActiveWorkload(Func<int, bool> isActive) {
        ArgumentNullException.ThrowIfNull(isActive);

        var leaves = 0;

        for (var avatar = 0; (avatar < WorldPopulation.MaxPopulation); avatar++) {
            if (isActive(arg: avatar)) {
                leaves += s_ranges[avatar].Count;
            }
        }

        return (Leaves: leaves, Instructions: (leaves * InstructionsPerLeaf));
    }

    /// <summary>Emits every active avatar's distinct leaf chains. Each leaf is its own dynamic cull instance: a tile
    /// touching one hand does not admit the other bones of that avatar or its neighbors.</summary>
    public static void Emit(
        SdfProgramBuilder builder,
        Func<int, bool> isActive,
        ReadOnlySpan<int> bodyMaterials,
        ReadOnlySpan<int> accentMaterials,
        bool probeWorstCase
    ) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(isActive);

        for (var avatar = 0; (avatar < WorldPopulation.MaxPopulation); avatar++) {
            if (!probeWorstCase && !isActive(arg: avatar)) {
                continue;
            }

            var range = s_ranges[avatar];

            for (var slot = range.First; (slot < range.End); slot++) {
                var leaf = s_leaves[slot];
                var material = (leaf.UseAccent ? accentMaterials[avatar] : bodyMaterials[avatar]);

                builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: LeafBoundRadius, active: true);
                var chain = builder
                    .ResetPoint()
                    .TransformDynamic(slot: slot)
                    .Translate(offset: leaf.AuthoredOffset)
                    .Rotate(rotation: leaf.AuthoredRotation);

                _ = leaf.Shape switch {
                    AvatarShape.Box => chain.Box(
                        halfExtents: new Vector3(x: (0.105f * leaf.Scale), y: (0.17f * leaf.Scale), z: (0.085f * leaf.Scale)),
                        round: (0.038f * leaf.Scale),
                        material: material
                    ),
                    AvatarShape.Capsule => chain.Capsule(
                        endpoint: new Vector3(x: 0f, y: (0.27f * leaf.Scale), z: 0f),
                        radius: (0.068f * leaf.Scale),
                        material: material
                    ),
                    AvatarShape.Cylinder => chain.Cylinder(
                        radius: (0.082f * leaf.Scale),
                        halfHeight: (0.155f * leaf.Scale),
                        material: material
                    ),
                    _ => chain.Sphere(radius: (0.108f * leaf.Scale), material: material),
                };

                builder.EndInstance();
            }
        }
    }

    /// <summary>Packs one avatar's root pose plus movement-driven gait into its frozen leaf slots.</summary>
    public static void PackTransforms(
        int avatar,
        Vector3 rootPosition,
        Quaternion rootOrientation,
        float gaitPhase,
        bool castsSoftShadow,
        Span<DynamicTransform> transforms
    ) {
        var range = s_ranges[avatar];

        if (transforms.Length < range.End) {
            throw new ArgumentException(message: $"The avatar transform span has {transforms.Length} slots; avatar {avatar} requires {range.End}.", paramName: nameof(transforms));
        }

        for (var slot = range.First; (slot < range.End); slot++) {
            var leaf = s_leaves[slot];
            var swing = (leaf.GaitAmplitude <= 0f)
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(
                    axis: Vector3.UnitX,
                    angle: (leaf.GaitAmplitude * MathF.Sin(x: (gaitPhase + leaf.GaitPhaseOffset)))
                );
            var orientation = Quaternion.Normalize(value: (swing * rootOrientation));
            var position = (rootPosition + Vector3.Transform(value: leaf.Anchor, rotation: rootOrientation));

            transforms[slot] = new DynamicTransform(
                Position: position,
                Orientation: orientation,
                CastsSoftShadow: castsSoftShadow
            );
        }
    }

    /// <summary>Returns the exact authored instruction count for an avatar.</summary>
    public static int InstructionCount(int avatar) => (s_ranges[avatar].Count * InstructionsPerLeaf);

    private static AvatarLeaf BuildLeaf(int avatar, int bone) {
        var sampleIndex = (ulong)(((avatar * MaxLeafCount) + bone) + 1);
        var (x, y) = LowDiscrepancy.R2(index: sampleIndex);
        var ux = (float)(double)x;
        var uy = (float)(double)y;
        var role = (bone % MinLeafCount);
        var detailLayer = (bone / MinLeafCount);
        var anchor = HumanoidAnchor(role: role);

        // Extra leaves enrich the same recognizable skeleton with armor/joint/face detail, offset just enough that the
        // 12-, 24-, and 36-leaf avatars are genuinely different programs rather than repeated coincident geometry.
        anchor += new Vector3(
            x: ((ux - 0.5f) * (0.025f + (0.015f * detailLayer))),
            y: ((uy - 0.5f) * 0.035f),
            z: ((uy - 0.5f) * (0.045f + (0.015f * detailLayer)))
        );

        var authoredRotation = Quaternion.CreateFromYawPitchRoll(
            yaw: ((ux - 0.5f) * 0.20f),
            pitch: ((uy - 0.5f) * 0.14f),
            roll: (((ux + uy) - 1f) * 0.08f)
        );
        var gaitAmplitude = role >= 8 ? 0.52f : (role >= 4 ? 0.34f : 0f);
        var gaitPhaseOffset = role switch {
            // Arms counter-swing the opposite leg. Keeping amplitude positive and encoding side in phase avoids
            // accidentally cancelling the phase shift with a second sign inversion.
            4 or 6 or 9 or 11 => MathF.PI,
            _ => 0f,
        };

        return new AvatarLeaf(
            Anchor: anchor,
            AuthoredOffset: new Vector3(x: 0f, y: ((ux - 0.5f) * 0.025f), z: 0f),
            AuthoredRotation: authoredRotation,
            Shape: (AvatarShape)(((ulong)x.Value * 4u) >> 32),
            Scale: (0.82f + (0.34f * uy)),
            GaitAmplitude: gaitAmplitude,
            GaitPhaseOffset: gaitPhaseOffset,
            UseAccent: ((role == 3) || (role is 6 or 7) || ((detailLayer > 0) && (ux > 0.67f)))
        );
    }

    private static int LeafCountFor(int avatar) {
        var fraction = LowDiscrepancy.R1(index: (ulong)avatar);
        var span = ((MaxLeafCount - MinLeafCount) + 1);

        return (MinLeafCount + (int)(((ulong)fraction.Value * (uint)span) >> 32));
    }

    private static ulong IdentityHashFor(int avatar, int leafCount) {
        var hash = Fnv1aHash.Create();
        var countSample = LowDiscrepancy.R1(index: (ulong)avatar);

        hash.Add(value: countSample.Value);
        hash.Add(value: (uint)leafCount);

        for (var bone = 0; (bone < leafCount); bone++) {
            var sampleIndex = (ulong)(((avatar * MaxLeafCount) + bone) + 1);
            var (x, y) = LowDiscrepancy.R2(index: sampleIndex);

            hash.Add(value: x.Value);
            hash.Add(value: y.Value);
        }

        return hash.Value;
    }

    private static Vector3 HumanoidAnchor(int role) => role switch {
        0 => new Vector3(x: 0f, y: 0.68f, z: 0f),       // pelvis
        1 => new Vector3(x: 0f, y: 0.91f, z: 0f),       // abdomen
        2 => new Vector3(x: 0f, y: 1.17f, z: 0f),       // chest
        3 => new Vector3(x: 0f, y: 1.48f, z: -0.03f),   // head
        4 => new Vector3(x: -0.27f, y: 1.20f, z: 0f),   // left upper arm
        5 => new Vector3(x: 0.27f, y: 1.20f, z: 0f),    // right upper arm
        6 => new Vector3(x: -0.43f, y: 0.96f, z: 0f),   // left hand
        7 => new Vector3(x: 0.43f, y: 0.96f, z: 0f),    // right hand
        8 => new Vector3(x: -0.15f, y: 0.49f, z: 0f),   // left thigh
        9 => new Vector3(x: 0.15f, y: 0.49f, z: 0f),    // right thigh
        10 => new Vector3(x: -0.16f, y: 0.18f, z: -0.05f), // left shin/foot
        _ => new Vector3(x: 0.16f, y: 0.18f, z: -0.05f),   // right shin/foot
    };

    private enum AvatarShape : byte {
        Box,
        Capsule,
        Cylinder,
        Sphere,
    }

    private readonly record struct AvatarRange(int First, int Count) {
        public int End => (First + Count);
    }

    private readonly record struct AvatarLeaf(
        Vector3 Anchor,
        Vector3 AuthoredOffset,
        Quaternion AuthoredRotation,
        AvatarShape Shape,
        float Scale,
        float GaitAmplitude,
        float GaitPhaseOffset,
        bool UseAccent
    );
}
