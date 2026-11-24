using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Text;
using Puck.World.Authoring;

namespace Puck.World.Client;

public sealed partial class WorldRigidAssets {
    private sealed class StaticAsset(WorldPrototype creation) {
        public readonly WorldPrototype Creation = creation;
        public float MaxScale;
        public readonly TemplateScratch Scratch = new(creation);
        public float BakeScale;
        public SdfMeshTemplateRange Range;
    }
    // Audience is the draw tag every draw of the copy carries (SdfMeshAudience): neutral for a row every reader is shown
    // alike, the residency's own for a row the disclosure varies, an overlay's for that overlay's row.
    private sealed record StaticCopy(StaticAsset Asset, WorldPlacement Placement, WorldPickTarget Target, object MotionKey, Vector3 Position, Quaternion Orientation, byte Audience);
    // One static row as the latest reconcile admitted it: the creation it showed, the audience its copies are drawn for,
    // whether it was admitted, and the copies its stamp placed (empty for a refused row or an empty stamp). A row is
    // immutable, so the same instance showing the same creation instance is admitted alike, and the same compiled stamp
    // (WorldPlacementStamp keeps one per row while its frame and inputs stand) places the same copies.
    // A static row as the roster meets it: the row instance, compared by reference, and the overlay's view audience it is
    // placed for, zero for the presentation's own.
    private readonly record struct RowKey(WorldPlacement Placement, byte Overlay) {
        public bool Equals(RowKey other) => (ReferenceEquals(objA: Placement, objB: other.Placement) && (Overlay == other.Overlay));
        public override int GetHashCode() => HashCode.Combine(value1: System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: Placement), value2: Overlay);
    }
    private sealed class StaticRow(WorldPrototype creation, string? refusal, byte audience) {
        public readonly WorldPrototype Creation = creation;
        public readonly string? Refusal = refusal;
        public readonly byte Audience = audience;
        public CompiledPlacementStamp? Stamp;
        public StaticAsset? Asset;
        public StaticCopy[] Copies = [];
    }
    private readonly List<StaticAsset> m_staticAssets = [];
    private readonly List<StaticCopy> m_staticCopies = [];
    // The rows the latest reconcile met, by instance, and the scratch the next reconcile fills before the two swap.
    private Dictionary<RowKey, StaticRow> m_staticRowsMet = [];
    private Dictionary<RowKey, StaticRow> m_staticRowsNext = [];
    private readonly Dictionary<string, StaticAsset> m_staticVariants = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, StaticAsset> m_staticVariantsNext = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, int> m_staticUnsupported = new(comparer: StringComparer.Ordinal);
    private readonly List<SdfVolume> m_staticVolumes = [];
    private WorldDefinition? m_staticDefinition;
    private IReadOnlyList<WorldPlacement>? m_staticRows;
    private IReadOnlyList<WorldPrototype>? m_staticCreations;
    private ulong m_staticSeed;
    private string? m_staticScope;

    /// <summary>Gets the last emission's occurrences with at least one shape left visible by isolation.</summary>
    public int StaticVisibleCopies { get; private set; }
    /// <summary>Returns whether this exact row is owned by the retained static-copy roster.</summary>
    public bool ContainsPlacement(WorldPlacement placement) => (m_staticRowsMet.TryGetValue(key: new RowKey(Placement: placement, Overlay: 0), value: out var row) && (row.Refusal is null));

    /// <summary>Prepares static occurrences from the same definition and rows the visual scene will emit. This is the
    /// sole admission door: visual emission skips exactly these rows; query emission never passes this catalog. A
    /// definition that replaces some rows costs those rows: a row instance met before, showing the same creation
    /// instance under the same seed and scope, keeps its verdict, and keeps its copies while its compiled stamp
    /// stands.</summary>
    public void ReconcileStatic(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements, string? scope = null, bool riding = false, IReadOnlyList<WorldAudienceOverlay>? overlays = null) {
        m_staticRiding = riding;
        overlays ??= [];
        var seed = (definition.Generation?.WorldSeed ?? 0UL);
        if (ReferenceEquals(m_staticDefinition, definition) && ReferenceEquals(m_staticRows, placements) && ReferenceEquals(m_staticCreations, definition.Creations) && (m_staticSeed == seed) && (m_staticScope == scope) && SameOverlays(overlays: overlays)) { return; }
        // Copies carry the scope and the seed places them, so a change of either starts every row again.
        var keeps = ((m_staticSeed == seed) && (m_staticScope == scope));
        m_staticDefinition = definition;
        m_staticRows = placements;
        m_staticCreations = definition.Creations;
        m_staticSeed = seed;
        m_staticScope = scope;
        m_staticOverlays = [.. overlays];
        if (!keeps) { m_staticRowsMet.Clear(); }
        var met = m_staticRowsMet;
        var next = m_staticRowsNext;
        var variants = m_staticVariantsNext;
        var rebuilt = 0;

        next.Clear();
        variants.Clear();
        m_staticAssets.Clear();
        m_staticCopies.Clear();
        m_staticUnsupported.Clear();
        rebuilt += ReconcileRows(creations: definition, met: met, next: next, overlay: null, rows: definition, placements: placements, scope: scope, seed: seed, variants: variants);
        foreach (var overlay in overlays) {
            rebuilt += ReconcileRows(creations: definition, met: met, next: next, overlay: overlay.Audience, rows: overlay.Definition, placements: overlay.Definition.Placements, scope: scope, seed: seed, variants: variants);
        }
        m_staticRowsMet = next;
        m_staticRowsNext = met;
        m_staticVariants.Clear();
        foreach (var (key, asset) in variants) { m_staticVariants.Add(key, asset); }
        if (!StaticTemplatesStand) { StaticTemplateRevision++; }
        WorldCreationWork.ProcessWork.Count(WorldCreationWork.StaticReconciles);
        WorldCreationWork.ProcessWork.Add(WorldCreationWork.StaticCopiesRebuilt, rebuilt);
    }
    // The overlays the latest reconcile placed copies for.
    private WorldAudienceOverlay[] m_staticOverlays = [];
    // Whether the overlays are the ones the latest reconcile placed, audience by audience and document by instance.
    private bool SameOverlays(IReadOnlyList<WorldAudienceOverlay> overlays) {
        if (overlays.Count != m_staticOverlays.Length) { return false; }
        for (var index = 0; (index < overlays.Count); index++) {
            if ((overlays[index].Audience != m_staticOverlays[index].Audience) || !ReferenceEquals(objA: overlays[index].Definition, objB: m_staticOverlays[index].Definition)) { return false; }
        }
        return true;
    }
    // Reconciles one document's static rows into the roster: the presentation's own rows (overlay null), each tagged for
    // every view when no reader is shown it otherwise and for the residency's own views when the disclosure varies it
    // (WorldPlacementAudiences.Varies); or an overlay's rows, of which only those the disclosure varies are placed, each
    // tagged for that overlay's views alone, since every other row is the presentation's own. Creations always resolve
    // in the presentation's document, whose templates every copy draws. Returns the copies placed anew.
    private int ReconcileRows(WorldDefinition creations, WorldDefinition rows, IReadOnlyList<WorldPlacement> placements, byte? overlay, Dictionary<RowKey, StaticRow> met, Dictionary<RowKey, StaticRow> next, Dictionary<string, StaticAsset> variants, string? scope, ulong seed) {
        IReadOnlyDictionary<string, WorldPlacement>? byId = null;
        var rebuilt = 0;

        foreach (var placement in placements) {
            var varies = (MayVary(placement: placement) && WorldPlacementAudiences.Varies(definition: creations, placement: placement, placementsById: (byId ??= WorldPlacementIndex.ById(placements: placements))));
            if ((overlay is not null) && !varies) { continue; }
            var audience = ((overlay is { } own) ? SdfMeshAudience.DrawTagOf(viewAudience: own) : (varies ? SdfMeshAudience.Own : SdfMeshAudience.Neutral));
            var creation = WorldDefinitionRows.FindCreation(creations.Creations, placement.ShownPrototypeId);
            if ((creation is null) || !CopiesRow(placement: placement, creation: creation)) { continue; }
            var key = new RowKey(Placement: placement, Overlay: (overlay ?? 0));
            if (!met.TryGetValue(key: key, value: out var row) || !ReferenceEquals(objA: row.Creation, objB: creation) || (row.Audience != audience)) {
                row = new StaticRow(audience: audience, creation: creation, refusal: Refusal(creation: creation, placement: placement));
            }
            // A row listed twice is one occurrence set, as the instance it is. Sessions of one disclosure share their
            // delivered row instances, with the presentation's too where a row stands as authored, so a row is met once
            // for the presentation and once for each overlay that holds it.
            if (!next.TryAdd(key: key, value: row)) { continue; }
            if (row.Refusal is { } reason) {
                m_staticUnsupported[reason] = (m_staticUnsupported.GetValueOrDefault(reason) + 1);
                continue;
            }
            var stamp = WorldPlacementStamp.Resolve(rows, placement, worldSeed: seed);
            if (stamp.Count == 0) { row.Stamp = stamp; row.Copies = []; continue; }
            // A riding row is one copy rooted on its body; a pattern of occurrences stays with the stamp pool.
            if ((placement.Attach is not null) && (stamp.Count != 1)) { next.Remove(key: key); m_staticUnsupported["riding-pattern"] = (m_staticUnsupported.GetValueOrDefault("riding-pattern") + 1); continue; }
            // A responding row holds a template for every creation it can show, in its authored order, so a sweep that
            // moves its holding swaps the copies' template and leaves the template set, and the program, standing.
            if (placement.Respond is { Count: > 0 } responses) {
                AdmitVariant(definition: creations, placement: placement, prototypeId: placement.PrototypeId, variants: variants);
                foreach (var response in responses) { AdmitVariant(definition: creations, placement: placement, prototypeId: response.PrototypeId, variants: variants); }
            }
            var asset = AdmitAsset(creation: creation, scale: placement.Scale, variants: variants);
            if (!ReferenceEquals(objA: row.Stamp, objB: stamp) || !ReferenceEquals(objA: row.Asset, objB: asset)) {
                var copies = new StaticCopy[stamp.Count];
                var visitor = new StaticCopyVisitor(copies, asset, placement, stamp, scope, audience);

                stamp.ForEach(ref visitor);
                row.Stamp = stamp;
                row.Asset = asset;
                row.Copies = copies;
                rebuilt += copies.Length;
            }
            m_staticCopies.AddRange(collection: row.Copies);
        }
        return rebuilt;
    }
    // Whether a row can be one the disclosure varies at all: under a parent, or responding. Every other row is shown
    // alike to every reader, so the parent walk is never taken for it.
    private static bool MayVary(WorldPlacement placement) => ((placement.Parent is not null) || (placement.Respond is { Count: > 0 }));
    /// <summary>Returns why one residency of a world could not draw a document's varying placements as rigid copies for
    /// their audience alone, or <see langword="null"/> when it can: every placement the disclosure varies
    /// (<see cref="WorldPlacementAudiences.Varies"/>) shows a creation of the presentation's document that this catalog
    /// copies (a rigid static stamp, or with <paramref name="riding"/> a responding row riding a body). A varying row
    /// anything else drew would enter a merged bake group or the stamp pool, whose draws every view of the residency
    /// draws, so a viewer of another disclosure would be shown it.</summary>
    /// <param name="rows">The document whose placements are judged: the presentation's own, or a session's delivery.</param>
    /// <param name="creations">The presentation's document, whose creations the copies draw.</param>
    /// <param name="riding">Whether the catalog roots riding responding rows on their bodies.</param>
    /// <returns>The first refusal, naming the placement and why, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> or <paramref name="creations"/> is <see langword="null"/>.</exception>
    public static string? AudienceRefusal(WorldDefinition rows, WorldDefinition creations, bool riding) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: creations);

        var placements = rows.Placements;
        IReadOnlyDictionary<string, WorldPlacement>? byId = null;

        foreach (var placement in placements) {
            if (!MayVary(placement: placement) || !WorldPlacementAudiences.Varies(definition: creations, placement: placement, placementsById: (byId ??= WorldPlacementIndex.ById(placements: placements)))) { continue; }
            // A deal template renders nothing itself; its dealt children are the rows.
            if (placement.Deal is not null) { continue; }
            if (WorldDefinitionRows.FindCreation(creations.Creations, placement.ShownPrototypeId) is not { } creation) { return $"placement '{placement.Id}' shows '{placement.ShownPrototypeId}', which the presentation's document does not hold"; }
            var copies = (WorldPlacementStamper.IsStaticStamp(placement, creation) || (riding && Rides(placement: placement, creation: creation)));
            if (!copies) { return $"placement '{placement.Id}' is no rigid static copy (animated, inhabited or riding)"; }
            if (Refusal(creation: creation, placement: placement) is { } reason) { return $"placement '{placement.Id}' is no rigid copy ({reason})"; }
        }
        return null;
    }
    /// <summary>Gets whether any copy the latest placement placed is drawn for an audience
    /// (<see cref="SdfMeshAudience.DrawTagOf"/> of <paramref name="viewAudience"/>), so a view of that audience could
    /// draw it: an audience is handed to another viewer only once no copy carries it.</summary>
    /// <param name="viewAudience">The view audience, one or more.</param>
    /// <returns>Whether a copy carries it.</returns>
    public bool CarriesAudience(byte viewAudience) => m_carriedAudiences[SdfMeshAudience.DrawTagOf(viewAudience: viewAudience)];
    // Which draw tags the latest placement's copies carry.
    private readonly bool[] m_carriedAudiences = new bool[(byte.MaxValue + 1)];
    // The template a creation's copies share, admitted once a reconcile and scaled to the largest row showing it.
    private StaticAsset AdmitAsset(WorldPrototype creation, float scale, Dictionary<string, StaticAsset> variants) {
        var key = creation.Id.Value;

        if (!variants.TryGetValue(key, out var asset)) {
            if (!m_staticVariants.TryGetValue(key, out asset) || !ReferenceEquals(asset.Creation, creation)) { asset = new StaticAsset(creation); }
            asset.MaxScale = 0f;
            variants.Add(key, asset);
            m_staticAssets.Add(asset);
        }
        asset.MaxScale = MathF.Max(asset.MaxScale, scale);
        return asset;
    }
    // Whether this catalog may copy a row's creation: a static stamp, or a responding row riding a body (an attached row
    // whose every response is a rigid creation), which the stamp pool otherwise registers and composes again at every
    // response. A copy this catalog admits the stamp pool leaves alone (WorldCreationInstances.ReconcileSlots).
    private bool CopiesRow(WorldPlacement placement, WorldPrototype creation) =>
        (WorldPlacementStamper.IsStaticStamp(placement, creation) || (m_staticRiding && Rides(placement: placement, creation: creation)));
    // Whether the latest reconcile copies riding rows: only a catalog whose owner packs it (Pack) roots them on a body.
    private bool m_staticRiding;
    private static bool Rides(WorldPlacement placement, WorldPrototype creation) =>
        ((placement.Attach is not null) && (placement.Respond is { Count: > 0 }) && (placement.Inhabit is null) && (placement.Deal is null) && !WorldPlacementStamper.IsAnimated(creation: creation));
    // Admits the template of one creation a responding row can show, when that creation would be a rigid copy there.
    private void AdmitVariant(WorldDefinition definition, WorldPlacement placement, string prototypeId, Dictionary<string, StaticAsset> variants) {
        if ((WorldDefinitionRows.FindCreation(definition.Creations, prototypeId) is not { } creation) || !CopiesRow(placement: placement, creation: creation) || (Refusal(creation: creation, placement: placement) is not null)) { return; }
        _ = AdmitAsset(creation: creation, scale: placement.Scale, variants: variants);
    }
    /// <summary>Gets whether the static templates the latest reconcile admitted are the ones the latest emission wrote, at
    /// the same scales, so the copies it placed can be placed again (<see cref="PlaceStaticCopies"/>) without composing
    /// the program again.</summary>
    public bool StaticTemplatesStand {
        get {
            if (m_staticAssets.Count != m_emittedStaticAssets.Count) { return false; }
            for (var index = 0; (index < m_staticAssets.Count); index++) {
                var (asset, scale) = m_emittedStaticAssets[index];

                if (!ReferenceEquals(objA: m_staticAssets[index], objB: asset) || (m_staticAssets[index].MaxScale != scale)) { return false; }
            }
            return true;
        }
    }
    /// <summary>Gets a counter that moves when a reconcile admits a static template set other than the one the latest
    /// emission wrote: a program-rebuild watch component of the scene that composes this catalog.</summary>
    public int StaticTemplateRevision { get; private set; }
    /// <summary>Gets a counter that moves when a holding moved on a responding row this catalog cannot present by its copies
    /// (<see cref="PresentHoldings"/>): a program-rebuild watch component of the scene that composes this catalog.</summary>
    public int HoldingComposeRevision { get; private set; }
    // Each responding row's holding as the latest PresentHoldings presented it.
    private readonly Dictionary<string, int> m_presentedHoldings = new(comparer: StringComparer.Ordinal);

    /// <summary>Presents a delivery that moved holdings alone without reconciling anything else: reconciles the static rows
    /// against the delivered definition, and while every responding row whose holding moved is a copy here, the copy count
    /// stands and the templates stand, places the copies again (<see cref="PlaceStaticCopies"/>) and answers
    /// <see langword="true"/>. Otherwise it presents nothing and answers <see langword="false"/>, and the caller reconciles
    /// in full and calls <see cref="PresentHoldings"/>.
    /// <para>The invariant: a delivery that moved holdings alone reaches the program only through
    /// <see cref="HoldingComposeRevision"/> and <see cref="StaticTemplateRevision"/>, since the readers that deliver it
    /// keep such a delivery out of their rebuild watch (<c>WorldClient.HoldingRevision</c>,
    /// <c>WorldSessionMirror.ComposingRevision</c>).</para></summary>
    /// <param name="definition">The delivered definition.</param>
    /// <returns>Whether the delivery was presented by the copies alone.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool TryPresentHoldingsAlone(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        var placements = definition.Placements;
        var copies = m_staticCopies.Count;

        ReconcileStatic(definition: definition, overlays: m_staticOverlays, placements: placements, riding: m_staticRiding, scope: m_staticScope);
        if ((m_staticCopies.Count != copies) || !StaticTemplatesStand) { return false; }
        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            if (placement.Respond is not { Count: > 0 }) { continue; }
            if (m_presentedHoldings.TryGetValue(key: placement.Id, value: out var held) && (held == placement.Holding)) { continue; }
            if (!ContainsPlacement(placement: placement)) { return false; }
        }
        for (var index = 0; (index < placements.Count); index++) {
            if (placements[index].Respond is { Count: > 0 }) { m_presentedHoldings[placements[index].Id] = placements[index].Holding; }
        }
        PlaceStaticCopies();
        return true;
    }
    /// <summary>Presents the responses the latest reconcile met. A responding row that is a rigid static copy keeps a
    /// template for every creation it can show (<see cref="ReconcileStatic"/>), so while the templates stand its copies
    /// alone move (<see cref="PlaceStaticCopies"/>). A holding that moved on any other row moves
    /// <see cref="HoldingComposeRevision"/>, and a template set the latest emission did not write moves
    /// <see cref="StaticTemplateRevision"/>; either composes the program again. Call after every reconcile of the rows.</summary>
    /// <param name="placements">The placement rows the latest reconcile read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="placements"/> is <see langword="null"/>.</exception>
    public void PresentHoldings(IReadOnlyList<WorldPlacement> placements) {
        ArgumentNullException.ThrowIfNull(argument: placements);
        var compose = false;

        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            if (placement.Respond is not { Count: > 0 }) { continue; }
            if (m_presentedHoldings.TryGetValue(key: placement.Id, value: out var held) && (held == placement.Holding)) { continue; }
            m_presentedHoldings[placement.Id] = placement.Holding;
            compose |= !ContainsPlacement(placement: placement);
        }
        if (compose) {
            HoldingComposeRevision++;
        } else if (StaticTemplatesStand) {
            PlaceStaticCopies();
        }
    }
    // The templates the latest emission wrote, with the scale each baked at.
    private readonly List<(StaticAsset Asset, float Scale)> m_emittedStaticAssets = [];

    // Why a static row is left to the ordinary visual emission, or null when it is a rigid static copy.
    private static string? Refusal(WorldPrototype creation, WorldPlacement placement) {
        var document = creation.EngineDocument;

        return (!IsRigid(creation) ? "not-rigid" :
            ((WorldStateDocumentValues.HasReference(creation.Document) || WorldStateDocumentValues.HasReference(placement)) ? "state-reference" :
            ((placement.Mirror is not null) ? "reflection" :
            (((document.TextRuns is { Count: > 0 }) && (placement.Respond is not { Count: > 0 })) ? "text" :
            ((creation.Mesh is not null) ? "authored-mesh" :
            (document.HasGlass ? "glass" :
            ((document.Shapes is not { Count: > 0 }) ? "no-shapes" :
            (((placement.Attach is not null) && (document.Volumes is { Count: > 0 })) ? "riding-volumes" : null))))))));
    }
    private struct StaticCopyVisitor(StaticCopy[] copies, StaticAsset asset, WorldPlacement placement, CompiledPlacementStamp stamp, string? scope, byte audience) : ICreationStampVisitor {
        private int m_ordinal;
        public void Visit(CreationStampInstance instance) {
            var index = m_ordinal++;
            var ordinal = stamp.OccurrenceAt(index);
            var target = new WorldPickTarget(placement.Id, null) { Prototype = asset.Creation.Id, Occurrence = ordinal };
            // An overlay's copy of a row keeps a motion history apart from the presentation's copy of the same id.
            var motion = ((audience > SdfMeshAudience.Own) ? (object)(scope, audience, placement.Id, ordinal) : (scope, placement.Id, ordinal));
            copies[index] = new StaticCopy(asset, placement, target, motion, instance.Origin, stamp.Rotation, audience);
        }
    }
    // Only body assets need template transform slots. Static templates retain the ordinary instance partitions.
    private void PackTemplates(Span<DynamicTransform> transforms, int slotBase) {
        for (var index = 0; (index < m_slotCount); index++) { transforms[slotBase + index] = new DynamicTransform(Vector3.Zero, Quaternion.Identity); }
    }
    private void EmitStaticAssets(SdfProgramBuilder builder, WorldBakedColors colors, WorldPickMapBuilder picks, WorldShapeIsolation? isolation, PackedFontAtlasCatalog? text) {
        foreach (var asset in m_staticAssets) {
            var creation = asset.Creation;
            var document = creation.EngineDocument;
            var scratch = asset.Scratch;
            var first = builder.InstanceCount;
            // No old range may bind another template when isolation removes all of this one's shapes.
            asset.Range = new SdfMeshTemplateRange(first, 0);
            asset.BakeScale = asset.MaxScale;
            var hidden = isolation?.HiddenFor(creation.Id, document);
            if ((hidden is not null) && (Array.IndexOf(hidden, false) < 0)) { continue; }
            var length = WorldPlacementStamper.PaletteLength(document);
            if (scratch.Palette.Length != length) { scratch.Palette = new int[length]; }
            WorldPlacementStamper.FillPalette(builder, colors, document, null, scratch.Palette, hidden: hidden);
            picks.Materials(creation.Id, scratch.Palette);
            if ((scratch.Emission is { } held) && (scratch.BakeScale == asset.BakeScale) && ReferenceEquals(scratch.Hidden, hidden) && ReferenceEquals(scratch.Text, text) && builder.CanAppendEmission(held)) {
                builder.AppendEmission(held);
            } else {
                var mark = builder.MarkEmission();
                // The existing static visitor owns every shape bound, detail, union partition and warped split.
                // The largest demanded authored scale bakes at the ordinary world-space quality; smaller occurrences
                // share that result under a relative matrix, without turning a default into explicit authored detail.
                WorldPlacementStamper.EmitPlacement(builder, document, m_staticDefinition!, scratch.Palette,
                    new WorldPlacement(string.Empty, creation.Id, Vector3.Zero, 0f, asset.BakeScale),
                    textCatalog: text, worldSeed: m_staticSeed, volumes: null, mesh: null, meshMaterial: 0,
                    meshDraws: null, meshScope: null, firstMesh: 0, hidden: hidden,
                    frameOverride: new CompiledPlacementFrame(Vector3.Zero, 0f));
                scratch.Emission = builder.RetainEmission(mark);
                scratch.BakeScale = asset.BakeScale;
                scratch.Hidden = hidden;
                scratch.Text = text;
            }
            asset.Range = new SdfMeshTemplateRange(first, (builder.InstanceCount - first));
            picks.Instances(first, builder.InstanceCount, scratch.Target);
            WorldCreationWork.ProcessWork.Count(WorldCreationWork.StaticTemplates);
        }
        m_emittedStaticAssets.Clear();
        foreach (var asset in m_staticAssets) { m_emittedStaticAssets.Add(item: (asset, asset.MaxScale)); }
        PlaceStaticCopies();
    }
    /// <summary>Writes every static copy's row from the templates the latest emission wrote: its pose, its scale against its
    /// template's baked scale, and the template range it draws. An emission places them; a reconcile whose templates stand
    /// (<see cref="StaticTemplatesStand"/>) places them again, so a response that swaps a row between creations it keeps a
    /// template for moves its copies alone.</summary>
    /// <exception cref="InvalidOperationException">The templates the latest reconcile admitted are not the ones the latest
    /// emission wrote.</exception>
    public void PlaceStaticCopies() {
        if (!StaticTemplatesStand) { throw new InvalidOperationException(message: "Static copies are placed only over the templates the latest emission wrote."); }
        var isolation = m_isolation;

        m_staticVolumes.Clear();
        m_ridingCopies.Clear();
        Array.Clear(array: m_carriedAudiences);
        StaticVisibleCopies = 0;
        for (var index = 0; index < m_staticCopies.Count; index++) {
            var occurrence = m_staticCopies[index];
            var asset = occurrence.Asset;
            var copyIndex = (m_bodies.Count + index);
            var previous = m_copies[copyIndex];
            var scale = (occurrence.Placement.Scale / asset.BakeScale);

            m_carriedAudiences[occurrence.Audience] = true;
            if (occurrence.Placement.Attach is not null) {
                // A riding copy keeps the pose its body last gave it; the next pack reads the body again (PackRiding).
                _ = previous.Update(-1, occurrence.Target, occurrence.MotionKey, previous.Transform, scale, true, null, null, 0, [], usesRootMaterialFrame: false, templateRange: asset.Range, audience: occurrence.Audience);
                m_ridingCopies.Add(item: index);
                if (asset.Range.InstanceCount > 0) { StaticVisibleCopies++; }
                continue;
            }
            var transform = new DynamicTransform(occurrence.Position, occurrence.Orientation);
            _ = previous.Update(-1, occurrence.Target, occurrence.MotionKey, transform, scale, false, null, null, 0, [], usesRootMaterialFrame: false, templateRange: asset.Range, audience: occurrence.Audience);
            // A light or medium is no draw a view's audience filters, so an overlay's copy brings none: the
            // presentation's own copy of the row lights every view.
            if ((asset.Range.InstanceCount > 0) && (occurrence.Audience > SdfMeshAudience.Own)) {
                StaticVisibleCopies++;
            } else if (asset.Range.InstanceCount > 0) {
                var hidden = isolation?.HiddenFor(asset.Creation.Id, asset.Creation.EngineDocument);
                StaticVisibleCopies++;
                WorldPlacementStamper.AppendStaticVolumes(asset.Creation.EngineDocument, occurrence.Position, occurrence.Orientation, occurrence.Placement.Scale, m_staticVolumes, hidden);
            }
        }
        m_volumesDirty = true;
        WorldCreationWork.ProcessWork.Add(WorldCreationWork.StaticCopies, m_staticCopies.Count);
        CopiesChanged();
    }
    // The static copies that ride a body, by their index in m_staticCopies, as the latest placement found them.
    private readonly List<int> m_ridingCopies = [];

    // Roots every riding copy on its body's presented pose (WorldAttachedPose), naming each copy whose row moved. A riding
    // copy is dynamic: its pose reaches its draws through its root row, as a body copy's does.
    private void PackRiding(IWorldStampSource source) {
        foreach (var index in m_ridingCopies) {
            var occurrence = m_staticCopies[index];
            var copyIndex = (m_bodies.Count + index);
            var previous = m_copies[copyIndex];
            var (position, orientation) = WorldAttachedPose.Root(attach: occurrence.Placement.Attach!, source: source);

            if (previous.Update(-1, occurrence.Target, occurrence.MotionKey, new DynamicTransform(Position: position, Orientation: orientation), previous.Scale, true, null, null, 0, [], usesRootMaterialFrame: false, templateRange: occurrence.Asset.Range)) {
                m_changedCopies.Add(item: copyIndex);
            }
        }
    }
}
