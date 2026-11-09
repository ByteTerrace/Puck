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
    private readonly List<StaticAsset> m_staticAssets = [];
    private readonly List<StaticCopy> m_staticCopies = [];
    private readonly HashSet<WorldPlacement> m_staticPlacements = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> m_staticUnsupported = new(comparer: StringComparer.Ordinal);
    private readonly List<SdfVolume> m_staticVolumes = [];
    private WorldDefinition? m_staticDefinition;
    private IReadOnlyList<WorldPlacement>? m_staticRows;
    private IReadOnlyList<WorldPrototype>? m_staticCreations;
    private ulong m_staticSeed;
    private string? m_staticScope;

    /// <summary>Gets the number of admitted static placement rows.</summary>
    public int StaticRows => m_staticPlacements.Count;
    /// <summary>Gets the number of actual admitted lattice occurrences.</summary>
    public int StaticCopies => m_staticCopies.Count;
    /// <summary>Gets the last emission's occurrences with at least one shape left visible by isolation.</summary>
    public int StaticVisibleCopies { get; private set; }
    /// <summary>Gets the number of shared local static prototypes, independent of occurrence scale.</summary>
    public int StaticAssets => m_staticAssets.Count;
    /// <summary>Gets counts of ordinary static rows kept on the existing emission path, by eligibility reason.</summary>
    public IReadOnlyDictionary<string, int> StaticUnsupported => m_staticUnsupported;
    /// <summary>Returns whether this exact row is owned by the retained static-copy roster.</summary>
    public bool ContainsPlacement(WorldPlacement placement) => m_staticPlacements.Contains(placement);

    /// <summary>Prepares static occurrences from the same definition and rows the visual scene will emit. This is the
    /// sole admission door: visual emission skips exactly these rows; query emission never passes this catalog.</summary>
    public void ReconcileStatic(WorldDefinition definition, IReadOnlyList<WorldPlacement> placements, string? scope = null) {
        var seed = (definition.Generation?.WorldSeed ?? 0UL);
        if (ReferenceEquals(m_staticDefinition, definition) && ReferenceEquals(m_staticRows, placements) && ReferenceEquals(m_staticCreations, definition.Creations) && (m_staticSeed == seed) && (m_staticScope == scope)) { return; }
        m_staticDefinition = definition;
        m_staticRows = placements;
        m_staticCreations = definition.Creations;
        m_staticSeed = seed;
        m_staticScope = scope;
        var previous = m_staticAssets.ToDictionary(asset => asset.Creation.Id.Value, StringComparer.Ordinal);
        var variants = new Dictionary<string, StaticAsset>(StringComparer.Ordinal);
        foreach (var asset in previous.Values) { asset.MaxScale = 0f; }
        m_staticAssets.Clear();
        m_staticCopies.Clear();
        m_staticPlacements.Clear();
        m_staticUnsupported.Clear();
        foreach (var placement in placements) {
            var creation = WorldDefinitionRows.FindCreation(definition.Creations, placement.ShownPrototypeId);
            if ((creation is null) || !WorldPlacementStamper.IsStaticStamp(placement, creation)) { continue; }
            var document = creation.EngineDocument;
            var reason = (!IsRigid(creation) ? "not-rigid" :
                ((WorldStateDocumentValues.HasReference(creation.Document) || WorldStateDocumentValues.HasReference(placement)) ? "state-reference" :
                ((placement.Mirror is not null) ? "reflection" :
                ((document.TextRuns is { Count: > 0 }) ? "text" :
                ((creation.Mesh is not null) ? "authored-mesh" :
                (document.HasGlass ? "glass" :
                ((document.Shapes is not { Count: > 0 }) ? "no-shapes" : null)))))));
            if (reason is not null) {
                m_staticUnsupported[reason] = (m_staticUnsupported.GetValueOrDefault(reason) + 1);
                continue;
            }
            m_staticPlacements.Add(placement);
            var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: seed);
            if (stamp.Count == 0) { continue; }
            var key = creation.Id.Value;
            if (!variants.TryGetValue(key, out var asset)) {
                if (!previous.TryGetValue(key, out asset) || !ReferenceEquals(asset.Creation, creation)) { asset = new StaticAsset(creation); }
                variants.Add(key, asset);
                m_staticAssets.Add(asset);
            }
            asset.MaxScale = MathF.Max(asset.MaxScale, placement.Scale);
            var visitor = new StaticCopyVisitor(m_staticCopies, asset, placement, stamp, scope);
            stamp.ForEach(ref visitor);
        }
    }
    private struct StaticCopyVisitor(List<StaticCopy> copies, StaticAsset asset, WorldPlacement placement, CompiledPlacementStamp stamp, string? scope) : ICreationStampVisitor {
        private int m_ordinal;
        public void Visit(CreationStampInstance instance) {
            var ordinal = stamp.OccurrenceAt(m_ordinal++);
            var target = new WorldPickTarget(placement.Id, null) { Prototype = asset.Creation.Id, Occurrence = ordinal };
            copies.Add(new StaticCopy(asset, placement, target, (scope, placement.Id, ordinal), instance.Origin, stamp.Rotation));
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
            WorldPlacementStamper.FillPalette(builder, colors, document, null, scratch.Palette, hidden: hidden, templateScale: asset.BakeScale);
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
            if (!previous.SamePose(-1, occurrence.Target, occurrence.MotionKey, transform, scale, false, null, null, 0, [], usesRootMaterialFrame: false, templateRange: asset.Range)) {
                m_copies[copyIndex] = new SdfMeshInstance(-1, occurrence.Target, transform, scale, IsDynamic: false) {
                    MotionIdentity = occurrence.MotionKey, UsesRootMaterialFrame = false, TemplateRange = asset.Range,
                };
            }
            if (asset.Range.InstanceCount > 0) {
                var hidden = isolation?.HiddenFor(asset.Creation.Id, asset.Creation.EngineDocument);
                StaticVisibleCopies++;
                WorldPlacementStamper.AppendStaticVolumes(asset.Creation.EngineDocument, occurrence.Position, occurrence.Orientation, occurrence.Placement.Scale, m_staticVolumes, hidden);
            }
        }
        WorldCreationWork.ProcessWork.Add(WorldCreationWork.StaticCopies, m_staticCopies.Count);
    }
}
