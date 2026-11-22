namespace Puck.SignedDistance;

/// <summary>Relief mask rows and their admission and shader facets.</summary>
[SdfFieldCatalogue("SdfReliefRowKind", unsigned: false)]
public static partial class SdfFieldReliefRows {
    /// <summary>The kernels' call for the weight of the open mask's band (start, width, inside, outside) at its regions' distance.</summary>
    public static string BandWeight => SdfFieldFormulas.ReliefBandWeight.Dispatch(shader: true,
        ("start", "band.x"), ("end", "(band.x + band.y)"), ("inside", "band.z"), ("outside", "band.w"));

    private abstract class Entry {
        // The shared formula (SdfFieldFormulas) the row's readers lower, where it has one.
        public virtual SdfFormula? Formula => null;
        public abstract SdfShaderAction Shader { get; }
        public abstract SdfReliefRowAdmissionAction ReliefRowAdmission { get; }
    }

    /// <summary>A mask's band: Data0 holds its start, width, inside weight and outside weight.</summary>
    [SdfFieldEntry(0)]
    private sealed class Band : Entry {
        public override SdfFormula? Formula => SdfFieldFormulas.ReliefBandWeight;
        public override SdfShaderAction Shader => new(
            Code: $$"""
                if (open) {
                    weight *= {{BandWeight}};
                    if (weight <= 0.0) { return 0.0; }
                }
                band = rowData0;
                distance = SDF_FAR_DISTANCE;
                open = true;
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfReliefRowAdmissionAction ReliefRowAdmission => SdfReliefRowAdmissionAction.Band;
    }

    /// <summary>A region's pose in the relief's frame: Data0.xyz holds its origin and Data1 its unit quaternion.</summary>
    [SdfFieldEntry(1)]
    private sealed class Pose : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                origin = rowData0.xyz;
                turn = rowData1;
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfReliefRowAdmissionAction ReliefRowAdmission => SdfReliefRowAdmissionAction.Pose;
    }

    /// <summary>A region's primitive: its Shape lane and data vectors as a shape instruction packs them, Data1.x zero.</summary>
    [SdfFieldEntry(2)]
    private sealed class Primitive : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                if (distance > band.x) {
                    float3 u = rotatePointByInverseQuaternion((q - origin), turn);
                    distance = min(distance, sdfReliefRegionDistance(SDF_INSTRUCTION_SHAPE(header), u, rowData0, rowData1));
                }
                break;
                """,
            Operands: SdfShaderOperands.None
        );
        public override SdfReliefRowAdmissionAction ReliefRowAdmission => SdfReliefRowAdmissionAction.Primitive;
    }
}
