using System.Numerics;

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
/// <param name="FieldRescale">The largest product of factors <c>L</c> along a nested scope path dividing its field before
/// it joins the parent (<see cref="SdfFieldScopeClamp.StepScale"/> is <c>1/L</c>), or 1 when no scope does. The packed bound
/// contains the instance's surface and its blends' influence whatever this is; the instance's field outside the bound is
/// at least its distance to the bound divided by this, not the distance itself.</param>
public readonly record struct SdfInstanceCost(int OwnedWords, int Shapes, int ScopeClamps, float BoundRadius, float Halo, bool Unmaskable, float FieldRescale);
/// <summary>One packed skip sphere, as the kernels read it: <c>mapCore</c> skips the shape or segment it bounds when the
/// sample's distance to the sphere cannot beat the running minimum.</summary>
/// <param name="Mode">The bound mode: <see cref="SdfProgram.BoundModeNone"/> (always evaluated),
/// <see cref="SdfProgram.BoundModeStatic"/>, or <see cref="SdfProgram.BoundModeDynamic"/>.</param>
/// <param name="Slot">The dynamic transform slot whose position the center rides, for a dynamic sphere.</param>
/// <param name="Center">The world-space center, or the pre-dynamic offset of a dynamic sphere.</param>
/// <param name="Radius">The packed radius, float-safety padding included.</param>
public readonly record struct SdfSkipSphere(uint Mode, int Slot, Vector3 Center, float Radius);
public sealed partial class SdfProgram {
    /// <summary>Gets the number of entries in the packed segment directory, after adjacent skippable segments merged.</summary>
    public int SkipSegmentCount => ((int)m_words[(SegmentDirectoryWord() + SegmentCountLane)]);

    /// <summary>Reads the packed skip sphere of one instruction. Only a <see cref="SdfOp.ShapeBlend"/> can carry one.</summary>
    /// <param name="instruction">The zero-based instruction index.</param>
    /// <returns>The sphere, with <see cref="BoundModeNone"/> when the shape always evaluates.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is absent.</exception>
    public SdfSkipSphere ShapeSkipSphere(int instruction) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: instruction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: instruction, other: m_instructions.Length);

        return ReadSkipSphere(word: (BoundsWord() + ((BoundRecordVectors * instruction) * WordsPerVector)), modeMask: uint.MaxValue);
    }
    /// <summary>Reads the packed skip sphere of one segment-directory entry.</summary>
    /// <param name="segment">The zero-based directory entry, below <see cref="SkipSegmentCount"/>.</param>
    /// <returns>The sphere, with <see cref="BoundModeNone"/> when the segment always evaluates.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The entry is absent.</exception>
    public SdfSkipSphere SegmentSkipSphere(int segment) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: segment);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: segment, other: SkipSegmentCount);

        return ReadSkipSphere(word: (SegmentDirectoryWord() + ((DirectoryHeaderVectors + (BoundRecordVectors * segment)) * WordsPerVector)), modeMask: SegmentBoundModeMask);
    }
    /// <summary>Reads an instance's live packed cost without re-emitting it or attributing shared tables to it.</summary>
    /// <param name="index">The zero-based instance ordinal.</param>
    /// <returns>The instance's exclusive word count and culling facts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is absent.</exception>
    public SdfInstanceCost InspectInstance(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: m_instances.Length);
        var instance = m_instances[index];
        var instructionCount = (instance.End - instance.First);
        var segments = SegmentDirectoryWord();
        var instances = (segments + ((DirectoryHeaderVectors + (BoundRecordVectors * ((int)m_words[(segments + SegmentCountLane)]))) * WordsPerVector));
        var entry = (instances + (((DirectoryHeaderVectors + (BoundRecordVectors * index)) + 1) * WordsPerVector));
        var firstSegment = ((int)m_words[(entry + 2)]);
        var endSegment = ((int)(m_words[(entry + 3)] & SegmentEndMask));
        var rigid = (((int)m_words[(segments + SegmentRigidPlanLane)]) * WordsPerVector);
        var vectors = (((((2 + InstructionDataVectors) + BoundRecordVectors) * instructionCount) + BoundRecordVectors)
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

        var rescale = FieldScopeExtent(instance.First, instance.End).Rescale;

        foreach (var clamp in FieldScopeClamps) {
            if (clamp.InstanceIndex == index) {
                clamps++;
            }
        }
        return new SdfInstanceCost(OwnedWords: checked((vectors * WordsPerVector)), Shapes: shapes, ScopeClamps: clamps,
            BoundRadius: m_instanceBinning[index].Radius,
            Halo: (MaxSmoothBlendRadius(first: instance.First, end: instance.End) + MaxScopedFieldReach(first: instance.First, end: instance.End)),
            Unmaskable: (m_instanceBinning[index].Radius == UnmaskableBoundRadius),
            FieldRescale: rescale);

        bool Owns(int instruction) => ((instruction >= instance.First) && (instruction < instance.End));
    }

    // The first word of the per-instruction bound table, then of the segment directory that follows it.
    private int BoundsWord() => ((((int)m_words[ProgramMaterialOffsetLane]) + (MaterialVectorsPerEntry * ((int)m_words[ProgramMaterialCountLane]))) * WordsPerVector);
    private int SegmentDirectoryWord() => (BoundsWord() + ((BoundRecordVectors * m_instructions.Length) * WordsPerVector));
    private SdfSkipSphere ReadSkipSphere(int word, uint modeMask) => new(
        Mode: m_words[(word + WordsPerVector)] & modeMask,
        Slot: ((int)m_words[((word + WordsPerVector) + 1)]),
        Center: new Vector3(
            x: BitConverter.UInt32BitsToSingle(value: m_words[word]),
            y: BitConverter.UInt32BitsToSingle(value: m_words[(word + 1)]),
            z: BitConverter.UInt32BitsToSingle(value: m_words[(word + 2)])
        ),
        Radius: BitConverter.UInt32BitsToSingle(value: m_words[(word + 3)])
    );
}
