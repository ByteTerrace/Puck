namespace Puck.SignedDistance;

/// <summary>Relief mask rows and their admission and shader facets.</summary>
[SdfFieldCatalogue("SdfReliefRowKind", unsigned: false)]
public static partial class SdfFieldReliefRows {
    private abstract class Entry {
        public abstract SdfShaderAction Shader { get; }
        public abstract SdfReliefRowAdmissionAction ReliefRowAdmission { get; }
    }

    /// <summary>A mask's band: Data0 holds its start, width, inside weight and outside weight.</summary>
    [SdfFieldEntry(0)]
    private sealed class Band : Entry {
        public override SdfShaderAction Shader => new(
            Code: """
                if (open) {
                    weight *= sdfReliefBandWeight(band, distance);
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
