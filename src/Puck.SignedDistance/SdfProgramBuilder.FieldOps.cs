using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    /// <summary>Inflates the entire field accumulated so far by a radius (rounds and fattens everything before it) —
    /// a field op: order it after everything it should inflate.</summary>
    /// <param name="radius">The inflation radius. A negative radius is legal and erodes instead — the decoder is a
    /// plain <c>d -= radius</c>, exact and 1-Lipschitz in both directions.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is not finite.</exception>
    public SdfProgramBuilder Dilate(float radius) {
        // Sign DELIBERATELY unconstrained (unlike Onion's thickness): `result.distance -= data0.x` is a pure offset of
        // a signed distance field, so a negative radius is an erosion — real geometry, not an empty field — and
        // SdfProgram's cull margin already folds MathF.Abs of this lane.
        return FiniteScalarTransform(
            op: SdfOp.Dilate,
            value: radius,
            paramName: nameof(radius),
            subject: "A dilation radius"
        );
    }
    /// <summary>Adds a bounded sinusoidal displacement to the field accumulated so far — surface relief (bumps,
    /// corrugation, a rippled skin) evaluated at the current point: the SDF-native answer to height/parallax mapping,
    /// where the relief is real geometry (it shadows and self-occludes). A field op (like <see cref="Onion"/>/
    /// <see cref="Dilate"/>): order it after the shapes it should displace. The separable <c>sin·sin·sin</c> basis is
    /// deterministic across both backends. Not 1-Lipschitz — the relief's gradient reaches <c>amplitude·‖frequency‖</c>,
    /// so the field can overestimate true distance by up to <c>1 + amplitude·‖frequency‖</c> and <c>AnalyzeLipschitz</c>
    /// bakes that as a conservative step clamp; keep <c>amplitude·‖frequency‖</c> moderate (a large product clamps the
    /// march to tiny steps). KEEP IN SYNC with SDF_OP_DISPLACE in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="frequency">Per-axis angular frequency of the sinusoid (radians per world unit).</param>
    /// <param name="amplitude">Peak displacement added to the field (world units; 0 = an exact identity).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frequency"/> or <paramref name="amplitude"/> is
    /// not finite.</exception>
    public SdfProgramBuilder Displace(Vector3 frequency, float amplitude) {
        // Both are signed by construction (a negative frequency or amplitude reverses the relief's phase, which is a
        // real authoring choice), and every consumer of the amplitude takes its magnitude — SdfProgram's cull margin
        // and DisplaceWarpLipschitz both read MathF.Abs — so only finiteness is refused.
        RequireFinite(
            value: frequency,
            paramName: nameof(frequency),
            subject: "A displacement frequency"
        );
        RequireFinite(
            value: amplitude,
            paramName: nameof(amplitude),
            subject: "A displacement amplitude"
        );

        return Transform(
            data0: new Vector4(
                value: frequency,
                w: amplitude
            ),
            op: SdfOp.Displace
        );
    }
    /// <summary>Shells the entire field accumulated so far into a hollow skin of the given thickness — a field op:
    /// order it after everything it should shell.</summary>
    /// <param name="thickness">The shell half-thickness.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thickness"/> is not finite and non-negative.</exception>
    public SdfProgramBuilder Onion(float thickness) {
        // The decoder is `d = abs(d) - thickness`: a negative thickness leaves the field strictly positive everywhere,
        // so the shell has no zero set at all — the op silently erases the geometry it was ordered after. (Unlike
        // Dilate, whose negative branch is a real erosion; see its remarks.)
        RequireNonNegative(
            value: thickness,
            paramName: nameof(thickness),
            subject: "An onion shell thickness"
        );

        return ScalarTransform(
            op: SdfOp.Onion,
            value: thickness
        );
    }
    /// <summary>Closes the scope opened by the matching <see cref="PushField"/> and composes its field back into the
    /// parent as one candidate, using the compose blend + smooth radius that <see cref="PushField"/> recorded. KEEP IN
    /// SYNC with SDF_OP_POP_FIELD in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <exception cref="InvalidOperationException">No field scope is open, or the scope emitted no shape.</exception>
    public SdfProgramBuilder PopField() {
        if (m_fieldScope is not { } scope) {
            throw new InvalidOperationException(message: "PopField was called with no open field scope (unbalanced PushField/PopField).");
        }

        m_fieldScope = null;

        if (m_shapeCount == scope.ShapeCountAtOpen) {
            throw new InvalidOperationException(message: "A field scope (PushField/PopField) must contain at least one shape — an empty scope composes SDF_FAR_DISTANCE and would carve nothing.");
        }

        // The POP carries the compose blend (Blend lane, header.z) and its smooth radius (Data1.x) — the SAME lanes a
        // ShapeBlend uses, because the shader treats a POP as just another candidate through the shared blend tail.
        m_instructions.Add(item: new SdfInstruction(
            Blend: ((uint)scope.Blend),
            Data0: default,
            Data1: new Vector4(
                w: 0f,
                x: MathF.Max(
                    x: 0f,
                    y: scope.Smooth
                ),
                y: 0f,
                z: 0f
            ),
            Material: 0,
            Op: SdfOp.PopField,
            Shape: 0
        ));

        return this;
    }
    /// <summary>Opens a scoped field accumulator (<see cref="SdfOp.PushField"/>): every accumulator-reading op emitted
    /// until the matching <see cref="PopField"/> — the intersection family, and the <see cref="Onion"/>/
    /// <see cref="Dilate"/>/<see cref="Displace"/> field ops — acts on this scope's shapes alone, not on everything
    /// emitted before it. Pair it with <see cref="PopField"/> to compose the scope back into the parent field; the
    /// <paramref name="compose"/> blend + <paramref name="smooth"/> given here are baked onto the pop instruction (a
    /// Union compose keeps the scope far-neutral, so a scoped instance stays cullable and segment-eligible; an
    /// intersection-family compose composes the scope globally, unmaskable). The scope must contain at least one shape,
    /// nest no deeper than <see cref="MaxFieldScopeDepth"/>, and close (via <see cref="PopField"/>) before
    /// <see cref="Build"/> or an enclosing <see cref="EndInstance"/>. A scope touches only the field, not the point, so
    /// per-shape cull bounds inside it stay sound and <see cref="ResetPoint"/> works as usual. KEEP IN SYNC with
    /// SDF_OP_PUSH_FIELD in Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    /// <param name="compose">How the closed scope's field composes back into the parent (default <see cref="SdfBlendOp.Union"/>).</param>
    /// <param name="smooth">The smooth/chamfer radius of the <paramref name="compose"/> blend (ignored by the hard blends).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="smooth"/> is not finite, or
    /// <paramref name="compose"/> is not a defined <see cref="SdfBlendOp"/>.</exception>
    /// <exception cref="InvalidOperationException">The scope would nest deeper than <see cref="MaxFieldScopeDepth"/>.</exception>
    public SdfProgramBuilder PushField(SdfBlendOp compose = SdfBlendOp.Union, float smooth = 0f) {
        // PopField bakes MathF.Max(0f, smooth) onto the POP instruction, which absorbs a negative radius but not NaN.
        RequireFinite(
            value: smooth,
            paramName: nameof(smooth),
            subject: "A field-scope compose smooth radius"
        );
        // compose doesn't flow through Shape() (PopField writes it directly onto its own instruction), so it needs its
        // own enum-floor check rather than inheriting Shape()'s.
        RequireDefined(
            value: compose,
            paramName: nameof(compose)
        );

        // The depth guard reads MaxFieldScopeDepth (rather than just testing m_fieldScope is not null) so raising the
        // cap past 1 stays a localized change to this field + guard (see m_fieldScope's doc).
        var openDepth = ((m_fieldScope is null)
            ? 0
            : 1
        );

        if (openDepth >= MaxFieldScopeDepth) {
            throw new InvalidOperationException(message: $"PushField would nest a field scope deeper than the depth-{MaxFieldScopeDepth} cap. Close the open scope (PopField) before opening another.");
        }

        m_fieldScope = (compose, smooth, m_shapeCount);

        // A bare marker: the compose blend + smooth ride the POP instruction (a POP is the candidate), so the PUSH
        // carries no data — the shader only saves the accumulator and reseeds. Not routed through Transform() because
        // that path is byte-for-byte the pre-scope emission and must not gain a new caller here.
        m_instructions.Add(item: new SdfInstruction(
            Blend: 0,
            Data0: default,
            Data1: default,
            Material: 0,
            Op: SdfOp.PushField,
            Shape: 0
        ));

        return this;
    }
}
