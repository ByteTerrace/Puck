using System.Numerics;
using Puck.Abstractions;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>The immutable canonical occurrences of a placement. Both graphics and contact consume these exact
/// fixed-point origins, including surface admission. Candidate ordinals survive dropped occurrences.</summary>
public sealed class CompiledPlacementStamp {
    private readonly FixedCreationStampInstance[] m_instances;
    private readonly int[]? m_ordinals;

    internal CompiledPlacementStamp(CompiledPlacementFrame frame, FixedQuaternion rotation,
        FixedCreationStampInstance[] instances, int[]? ordinals = null) {
        Frame = frame; FixedRotation = rotation; Rotation = rotation.ToQuaternion();
        m_instances = instances; m_ordinals = ordinals;
    }

    /// <summary>The undistributed root's resolved frame, or the authored distribution/mirror group frame.</summary>
    public CompiledPlacementFrame Frame { get; }
    /// <summary>The canonical fixed-point orientation.</summary>
    public FixedQuaternion FixedRotation { get; }
    /// <summary>The presentation widening of the canonical orientation.</summary>
    public Quaternion Rotation { get; }
    /// <summary>The number of accepted occurrences, including reflections.</summary>
    public int Count => m_instances.Length;
    /// <summary>The read-only fixed-world instances.</summary>
    public ReadOnlySpan<FixedCreationStampInstance> Instances => m_instances;

    /// <summary>The original candidate identity of an accepted occurrence.</summary>
    public int OccurrenceAt(int index) => ((m_ordinals is null) ? index : m_ordinals[index]);
    /// <summary>Visits the same canonical origins contact consumes, widened once at the presentation boundary.</summary>
    public void ForEach<TVisitor>(ref TVisitor visitor) where TVisitor : struct, ICreationStampVisitor {
        foreach (var instance in m_instances) {
            visitor.Visit(instance: new CreationStampInstance(Origin: instance.Origin.ToVector3(), ReflectionNormal: instance.ReflectionNormal?.ToVector3()));
        }
    }
    /// <summary>Visits fixed-world instances without a separate expansion or surface query.</summary>
    public void ForEachFixed(Action<FixedCreationStampInstance> visitor) {
        foreach (var instance in m_instances) { visitor(instance); }
    }
}
public static partial class WorldPlacementStamp {
    private sealed class StampEntry {
        public object Gate { get; } = new();

        public CompiledPlacementFrame Frame;
        public ulong Seed;

        public GroundInput[] Inputs = [];

        public CompiledPlacementStamp? Stamp;
    }
    private sealed class ContextEntry {
        public object Gate { get; } = new();

        public IReadOnlyList<WorldPrototype>? Creations;
        public ulong Seed;
        public CompilationContext? Context;
    }
    private readonly record struct GroundInput(CreationDocument Document, CompiledPlacementStamp Stamp, FixedQ4816 Scale, float Margin) {
        public bool Same(in GroundInput other) => (ReferenceEquals(objA: Document, objB: other.Document) &&
            ReferenceEquals(objA: Stamp, objB: other.Stamp) && (Scale == other.Scale) && (Margin == other.Margin));
    }
    private sealed class FieldEntry {
        public GroundInput[] Inputs = [];
        public SdfFieldEvaluator? Field;
    }
    private sealed class TargetComparer : IEqualityComparer<IReadOnlyList<string>> {
        public static readonly TargetComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? a, IReadOnlyList<string>? b) {
            if (ReferenceEquals(objA: a, objB: b)) { return true; }
            if ((a is null) || (b is null) || (a.Count != b.Count)) { return false; }
            for (var i = 0; (i < a.Count); i++) { if (!StringComparer.Ordinal.Equals(x: a[i], y: b[i])) { return false; } }
            return true;
        }
        public int GetHashCode(IReadOnlyList<string> values) {
            var hash = new HashCode();

            foreach (var value in values) { hash.Add(value, StringComparer.Ordinal); }
            return hash.ToHashCode();
        }
    }

    private static readonly WorldPlacementsSection EmptySection = new();
    private static readonly CodeDerivedTable<WorldPlacement, StampEntry> Stamps = new();
    private static readonly CodeDerivedTable<WorldPlacementsSection, ContextEntry> Contexts = new();
    private static readonly FixedQ4816 SurfaceNormalBackoff = FixedQ4816.FromDouble(value: 0.02);

    private static bool SameInputs(GroundInput[] a, GroundInput[] b) {
        if (a.Length != b.Length) { return false; }
        for (var i = 0; (i < a.Length); i++) { if (!a[i].Same(other: b[i])) { return false; } }
        return true;
    }
    private static CompilationContext ContextFor(WorldDefinition definition, ulong seed, int revision) {
        var section = (definition.PlacementsRaw ?? EmptySection);

        if (WorldPlacementFrameCompilation.HasLiveInputs(section: section)) { return new CompilationContext(section, definition.Creations, seed, revision); }
        var entry = Contexts.GetValue(section, static _ => new ContextEntry());

        lock (entry.Gate) {
            if ((entry.Context is { } held) && ReferenceEquals(objA: entry.Creations, objB: definition.Creations) && (entry.Seed == seed)) { return held; }
            var context = new CompilationContext(section, definition.Creations, seed, revision);

            if (CodeRevision.Current == revision) {
                entry.Creations = definition.Creations; entry.Seed = seed; entry.Context = context;
            }
            return context;
        }
    }

    /// <summary>Gets canonical fixed-point occurrences from immutable placement/creation/seed/code inputs. A warmed
    /// unchanged context answers before descriptor validation, source-array construction or surface queries.</summary>
    public static CompiledPlacementStamp Resolve(WorldDefinition definition, WorldPlacement placement,
        CompiledPlacementFrame? frameOverride = null, ulong? worldSeed = null) {
        var revision = CodeRevision.Current;
        var context = ContextFor(definition, (worldSeed ?? (definition.Generation?.WorldSeed ?? 0UL)), revision);

        lock (context.Gate) { return context.StampOf(frameOverride: frameOverride, row: placement); }
    }
    /// <summary>The indexed canonical frame provider. Only requested projected single roots are resolved; a
    /// distribution, reflection or deal template retains its authored group frame.</summary>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> ResolveFrames(WorldDefinition definition) =>
        ContextFor(definition, (definition.Generation?.WorldSeed ?? 0UL), CodeRevision.Current).FrameTable;

    // One row (declared or an edit in flight), through the same context the indexed provider uses. The dictionary
    // owns row identity checks, avoiding a document scan on every audio, animation or editor frame read.
    internal static CompiledPlacementFrame ResolveFrame(WorldDefinition definition, WorldPlacement row) {
        var context = ContextFor(definition, (definition.Generation?.WorldSeed ?? 0UL), CodeRevision.Current);

        lock (context.Gate) { return context.FrameOf(row: row); }
    }

    /// <summary>Validates and holds one surface descriptor in the immutable indexed context, without projecting.</summary>
    public static bool TryValidateSurface(WorldDefinition definition, WorldPlacement placement, out string reason) {
        if (placement.Surface is null) { reason = string.Empty; return true; }
        var context = ContextFor(definition, (definition.Generation?.WorldSeed ?? 0UL), CodeRevision.Current);

        lock (context.Gate) { return context.TryDescriptor(descriptor: out _, reason: out reason, row: placement); }
    }

    internal static void CarryAcrossHolding(WorldPlacement before, WorldPlacement after) {
        if (Stamps.TryGetValue(key: before, value: out var entry)) { Stamps.AddOrUpdate(key: after, value: entry); }
    }
}
