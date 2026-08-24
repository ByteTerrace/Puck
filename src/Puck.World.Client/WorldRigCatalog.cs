using System.Numerics;
using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>
/// The deterministic authored-avatar catalog used by the real World population. Avatar <c>i</c> owns a distinct
/// 12..20-leaf humanoid rig (60..100 VM instructions), chosen through Puck.Maths low-discrepancy sequences so counts,
/// shapes, dimensions, and poses cover their ranges without clones, clumps, RNG state, or modulo bands. Slot ranges
/// are frozen across population rebuilds: activating/deactivating an avatar changes which ranges are emitted, never
/// the dynamic-transform identity of an existing avatar.
/// </summary>
public static class WorldRigCatalog {
    private const int InstructionsPerLeaf = 5;
    private const float LeafBoundRadius = 0.42f;
    private const int MaxLeafCount = 20;
    private const int MinLeafCount = 12;

    // This procedural SDF look owns a fixed renderer catalog. Simulation population capacity is authored separately.
    public const int Capacity = WorldLookSource.Catalog.RigCount;

    private static readonly ulong[] IdentityHashes;
    private static readonly AvatarLeaf[] Leaves;
    // Avatar indices ordered by descending leaf count, so the first N of them are the largest N rigs the catalog can
    // produce — the supremum a bounded probe must reserve for ANY N-avatar subset.
    private static readonly int[] ProbeOrder;
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
        Ranges = new AvatarRange[Capacity];
        IdentityHashes = new ulong[Capacity];
        var leaves = new List<AvatarLeaf>(capacity: (Capacity * 24));
        var identities = new HashSet<ulong>();

        for (var avatar = 0; (avatar < Capacity); avatar++) {
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
        ProbeOrder = [.. Enumerable.Range(
            count: Capacity,
            start: 0
        )
            .OrderByDescending(keySelector: avatar => Ranges[avatar].Count)
            .ThenBy(keySelector: avatar => avatar)];
        var reach = 0f;

        foreach (var leaf in Leaves) {
            reach = MathF.Max(
                x: reach,
                y: ((leaf.Anchor + leaf.AuthoredOffset).Length() + (LeafBoundRadius * leaf.Scale))
            );
        }

        Reach = reach;
    }

    /// <summary>The all-128 rig's frozen dynamic-transform capacity (and leaf-instance count).</summary>
    public static int DynamicTransformCapacity => Leaves.Length;
    /// <summary>The all-128 authored VM instruction total, excluding the static world.</summary>
    public static int InstructionCapacity => (Leaves.Length * InstructionsPerLeaf);
    public static int MaxInstructionCount => (MaxLeafCount * InstructionsPerLeaf);
    /// <summary>Gets the largest leaf — and therefore cull-instance — count any catalog rig emits.</summary>
    public static int MaxInstancesPerAvatar => MaxLeafCount;
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
        // 12-, 24-, and 36-leaf avatars are genuinely different programs rather than repeated coincident geometry.
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
    // The geometry-source rig for an avatar (a WorldLook.Catalog(Index) pin), clamped to the built-in 128; a negative or
    // out-of-range pin falls back to the avatar's own index.
    private static int RigIndex(Func<int, int>? rigFor, int avatar) {
        var rig = (rigFor?.Invoke(arg: avatar) ?? avatar);

        return ((((uint)rig) < Capacity)
            ? rig
            : avatar
        );
    }
    // The rig leaf a slot offset reads: the pinned rig's leaf at the same relative offset, CLAMPED to the rig's last
    // leaf when the entity's slot range is longer (so a pinned rig with fewer leaves fills the entity's slots safely and
    // a longer one is truncated — the frozen per-entity slot capacity is never exceeded).
    private static int RigLeaf(AvatarRange rigRange, int offset) {
        return (rigRange.First + Math.Min(
            val1: offset,
            val2: (rigRange.Count - 1)
        ));
    }
    private static float ScaleFor(Func<int, float>? scaleFor, int avatar) {
        var scale = (scaleFor?.Invoke(arg: avatar) ?? 1f);

        return ((float.IsFinite(f: scale) && (scale > 0f))
            ? scale
            : 1f
        );
    }

