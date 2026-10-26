using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>A boundary between complete instance emissions in this builder.</summary>
    public readonly record struct EmissionMark(int Instructions, int Instances, int Polygons, int Paths, int Curves, int Windows, int Shapes, int Materials, int Screens, int Scopes);
    /// <summary>A retained emission sharing its immutable profile tables. Scope membership is rebound on append,
    /// so retaining an emission holds no reference to the builder that emitted it.</summary>
    public sealed class Emission {
        internal Emission() { }

        internal SdfSweepCurve[] Curves { get; init; } = [];
        internal int First { get; init; }
        internal (int InstructionIndex, int ReachPerUnit, int RawValue)? Fold { get; init; }
        internal SdfInstanceRange[] Instances { get; init; } = [];
        internal SdfInstruction[] Instructions { get; init; } = [];
        internal int Materials { get; init; }
        internal SdfCompiledPath[] Paths { get; init; } = [];
        internal (int InstructionIndex, Vector2[] Vertices)[] Polygons { get; init; } = [];
        internal (int InstructionIndex, int ReachPerUnit, SdfOp Op)? Recolor { get; init; }
        internal int? ScopeBase { get; init; }
        internal int Shapes { get; init; }
        internal (SdfOp Op, int Material, long MaxDelta, bool Scoped)[] Windows { get; init; } = [];
    }

    /// <summary>Marks the start of an emission with no instance or field scope open. Register materials and screens before
    /// this boundary; retained instances name those existing rows.</summary>
    public EmissionMark MarkEmission() {
        RequireEmissionBoundary();
        return new(m_instructions.Count, m_instances.Count, m_convexPolygonProfiles.Count, m_paths.Count, m_sweepCurves.Count, m_materialRecolorWindows.Count, m_shapeCount, m_materials.Count, m_screenSurfaces.Count, m_materialScopes.Count);
    }
    /// <summary>Retains the completed instances since a mark. Materials remain registered by the caller.</summary>
    public Emission RetainEmission(EmissionMark mark) {
        RequireEmissionBoundary();
        if ((mark.Materials != m_materials.Count) || (mark.Screens != m_screenSurfaces.Count) || (mark.Scopes != m_materialScopes.Count)) {
            throw new InvalidOperationException(message: "A retained emission must use materials and screens registered before its mark.");
        }
        var scope = ((m_materialScopes.Count == 0) ? null : m_materialScopes[^1]);
        var windows = new (SdfOp Op, int Material, long MaxDelta, bool Scoped)[(m_materialRecolorWindows.Count - mark.Windows)];

        for (var index = 0; (index < windows.Length); index++) {
            var window = m_materialRecolorWindows[(index + mark.Windows)];

            if (!ReferenceEquals(objA: window.Scope, objB: scope)) {
                throw new InvalidOperationException(message: "A retained emission cannot register material scopes of its own.");
            }
            windows[index] = (window.Op, window.Material, window.MaxDelta, (window.Scope is not null));
        }
        return new Emission {
            First = mark.Instructions,
            Instructions = m_instructions.GetRange(mark.Instructions, (m_instructions.Count - mark.Instructions)).ToArray(),
            Instances = m_instances.GetRange(mark.Instances, (m_instances.Count - mark.Instances)).ToArray(),
            Polygons = m_convexPolygonProfiles.GetRange(mark.Polygons, (m_convexPolygonProfiles.Count - mark.Polygons)).ToArray(),
            Paths = m_paths.GetRange(mark.Paths, (m_paths.Count - mark.Paths)).ToArray(),
            Curves = m_sweepCurves.GetRange(mark.Curves, (m_sweepCurves.Count - mark.Curves)).ToArray(),
            Windows = windows,
            Materials = m_materials.Count,
            ScopeBase = scope?.MaterialBase,
            Shapes = (m_shapeCount - mark.Shapes),
            Recolor = m_materialRecolor,
            Fold = m_positionalFold,
        };
    }
    /// <summary>Returns whether the current palette and material scope match a retained emission's.</summary>
    public bool CanAppendEmission(Emission emission) => ((emission.Materials == m_materials.Count) && (emission.ScopeBase == ((m_materialScopes.Count == 0) ? null : m_materialScopes[^1].MaterialBase)));
    /// <summary>Appends a retained emission using the same material ids, relocating its instruction references.</summary>
    public void AppendEmission(Emission emission) {
        ArgumentNullException.ThrowIfNull(emission);
        RequireEmissionBoundary();
        if (!CanAppendEmission(emission: emission)) {
            throw new InvalidOperationException(message: "A retained emission requires the same palette and material scope base.");
        }
        if ((m_instances.Count + emission.Instances.Length) > MaxInstances) {
            throw new SdfProgramCapacityException(capacity: "instances", limit: MaxInstances, message: $"A program may declare at most {MaxInstances} instances.");
        }
        var offset = (m_instructions.Count - emission.First);

        m_instructions.AddRange(collection: emission.Instructions);
        foreach (var instance in emission.Instances) {
            m_instances.Add(item: instance with { First = (instance.First + offset), End = (instance.End + offset) });
        }
        foreach (var polygon in emission.Polygons) {
            m_convexPolygonProfiles.Add(item: ((polygon.InstructionIndex + offset), polygon.Vertices));
        }
        foreach (var path in emission.Paths) {
            m_paths.Add(item: path with { InstructionIndex = (path.InstructionIndex + offset) });
        }
        foreach (var curve in emission.Curves) {
            m_sweepCurves.Add(item: curve with { InstructionIndex = (curve.InstructionIndex + offset) });
        }
        foreach (var window in emission.Windows) {
            m_materialRecolorWindows.Add(item: (window.Op, window.Material, window.MaxDelta, (window.Scoped ? m_materialScopes[^1] : null)));
        }
        m_shapeCount += emission.Shapes;
        if (emission.Instructions.Length > 0) {
            m_materialRecolor = ((emission.Recolor is { } recolor) ? ((recolor.InstructionIndex + offset), recolor.ReachPerUnit, recolor.Op) : null);
            m_positionalFold = ((emission.Fold is { } fold) ? ((fold.InstructionIndex + offset), fold.ReachPerUnit, fold.RawValue) : null);
        }
    }

    private void RequireEmissionBoundary() {
        if ((m_openInstanceFirst >= 0) || (m_fieldScopes is { Count: > 0 })) {
            throw new InvalidOperationException(message: "A retained emission must begin and end between instances and field scopes.");
        }
    }
}
