namespace Puck.SignedDistance;

/// <summary>One live instance's packed ownership and the exact culling facts derived when its program was built.</summary>
/// <param name="OwnedWords">Words exclusively owned by this instance: instruction headers/data/bounds, instance and
/// segment rows, rigid leaves, shape side tables and per-instance part bindings. Shared palettes, grid and part assets
/// are excluded and remain in the program's shared remainder.</param>
/// <param name="Shapes">ShapeBlend instructions in the instance.</param>
/// <param name="ScopeClamps">Non-unit field-scope clamps in the instance.</param>
/// <param name="BoundRadius">The packed culling radius, including padding or a parked/unmaskable sentinel.</param>
/// <param name="Halo">Soft-blend and scoped-field outward reach before float-safety padding.</param>
/// <param name="Unmaskable">Whether no finite bound can mask this instance.</param>
public readonly record struct SdfInstanceCost(int OwnedWords, int Shapes, int ScopeClamps, float BoundRadius, float Halo, bool Unmaskable);
public sealed partial class SdfProgram {
    /// <summary>Reads an instance's live packed cost without re-emitting it or attributing shared tables to it.</summary>
    /// <param name="index">The zero-based instance ordinal.</param>
    /// <returns>The instance's exclusive word count and culling facts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is absent.</exception>
    public SdfInstanceCost InspectInstance(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: m_instances.Length);
        var instance = m_instances[index];
        var instructionCount = (instance.End - instance.First);
        var segments = (((((int)m_words[ProgramMaterialOffsetLane]) + (MaterialVectorsPerEntry * ((int)m_words[ProgramMaterialCountLane])))
            + (BoundRecordVectors * m_instructions.Length)) * WordsPerVector);
        var instances = (segments + ((DirectoryHeaderVectors + (BoundRecordVectors * ((int)m_words[(segments + SegmentCountLane)]))) * WordsPerVector));
        var entry = (instances + (((DirectoryHeaderVectors + (BoundRecordVectors * index)) + 1) * WordsPerVector));
        var firstSegment = ((int)m_words[(entry + 2)]);
        var endSegment = ((int)(m_words[(entry + 3)] & SegmentEndMask));
        var rigid = (((int)m_words[(segments + SegmentRigidPlanLane)]) * WordsPerVector);
        var vectors = (((((1 + InstructionDataVectors) + BoundRecordVectors) * instructionCount) + BoundRecordVectors)
            + ((BoundRecordVectors + 1) * (endSegment - firstSegment)));

        for (var segment = firstSegment; (segment < endSegment); segment++) {
            vectors += (3 * ((int)m_words[((rigid + (segment * WordsPerVector)) + 1)]));
        }
        foreach (var profile in m_convexPolygonProfiles) {
            if (Owns(instruction: profile.InstructionIndex)) { vectors += ((profile.Vertices.Length + 1) / 2); }
        }
        foreach (var curve in m_sweepCurves) {
            if (Owns(instruction: curve.InstructionIndex)) { vectors += SweepCurveVectorsPerEntry; }
        }
        foreach (var path in m_paths) {
            if (Owns(instruction: path.InstructionIndex)) { vectors += (path.Edges.Count * 2); }
        }
        var part = (((int)m_words[(instances + InstancePartProgramsLane)]) * WordsPerVector);

        if (part != 0) {
            var bindings = ((int)(m_words[((part + ((1 + index) * WordsPerVector)) + 2)] & 0x7FFFFFFFu));

            vectors += (1 + bindings);
        }
        var shapes = 0;

        for (var instruction = instance.First; (instruction < instance.End); instruction++) {
            if (m_instructions[instruction].Op == SdfOp.ShapeBlend) { shapes++; }
        }
        var clamps = 0;

        foreach (var clamp in FieldScopeClamps) { if (clamp.InstanceIndex == index) { clamps++; } }
        return new SdfInstanceCost(OwnedWords: checked((vectors * WordsPerVector)), Shapes: shapes, ScopeClamps: clamps,
            BoundRadius: m_instanceBinning[index].Radius,
            Halo: (MaxSmoothBlendRadius(first: instance.First, end: instance.End) + MaxScopedFieldReach(first: instance.First, end: instance.End)),
            Unmaskable: (m_instanceBinning[index].Radius == UnmaskableBoundRadius));

        bool Owns(int instruction) => ((instruction >= instance.First) && (instruction < instance.End));
    }
}