    /// <summary>Counts the active catalog leaves and their authored VM instructions for diagnostics.</summary>
    /// <param name="isActive">Whether the avatar at an index is active (a server-table or client-view read).</param>
    /// <param name="capacity">The caller's own live table size — a client-view read is always fixed at the engine's
    /// <see cref="Capacity"/> (128, its arrays are allocated at that width regardless of the authored document), but a
    /// server-table read (<c>Puck.World.Server.WorldPopulation.IsActive</c>) is backed by an array sized to the AUTHORED
    /// <c>population.capacity</c>, which is legally anywhere from 4 to 128. Querying <paramref name="isActive"/> past
    /// that live width is undefined for a server-table delegate, so the scan never walks further than the honest
    /// minimum of the two — mirroring the bound every other <c>WorldPopulation.IsActive</c> caller already applies
    /// (<c>WorldServer.BuildSnapshot</c>, <c>WorldReplaySnapshot.HashState</c>, <c>WorldEventFeed</c>,
    /// <c>WorldPlacementAttachment.TryResolve</c> all loop to <c>population.Capacity</c>, never a fixed 128).</param>
    public static (int Leaves, int Instructions) ActiveWorkload(Func<int, bool> isActive, int capacity) {
        ArgumentNullException.ThrowIfNull(isActive);

        var leaves = 0;
        var bound = Math.Min(
            val1: capacity,
            val2: Capacity
        );

        for (var avatar = 0; (avatar < bound); avatar++) {
            if (isActive(arg: avatar)) {
                leaves += Ranges[avatar].Count;
            }
        }

        return (Leaves: leaves, Instructions: (leaves * InstructionsPerLeaf));
    }
    /// <summary>Emits every active avatar's distinct leaf chains. Each leaf is its own dynamic cull instance: a tile
    /// touching one hand does not admit the other bones of that avatar or its neighbors.</summary>
    /// <remarks>A <see cref="WorldLook"/> may pin a catalog rig (<paramref name="rigFor"/>) and a uniform render scale
    /// (<paramref name="scaleFor"/>). Geometry is SOURCED from the pinned rig but WRITTEN to the entity's OWN frozen slot
    /// range, clamped to that range's leaf count — so a pinned look never grows the frozen dynamic-transform capacity
    /// (the probe emits the identity rig per slot). Defaults reproduce the pre-look behaviour: rig = avatar, scale = 1.</remarks>
    /// <param name="builder">The program builder.</param>
    /// <param name="isActive">Whether the avatar at an index is active.</param>
    /// <param name="bodyMaterials">Each avatar's body material id.</param>
    /// <param name="accentMaterials">Each avatar's accent material id.</param>
    /// <param name="probeWorstCase">Emit every catalog range at unit scale (the frozen worst case).</param>
    /// <param name="slotBase">The owning emitter's first dynamic-transform slot
    /// (<see cref="Puck.SdfVm.SdfEmitContext.SlotBase"/>) — the catalog's own ranges start at 0 and are shifted by this
    /// when the absolute slot lane is baked into an instruction.</param>
    /// <param name="rigFor">The catalog rig each avatar sources its leaves from, or <see langword="null"/> for its own.</param>
    /// <param name="scaleFor">Each avatar's uniform render scale, or <see langword="null"/> for 1.</param>
    /// <param name="probeAvatarLimit">Bounds a worst-case probe to that many avatars — the largest rigs the catalog
    /// carries, so the reservation covers any subset of that size a live emission may select. <see langword="null"/>
    /// reserves the whole catalog.</param>
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

