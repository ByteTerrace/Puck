using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Twenty float4 rows. KEEP IN SYNC with sdfMaterialLoad in sdf-vm.hlsli.
    // 0..3 base shading; 4..7 inset frame/ramp controls; 8..11 radial stops;
    // 12..13 weathering controls; 14..17 two reveal surfaces; 18..19 deposit.
    private void PackMaterials(int materialOffsetVectors, IReadOnlyList<SdfMaterial> materialTable, int materialCount) {
        for (var index = 0; (index < materialCount); index++) {
            var m = materialTable[index];
            var materialBase = ((materialOffsetVectors + (MaterialVectorsPerEntry * index)) * WordsPerVector);

            void Row(int row, Vector4 value) => WriteVector4(
                baseIndex: (materialBase + (row * WordsPerVector)),
                w: value.W,
                words: m_words,
                x: value.X,
                y: value.Y,
                z: value.Z
            );
            Row(
                row: 0,
                value: new(
                    value: m.Albedo,
                    w: m.Emissive
                )
            );
            Row(
                row: 1,
                value: new(
                    m.Specular,
                    m.Roughness,
                    m.Sheen,
                    m.Metal
                )
            );
            Row(
                row: 2,
                value: new(
                    m.Coat,
                    m.Wrap,
                    m.Soften,
                    0
                )
            );
            Row(
                row: 3,
                value: new(
                    value: m.Bounce,
                    w: 0
                )
            );
            if (m.Inset is { } inset) {
                var paint = inset.Paint;

                Row(
                    row: 4,
                    value: new(
                        value: inset.Origin,
                        w: inset.Depth
                    )
                );
                var rotation = Quaternion.Normalize(value: inset.Rotation);

                Row(
                    row: 5,
                    value: new(
                        w: rotation.W,
                        x: rotation.X,
                        y: rotation.Y,
                        z: rotation.Z
                    )
                );
                Row(
                    row: 6,
                    value: new(
                        inset.Ior,
                        paint.Stops.Count,
                        paint.Softness,
                        paint.ModulationAmplitude
                    )
                );
                Row(
                    row: 7,
                    value: new(
                        paint.ModulationFrequency,
                        BitConverter.UInt32BitsToSingle(value: paint.Seed),
                        0,
                        0
                    )
                );
                for (var stop = 0; (stop < paint.Stops.Count); stop++) { Row(
                    row: (8 + stop),
                    value: new(
                        value: paint.Stops[stop].Color,
                        w: paint.Stops[stop].Radius
                    )
                ); }
            }
            if (m.Weathering is { } w) {
                Row(
                    row: 12,
                    value: new(
                        w.Edge,
                        w.Lines,
                        w.Settle,
                        w.Reach
                    )
                );
                Row(
                    row: 13,
                    value: new(
                        BitConverter.UInt32BitsToSingle(value: w.Seed),
                        w.Scale,
                        w.Floor,
                        w.Lane
                    )
                );
                if (w.Under is { } stages) {
                    for (var stage = 0; (stage < stages.Count); stage++) {
                        Row(
                            row: (14 + (2 * stage)),
                            value: new(
                                value: stages[stage].Surface.Color,
                                w: stages[stage].Threshold
                            )
                        );
                        Row(
                            row: (15 + (2 * stage)),
                            value: new(
                                stages[stage].Surface.Roughness,
                                stages[stage].Surface.Metal,
                                stages.Count,
                                0
                            )
                        );
                    }
                }
                if (w.Deposit is { } deposit) {
                    Row(
                        row: 18,
                        value: new(
                            value: deposit.Color,
                            w: 1
                        )
                    );
                    Row(
                        row: 19,
                        value: new(
                            deposit.Roughness,
                            deposit.Metal,
                            0,
                            0
                        )
                    );
                }
            }
        }
    }
}
