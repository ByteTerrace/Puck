using System.Numerics;
using Puck.World.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>
/// The deterministic appearance catalog used by the World population. Each catalog entry is a distinct
/// 12..20-leaf humanoid rig (60..100 VM instructions), chosen through Puck.Maths low-discrepancy sequences.
/// Appearance indices are independent of body indices. The lowest-index detailed band reserves one maximum-sized
/// transform range per body; the remaining crowd band reserves one stable root slot per body and renders a coarse
/// capsule. Activating or restyling one body never moves another body's transform slots.
/// </summary>
public static class WorldRigCatalog {
    private const int InstructionsPerLeaf = 5;
    private const float LeafBoundRadius = 0.42f;
    private const int MaxLeafCount = 20;
    private const int MinLeafCount = 12;
    private const int CoarseInstructionCount = 4;
    private const float CoarseRadius = 0.42f;
    private const float CoarseSegmentHeight = 0.68f;

    // This procedural SDF look owns a fixed renderer catalog. Simulation population capacity is authored separately.
    public const int RigCount = WorldLookSource.Catalog.RigCount;

    private static readonly ulong[] IdentityHashes;
    private static readonly AvatarLeaf[] Leaves;
    private static readonly int LargestRig;
    // The procedural humanoid's authored content. These identifiers are not an engine vocabulary: another look
    // publishes any part ids its own geometry needs against its own transform slots.
    private static readonly AuthoredPartTable Parts = new(slots: [
        new(
            PartId: "pelvis",
            TransformSlot: 0
        ),
        new(
            PartId: "abdomen",
            TransformSlot: 1
        ),
        new(
            PartId: "chest",
            TransformSlot: 2
        ),
        new(
            PartId: "head",
            TransformSlot: 3
        ),
        new(
            PartId: "left-upper-arm",
            TransformSlot: 4
        ),
        new(
            PartId: "right-upper-arm",
            TransformSlot: 5
        ),
        new(
            PartId: "left-hand",
            TransformSlot: 6
        ),
        new(
            PartId: "right-hand",
            TransformSlot: 7
        ),
        new(
            PartId: "left-thigh",
            TransformSlot: 8
        ),
        new(
            PartId: "right-thigh",
            TransformSlot: 9
        ),
        new(
            PartId: "left-shin",
            TransformSlot: 10
        ),
        new(
            PartId: "right-shin",
            TransformSlot: 11
        ),
    ]);
    private static readonly AvatarRange[] Ranges;

    static WorldRigCatalog() {
        Ranges = new AvatarRange[RigCount];
        IdentityHashes = new ulong[RigCount];
        var leaves = new List<AvatarLeaf>(capacity: (RigCount * MaxLeafCount));
        var identities = new HashSet<ulong>();

        for (var avatar = 0; (avatar < RigCount); avatar++) {
            var first = leaves.Count;
            var count = LeafCountFor(avatar: avatar);

            for (var bone = 0; (bone < count); bone++) {
                leaves.Add(item: BuildLeaf(
                    avatar: avatar,
                    bone: bone
                ));
            }

            Ranges[avatar] = new AvatarRange(
                Count: count,
                First: first
            );
            if (count > Ranges[LargestRig].Count) { LargestRig = avatar; }
            var identity = IdentityHashFor(
                avatar: avatar,
                leafCount: count
            );

            if (!identities.Add(item: identity)) {
                throw new InvalidOperationException(message: $"Avatar {avatar} generated a duplicate deterministic identity {identity:x16}.");
            }

            IdentityHashes[avatar] = identity;
        }

        Leaves = leaves.ToArray();
        var reach = 0f;

        foreach (var leaf in Leaves) {
            reach = MathF.Max(
                x: reach,
                y: ((leaf.Anchor + leaf.AuthoredOffset).Length() + (LeafBoundRadius * leaf.Scale))
            );
        }

        Reach = reach;
    }

