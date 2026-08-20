using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The screen frame's admission tolerances. The skew bound is a cosine (~0.057 degrees); the unit bound admits the
    // rounding a quaternion-derived or fixed-point-derived axis carries. KEEP IN SYNC with SdfProgramBuilder's
    // BasisSkewTolerance — the builder refuses the same frame one layer earlier, naming the caller's argument.
    private const float ScreenFrameSkewTolerance = 1.0e-3f;
    private const float ScreenFrameUnitTolerance = 1.0e-3f;

    // The operand lanes a shape carries as reinterpreted integer BITS rather than a float value, as a bit per lane over
    // (Data0.xyzw, Data1.xyzw). A bit pattern there reads as NaN or an infinity as often as it reads as a number, so the
    // finiteness sweep must skip exactly these and no others. KEEP IN SYNC with the asuint() reads in
    // Assets/Shaders/Sdf/sdf-vm.hlsli: sdfGlyphUnpackUv(data0.x)/sdfGlyphUnpackUv(data0.y) and sdfSampledRegion's
    // asuint(data1.y) packedDims / asuint(data1.z) brickWordOffset.
    private const uint GlyphReinterpretedLanes = 0b0000_0011u;
    private const uint SampledRegionReinterpretedLanes = 0b0110_0000u;

    private static readonly string[] OperandLaneNames = ["Data0.x", "Data0.y", "Data0.z", "Data0.w", "Data1.x", "Data1.y", "Data1.z", "Data1.w"];

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(f: value.X) &&
        float.IsFinite(f: value.Y) &&
        float.IsFinite(f: value.Z);

    private bool DeclaresScreenIndex(int screenIndex) {
        foreach (var surface in m_screenSurfaces) {
            if (surface.ScreenIndex == screenIndex) {
                return true;
            }
        }

        return false;
    }
    // Every lane a caller supplies reaches the GPU as a packed word bit-for-bit (WriteVector4 reinterprets, it never
    // arithmetically normalizes), so a NaN or an infinity is absorbed nowhere downstream: it survives into the
    // instruction stream, poisons the program-wide Lipschitz step scale (AnalyzeLipschitz folds every shape dimension
    // and warp rate into one scalar) and the cull bounds derived from the same lanes, and the shader propagates it
    // through every blend. SdfProgramBuilder refuses the same values one layer earlier, naming the caller's argument.
    private static void RequireFiniteOperands(SdfInstruction instruction, int index, string paramName) {
        var reinterpreted = ((instruction.Op == SdfOp.ShapeBlend)
            ? (((SdfShapeType)instruction.Shape) switch {
                SdfShapeType.Glyph => GlyphReinterpretedLanes,
                SdfShapeType.SampledRegion => SampledRegionReinterpretedLanes,
                _ => 0u,
            })
            : 0u
        );
        ReadOnlySpan<float> lanes = [
            instruction.Data0.X,
            instruction.Data0.Y,
            instruction.Data0.Z,
            instruction.Data0.W,
            instruction.Data1.X,
            instruction.Data1.Y,
            instruction.Data1.Z,
            instruction.Data1.W,
        ];

        for (var lane = 0; (lane < lanes.Length); lane++) {
            if (
                (0u != (reinterpreted & (1u << lane))) ||
                float.IsFinite(f: lanes[lane])
            ) {
                continue;
            }

            throw new ArgumentException(
                message: $"Instruction {index} carries {lanes[lane]} in {OperandLaneNames[lane]}; every operand lane packs into a GPU word bit-for-bit, so a non-finite value reaches the Lipschitz step scale, the cull bounds, and every blend downstream.",
                paramName: paramName
            );
        }
    }
    private static void RequirePackedBlend(uint blend, int index, string paramName) {
        if (!Enum.IsDefined(value: ((SdfBlendOp)blend))) {
            throw new ArgumentException(
                message: $"SDF ISA v{SdfIsa.Version} refuses undeclared blend {blend} at instruction {index}.",
                paramName: paramName
            );
        }
    }
    // The public constructor is an equal admission door to SdfProgramBuilder.Build: callers may assemble the packed
    // tables directly, so material values need the same finite/non-negative domain AddMaterial enforces.
    private static void RequirePackedMaterials(IReadOnlyList<SdfMaterial> materials, string paramName) {
        for (var index = 0; (index < materials.Count); index++) {
            var material = materials[index];

            if (
                !IsFinite(value: material.Albedo) ||
                (material.Albedo.X < 0f) ||
                (material.Albedo.Y < 0f) ||
                (material.Albedo.Z < 0f) ||
                !float.IsFinite(f: material.Emissive) ||
                (material.Emissive < 0f) ||
                !float.IsFinite(f: material.Specular) ||
                (material.Specular < 0f) ||
                !float.IsFinite(f: material.Shininess) ||
                (material.Shininess < 0f)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: paramName,
                    message: $"Material {index} must carry finite, non-negative albedo, emissive, specular, and shininess values; got {material}."
                );
            }
        }
    }
    // Instance ranges must PARTITION the instructions they claim. The segment walk attributes each segment to ONE owner,
    // so a second instance naming an instruction the first already owns packs the empty segment range [0, 0) while still
    // carrying a real cull bound: its geometry then renders only where the winner's mask bit happens to be set, and
    // vanishes wherever the beam culls the winner but not the loser. The owner resolve keeps its deterministic
    // first-match tie-break as defence in depth — it makes the packed words a total function of any range set that
    // reaches it — but this door never admits a set that needs it. (SdfProgramBuilder cannot author one at all:
    // BeginInstance refuses a nested open instance.)
    private void RequireDisjointInstanceRanges(string paramName) {
        if (m_instances.Length < 2) {
            return;
        }

        var spans = new (int First, int End, int Index)[m_instances.Length];

        for (var index = 0; (index < m_instances.Length); index++) {
            spans[index] = (m_instances[index].First, m_instances[index].End, index);
        }

        // Tuples order lexicographically, so this sorts by First then End. Every range satisfies First <= End (checked
        // before this runs), so once each neighbour clears its predecessor's End the Ends are non-decreasing and the
        // adjacent test is the whole pairwise-disjointness test.
        Array.Sort(array: spans);

        for (var index = 1; (index < spans.Length); index++) {
            var previous = spans[(index - 1)];
            var current = spans[index];

            if (current.First < previous.End) {
                throw new ArgumentException(
                    message: $"Instances {previous.Index} [{previous.First}, {previous.End}) and {current.Index} [{current.First}, {current.End}) overlap. Each instruction is owned by at most one instance, so the loser would pack an empty segment range behind a live cull bound.",
                    paramName: paramName
                );
            }
        }
    }
    // A PushField/PopField pair saves one accumulator slot in every interpreter. A hand-assembled stream must obey
    // the same one-deep, balanced, single-owner discipline as the builder: crossing an instance boundary would let a
    // masked segment observe a save or restore emitted by a different mask bit (or by the unmasked world stream).
    private void RequireBalancedFieldScopes(string paramName) {
        var instructionOwners = BuildInstructionOwners();
        var scopeDepth = 0;
        var scopeOwner = -1;

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            if (
                (scopeDepth > 0) &&
                (instructionOwners[index] != scopeOwner)
            ) {
                throw new ArgumentException(
                    message: $"The field scope opened before instruction {index} crosses from instruction owner {scopeOwner} to {instructionOwners[index]}. A scope must stay wholly inside one instance slice or wholly in the world stream.",
                    paramName: paramName
                );
            }

            if (instruction.Op == SdfOp.PushField) {
                scopeDepth++;

                if (scopeDepth > SdfProgramBuilder.MaxFieldScopeDepth) {
                    throw new ArgumentException(
                        message: $"Instruction {index} opens field-scope depth {scopeDepth}, above the supported maximum {SdfProgramBuilder.MaxFieldScopeDepth}.",
                        paramName: paramName
                    );
                }

                scopeOwner = instructionOwners[index];

                continue;
            }

            if (instruction.Op != SdfOp.PopField) {
                continue;
            }

            if (scopeDepth == 0) {
                throw new ArgumentException(
                    message: $"Instruction {index} closes a field scope when no PushField is open.",
                    paramName: paramName
                );
            }

            scopeDepth--;

            if (scopeDepth == 0) {
                scopeOwner = -1;
            }
        }

        if (scopeDepth != 0) {
            throw new ArgumentException(
                message: "The instruction stream ends with an open PushField scope. Every field scope must close with PopField.",
                paramName: paramName
            );
        }
    }
    // The exact trapezoid core projects onto the slanted side by dividing by that side's squared length, so a profile
    // whose slant vanishes returns NaN from the shader and divides by zero in the deterministic fixed-point evaluator.
    // The slant is read from the lanes exactly as sdfTrapezoid2D reads them: k2 = (Data0.y - Data0.x, 2 * Data0.z).
    // KEEP IN SYNC with SdfProgramBuilder's MinTrapezoidProfileSlant refusal, which names the caller's argument.
    private static void RequireTrapezoidProfileSlant(SdfInstruction instruction, int index, string paramName) {
        var slant = new Vector2(
            x: (instruction.Data0.Y - instruction.Data0.X),
            y: (2f * instruction.Data0.Z)
        );

        if (slant.LengthSquared() < (SdfProgramBuilder.MinTrapezoidProfileSlant * SdfProgramBuilder.MinTrapezoidProfileSlant)) {
            throw new ArgumentOutOfRangeException(
                paramName: paramName,
                message: $"Instruction {index} declares a trapezoid whose profile slant vector (topHalfWidth - bottomHalfWidth, 2*halfHeight) is {slant.Length()} long, under the {SdfProgramBuilder.MinTrapezoidProfileSlant} the deterministic fixed-point field evaluator can distinguish from a point."
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
    // screenSurfaces/decal tables past their entries, an instance range outside the stream feeds the segment walk an
    // interval it cannot own, a non-finite operand lane poisons the whole program's step scale and cull bounds, a
    // vanishing trapezoid slant or screen half-extent divides by zero in a core that projects by it, and two instance
    // ranges claiming one instruction leave the loser packing an empty segment range behind a live cull bound. Each is
    // refused by name here, at the type, rather than surfacing as an incidental IndexOutOfRangeException from a packing
    // loop or as pixels nobody can explain.
    private void ValidatePackedContract(IReadOnlyList<SdfMaterial> materials, string instancesParamName, string instructionsParamName, string screenSurfacesParamName) {
        var materialCount = materials.Count;

        // Ids from SdfProgramBuilder.ScreenMaterialId up decode as screen shading, so a palette reaching that far
        // carries rows no instruction can name (SdfProgramBuilder.AddMaterial refuses the same row at its own door).
        if (materialCount > SdfProgramBuilder.ScreenMaterialId) {
            throw new ArgumentException(
                message: $"A program declares {materialCount} materials, but ids from {SdfProgramBuilder.ScreenMaterialId} up are the screen sentinels, so those rows can never be addressed.",
                paramName: "materials"
            );
        }

        RequirePackedMaterials(
            materials: materials,
            paramName: "materials"
        );

        for (var index = 0; (index < m_instructions.Length); index++) {
            var instruction = m_instructions[index];

            RequireFiniteOperands(
                index: index,
                instruction: instruction,
                paramName: instructionsParamName
            );

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

            if (instruction.Shape == ((uint)SdfShapeType.Trapezoid)) {
                RequireTrapezoidProfileSlant(
                    index: index,
                    instruction: instruction,
                    paramName: instructionsParamName
                );
            }

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

            if (!IsFinite(value: surface.Origin)) {
                throw new ArgumentOutOfRangeException(
                    paramName: screenSurfacesParamName,
                    message: $"A screen surface's origin must be finite; got {surface.Origin}."
                );
            }

            // sampleScreenSurface resolves the UV as dot(local, right)/right.w and dot(local, up)/up.w, so a zero (or
            // NaN) half-extent maps every hit on a reachable surface to a non-finite UV. KEEP IN SYNC with
            // SdfProgramBuilder's indexed ScreenSlab overload, which refuses the same face naming halfExtents. The
            // slab's DEPTH half-extent is unconstrained here: nothing divides by it.
            if (
                !float.IsFinite(f: surface.HalfWidth) ||
                !float.IsFinite(f: surface.HalfHeight) ||
                !(surface.HalfWidth > 0f) ||
                !(surface.HalfHeight > 0f)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: screenSurfacesParamName,
                    message: $"A screen surface's half-width and half-height must be positive; got {surface.HalfWidth} and {surface.HalfHeight}. The shader divides the hit's projection onto each axis by that axis's half-extent."
                );
            }

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
            if (
                !IsFinite(value: instance.Center) ||
                !float.IsFinite(f: instance.Radius) ||
                (instance.Radius < 0f)
            ) {
                throw new ArgumentOutOfRangeException(
                    paramName: instancesParamName,
                    message: $"An instance bound must carry a finite center and a finite, non-negative radius; got center {instance.Center} and radius {instance.Radius}."
                );
            }
        }

        RequireDisjointInstanceRanges(paramName: instancesParamName);
        RequireBalancedFieldScopes(paramName: instructionsParamName);
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
