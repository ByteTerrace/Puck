using System.Runtime.InteropServices;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    /// <summary>Re-uploads the scene program (the host's <c>ProgramChanged</c> path — e.g. a rebuilt overworld scene).
    /// The program region and instance capacity grow on demand without waiting for the device. Persistent images,
    /// screen bindings and pipelines remain in place. Palette-value edits keep geometry signatures; wrap and shadow
    /// density separately invalidate their consuming passes.
    /// Material reassignment or any other field/layout change invalidates them.</summary>
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
            programChanged = (geometryChanged || !previous.ShadingWords.SequenceEqual(other: program.ShadingWords));
        }

        EnsureDynamicTransformCapacity(program.RequiredDynamicTransformCapacity);

        // The program words go through their region like every other table, so a frame in flight keeps reading the words
        // its slot was sent. The region keeps room for the default shade program the tables last packed after it.
        EnsureProgramCapacity(program: program, shade: m_shadeDefault);

        if (
            (delta is { } known) &&
            ReferenceEquals(objA: known.To, objB: program) &&
            known.LeadsFrom(program: m_liveProgram)
        ) {
            _ = m_programRegion.WriteRuns(
                head: program.FieldWords,
                runs: known.Runs,
                tail: program.ShadingWords
            );
        } else {
            WriteProgramWords(program: program);
        }
        // Seed the screen-surface table from the program's declared surfaces (the "program uploaded once" baseline);
        // any SetScreenSurface call made before the next produced frame patches this same table before it goes out — a
        // re-upload never resurrects the program's original frame over a live SetScreenSurface write made in between.
        // Only the words that differ are owed.
        _ = m_screenSurfaceRegion.Write(
            bytes: MemoryMarshal.Cast<uint, byte>(span: program.Appearance.ScreenSurfaceWords),
            offset: 0
        );
        // The program's words carry no range; each screen keeps the one its last frame sized (PackScreenReaches).
        for (var screen = 0; (screen < MaxScreenSurfaces); screen++) { WriteScreenReach(screen: screen); }

        m_liveInstanceMaskWordCount = program.InstanceMaskWordCount;
        m_liveProgram = program;
        m_wrapSignature = WrapSignatureOf(program: program);
        m_shadowDensitySignature = ShadowDensitySignatureOf(program: program);
        m_requiredDynamicTransformCapacity = program.RequiredDynamicTransformCapacity;
        if (programChanged) { m_programRevision++; }
        if (geometryChanged) {
            m_programGeometryRevision++;
            // Changed instructions or bindings may give a dynamic slot another owner.
            m_seedDynamicHistory = true;
        }
        ReconfigureWork();
    }

    // The hash of every material's wrap in table order: the surface pass reads it to decide which
    // pixels past a light's terminator query its cascades (sdfMaterialShadowDemand in passes/sdf-surface-stage.hlsli).
    private static ulong WrapSignatureOf(SdfProgram program) {
        var hash = Fnv1aHash.Create();

        foreach (var material in program.Appearance.Materials) {
            hash.AddBits(value: material.Wrap);
            hash.AddBits(value: (material.Shade?.Demand.Cosine(material.Wrap) ?? -material.Wrap));
        }

        return hash.Value;
    }
    // Cascades read density; the surface pass reads their optical term. Palette-only uploads must invalidate both.
    private static ulong ShadowDensitySignatureOf(SdfProgram program) {
        var hash = Fnv1aHash.Create();
        foreach (var material in program.Appearance.Materials) {
            hash.AddBits(value: material.Glass?.ShadowDensity ?? 0f);
        }
        return hash.Value;
    }
    // Paint and material values do not change geometry. Wrap and density have separate pass signatures.
    // The field's exact words retain every instruction material ID, compiled-part binding, shape/instance flag, bound and
    // auxiliary table; a reordered palette is not inferred. Programs that share a field share those words, so only a
    // field rebuild compares them.
    private static bool SameProgramGeometry(SdfProgram previous, SdfProgram current) =>
        (previous.ShadingWords.Length == current.ShadingWords.Length) &&
        previous.Appearance.ScreenSurfaceWords.SequenceEqual(other: current.Appearance.ScreenSurfaceWords) &&
        (current.SharesField(other: previous) || current.FieldWords.SequenceEqual(other: previous.FieldWords));
}