    /// <summary>The number of lowest-index bodies retaining the complete independently animated humanoid rig —
    /// <see cref="WorldBodiesLimits.DetailedRenderBand"/>, the one detailed-render band every per-body presentation
    /// reservation reads. Remaining bodies still render as one individually positioned coarse capsule. The local-seat
    /// band therefore always remains detailed while SDF storage and evaluation inputs stay finitely bounded; this
    /// representation alone does not establish a dense-crowd frame-rate target.</summary>
    public const int DetailedAvatarCapacity = WorldBodiesLimits.DetailedRenderBand;
    /// <summary>The compact dynamic-transform lane: full ranges for the detailed band, then one root per coarse body.</summary>
    public static int DynamicTransformCapacity => checked(
        Math.Min(WorldBodiesLimits.CapacityCeiling, DetailedAvatarCapacity) * MaxLeafCount +
        Math.Max(0, WorldBodiesLimits.CapacityCeiling - DetailedAvatarCapacity));
    /// <summary>The maximum authored VM instruction total for the hybrid detailed/coarse population representation.</summary>
    public static int InstructionCapacity => checked(
        Math.Min(WorldBodiesLimits.CapacityCeiling, DetailedAvatarCapacity) * MaxInstructionCount +
        Math.Max(0, WorldBodiesLimits.CapacityCeiling - DetailedAvatarCapacity) * CoarseInstructionCount);
    public static int MaxInstructionCount => (MaxLeafCount * InstructionsPerLeaf);
    /// <summary>Gets the maximum leaf-cull-instance count of one active catalog body.</summary>
    public static int MaxInstancesPerAvatar => MaxLeafCount;
    /// <summary>Gets the frozen transform-range width reserved for one detailed body's animated parts.</summary>
    public static int TransformSlotsPerBody => MaxLeafCount;
    /// <summary>Gets the one root-transform slot used by a coarse body.</summary>
    public const int CoarseTransformSlotsPerBody = 1;
    /// <summary>The minimum and maximum authored instruction counts of any catalog avatar.</summary>
    public static int MinInstructionCount => (MinLeafCount * InstructionsPerLeaf);
    /// <summary>Gets the avatar-local radius, in world units at unit render scale, enclosing every catalog rig's
    /// leaves — the proximity reach a band-relevance test scales by a look's own render scale.</summary>
    public static float Reach { get; }

