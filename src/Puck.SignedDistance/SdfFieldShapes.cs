namespace Puck.SignedDistance;

/// <summary>Primitive shapes and their CPU and shader facets.</summary>
[SdfFieldCatalogue("SdfShapeType", unsigned: true)]
public static partial class SdfFieldShapes {
    private abstract class Entry {
        public abstract SdfOperandLanes Reinterpreted { get; }
        public abstract SdfPartShapeAction PartShape { get; }
        public abstract SdfShaderAction Shader { get; }
        public abstract SdfFormula? Formula { get; }
        public abstract bool ReliefRegion { get; }
        public abstract SdfShaderAction ReliefShader { get; }
        public abstract SdfCertificateAction Certificate { get; }
        public abstract SdfShapePointAction ShapePoint { get; }
        public abstract SdfShapeIntervalAction ShapeInterval { get; }
        public abstract SdfProfileIntervalAction ProfileInterval { get; }
        public abstract SdfShapeAdmissionAction ShapeAdmission { get; }
        public abstract SdfShapeDistanceGainAction ShapeDistanceGain { get; }
        public abstract SdfLocalBoundAction LocalBound { get; }
        public abstract bool Contact { get; }
        public abstract SdfFieldResource Resources { get; }
    }

    /// <summary>Returns the external field inputs this entry needs.</summary>
    public static SdfFieldResource ResourcesOf(SdfShapeType value) => GetResourcesOf(value: value);

    private static SdfShaderAction FormulaShader(SdfFormula formula) =>
        new(Code: $"result = {formula.Dispatch(shader: true)}; break;", Operands: SdfShaderOperands.None);

    // Data0 = (halfX, halfY, halfZ, roundingRadius)
    [SdfFieldEntry(0)]
    private sealed class Box : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfBox(p, data0.xyz, data0.w); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => true;
        public override SdfShaderAction ReliefShader => Shader;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Box;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Box;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Box;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Box;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Box;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (endX, endY, endZ, radius); the segment runs from the local origin to the endpoint
    [SdfFieldEntry(1)]
    private sealed class Capsule : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.Capsule;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => FormulaShader(Formula!);
        public override bool ReliefRegion => true;
        public override SdfShaderAction ReliefShader => Shader;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Capsule;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Capsule;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Capsule;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Capsule;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Capsule;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0.x = radius
    [SdfFieldEntry(2)]
    private sealed class Sphere : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfSphere(p, data0.x); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Sphere;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Sphere;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Sphere;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Sphere;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Sphere;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (majorRadius, minorRadius, _, _)
    [SdfFieldEntry(3)]
    private sealed class Torus : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfTorus(p, data0.x, data0.y); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Torus;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Torus;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Torus;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Torus;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Torus;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (radius, halfHeight, _, _) inset by Data1.w, Data1.w = edge-rounding radius; upright, centered on the local origin
    [SdfFieldEntry(4)]
    private sealed class Cylinder : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = (sdfCylinder(p, data0.x, data0.y) - data1.w); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Cylinder;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Cylinder;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Cylinder;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Cylinder;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Cylinder;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (normalX, normalY, normalZ, offset)
    [SdfFieldEntry(5)]
    private sealed class Plane : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfPlane(p, data0.xyz, data0.w); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => true;
        public override SdfShaderAction ReliefShader => Shader;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Plane;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Plane;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Plane;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Unbounded;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (radius, halfSeparation, halfHeight[baked √(r²−d²)], _); exact 2D vesica revolved to a lens (d < r)
    [SdfFieldEntry(6)]
    private sealed class Vesica : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfVesica(p, data0.x, data0.y, data0.z); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Vesica;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Vesica;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Vesica;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Vesica;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // --- The 2D-primitive family (an exact 2D SDF lifted to 3D by revolve/extrude). SHARED lane layout for all of
    // them: Data0.xyz = the 2D shape params, Data0.w = the lift amount (revolve offset o OR extrude half-height h),
    // Data1.x = smooth radius, Data1.y = the lift MODE (0 = revolve around Y, 1 = extrude along Z), Data1.z = per shape
    // (RegularPolygon/Star: the baked ecs.y constant; RoundedRectangle/Trapezoid/Ellipse: the cap-chamfer radius that
    // bevels an extrude's two cap rims through sdfExtrudeChamfer2D, zero = the plain join; ChamferedRectangle: unused,
    // its own chamfer doubles as the cap bevel), Data1.w = the edge-rounding radius (family-wide lane: the Data0
    // profile params and, for an extrude, the lift amount arrive already inset by it, and the kernel offsets the whole
    // field back out by it).
    // Each is exact + 1-Lipschitz (no step clamp): extrusion is always exact; revolution is exact
    // when the profile clears the axis (offset ≥ its radial extent) and a harmless conservative bound near the axis.

