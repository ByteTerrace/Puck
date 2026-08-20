using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The screen frame's admission tolerances. The skew bound is a cosine (~0.057 degrees); the unit bound admits the
    // rounding a quaternion-derived or fixed-point-derived axis carries. KEEP IN SYNC with SdfProgramBuilder's
    // BasisSkewTolerance — the builder refuses the same frame one layer earlier, naming the caller's argument.
    private const float ScreenFrameSkewTolerance = 1.0e-3f;
    private const float ScreenFrameUnitTolerance = 1.0e-3f;

    private bool DeclaresScreenIndex(int screenIndex) {
        foreach (var surface in m_screenSurfaces) {
            if (surface.ScreenIndex == screenIndex) {
                return true;
            }
        }

        return false;
    }
    private static void RequirePackedBlend(uint blend, int index, string paramName) {
        if (!Enum.IsDefined(value: ((SdfBlendOp)blend))) {
            throw new ArgumentException(
                message: $"SDF ISA v{SdfIsa.Version} refuses undeclared blend {blend} at instruction {index}.",
                paramName: paramName
            );
        }
    }
    // The screen frame is packed as two independent axes the shader projects a hit point onto, while the slab's own
    // geometry rides the rotation derived from that same pair — so a non-unit or skewed pair packs a UV that does not
    // describe the surface it labels. KEEP IN SYNC with SdfProgramBuilder's RequireOrthogonalBasis tolerance.
    private static void RequireOrthonormalScreenFrame(SdfScreenSurface surface, string paramName) {
        var axisRight = surface.Right;
        var axisUp = surface.Up;
        var skew = Vector3.Dot(
            vector1: axisRight,
            vector2: axisUp
        );

        if (
            !(MathF.Abs(x: (axisRight.Length() - 1f)) <= ScreenFrameUnitTolerance) ||
            !(MathF.Abs(x: (axisUp.Length() - 1f)) <= ScreenFrameUnitTolerance) ||
            !(MathF.Abs(x: skew) <= ScreenFrameSkewTolerance)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: paramName,
                message: $"A screen surface's right/up axes must be unit and orthogonal; got |right| {axisRight.Length()}, |up| {axisUp.Length()}, dot {skew}."
            );
        }
    }
    // The packed format's OWN admission test, run before anything reads a lane. ValidateIsa covers the opcode; these
    // are the other lanes the packing writes straight into GPU words, where an out-of-domain value is not a fault but a
    // silently wrong program: an undeclared shape/blend id falls through the kernel's switch, a material id past the
    // palette is clamped to row 0 by sdfMaterialLoad, a screen sentinel naming no declared surface indexes the
    // screenSurfaces/decal tables past their entries, and an instance range outside the stream feeds the segment walk
    // an interval it cannot own. Each is refused by name here, at the type, rather than surfacing as an incidental
    // IndexOutOfRangeException from a packing loop or as pixels nobody can explain.
    private void ValidatePackedContract(int materialCount, string instancesParamName, string instructionsParamName, string screenSurfacesParamName) {
        // Ids from SdfProgramBuilder.ScreenMaterialId up decode as screen shading, so a palette reaching that far
        // carries rows no instruction can name (SdfProgramBuilder.AddMaterial refuses the same row at its own door).
        if (materialCount > SdfProgramBuilder.ScreenMaterialId) {
            throw new ArgumentException(
                message: $"A program declares {materialCount} materials, but ids from {SdfProgramBuilder.ScreenMaterialId} up are the screen sentinels, so those rows can never be addressed.",
                paramName: "materials"
            );
        }

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op == SdfOp.PopField) {
                RequirePackedBlend(
                    blend: instruction.Blend,
                    index: index,
                    paramName: instructionsParamName
                );
                continue;
            }

            if (instruction.Op != SdfOp.ShapeBlend) {
                continue;   // Every other op's Shape/Blend/Material lanes carry op-specific data (a fold's axis, a jitter's noise flavor and variant count), not these domains.
            }

            if (!Enum.IsDefined(value: ((SdfShapeType)instruction.Shape))) {
                throw new ArgumentException(
                    message: $"SDF ISA v{SdfIsa.Version} refuses undeclared shape {instruction.Shape} at instruction {index}.",
                    paramName: instructionsParamName
                );
            }

            RequirePackedBlend(
                blend: instruction.Blend,
                index: index,
                paramName: instructionsParamName
            );

            if (instruction.Material >= ((uint)SdfProgramBuilder.ScreenMaterialId)) {
                // The sentinel band: SdfProgramBuilder.ScreenMaterialId is the plain procedural screen material (it
                // reads no side table), and every id above it decodes to a direct screen-surface index.
                if (instruction.Material == ((uint)SdfProgramBuilder.ScreenMaterialId)) {
                    continue;
                }

                var screenIndex = ((int)((instruction.Material - SdfProgramBuilder.ScreenMaterialId) - 1u));

                if (!DeclaresScreenIndex(screenIndex: screenIndex)) {
                    throw new ArgumentException(
                        message: $"Instruction {index} names screen material {instruction.Material}, which decodes to screen index {screenIndex}, but the program declares no screen surface at that index.",
                        paramName: instructionsParamName
                    );
                }

                continue;
            }

            if (instruction.Material >= ((uint)materialCount)) {
                throw new ArgumentException(
                    message: $"Instruction {index} names material {instruction.Material}, but the program declares {materialCount} material(s).",
                    paramName: instructionsParamName
                );
            }
        }

        var seenScreenIndices = 0u;   // A bit per slot: MaxScreenSurfaces is 32, the same width the engine's screenMask push word carries.

        foreach (var surface in m_screenSurfaces) {
            if (
                (surface.ScreenIndex < 0) ||
                (surface.ScreenIndex >= SdfProgramBuilder.MaxScreenSurfaces)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: screenSurfacesParamName,
                    message: $"A screen index must be 0..{(SdfProgramBuilder.MaxScreenSurfaces - 1)}; got {surface.ScreenIndex}."
                );
            }

            var bit = (1u << surface.ScreenIndex);

            if (0u != (seenScreenIndices & bit)) {
                throw new ArgumentException(
                    message: $"Two screen surfaces declare index {surface.ScreenIndex}. The packed table is indexed BY screen index, so one would silently overwrite the other.",
                    paramName: screenSurfacesParamName
                );
            }

            seenScreenIndices |= bit;

            RequireOrthonormalScreenFrame(
                surface: surface,
                paramName: screenSurfacesParamName
            );
        }

        foreach (var instance in m_instances) {
            if (
                (instance.First < 0) ||
                (instance.End > m_instructions.Length) ||
                (instance.First > instance.End)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: instancesParamName,
                    message: $"An instance range must satisfy 0 <= First <= End <= {m_instructions.Length}; got [{instance.First}, {instance.End})."
                );
            }
        }
    }
    public void ValidateIsa() {
        for (var index = 0; (index < m_instructions.Length); index++) {
            var opcode = m_instructions[index].Op;

            if (!Enum.IsDefined(value: opcode)) {
                var raw = ((uint)opcode);

                throw new ArgumentException(
                    message: $"SDF ISA v{SdfIsa.Version} refuses undeclared opcode {raw} (0x{raw:X8}) at instruction {index}.",
                    paramName: "instructions"
                );
            }
        }
    }
}
