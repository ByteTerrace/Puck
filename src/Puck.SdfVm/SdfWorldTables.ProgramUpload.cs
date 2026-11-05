using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    /// <summary>Re-uploads the scene program (the host's <c>ProgramChanged</c> path — e.g. a rebuilt overworld scene).
    /// Program and instance buffers grow on demand once the device is idle. Persistent images,
    /// screen bindings and pipelines remain in place. Palette-value edits keep transport and geometry signatures;
    /// material reassignment or any other field/layout change invalidates them.</summary>
    /// <param name="program">The scene program to upload.</param>
    /// <param name="delta">The words <paramref name="program"/> differs in from the program it was built after, or
    /// <see langword="null"/>: when that program is the one the tables hold, only those words are copied, and otherwise
    /// the program's words are compared with what the tables hold.</param>
    /// <exception cref="ArgumentException">The program contains an opcode not declared by <see cref="SdfOp"/>.</exception>
    public void UploadProgram(SdfProgram program, SdfProgramDelta? delta = null) {
        ArgumentNullException.ThrowIfNull(program);
        program.ValidateIsa();
        var geometryChanged = true;
        var programChanged = true;

        if (m_liveProgram is { } previous) {
            geometryChanged = !SameProgramGeometry(current: program, previous: previous);
            programChanged = (geometryChanged || !previous.Words.SequenceEqual(other: program.Words));
        }

        EnsureDynamicTransformCapacity(program.RequiredDynamicTransformCapacity);

        // The program words go through their region like every other table, so a frame in flight keeps reading the words
        // its slot was sent; only growing a capacity waits for the device to go idle.
        var region = m_programRegion;

        EnsureProgramCapacity(program: program);

        // A region the capacity replaced holds none of the earlier program's words.
        if (
            (delta is { } known) &&
            ReferenceEquals(objA: known.To, objB: program) &&
            ReferenceEquals(objA: known.From, objB: m_liveProgram) &&
            ReferenceEquals(objA: region, objB: m_programRegion)
        ) {
            _ = m_programRegion.WriteRuns(
                runs: known.Runs,
                words: program.Words
            );
        } else {
            WriteProgramWords(program: program);
        }
        // Seed the screen-surface table from the program's declared surfaces (the "program uploaded once" baseline);
        // any SetScreenSurface call made before the next produced frame patches this same table before it goes out — a
        // re-upload never resurrects the program's original frame over a live SetScreenSurface write made in between.
        // Only the words that differ are owed.
        _ = m_screenSurfaceRegion.Write(
            bytes: MemoryMarshal.Cast<uint, byte>(span: program.ScreenSurfaceWords),
            offset: 0
        );

        m_liveInstanceMaskWordCount = program.InstanceMaskWordCount;
        m_liveProgram = program;
        m_wrapSignature = WrapSignatureOf(program: program);
        m_requiredDynamicTransformCapacity = program.RequiredDynamicTransformCapacity;
        // CADENCE GATE: whether ANY declared screen forces every frame to render — see m_programDeclaresScreenSlab.
        m_programDeclaresScreenSlab = ProgramDeclaresShape(
            program: program,
            shapeType: SdfShapeType.ScreenSlab
        );
        if (programChanged) { m_programRevision++; }
        if (geometryChanged) {
            m_programGeometryRevision++;
            // Changed instructions or bindings may give a dynamic slot another owner.
            m_seedDynamicHistory = true;
        }
        ReconfigureWork();
    }

    // The hash of every material's wrap in table order: the one material value the surface pass reads, deciding which
    // pixels past a light's terminator query its cascades (sdfMaterialWrap in passes/sdf-surface-stage.hlsli).
    private static ulong WrapSignatureOf(SdfProgram program) {
        var hash = Fnv1aHash.Create();

        foreach (var material in program.Materials) {
            hash.Add(value: BitConverter.SingleToUInt32Bits(value: material.Wrap));
        }

        return hash.Value;
    }
    // Only paint and material values are lighting-only. The exact prefix and suffix retain every instruction material
    // ID, compiled-part binding, shape/instance flag, bound and auxiliary table; a reordered palette is not inferred.
    private static bool SameProgramGeometry(SdfProgram previous, SdfProgram current) {
        var before = previous.Words;
        var after = current.Words;

        if ((before.Length != after.Length) || (previous.MaterialCount != current.MaterialCount) ||
            !previous.ShadingWords.Equals(other: current.ShadingWords) ||
            !previous.ScreenSurfaceWords.SequenceEqual(other: current.ScreenSurfaceWords)) { return false; }
        var (first, end) = (current.ShadingWords.Start.Value, current.ShadingWords.End.Value);

        return (before[..first].SequenceEqual(other: after[..first]) && before[end..].SequenceEqual(other: after[end..]));
    }
}