    private static AvatarLeaf BuildLeaf(int avatar, int bone) {
        var sampleIndex = ((ulong)(((avatar * MaxLeafCount) + bone) + 1));

        var (x, y) = LowDiscrepancy.R2(index: sampleIndex);
        var ux = ((float)((double)x));
        var uy = ((float)((double)y));
        var role = (bone % MinLeafCount);
        var detailLayer = (bone / MinLeafCount);
        var anchor = HumanoidAnchor(role: role);

        // Extra leaves enrich the same recognizable skeleton with armor/joint/face detail, offset just enough that the
        // different leaf counts produce genuinely different programs rather than repeated coincident geometry.
        anchor += new Vector3(
            x: ((ux - 0.5f) * (0.025f + (0.015f * detailLayer))),
            y: ((uy - 0.5f) * 0.035f),
            z: ((uy - 0.5f) * (0.045f + (0.015f * detailLayer)))
        );

        var authoredRotation = Quaternion.CreateFromYawPitchRoll(
            pitch: ((uy - 0.5f) * 0.14f),
            roll: (((ux + uy) - 1f) * 0.08f),
            yaw: ((ux - 0.5f) * 0.20f)
        );
        var gaitAmplitude = ((role >= 8)
            ? 0.52f
            : ((role >= 4)
                ? 0.34f
                : 0f
        ));
        var gaitPhaseOffset = role switch {
            // Arms counter-swing the opposite leg. Keeping amplitude positive and encoding side in phase avoids
            // accidentally cancelling the phase shift with a second sign inversion.
            4 or 6 or 9 or 11 => MathF.PI,
            _ => 0f,
        };

        return new AvatarLeaf(
            Anchor: anchor,
            AuthoredOffset: new Vector3(
                x: 0f,
                y: ((ux - 0.5f) * 0.025f),
                z: 0f
            ),
            AuthoredRotation: authoredRotation,
            Shape: ((AvatarShape)((((ulong)x.Value) * 4u) >> 32)),
            Scale: (0.82f + (0.34f * uy)),
            GaitAmplitude: gaitAmplitude,
            GaitPhaseOffset: gaitPhaseOffset,
            UseAccent: ((role == 3) || (role is 6 or 7) || ((detailLayer > 0) && (ux > 0.67f)))
        );
    }
    private static Vector3 HumanoidAnchor(int role) => role switch {
        0 => new Vector3(
        x: 0f,
        y: 0.68f,
        z: 0f
    ),       // pelvis
        1 => new Vector3(
        x: 0f,
        y: 0.91f,
        z: 0f
    ),       // abdomen
        2 => new Vector3(
        x: 0f,
        y: 1.17f,
        z: 0f
    ),       // chest
        3 => new Vector3(
        x: 0f,
        y: 1.48f,
        z: -0.03f
    ),   // head
        4 => new Vector3(
        x: -0.27f,
        y: 1.20f,
        z: 0f
    ),   // left upper arm
        5 => new Vector3(
        x: 0.27f,
        y: 1.20f,
        z: 0f
    ),    // right upper arm
        6 => new Vector3(
        x: -0.43f,
        y: 0.96f,
        z: 0f
    ),   // left hand
        7 => new Vector3(
        x: 0.43f,
        y: 0.96f,
        z: 0f
    ),    // right hand
        8 => new Vector3(
        x: -0.15f,
        y: 0.49f,
        z: 0f
    ),   // left thigh
        9 => new Vector3(
        x: 0.15f,
        y: 0.49f,
        z: 0f
    ),    // right thigh
        10 => new Vector3(
        x: -0.16f,
        y: 0.18f,
        z: -0.05f
    ), // left shin/foot
        _ => new Vector3(
        x: 0.16f,
        y: 0.18f,
        z: -0.05f
    ),   // right shin/foot
    };
    private static ulong IdentityHashFor(int avatar, int leafCount) {
        var hash = Fnv1aHash.Create();
        var countSample = LowDiscrepancy.R1(index: ((ulong)avatar));

        hash.Add(value: countSample.Value);
        hash.Add(value: ((uint)leafCount));

        for (var bone = 0; (bone < leafCount); bone++) {
            var sampleIndex = ((ulong)(((avatar * MaxLeafCount) + bone) + 1));

            var (x, y) = LowDiscrepancy.R2(index: sampleIndex);

            hash.Add(value: x.Value);
            hash.Add(value: y.Value);
        }

        return hash.Value;
    }
    private static int LeafCountFor(int avatar) {
        var fraction = LowDiscrepancy.R1(index: ((ulong)avatar));
        var span = ((MaxLeafCount - MinLeafCount) + 1);

        return (MinLeafCount + ((int)((((ulong)fraction.Value) * ((uint)span)) >> 32)));
    }
    // The geometry-source rig is never a population slot. An absent or invalid pin uses the catalog's default pick.
    private static int RigIndex(Func<int, int>? rigFor, int avatar) {
        var rig = (rigFor?.Invoke(arg: avatar) ?? -1);

        return ((((uint)rig) < RigCount)
            ? rig
            : WorldLookSource.Catalog.DefaultIndex(avatar)
        );
    }
    private static int BodySlotBase(int avatar) {
        ArgumentOutOfRangeException.ThrowIfNegative(avatar);
        return avatar < DetailedAvatarCapacity
            ? checked(avatar * MaxLeafCount)
            : checked(DetailedAvatarCapacity * MaxLeafCount + avatar - DetailedAvatarCapacity);
    }
    private static bool IsDetailed(int avatar) => avatar < DetailedAvatarCapacity;
    private static float ScaleFor(Func<int, float>? scaleFor, int avatar) {
        var scale = (scaleFor?.Invoke(arg: avatar) ?? 1f);

        return ((float.IsFinite(f: scale) && (scale > 0f))
            ? scale
            : 1f
        );
    }

