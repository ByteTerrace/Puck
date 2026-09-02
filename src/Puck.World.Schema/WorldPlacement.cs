using Puck.Assets.Documents;
using System.Numerics;
using System.Text.Json.Serialization;
using Puck.World.Authoring;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// One creation asset row — a whole <c>puck.creation.v1</c> document embedded inline, in canonical form, in the world
/// file with its identity hash pinned beside it. The document and hash must come from the same
/// <see cref="Puck.Assets.Documents.CanonicalDocument{TDocument}"/>: the compose boundary canonicalizes on upsert
/// and rejects a hash the pipeline did not itself compute; the validator re-verifies the pin on every candidate, so a
/// tampered world file rejects loudly. World files stay self-contained — the CAS is an authoring-time import/export
/// cache, never a load-time dependency.
/// </summary>
/// <param name="Id">The row's stable string id — its mutation address and the handle placements reference — authored
/// literally or through a Text state cell.</param>
/// <param name="Document">The canonical (validated + normalized) creation document.</param>
/// <param name="HashRaw">The SHA-256 hex64 of the document's canonical bytes (<see cref="Puck.Assets.Documents.CanonicalDocument{TDocument}.Hash"/>
/// on the canonical result the compose boundary produces). ABSENT resolves to the hash computed from
/// <paramref name="Document"/> at load — an author never writes a content hash by hand; see <see cref="Hash"/>.</param>
public sealed record WorldPrototype(
    DocumentIdentifier Id,
    CreationDocument Document,
    [property: JsonPropertyName("hash"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HashRaw = null
) {
    private CreationDocument? m_engineDocument;

    /// <summary>Gets <see cref="Document"/> converted once from the author frame to the engine frame
    /// (<see cref="Puck.World.Authoring.CreationFrame.ToEngine"/>) — every render, collision, and anchor consumer
    /// reads this, never <see cref="Document"/>, which stays the author's own bytes (what <see cref="Hash"/> pins).
    /// Cached on first read since <see cref="WorldPrototype"/> rows are replaced, not mutated, on edit.</summary>
    [JsonIgnore]
    public CreationDocument EngineDocument => (m_engineDocument ??= Puck.World.Authoring.CreationFrame.ToEngine(document: Document));
    /// <summary>Gets the SHA-256 hex64 of <see cref="Document"/>'s canonical bytes — <see cref="HashRaw"/> when
    /// authored, else computed fresh through the same pipeline the validator re-verifies every hash against
    /// (<see cref="Puck.World.Authoring.CreationCanonicalizer"/>), so an absent hash is trivially self-consistent
    /// with no validator special case.</summary>
    [JsonIgnore]
    public string Hash => (HashRaw ?? Puck.World.Authoring.CreationCanonicalizer.Canonicalize(
        document: Document,
        source: Id
    ).Hash);
}
/// <summary>A reflection plane in a placement's local frame.</summary>
/// <param name="Normal">The plane normal.</param>
/// <param name="Offset">The signed plane offset along the normalized <paramref name="Normal"/>.</param>
public sealed record WorldPlacementMirror(DocumentVector3 Normal, float Offset);
/// <summary>A placement's inhabit facet — the row's binding to live population bodies. An inhabited placement is a
/// normal entry in the entity table: it holds a <c>Puck.World.Server.WorldBody</c>, integrates under the named
/// kit, and is addressable as <see cref="WorldAnchor.Entity"/> like any avatar. Its stamp rides the body's pose instead
/// of the row's static transform; the row's position/yaw become its spawn pose. Absent (null) = decoration, the
/// unchanged furniture behaviour.</summary>
/// <param name="Kit">The <see cref="WorldKit.Name"/> the bodies move under. Null resolves the creation's own
/// <see cref="Puck.World.Authoring.CreationBehaviorDocument.Locomotion"/> token AS a kit name — a creation declaring "swim"
/// inhabits the world's kit row named "swim". Neither resolving is a loud rejection naming every kit the world
/// declares.</param>
/// <param name="Look">The <see cref="WorldLook.Name"/> the bodies wear, or null to wear an implicit creation look on
/// this placement's own <c>PrototypeId</c>.</param>
/// <param name="Source">The live, idle, or named producer source the bodies wake on.</param>
/// <param name="Count">How many bodies, bounded by the world's authored peer capacity.</param>
/// <param name="Distribution">The region and deterministic fill sequence that place the bodies relative to the
/// placement root.</param>
public sealed record WorldPlacementInhabit(
    string? Kit,
    string? Look,
    Puck.World.Protocol.IntentSource Source,
    int Count = 1,
    WorldDistribution? Distribution = null
);
/// <summary>A per-instance override of one declared creation face's feed — the face twin of the emission facet's
/// per-instance override channel.</summary>
/// <param name="Face">The declared <see cref="Puck.World.Authoring.CreationFaceDocument.Name"/> to override.</param>
/// <param name="Source">The screen source the face shows, in the existing <see cref="WorldScreenSource"/> vocabulary.</param>
/// <param name="Portal">The face's portal facet (see <see cref="WorldPlacementPortal"/>) — absent (the default)
/// means this face is not a door. Optional and trailing deliberately: a face authored before this facet existed
/// round-trips unchanged, and it composes freely with <paramref name="Source"/> — the door and the screen it shows
/// are independent facts about the same face.</param>
public sealed record WorldPlacementFace(
    string Face,
    WorldScreenSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementPortal? Portal = null
);
/// <summary>A placement's region facet — a named volume row, not a trigger system: any placement may carry one,
/// turning its stamp into a sensing volume the world-events feed watches for body enter/exit edges (see
/// <c>Server.WorldEventFeed</c> and the <c>observe region:&lt;name&gt;</c> grant subject). The region's name is the
/// carrying placement's <see cref="WorldPlacement.Id"/> — one identity, never a second string kept in sync by hand.
/// The volume is a sphere centered on the placement's <see cref="WorldPlacement.Position"/> (the placement's own
/// <see cref="WorldPlacement.Scale"/>/<see cref="WorldPlacement.YawDegrees"/> do not affect it — a region's size is
/// its own authored radius, never derived from the creation's visual bounds). Presentation-only in itself (drawing
/// no geometry); sensing reads the same document-authored center every tick, converted to fixed-point at the same
/// boundary <see cref="WorldSolid"/> facets already cross through — unless the row also carries
/// <see cref="WorldPlacement.Attach"/>, in which case the center is the resolved live body pose instead
/// (<c>Server.WorldEventFeed.CollectRegions</c>, the same resolve <c>world.attachments</c> answers): the sensing
/// sphere follows the carrier, and an inactive carrier senses nobody rather than sensing at a stale point.</summary>
/// <param name="Radius">The sensing radius, world units. Must be finite and positive (validated).</param>
public sealed record WorldPlacementRegion(float Radius);
/// <summary>A placement's grip facet — overrides the world's <see cref="WorldCollision.DefaultHold"/> hold
/// policy for every collider this row compiles, composing as the tighter authoring layer: present, it decides;
/// absent, the row's colliders fall back to the world default. Requires <see cref="WorldPlacement.Solid"/> (nothing
/// else compiles a collider a grip trait could apply to). Every collider a distribution/mirror expands from one row
/// shares the row's single grip decision — a lattice of holdable handholds is authored as one placement, not one
/// per copy.</summary>
/// <param name="Holdable">Whether a body's surface hold may take this row's compiled surface(s), overriding the
/// world default.</param>
public sealed record WorldPlacementGrip(bool Holdable);
/// <summary>A placement's attach facet — binds the row's stamp to a live population body's transform, so the
/// resolved world pose follows that body every tick (an avatar's hat, held item, nameplate, or aura) instead of
/// sitting at the row's own authored <see cref="WorldPlacement.Position"/>/<see cref="WorldPlacement.YawDegrees"/>.
/// The offset rides the body's own local frame — rotated by the body's orientation before adding, the
/// <c>Puck.SdfVm.Views.OrientedFollowRig</c>/<c>FirstPersonRig</c> convention for a moving anchor, never the
/// world-axis <c>FollowRig</c> shape a fixed subject would use. The resolved pose is never written back into the
/// document, and it is derived twice, at two clocks, from the one authored facet:
/// <list type="bullet">
/// <item><description>the authoritative answer is fixed point — the body's fixed-point pose composed with this
/// facet's authored (float, quantized at resolution like every other placement field) offset, by
/// <c>Puck.World.Server.WorldPlacementAttachment.TryResolve</c>, on demand by <c>world.attachments</c> and once per
/// tick by attached local gravity areas;</description></item>
/// <item><description>the rendered pose is presentation float — the same composition over the client's
/// interpolated body pose, packed every frame by <c>Client.WorldStampPool</c>, which is what makes an attached row
/// visibly ride its body as smoothly as the body itself. An attached row draws through that reserved stamp pool and
/// not as a static stamp (<c>Client.WorldPlacementStamper.IsStaticStamp</c>), and it charges
/// <see cref="WorldPlacementPolicy.MaxStampRegistrations"/> like an animated row does.</description></item>
/// </list>
/// Region, solid (under the analytic contact provider), and emission were once refused on the same row as this one
/// because each read the row's own static transform — all three now read the same resolved dynamic pose instead
/// (<c>Server.WorldEventFeed.CollectRegions</c>, <c>Server.WorldColliderSet.RefreshAttached</c>,
/// <c>Server.WorldGravityField.RefreshAttachedAreas</c>,
/// <c>Client.WorldStampPool.TryShapePosition</c>/<c>RootPose</c>), so a region's aura, an analytic collider's
/// hitbox, and an emission's voice all track the carrier: an equipped item's sensing sphere, hitbox, or source point
/// rides the body it is attached to. What stays refused: distribution/mirror (static-stamp-only, the same rule an
/// animated or inhabited row already enforces), inhabit (a row cannot both spawn its own driven bodies and ride
/// another's), and solid specifically under the field contact provider (it compiles every solid row's geometry once
/// into one SDF program and never rebuilds it per tick) — refused by name rather than defining a blend (see
/// <see cref="WorldDefinitionValidator"/>).</summary>
/// <param name="BodyIndex">The 0-based population entity index the placement rides — the same indexing
/// <see cref="WorldAnchor.Entity"/> and the console's <c>body:&lt;n&gt;</c> grant subject use, not the 1-based
/// <c>player.*</c> seat number (<c>body:1</c> is "player 2"). Validated within <c>0..</c>the world's authored
/// population capacity; the target need not be active at author time (see remarks — an inactive body at runtime
/// makes the row contribute nothing, it does not refuse).</param>
/// <param name="LocalOffset">The stamp's position offset in the body's own local frame, world units.</param>
/// <param name="LocalYawDegrees">The stamp's yaw offset from the body's own heading, degrees. Zero rides the
/// body's exact facing.</param>
public sealed record WorldPlacementAttach(int BodyIndex, DocumentVector3 LocalOffset, float LocalYawDegrees = 0f);
/// <summary>
/// One placement instance row — a creation asset stamped into the world by reference: transform + facets as
/// data, addressed by its stable <paramref name="Id"/>. A placement whose creation carries timeline frames is
/// animated: it replays client-side on the render clock through the reserved dynamic-transform pool (distribution/mirror
/// facets are static-stamp-only and reject on an animated row). A placement carrying an <paramref name="Inhabit"/>
/// facet is a live population body rather than furniture (see <see cref="WorldPlacementInhabit"/>); its declared
/// creation eyes derive <see cref="WorldCamera"/> feeds and its declared faces derive screens (both at the delivery
/// boundary, never written to the document).
/// </summary>
/// <param name="Id">The row's stable string id (its mutation address).</param>
/// <param name="PrototypeId">The referenced <see cref="WorldPrototype.Id"/> (must resolve; removal of a referenced
/// creation rejects loudly).</param>
/// <param name="Position">The stamp position, world space. Inert (still validated and stored, but read by nothing —
/// neither the resolve nor the renderer) when <paramref name="Attach"/> is set: the row's live position is the resolved
/// attachment, never this authored one.</param>
/// <param name="YawDegrees">The stamp yaw about +Y, degrees. Same attach caveat as <paramref name="Position"/>.</param>
/// <param name="Scale">The uniform stamp scale (clamped to the placement policy envelope by validation).</param>
/// <param name="Distribution">The placement distribution, or <see langword="null"/> for a single copy. Static
/// placements accept a Lattice, Noise, or Scatter region, each with a <c>none</c> fill — Lattice materializes a
/// regular two-axis grid (<see cref="WorldPlacementStamp.PatternFor"/>); Noise and Scatter materialize a
/// deterministic hash-sampled instance set instead (<see cref="WorldPlacementStamp.SampledFixedOffsetsFor"/>), the
/// placement twin of the field lattice's own Noise/Scatter fills. Refused together with <paramref name="Attach"/>.</param>
/// <param name="Mirror">The authored local reflection plane, or <see langword="null"/> for no reflected copy. Refused
/// together with <paramref name="Attach"/>.</param>
/// <param name="Emission">The placement's emission facet (a synth voice the stamp itself makes — see
/// <see cref="WorldEmission"/>), or <see langword="null"/> for silent. Under <paramref name="Distribution"/> the emission
/// binds to the placement root only. Omitted from the wire when null. Composes with <paramref name="Attach"/>: an
/// attached row's source point rides the resolved live pose (<c>Client.WorldStampPool.TryShapePosition</c>) instead
/// of the row's static position, and an inactive carrier silences the emitter rather than leaving it at a stale point.</param>
/// <param name="Solid">The placement's solidity facet (see <see cref="WorldSolid"/>). Both contact providers compile
/// the creation's emitted shapes; analytic collision uses per-primitive colliders, including exact half-spaces for
/// planes. Omitted from the wire when null. Composes with <paramref name="Attach"/> under the analytic provider only
/// (<c>WorldColliderSet.RefreshAttached</c> recomputes an attached row's colliders every tick from the resolved live
/// pose); still refused together under the field provider, which compiles every solid row's geometry once into one
/// SDF program and never rebuilds it per tick.</param>
/// <param name="Inhabit">The inhabit facet (null = decoration), binding the row to live population bodies. Omitted from
/// the wire when null. Refused together with <paramref name="Attach"/> — a row cannot both spawn its own driven
/// bodies and ride another body's pose.</param>
/// <param name="FaceSources">Per-instance overrides of the creation's declared faces (null = every face shows its
/// declared default). Omitted from the wire when null. Orthogonal to <paramref name="Attach"/> (a content selector,
/// not a transform) — composes freely, like every other facet that now tracks the dynamic pose.</param>
/// <param name="Region">The placement's region facet (see <see cref="WorldPlacementRegion"/>) — a named volume the
/// world-events feed watches for body enter/exit, or <see langword="null"/> for none. Omitted from the wire when null.
/// Composes with <paramref name="Attach"/>: an attached row's sensing sphere centers on the resolved live pose
/// (<c>Server.WorldEventFeed.CollectRegions</c>) instead of the row's static position — see
/// <see cref="WorldPlacementRegion"/>'s own remarks.</param>
/// <param name="Attach">The placement's attach facet (see <see cref="WorldPlacementAttach"/>) — binds the row's
/// resolved world pose to a live population body, or <see langword="null"/> for a static/authored transform (the
/// default, unchanged behavior). Omitted from the wire when null.</param>
/// <param name="Contribution">The placement's contribution facet (see <see cref="WorldPlacementContribution"/>) —
/// marks the row a host-authored slot a federation partner fills, or <see langword="null"/> for an ordinary
/// placement. Omitted from the wire when null. Composes with every other facet: the facet governs which creation the
/// row shows and for how long, never its transform.</param>
/// <param name="Respond">The placement's response facet (see <see cref="WorldPlacementResponse"/>) — the ordered
/// state-driven prototype swaps a lattice-field condition can fire, or <see langword="null"/> for an ordinary
/// placement that always shows <paramref name="PrototypeId"/>. Omitted from the wire when null. Refused together
/// with <paramref name="Attach"/>, <paramref name="Inhabit"/>, and <paramref name="FaceSources"/>.</param>
/// <param name="Grip">The placement's grip facet (see <see cref="WorldPlacementGrip"/>) — overrides the world's
/// default hold policy for this row's compiled surface(s), or <see langword="null"/> to inherit the world default.
/// Omitted from the wire when null. Requires <paramref name="Solid"/> (validated).</param>
public sealed record WorldPlacement(
    string Id,
    string PrototypeId,
    DocumentVector3 Position,
    float YawDegrees,
    float Scale,
    WorldDistribution? Distribution = null,
    WorldPlacementMirror? Mirror = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEmission? Emission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSolid? Solid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementInhabit? Inhabit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacementFace>? FaceSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementRegion? Region = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementAttach? Attach = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementContribution? Contribution = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPlacementResponse>? Respond = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementGrip? Grip = null
);
/// <summary>Adapts placement document facets to the shared creation-stamp vocabulary.</summary>
public static class WorldPlacementStamp {
    /// <summary>Returns the placement's shared reflection plane.</summary>
    public static CreationStampPlane? MirrorFor(WorldPlacement placement) => ((placement.Mirror is { } mirror)
        ? new CreationStampPlane(
            Normal: mirror.Normal,
            Offset: mirror.Offset
        )
        : null
    );
    /// <summary>Returns the placement's shared lattice declaration.</summary>
    public static CreationStampPattern? PatternFor(WorldPlacement placement) => ((placement.Distribution?.Region is WorldDistributionRegion.Lattice lattice)
        ? new CreationStampPattern(
            StepA: lattice.StepA,
            CountA: lattice.CountA,
            StepB: lattice.StepB,
            CountB: lattice.CountB
        )
        : null
    );
    /// <summary>Resolves a Noise/Scatter distribution's placement-local offsets in fixed point — the deterministic,
    /// Q48.16-only instance decision (see <see cref="CreationStampSampling"/>). <see langword="null"/> when the
    /// placement carries no distribution or a Lattice/none region (<see cref="PatternFor"/> governs those instead).</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static IReadOnlyList<FixedVector3>? SampledFixedOffsetsFor(WorldPlacement placement, ulong worldSeed) => placement.Distribution?.Region switch {
        WorldDistributionRegion.Noise noise => CreationStampSampling.ResolveNoise(
            cellSize: FixedQ4816.FromDouble(value: noise.CellSize),
            width: noise.Width,
            depth: noise.Depth,
            frequency: noise.Frequency,
            threshold: FixedQ4816.FromDouble(value: noise.Threshold),
            octaves: noise.Octaves,
            seed: noise.Seed,
            worldSeed: worldSeed
        ),
        WorldDistributionRegion.Scatter scatter => CreationStampSampling.ResolveScatter(
            cellSize: FixedQ4816.FromDouble(value: scatter.CellSize),
            width: scatter.Width,
            depth: scatter.Depth,
            spacing: scatter.Spacing,
            radius: scatter.Radius,
            seed: scatter.Seed,
            worldSeed: worldSeed
        ),
        _ => null,
    };
    /// <summary>The presentation-float widening of <see cref="SampledFixedOffsetsFor"/>, for the renderer's stamp
    /// emission — never fed back into simulation state.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static IReadOnlyList<Vector3>? SampledOffsetsFor(WorldPlacement placement, ulong worldSeed) {
        if (SampledFixedOffsetsFor(placement: placement, worldSeed: worldSeed) is not { } fixedOffsets) {
            return null;
        }

        var offsets = new Vector3[fixedOffsets.Count];

        for (var index = 0; (index < offsets.Length); index++) {
            offsets[index] = fixedOffsets[index].ToVector3();
        }

        return offsets;
    }
    /// <summary>The worst-case, seed-independent materialized copy count a placement's distribution could ever
    /// produce — a Lattice's exact CountA x CountB, a Scatter's exact block count, a Noise grid's worst case
    /// (Width x Depth, since actual admission needs the world seed and is not paid for during validation), or 1 for
    /// no distribution — mirror-doubled and saturated at <paramref name="ceiling"/>. The seed-independent ceiling
    /// the document validator bounds an authored grid against; see <see cref="SampledFixedOffsetsFor"/> for the
    /// actual (seed-resolved) count a booted world materializes.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="ceiling">The largest returned value.</param>
    public static long MaterializedCopyCeiling(WorldPlacement placement, long ceiling = long.MaxValue) {
        var mirror = MirrorFor(placement: placement);

        return placement.Distribution?.Region switch {
            WorldDistributionRegion.Noise noise => WithMirror(
                copies: Math.Min(val1: CreationStampSampling.NoiseInstanceCeiling(width: noise.Width, depth: noise.Depth), val2: ceiling),
                mirror: mirror,
                ceiling: ceiling
            ),
            WorldDistributionRegion.Scatter scatter => WithMirror(
                copies: Math.Min(val1: CreationStampSampling.ScatterInstanceCeiling(width: scatter.Width, depth: scatter.Depth, spacing: scatter.Spacing), val2: ceiling),
                mirror: mirror,
                ceiling: ceiling
            ),
            _ => CreationStampLattice.MaterializedCopyCount(
                pattern: PatternFor(placement: placement),
                sampledCount: null,
                mirror: mirror,
                ceiling: ceiling
            ),
        };

        static long WithMirror(long copies, CreationStampPlane? mirror, long ceiling) => ((mirror is null)
            ? copies
            : CreationStampLattice.MultiplySaturated(ceiling: ceiling, left: copies, right: 2L)
        );
    }
}