        var bounded = (probeWorstCase && (probeAvatarLimit is not null));
        var count = (bounded
            ? Math.Clamp(
                max: Capacity,
                min: 0,
                value: probeAvatarLimit!.Value
            )
            : Capacity
        );

        for (var position = 0; (position < count); position++) {
            var avatar = (bounded
                ? ProbeOrder[position]
                : position
            );

            if (
                !probeWorstCase &&
                !isActive(arg: avatar)
            ) {
                continue;
            }

            var range = Ranges[avatar];
            // The probe sources every avatar's own rig at unit scale (the frozen worst case); a live build sources the
            // pinned rig's leaves and the look's uniform scale.
            var rigRange = (probeWorstCase
                ? range
                : Ranges[RigIndex(
                    avatar: avatar,
                    rigFor: rigFor
                )]
            );
            var scale = (probeWorstCase
                ? 1f
                : ScaleFor(
                    avatar: avatar,
                    scaleFor: scaleFor
                )
            );

            for (var slot = range.First; (slot < range.End); slot++) {
                var leaf = Leaves[RigLeaf(
                    rigRange: rigRange,
                    offset: (slot - range.First)
                )];
                var material = (leaf.UseAccent
                    ? accentMaterials[avatar]
                    : bodyMaterials[avatar]
                );
                var leafScale = (leaf.Scale * scale);

                // The catalog's slot ranges are relative to the OWNING emitter's assigned base; the baked instruction
                // lanes are absolute indices into the composed buffer, so the base is added exactly here.
                var packedSlot = (slotBase + slot);

                builder.BeginInstanceDynamic(
                    slot: packedSlot,
                    boundOffset: Vector3.Zero,
                    boundRadius: (LeafBoundRadius * scale),
                    active: true
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
    /// <summary>Returns the literal avatar-local eyeball attachment point for a first-person camera. It follows the
    /// catalog's authored head leaf, so camera and morphology cannot silently drift apart.</summary>
    public static Vector3 EyeOffset(int avatar) {
        var head = Leaves[(Ranges[avatar].First + 3)];

        return ((head.Anchor + head.AuthoredOffset) + new Vector3(
            x: 0f,
            y: 0.015f,
            z: -0.12f
        ));
    }
    /// <summary>Returns the avatar's stable descriptor identity. The digest folds the Puck.Maths R1/R2 source samples
    /// that author the rig, and catalog construction rejects a collision across the built-in 128.</summary>
    public static ulong IdentityHash(int avatar) => IdentityHashes[avatar];
    /// <summary>Returns the exact authored instruction count for an avatar.</summary>
    public static int InstructionCount(int avatar) => (Ranges[avatar].Count * InstructionsPerLeaf);
    /// <summary>Resolves the catalog rig a look's geometry sources from: an authored <c>Catalog(Index)</c> pin, or
    /// <paramref name="catalogRig"/> for an unpinned catalog look or a Creation look (the occupant-owned carried rig
    /// — the same fallback <c>rig = -1</c> takes on <see cref="PackTransforms"/>/<see cref="TryPartOffset"/>/
    /// <see cref="TryPartPose(int, string, int, System.ReadOnlySpan{DynamicTransform}, out SdfAnchor)"/>). The ONE
    /// selector every consumer of a look's rig — emission and part-anchor resolution alike — must call, so a body's
    /// rendered geometry and its part anchors never disagree about which rig it carries.</summary>
    /// <param name="look">The entity's resolved look.</param>
    /// <param name="catalogRig">The entity's own carried catalog rig — the fallback for an unpinned look.</param>
    public static int RigFor(WorldLook look, byte catalogRig) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned })
        ? pinned
        : catalogRig
    );
    /// <summary>Packs one avatar's root pose plus movement-driven gait into its frozen leaf slots. A pinned rig sources
    /// the leaf poses (clamped to the entity's own slot range) and the look's uniform scale multiplies the anchor
    /// offsets — rig = avatar, scale = 1 reproduce the pre-look behaviour.</summary>
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
        var range = Ranges[avatar];
        var rigRange = Ranges[((rig < 0)
            ? avatar
            : rig)];