    /// <summary>Counts the active catalog leaves and their authored VM instructions for diagnostics.</summary>
    /// <param name="isActive">Whether the avatar at an index is active (a server-table or client-view read).</param>
    /// <param name="capacity">The caller's live table size; never limited by the number of available looks.</param>
    /// <param name="rigFor">The resolved appearance index for each body, or null for the default catalog pick.</param>
    /// <returns>The catalog leaf, authored instruction, and leaf-cull-instance totals for active bodies.</returns>
    /// <exception cref="ArgumentNullException">The activity predicate is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The table capacity is negative.</exception>
    /// <exception cref="OverflowException">The workload exceeds a signed 32-bit count.</exception>
    public static (int Leaves, int Instructions, int Instances) ActiveWorkload(Func<int, bool> isActive, int capacity, Func<int, int>? rigFor = null) {
        ArgumentNullException.ThrowIfNull(isActive);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        var leaves = 0;
        var instructions = 0;

        for (var avatar = 0; (avatar < capacity); avatar++) {
            if (isActive(arg: avatar)) {
                var count = IsDetailed(avatar) ? Ranges[RigIndex(rigFor, avatar)].Count : 1;
                leaves = checked(leaves + count);
                instructions = checked(instructions + (IsDetailed(avatar) ? count * InstructionsPerLeaf : CoarseInstructionCount));
            }
        }

        return (Leaves: leaves, Instructions: instructions, Instances: leaves);
    }
    /// <summary>Emits every active body: animated leaf chains in the detailed band and one root-following capsule in
    /// the coarse crowd band. Each emitted primitive has its own dynamic cull instance.</summary>
    /// <remarks>A <see cref="WorldLook"/> may pin a catalog rig (<paramref name="rigFor"/>) and a uniform render scale
    /// (<paramref name="scaleFor"/>). A detailed body's complete pinned rig is written into its maximum-sized
    /// transform range; no leaf is duplicated or truncated to fit another appearance. A coarse body uses its one
    /// stable root slot. The material spans determine the body count. Probes reserve the largest rig throughout the
    /// detailed band and one capsule for each remaining body.</remarks>
    /// <param name="builder">The program builder.</param>
    /// <param name="isActive">Whether the avatar at an index is active.</param>
    /// <param name="bodyMaterials">Each avatar's body material id.</param>
    /// <param name="accentMaterials">Each avatar's accent material id.</param>
    /// <param name="probeWorstCase">Emit the largest rig in every reserved body range at unit scale.</param>
    /// <param name="slotBase">The owning emitter's first dynamic-transform slot
    /// (<see cref="Puck.SdfVm.SdfEmitContext.SlotBase"/>) — the catalog's own ranges start at 0 and are shifted by this
    /// when the absolute slot lane is baked into an instruction.</param>
    /// <param name="rigFor">The catalog rig each body sources its leaves from, or null for the default catalog pick.</param>
    /// <param name="scaleFor">Each avatar's uniform render scale, or <see langword="null"/> for 1.</param>
    /// <param name="probeAvatarLimit">Bounds a worst-case probe to that many bodies. Null reserves every material-span entry.</param>
    public static void Emit(
        SdfProgramBuilder builder,
        Func<int, bool> isActive,
        ReadOnlySpan<int> bodyMaterials,
        ReadOnlySpan<int> accentMaterials,
        bool probeWorstCase,
        int slotBase,
        Func<int, int>? rigFor = null,
        Func<int, float>? scaleFor = null,
        int? probeAvatarLimit = null
    ) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(isActive);
        if (bodyMaterials.Length != accentMaterials.Length) { throw new ArgumentException("Body and accent material spans must have equal lengths."); }

        var bounded = (probeWorstCase && (probeAvatarLimit is not null));
        var count = (bounded
            ? Math.Clamp(
                max: bodyMaterials.Length,
                min: 0,
                value: probeAvatarLimit!.Value
            )
            : bodyMaterials.Length
        );