    // Data0 = (halfX, halfY, cornerRadius, lift), Data1.z = cap chamfer; exact rounded-box 2D SDF
    [SdfFieldEntry(7)]
    private sealed class RoundedRectangle : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfRoundedRect(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.RoundedRectangle;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.LiftedWithCap;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.RoundedRectangle;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Rectangle;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.RoundedRectangle;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (circumRadius, π/n[baked], 0[ecs.x], lift), Data1.z = 1[ecs.y]; exact star-polygon SDF with m=2
    [SdfFieldEntry(8)]
    private sealed class RegularPolygon : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfPolyStar(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Unsupported;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Entire;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.RegularPolygon;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.RegularPolygon;
        public override bool Contact => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (outerRadius, π/n[baked], cos(π/m)[baked ecs.x], lift), Data1.z = sin(π/m)[baked ecs.y]; exact star-polygon SDF
    [SdfFieldEntry(9)]
    private sealed class Star : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfPolyStar(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Unsupported;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Entire;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Star;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.RegularPolygon;
        public override bool Contact => false;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (lowerRadius, upperRadius, height, _)
    [SdfFieldEntry(10)]
    private sealed class RoundCone : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfRoundCone(p, data0.x, data0.y, data0.z, data0.w, data1.y); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.RoundCone;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.RoundCone;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.RoundCone;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.RoundCone;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (bottomHalfWidth r1, topHalfWidth r2, halfHeight, lift), Data1.z = cap chamfer; exact isosceles-trapezoid 2D SDF
    [SdfFieldEntry(11)]
    private sealed class Trapezoid : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfTrapezoidSolid(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Trapezoid;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Trapezoid;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Trapezoid;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Trapezoid;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Trapezoid;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Trapezoid;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (semiX, semiY, _, lift), Data1.z = cap chamfer; exact ellipse 2D SDF (revolve→spheroid, extrude→elliptic prism)
    [SdfFieldEntry(12)]
    private sealed class Ellipse : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfEllipseSolid(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Ellipse;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.LiftedWithCap;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Ellipse;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Ellipse;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Ellipse;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Data0 = (halfX, halfY, halfZ, roundingRadius)
    [SdfFieldEntry(13)]
    private sealed class ScreenSlab : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfBox(p, data0.xyz, data0.w); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Box;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Box;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Box;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Box;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Box;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // A glyph SAMPLED FROM A FONT ATLAS as a DISTANCE-level field (not material-level like ScreenSlab): text becomes
    // real world geometry that marches, blends, extrudes, engraves (Subtraction) and takes shadows/AO. Data0 =
    // (packedUvMin, packedUvMax [each unorm2x16-packed atlas UV, host-baked], distanceScale, extrudeHalfDepth); Data1 =
    // (smooth [ISA-wide, header/Data1.x], halfWidth, halfHeight, samplingCorrection in (0,1]). Evaluated only where the glyph atlas is bound (the
    // world-views kernel, SDF_GLYPH_ATLAS); every other kernel falls back to the exact 2D quad's extruded box — a
    // conservative underestimate, since the glyph is strictly inside its cell.
    [SdfFieldEntry(14)]
    private sealed class Glyph : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.Data0X | SdfOperandLanes.Data0Y;
        public override SdfShaderAction Shader => new(
            Code: """
                #ifdef SDF_GLYPH_ATLAS
                result = sdfGlyph(p, data0, data1);
                #else
                result = sdfGlyphQuad(p, data0, data1);
                #endif
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Unsupported;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Entire;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Glyph;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Unbounded;
        public override bool Contact => false;
        public override SdfFieldResource Resources => SdfFieldResource.GlyphAtlas;
    }

    // A sampled distance field: a cubic-voxel lattice sampled by manual trilinear interpolation. Data0 = (boxMinX, boxMinY,
    // boxMinZ, cellSize); the box extent derives as dims*cellSize. Data1 = (smooth [ISA-wide, 0: a sampled field composes
    // hard], packedDims [uint bits: 3x10-bit dims, <= 1023/axis], handle [uint bits: the field's place in
    // SdfProgram.SampledFields], boundaryFloor [margin/lambda, the outside-box lower-bound offset]). The values are not in
    // the program: each frame carries its fields' snapshots (SdfSampledSet), and only the mesh bake, the kernel that defines
    // SDF_SAMPLED_REGIONS, samples them, from the snapshots its job's groups hashed. The stored values are pre-scaled 1/lambda
    // (lambda = sqrt(3)), so the interpolant is 1-Lipschitz with no stepScale change. Every other kernel answers the far
    // distance (the Glyph quad-fallback precedent).
    [SdfFieldEntry(15)]
    private sealed class SampledRegion : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Unshared;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.Data1Y | SdfOperandLanes.Data1Z;
        public override SdfShaderAction Shader => new(Code: "result = sdfSampledRegion(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Unsupported;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Entire;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.SampledRegion;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.SampledRegion;
        public override bool Contact => false;
        public override SdfFieldResource Resources => SdfFieldResource.SampledHandle;
    }

    // A member of the 2D-primitive family (SAME lane layout as RoundedRectangle): a 45-degree-beveled rectangle (the
    // box field intersected with a diagonal bevel half-plane per corner) lifted to 3D by revolve/extrude. Data0 =
    // (halfX, halfY, chamfer c, lift); Data1 = (smooth, lift mode, UNUSED, edge-rounding radius r — the family-wide
    // fillet lane, applied ON TOP of the chamfer). The extrude lift additionally bevels the two cap edges at the SAME c
    // (see sdfExtrudeChamfer2D): a chamfered box therefore reads chamfered on all twelve edges, not just the four the
    // 2D profile itself cuts. c = 0 is the identity — the 2D core and the extrude join both reduce to the plain
    // rectangle/box forms to the bit — so an unchamfered program stays byte-identical. The field is exact inside and
    // on the surface and a conservative lower bound outside in the wedge past each bevel vertex (like the chamfer
    // blend); 1-Lipschitz (no AnalyzeLipschitz step clamp), like the rest of the family.
    [SdfFieldEntry(16)]
    private sealed class ChamferedRectangle : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => new(Code: "result = sdfChamferedRect(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.ChamferedRectangle;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.ChamferedRectangle;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.ChamferedRectangle;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Rectangle;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.ChamferedRectangle;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // Superellipsoid: NOT a member of the 2D-primitive family above (it lifts nothing — a solid 3D formula
    // directly). Data0 = (radiusX, radiusY, radiusZ, exponent e in [2, 8]); Data1 = (smooth [ISA-wide], 1/radiusX,
    // 1/radiusY, 1/radiusZ [host-baked, KEEP IN SYNC with sdfSuperellipsoid]). e = 2 is the ellipsoid
    // (the scaled L2 gauge (|p/r| - 1) * min(r), exactly 1-Lipschitz like every admitted exponent), and this id is the
    // ISA's ONE ellipsoid: every builder, creation, and debug path emits it at e == 2, which both evaluators take on a
    // pow-free fast path.
    [SdfFieldEntry(17)]
    private sealed class Superellipsoid : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.Superellipsoid;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Inline;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.None;
        public override SdfShaderAction Shader => FormulaShader(Formula!);
        public override bool ReliefRegion => true;
        public override SdfShaderAction ReliefShader => Shader;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Superellipsoid;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Superellipsoid;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Superellipsoid;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Superellipsoid;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.Superellipsoid;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Unbounded;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.None;
    }

    // ConvexPolygon: a member of the 2D-primitive family (revolve/extrude lift, family-wide smooth/lift-mode/
    // cap-chamfer/edge-rounding lanes), but its 2D profile is a validated convex vertex list too large to fit inline
    // — the vertices live in a side table appended to the packed program's own word stream (the SAME sdfWords buffer
    // every other table already lives in, so no new GPU binding is needed), and the shape instruction carries only a
    // packed (tableOffset, vertexCount) reference. Data0.x = asfloat(packed uint: (tableOffset << 5) | (vertexCount - 1)),
    // Data0.w = lift amount; Data1 = (smooth [ISA-wide], lift mode, cap chamfer, edge-rounding radius) — the same
    // family lanes RoundedRectangle carries. The table itself is <c>ceil(vertexCount/2)</c> uvec4 entries, two
    // packed (x, y) float-bit vertices each, vertices in the shape's local XY plane, clockwise. KEEP IN SYNC with
    // SDF_SHAPE_CONVEX_POLYGON / sdfConvexPolygon2D / sdfPolygonVertex.
    [SdfFieldEntry(18)]
    private sealed class ConvexPolygon : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Polygon;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.Data0X;
        public override SdfShaderAction Shader => new(Code: "result = sdfConvexPolygonSolid(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.ConvexPolygon;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.ConvexPolygon;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.ConvexPolygon;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.ConvexPolygon;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.FromLocalBound;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.ConvexPolygon;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.SideTable;
    }

    // Sweep: a quadratic Bezier curve (control points A, B, C, in the shape's local frame) swept with a radius
    // that tapers linearly between two endpoints plus a mid-span bulge, optionally as 1-4 helical strands orbiting
    // the curve. Too much data to pack inline (9 floats for the control points alone) — the points and the per-strand
    // radius endpoints live in a side table appended to the packed program's own word stream, the SAME door
    // ConvexPolygon's vertex table uses (see that shape's remarks): no new GPU binding. Data0.x = asfloat(uint table
    // offset, in uvec4 units) — HOST-PATCHED by SdfProgram's constructor once the table's layout is known, exactly
    // like ConvexPolygon's Data0.x; Data0.y = strands (1..4, always a small positive integer, so it round-trips
    // through float exactly), Data0.z = twist (turns along the curve), Data0.w = strandOffset (creation units).
    // Data1 = (smooth [ISA-wide], reserved, reserved, reserved). The table is exactly 3 uvec4 entries: (A.xyz,
    // radiusStart), (B.xyz, radiusEnd), (C.xyz, bulge) — fixed size, unlike ConvexPolygon's variable vertex count, so
    // no count needs packing alongside the offset.
    //
    // Distance: the solid is the union of the spheres of radius r(t) about each strand's centre, and the field is the
    // least gap from the point to those spheres over t, each gap 1-Lipschitz: exact outside, a bound inside. A tube of
    // one radius about the centreline reads the closed-form nearest parameter (the standard depressed-cubic solve);
    // any other sweep searches t (samples, then golden-section rounds about the nearest), since the centreline's own
    // nearest parameter is not the nearest sphere's once the radius or the strand varies. KEEP IN SYNC with sdfSweep
    // (field/sdf-shapes.hlsli) and the fixed-point mirror in Puck.SignedDistance.Queries.SdfFieldEvaluator;
    // SdfProgramBuilder.Sweep refuses (by name) a declaration whose bulge/taper/strand-offset ratios leave the envelope
    // SweepLawTests verifies the field over.
    //
    // Fixed-point mirror (Puck.SignedDistance.Queries.SdfFieldEvaluator): supported ONLY for strands == 1 — a single
    // strand's search is fine in fixed point (FixedQ4816.Pow/Sin/Cos are available), but the
    // multi-strand orbit and its per-strand min are RENDER-ONLY and refused for deterministic field contact by name,
    // exactly like Glyph/SampledRegion are refused for reasons of their own.
    [SdfFieldEntry(19)]
    private sealed class Sweep : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Unshared;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.Data0X;
        public override SdfShaderAction Shader => new(Code: "result = sdfSweep(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Sweep;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Sweep;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Sweep;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.Uncertified;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Sweep;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.SideTable;
    }

    // Path: bounded line-edge table, two uvec4 per edge: (A.xy, B.xy), (radiusA, radiusB, 0, 0).
    // Data0 = (asfloat(tableOffset), edgeCount, enclosingRadius2D, halfDepth); Data1 = (smooth, stroke?1:0, 0, 0).
    // Filled paths use even/odd parity and distance to all boundaries; strokes union the convex hulls of endpoint
    // disks. Both are 1-Lipschitz. Curves are flattened once by SdfPathCompiler to an explicit author tolerance.
    // Extrusion only. The fixed-point evaluator reads the same edges from the program's own table (SdfProgram.Paths).
    [SdfFieldEntry(20)]
    private sealed class Path : Entry {
        public override SdfFormula? Formula => null;
        public override SdfPartShapeAction PartShape => SdfPartShapeAction.Unshared;
        public override SdfOperandLanes Reinterpreted => SdfOperandLanes.Data0X;
        public override SdfShaderAction Shader => new(Code: "result = sdfPathSolid(p, data0, data1); break;", Operands: SdfShaderOperands.None);
        public override bool ReliefRegion => false;
        public override SdfShaderAction ReliefShader => SdfShaderAction.Unavailable;
        public override SdfCertificateAction Certificate => SdfCertificateAction.Unavailable;
        public override SdfShapePointAction ShapePoint => SdfShapePointAction.Path;
        public override SdfShapeIntervalAction ShapeInterval => SdfShapeIntervalAction.Path;
        public override SdfProfileIntervalAction ProfileInterval => SdfProfileIntervalAction.Unsupported;
        public override SdfShapeAdmissionAction ShapeAdmission => SdfShapeAdmissionAction.Path;
        public override SdfShapeDistanceGainAction ShapeDistanceGain => SdfShapeDistanceGainAction.Uncertified;
        public override SdfLocalBoundAction LocalBound => SdfLocalBoundAction.Path;
        public override bool Contact => true;
        public override SdfFieldResource Resources => SdfFieldResource.SideTable;
    }
}
