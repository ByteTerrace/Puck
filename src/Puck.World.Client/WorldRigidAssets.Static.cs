using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
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
    private sealed record StaticCopy(StaticAsset Asset, WorldPlacement Placement, WorldPickTarget Target, object MotionKey, Vector3 Position, Quaternion Orientation);
    // One static row as the latest reconcile admitted it: the creation it showed, whether it was admitted, and the
    // copies its stamp placed (empty for a refused row or an empty stamp). A row is immutable, so the same instance
    // showing the same creation instance is admitted alike, and the same compiled stamp (WorldPlacementStamp keeps one
    // per row while its frame and inputs stand) places the same copies.
    private sealed class StaticRow(WorldPrototype creation, string? refusal) {
        public readonly WorldPrototype Creation = creation;
        public readonly string? Refusal = refusal;
        public CompiledPlacementStamp? Stamp;
        public StaticAsset? Asset;
        public StaticCopy[] Copies = [];
    }
    private readonly List<StaticAsset> m_staticAssets = [];
    private readonly List<StaticCopy> m_staticCopies = [];
    // The rows the latest reconcile met, by instance, and the scratch the next reconcile fills before the two swap.
    private Dictionary<WorldPlacement, StaticRow> m_staticRowsMet = new(comparer: ReferenceEqualityComparer.Instance);
    private Dictionary<WorldPlacement, StaticRow> m_staticRowsNext = new(comparer: ReferenceEqualityComparer.Instance);
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
    public bool ContainsPlacement(WorldPlacement placement) => (m_staticRowsMet.TryGetValue(key: placement, value: out var row) && (row.Refusal is null));

    /// <summary>Prepares static occurrences from the same definition and rows the visual scene will emit. This is the
    /// sole admission door: visual emission skips exactly these rows; query emission never passes this catalog. A
    /// definition that replaces some rows costs those rows: a row instance met before, showing the same creation
    /// instance under the same seed and scope, keeps its verdict, and keeps its copies while its compiled stamp
    /// stands.</summary>
    public void ReconcileStatic(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements, string? scope = null) {
        var seed = (definition.Generation?.WorldSeed ?? 0UL);
        if (ReferenceEquals(m_staticDefinition, definition) && ReferenceEquals(m_staticRows, placements) && ReferenceEquals(m_staticCreations, definition.Creations) && (m_staticSeed == seed) && (m_staticScope == scope)) { return; }
        // Copies carry the scope and the seed places them, so a change of either starts every row again.
        var keeps = ((m_staticSeed == seed) && (m_staticScope == scope));
        m_staticDefinition = definition;
        m_staticRows = placements;
        m_staticCreations = definition.Creations;
        m_staticSeed = seed;
        m_staticScope = scope;
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
        foreach (var placement in placements) {
            var creation = WorldDefinitionRows.FindCreation(definition.Creations, placement.ShownPrototypeId);
            if ((creation is null) || !WorldPlacementStamper.IsStaticStamp(placement, creation)) { continue; }
            if (!met.TryGetValue(key: placement, value: out var row) || !ReferenceEquals(objA: row.Creation, objB: creation)) {
                row = new StaticRow(creation: creation, refusal: Refusal(creation: creation, placement: placement));
            }
            // A row listed twice is one occurrence set, as the instance it is.
            if (!next.TryAdd(key: placement, value: row)) { continue; }
            if (row.Refusal is { } reason) {
                m_staticUnsupported[reason] = (m_staticUnsupported.GetValueOrDefault(reason) + 1);
                continue;
            }
            var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: seed);
            if (stamp.Count == 0) { row.Stamp = stamp; row.Copies = []; continue; }
            var key = creation.Id.Value;
            if (!variants.TryGetValue(key, out var asset)) {
                if (!m_staticVariants.TryGetValue(key, out asset) || !ReferenceEquals(asset.Creation, creation)) { asset = new StaticAsset(creation); }
                asset.MaxScale = 0f;
                variants.Add(key, asset);
                m_staticAssets.Add(asset);
            }
            asset.MaxScale = MathF.Max(asset.MaxScale, placement.Scale);
            if (!ReferenceEquals(objA: row.Stamp, objB: stamp) || !ReferenceEquals(objA: row.Asset, objB: asset)) {
                var copies = new StaticCopy[stamp.Count];
                var visitor = new StaticCopyVisitor(copies, asset, placement, stamp, scope);

                stamp.ForEach(ref visitor);
                row.Stamp = stamp;
                row.Asset = asset;
                row.Copies = copies;
                rebuilt += copies.Length;
            }
            m_staticCopies.AddRange(collection: row.Copies);
        }
        m_staticRowsMet = next;
        m_staticRowsNext = met;
        m_staticVariants.Clear();
        foreach (var (key, asset) in variants) { m_staticVariants.Add(key, asset); }
        WorldCreationWork.ProcessWork.Count(WorldCreationWork.StaticReconciles);
        WorldCreationWork.ProcessWork.Add(WorldCreationWork.StaticCopiesRebuilt, rebuilt);
    }
    // Why a static row is left to the ordinary visual emission, or null when it is a rigid static copy.
    private static string? Refusal(WorldPrototype creation, WorldPlacement placement) {
        var document = creation.EngineDocument;

        return (!IsRigid(creation) ? "not-rigid" :
            ((WorldStateDocumentValues.HasReference(creation.Document) || WorldStateDocumentValues.HasReference(placement)) ? "state-reference" :
            ((placement.Mirror is not null) ? "reflection" :
            ((document.TextRuns is { Count: > 0 }) ? "text" :
            ((creation.Mesh is not null) ? "authored-mesh" :
            (document.HasGlass ? "glass" :
            ((document.Shapes is not { Count: > 0 }) ? "no-shapes" : null)))))));
    }
    private struct StaticCopyVisitor(StaticCopy[] copies, StaticAsset asset, WorldPlacement placement, CompiledPlacementStamp stamp, string? scope) : ICreationStampVisitor {
        private int m_ordinal;
        public void Visit(CreationStampInstance instance) {
            var index = m_ordinal++;
            var ordinal = stamp.OccurrenceAt(index);
            var target = new WorldPickTarget(placement.Id, null) { Prototype = asset.Creation.Id, Occurrence = ordinal };
            copies[index] = new StaticCopy(asset, placement, target, (scope, placement.Id, ordinal), instance.Origin, stamp.Rotation);
        }
    }
    // Only body assets need template transform slots. Static templates retain the ordinary instance partitions.
    private void PackTemplates(Span<DynamicTransform> transforms, int slotBase) {
        for (var index = 0; (index < m_slotCount); index++) { transforms[slotBase + index] = new DynamicTransform(Vector3.Zero, Quaternion.Identity); }
    }
    private void EmitStaticAssets(SdfProgramBuilder builder, WorldBakedColors colors, WorldPickMapBuilder picks, WorldShapeIsolation? isolation) {
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
            if ((scratch.Emission is { } held) && (scratch.BakeScale == asset.BakeScale) && ReferenceEquals(scratch.Hidden, hidden) && builder.CanAppendEmission(held)) {
                builder.AppendEmission(held);
            } else {
                var mark = builder.MarkEmission();
                // The existing static visitor owns every shape bound, detail, union partition and warped split.
                // The largest demanded authored scale bakes at the ordinary world-space quality; smaller occurrences
                // share that result under a relative matrix, without turning a default into explicit authored detail.
                WorldPlacementStamper.EmitPlacement(builder, document, m_staticDefinition!, scratch.Palette,
                    new WorldPlacement(string.Empty, creation.Id, Vector3.Zero, 0f, asset.BakeScale),
                    textCatalog: null, worldSeed: m_staticSeed, volumes: null, mesh: null, meshMaterial: 0,
                    meshDraws: null, meshScope: null, firstMesh: 0, hidden: hidden,
                    frameOverride: new CompiledPlacementFrame(Vector3.Zero, 0f));
                scratch.Emission = builder.RetainEmission(mark);
                scratch.BakeScale = asset.BakeScale;
                scratch.Hidden = hidden;
            }
            asset.Range = new SdfMeshTemplateRange(first, (builder.InstanceCount - first));
            picks.Instances(first, builder.InstanceCount, scratch.Target);
            WorldCreationWork.ProcessWork.Count(WorldCreationWork.StaticTemplates);
        }
        m_staticVolumes.Clear();
        StaticVisibleCopies = 0;
        for (var index = 0; index < m_staticCopies.Count; index++) {
            var occurrence = m_staticCopies[index];
            var asset = occurrence.Asset;
            var copyIndex = (m_bodies.Count + index);
            var previous = m_copies[copyIndex];
            var transform = new DynamicTransform(occurrence.Position, occurrence.Orientation);
            var scale = (occurrence.Placement.Scale / asset.BakeScale);
            _ = previous.Update(-1, occurrence.Target, occurrence.MotionKey, transform, scale, false, null, null, 0, [], usesRootMaterialFrame: false, templateRange: asset.Range);
            if (asset.Range.InstanceCount > 0) {
                var hidden = isolation?.HiddenFor(asset.Creation.Id, asset.Creation.EngineDocument);
                StaticVisibleCopies++;
                WorldPlacementStamper.AppendStaticVolumes(asset.Creation.EngineDocument, occurrence.Position, occurrence.Orientation, occurrence.Placement.Scale, m_staticVolumes, hidden);
            }
        }
        m_volumesDirty = true;
        WorldCreationWork.ProcessWork.Add(WorldCreationWork.StaticCopies, m_staticCopies.Count);
    }
}