        for (var position = 0; (position < count); position++) {
            var avatar = position;

            if (
                !probeWorstCase &&
                !isActive(arg: avatar)
            ) {
                continue;
            }

            var firstSlot = BodySlotBase(avatar);
            var scale = (probeWorstCase
                ? 1f
                : ScaleFor(
                    avatar: avatar,
                    scaleFor: scaleFor
                )
            );
            if (!IsDetailed(avatar)) {
                var material = bodyMaterials[avatar];
                var packedSlot = checked(slotBase + firstSlot);
                builder.BeginInstanceDynamic(
                    slot: packedSlot,
                    boundOffset: Vector3.Zero,
                    // The dynamic instance offset is not orientation-relative. Centering the bound above the root
                    // would therefore fail for a pitched airborne/aquatic body; a root-centered sphere encloses the
                    // translated segment through every orientation.
                    boundRadius: (CoarseSegmentHeight + (2f * CoarseRadius)) * scale
                );
                _ = builder
                    .ResetPoint()
                    .TransformDynamic(slot: packedSlot)
                    .Translate(offset: new Vector3(0f, CoarseRadius * scale, 0f))
                    .Capsule(endpoint: new Vector3(0f, CoarseSegmentHeight * scale, 0f), radius: CoarseRadius * scale, material: material);
                builder.EndInstance();
                continue;
            }
            var rigRange = (probeWorstCase
                ? Ranges[LargestRig]
                : Ranges[RigIndex(
                    avatar: avatar,
                    rigFor: rigFor
                )]
            );
            for (var offset = 0; offset < rigRange.Count; offset++) {
                var leaf = Leaves[rigRange.First + offset];
                var material = (leaf.UseAccent
                    ? accentMaterials[avatar]
                    : bodyMaterials[avatar]
                );
                var leafScale = (leaf.Scale * scale);

                // The catalog's slot ranges are relative to the OWNING emitter's assigned base; the baked instruction
                // lanes are absolute indices into the composed buffer, so the base is added exactly here.
                var packedSlot = checked(slotBase + firstSlot + offset);

                // The instance center follows the bone without rotating its bound offset. Enclose the local offset
                // in the radius instead; it is unscaled even when the look shrinks. Keep PrimitiveReach paired with
                // the shape dimensions below. A body-wide sphere admits every limb to too many screen tiles.
                builder.BeginInstanceDynamic(
                    slot: packedSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: ((PrimitiveReach(leaf.Shape) * leafScale) + leaf.AuthoredOffset.Length())
                );
                var chain = builder
                    .ResetPoint()
                    .TransformDynamic(slot: packedSlot)
                    .Translate(offset: leaf.AuthoredOffset)
                    .Rotate(rotation: leaf.AuthoredRotation);

                _ = leaf.Shape switch {
                    AvatarShape.Box => chain.Box(
                    halfExtents: new Vector3(
                        x: (0.105f * leafScale),
                        y: (0.17f * leafScale),
                        z: (0.085f * leafScale)
                    ),
                    round: (0.038f * leafScale),
                    material: material
                ),
                    AvatarShape.Capsule => chain.Capsule(
                    endpoint: new Vector3(
                        x: 0f,
                        y: (0.27f * leafScale),
                        z: 0f
                    ),
                    radius: (0.068f * leafScale),
                    material: material
                ),
                    AvatarShape.Cylinder => chain.Cylinder(
                    radius: (0.082f * leafScale),
                    halfHeight: (0.155f * leafScale),
                    material: material
                ),
                    _ => chain.Sphere(
                    radius: (0.108f * leafScale),
                    material: material
                ),
                };

                builder.EndInstance();
            }
        }
    }
    private static float PrimitiveReach(AvatarShape shape) => shape switch {
        AvatarShape.Box => new Vector3(0.105f, 0.17f, 0.085f).Length() + 0.038f,
        AvatarShape.Capsule => 0.27f + 0.068f,
        AvatarShape.Cylinder => MathF.Sqrt((0.082f * 0.082f) + (0.155f * 0.155f)),
        _ => 0.108f,
    };
    /// <summary>Returns the literal rig-local eyeball attachment point for a first-person camera. It follows the
    /// catalog's authored head leaf, so camera and morphology cannot silently drift apart.</summary>
    /// <param name="avatar">The appearance catalog index, not a population slot.</param>
    public static Vector3 EyeOffset(int avatar) {
        var head = Leaves[(Ranges[avatar].First + 3)];

        return ((head.Anchor + head.AuthoredOffset) + new Vector3(
            x: 0f,
            y: 0.015f,
            z: -0.12f
        ));
    }
    /// <summary>Returns the rig's stable descriptor identity. The digest folds the Puck.Maths R1/R2 source samples
    /// that author the rig, and catalog construction rejects a collision across the built-in 128.</summary>
    /// <param name="avatar">The appearance catalog index, not a population slot.</param>
    public static ulong IdentityHash(int avatar) => IdentityHashes[avatar];
    /// <summary>Returns the exact authored instruction count for a catalog rig.</summary>
    /// <param name="avatar">The appearance catalog index, not a population slot.</param>
    public static int InstructionCount(int avatar) => (Ranges[avatar].Count * InstructionsPerLeaf);
    /// <summary>Resolves the catalog rig a look's geometry sources from: an authored <c>Catalog(Index)</c> pin, or
    /// <paramref name="catalogRig"/> for an unpinned catalog look or a Creation look (the occupant-owned carried rig
    /// rather than the destination body's default pick). Pass this result explicitly to <see cref="PackTransforms"/>,
    /// <see cref="TryPartOffset"/>, and
    /// <see cref="TryPartPose(int, string, int, System.ReadOnlySpan{DynamicTransform}, out SdfAnchor, float)"/>. The ONE
    /// selector every consumer of a look's rig — emission and part-anchor resolution alike — must call, so a body's
    /// rendered geometry and its part anchors never disagree about which rig it carries.</summary>
    /// <param name="look">The entity's resolved look.</param>
    /// <param name="catalogRig">The entity's own carried catalog rig — the fallback for an unpinned look.</param>
    public static int RigFor(WorldLook look, byte catalogRig) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned })
        ? pinned
        : catalogRig
    );
    /// <summary>Packs a detailed body's complete catalog rig into its independent maximum-sized transform range, or
    /// a coarse body's root pose into its single slot. The look's uniform scale multiplies detailed anchor offsets;
    /// rig = -1 selects the body's default catalog pick.</summary>
    public static void PackTransforms(
        int avatar,
        Vector3 rootPosition,
        Quaternion rootOrientation,
        float gaitPhase,
        bool castsSoftShadow,
        Span<DynamicTransform> transforms,
        int rig = -1,
        float scale = 1f
    ) {
        var firstSlot = BodySlotBase(avatar);
        if (!IsDetailed(avatar)) {
            if ((uint)firstSlot >= (uint)transforms.Length) {
                throw new ArgumentException(
                    message: $"The avatar transform span has {transforms.Length} slots; avatar {avatar} requires {firstSlot + 1}.",
                    paramName: nameof(transforms));
            }
            transforms[firstSlot] = new DynamicTransform(
                CastsSoftShadow: castsSoftShadow,
                Orientation: rootOrientation,
                Position: rootPosition);
            return;
        }
        var rigRange = Ranges[((rig < 0)
            ? WorldLookSource.Catalog.DefaultIndex(avatar)
            : rig)];

        var end = checked(firstSlot + rigRange.Count);
        if (transforms.Length < end) {
            throw new ArgumentException(
                message: $"The avatar transform span has {transforms.Length} slots; avatar {avatar} requires {end}.",
                paramName: nameof(transforms)
            );
        }

        for (var slot = firstSlot; slot < end; slot++) {
            var leaf = Leaves[rigRange.First + slot - firstSlot];
            var swing = ((leaf.GaitAmplitude <= 0f)
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(
                    axis: Vector3.UnitX,
                    angle: (leaf.GaitAmplitude * MathF.Sin(x: (gaitPhase + leaf.GaitPhaseOffset)))
                )
            );
            var orientation = Quaternion.Normalize(value: (swing * rootOrientation));
            var position = (rootPosition + Vector3.Transform(
                value: (leaf.Anchor * scale),
                rotation: rootOrientation
            ));

            transforms[slot] = new DynamicTransform(
                CastsSoftShadow: castsSoftShadow,
                Orientation: orientation,
                Position: position
            );
        }
    }
    /// <summary>Returns a published part's avatar-local authored rest offset.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="rig">The resolved catalog rig, or -1 for the body's default catalog pick. Pass a carried rig explicitly.</param>
    /// <param name="scale">The look's uniform render scale.</param>
    /// <param name="offset">The authored rest offset, or zero when unresolved.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/>.</returns>
    public static bool TryPartOffset(int avatar, string partId, int rig, float scale, out Vector3 offset) {
        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            offset = default;

            return false;
        }

        var rigRange = Ranges[((rig < 0)
            ? WorldLookSource.Catalog.DefaultIndex(avatar)
            : rig)];
        var leaf = Leaves[rigRange.First + relativeSlot];

        offset = ((leaf.Anchor * scale) + leaf.AuthoredOffset);

        return true;
    }
    /// <summary>Resolves a published part's live pose from the packed dynamic transforms.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="rig">The resolved catalog rig, or -1 for the body's default catalog pick. Pass a carried rig explicitly.</param>
    /// <param name="transforms">The frame's packed dynamic-transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <param name="scale">The body's uniform look scale, used to reconstruct a coarse body's published rest anchor.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/> and the slot is packed.</returns>
    public static bool TryPartPose(int avatar, string partId, int rig, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose, float scale = 1f) {
        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            pose = default;

            return false;
        }

        var transformSlot = checked(BodySlotBase(avatar) + (IsDetailed(avatar) ? relativeSlot : 0));

        if (((uint)transformSlot) >= ((uint)transforms.Length)) {
            pose = default;

            return false;
        }

        var packed = transforms[transformSlot];
        var rigRange = Ranges[((rig < 0)
            ? WorldLookSource.Catalog.DefaultIndex(avatar)
            : rig)];
        var leaf = Leaves[rigRange.First + relativeSlot];

        pose = IsDetailed(avatar)
            ? new SdfAnchor(
                Position: packed.Position + Vector3.Transform(leaf.AuthoredOffset, packed.Orientation),
                Orientation: packed.Orientation)
            : new SdfAnchor(
                Position: packed.Position + Vector3.Transform(leaf.Anchor * scale + leaf.AuthoredOffset, packed.Orientation),
                Orientation: packed.Orientation);

        return true;
    }
    /// <summary>Resolves a published part's live pose from a list-backed packed dynamic-transform buffer.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="rig">The resolved catalog rig, or -1 for the body's default catalog pick. Pass a carried rig explicitly.</param>
    /// <param name="transforms">The frame's packed dynamic-transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <param name="scale">The body's uniform look scale, used to reconstruct a coarse body's published rest anchor.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/> and the slot is packed.</returns>
    public static bool TryPartPose(int avatar, string partId, int rig, IReadOnlyList<DynamicTransform> transforms, out SdfAnchor pose, float scale = 1f) {
        ArgumentNullException.ThrowIfNull(transforms);

        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            pose = default;

            return false;
        }

        var transformSlot = checked(BodySlotBase(avatar) + (IsDetailed(avatar) ? relativeSlot : 0));

        if (((uint)transformSlot) >= ((uint)transforms.Count)) {
            pose = default;

            return false;
        }

        var packed = transforms[transformSlot];
        var rigRange = Ranges[((rig < 0)
            ? WorldLookSource.Catalog.DefaultIndex(avatar)
            : rig)];
        var leaf = Leaves[rigRange.First + relativeSlot];

        pose = IsDetailed(avatar)
            ? new SdfAnchor(
                Position: packed.Position + Vector3.Transform(leaf.AuthoredOffset, packed.Orientation),
                Orientation: packed.Orientation)
            : new SdfAnchor(
                Position: packed.Position + Vector3.Transform(leaf.Anchor * scale + leaf.AuthoredOffset, packed.Orientation),
                Orientation: packed.Orientation);

        return true;
    }
    /// <summary>Resolves a published part id to the avatar's stable packed transform slot.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transformSlot">The emitter-relative packed transform slot, or -1 when unresolved.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/>.</returns>
    public static bool TryPartTransformSlot(int avatar, string partId, out int transformSlot) {
        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            transformSlot = -1;

            return false;
        }

        transformSlot = checked(BodySlotBase(avatar) + (IsDetailed(avatar) ? relativeSlot : 0));

        return true;
    }

    private enum AvatarShape : byte {
        Box,
        Capsule,
        Cylinder,
        Sphere,
    }
    private readonly record struct AvatarRange(int First, int Count);
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