        if (transforms.Length < range.End) {
            throw new ArgumentException(
                message: $"The avatar transform span has {transforms.Length} slots; avatar {avatar} requires {range.End}.",
                paramName: nameof(transforms)
            );
        }

        for (var slot = range.First; (slot < range.End); slot++) {
            var leaf = Leaves[RigLeaf(
                rigRange: rigRange,
                offset: (slot - range.First)
            )];
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
    /// <param name="rig">The pinned catalog rig, or -1 for the entity's own rig.</param>
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
            ? avatar
            : rig)];
        var leaf = Leaves[RigLeaf(
            offset: relativeSlot,
            rigRange: rigRange
        )];

        offset = ((leaf.Anchor * scale) + leaf.AuthoredOffset);

        return true;
    }
    /// <summary>Resolves a published part's live pose from the packed dynamic transforms.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="rig">The pinned catalog rig, or -1 for the entity's own rig.</param>
    /// <param name="transforms">The frame's packed dynamic-transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/> and the slot is packed.</returns>
    public static bool TryPartPose(int avatar, string partId, int rig, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            pose = default;

            return false;
        }

        var transformSlot = (Ranges[avatar].First + relativeSlot);

        if (((uint)transformSlot) >= ((uint)transforms.Length)) {
            pose = default;

            return false;
        }

        var packed = transforms[transformSlot];
        var rigRange = Ranges[((rig < 0)
            ? avatar
            : rig)];
        var leaf = Leaves[RigLeaf(
            offset: relativeSlot,
            rigRange: rigRange
        )];

        pose = new SdfAnchor(
            Position: (packed.Position + Vector3.Transform(
                value: leaf.AuthoredOffset,
                rotation: packed.Orientation
            )),
            Orientation: packed.Orientation
        );

        return true;
    }
    /// <summary>Resolves a published part's live pose from a list-backed packed dynamic-transform buffer.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="rig">The pinned catalog rig, or -1 for the entity's own rig.</param>
    /// <param name="transforms">The frame's packed dynamic-transform buffer.</param>
    /// <param name="pose">The live part pose, or default when unresolved.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/> and the slot is packed.</returns>
    public static bool TryPartPose(int avatar, string partId, int rig, IReadOnlyList<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(transforms);

        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            pose = default;

            return false;
        }

        var transformSlot = (Ranges[avatar].First + relativeSlot);

        if (((uint)transformSlot) >= ((uint)transforms.Count)) {
            pose = default;

            return false;
        }

        var packed = transforms[transformSlot];
        var rigRange = Ranges[((rig < 0)
            ? avatar
            : rig)];
        var leaf = Leaves[RigLeaf(
            offset: relativeSlot,
            rigRange: rigRange
        )];

        pose = new SdfAnchor(
            Position: (packed.Position + Vector3.Transform(
                value: leaf.AuthoredOffset,
                rotation: packed.Orientation
            )),
            Orientation: packed.Orientation
        );

        return true;
    }
    /// <summary>Resolves a published part id to the avatar's stable packed transform slot.</summary>
    /// <param name="avatar">The avatar index.</param>
    /// <param name="partId">The ordinal, case-sensitive authored part identifier.</param>
    /// <param name="transformSlot">The catalog-relative packed transform slot, or -1 when unresolved.</param>
    /// <returns><see langword="true"/> when the procedural look publishes <paramref name="partId"/>.</returns>
    public static bool TryPartTransformSlot(int avatar, string partId, out int transformSlot) {
        if (!Parts.TryResolve(
            partId: partId,
            transformSlot: out var relativeSlot
        )) {
            transformSlot = -1;

            return false;
        }

        transformSlot = (Ranges[avatar].First + relativeSlot);

        return true;
    }

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
