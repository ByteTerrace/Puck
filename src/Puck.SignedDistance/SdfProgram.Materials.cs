using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // MaterialVectorsPerEntry float4 rows. KEEP IN SYNC with sdfMaterialLoad in shade/sdf-material.hlsli.
    // 0..3 base shading; 4..7 inset frame/ramp controls; 8..11 radial stops;
    // 12..13 weathering controls; 14..17 two reveal surfaces; 18..19 deposit;
    // 20 the paints: (first paint's vector, paint count, 0, 0) as uint bits.
    // Bleed occupies 2.w/7.zw, and indirect receive is 3.w.
    private void PackMaterials(int materialOffsetVectors, int paintOffsetVectors, IReadOnlyList<SdfMaterial> materialTable, int materialCount) {
        var paintCursor = paintOffsetVectors;

        for (var index = 0; (index < materialCount); index++) {
            var m = materialTable[index];
            var bleed = (m.Bleed ?? Vector3.One);
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
                    bleed.X
                )
            );
            Row(
                row: 3,
                value: new(
                    value: m.Fill,
                    w: m.Receive
                )
            );
            Row(row: 7, value: new Vector4(w: bleed.Z, x: 0, y: 0, z: bleed.Y));
            var paints = (m.Paints ?? []);

            Row(
                row: 20,
                value: new(
                    x: BitConverter.UInt32BitsToSingle(value: ((paints.Count > 0) ? (uint)paintCursor : 0u)),
                    y: BitConverter.UInt32BitsToSingle(value: (uint)paints.Count),
                    z: 0f,
                    w: 0f
                )
            );
            foreach (var paint in paints) {
                PackPaint(paint: paint, paintVector: paintCursor);
                paintCursor += PaintVectorsPerEntry;
            }
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
                        bleed.Y,
                        bleed.Z
                    )
                );
                for (var stop = 0; (stop < paint.Stops.Count); stop++) {
                    Row(
                    row: (8 + stop),
                    value: new(
                        value: paint.Stops[stop].Color,
                        w: paint.Stops[stop].Radius
                    )
                );
                }
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
    // PaintVectorsPerEntry float4 rows. KEEP IN SYNC with sdfApplyPaints in shade/shade-layers.hlsli.
    // 0 frame origin, shape type (uint bits); 1 frame rotation; 2 residual scale, its smallest component;
    // 3..4 the shape's data vectors, a side-table shape's offset naming row 8; 5 color, stroke;
    // 6 (dynamic slot as uint bits, bound center); 7 (bound half-extents, infinite when unbounded, 0); 8.. the side table.
    private void PackPaint(SdfPaint paint, int paintVector) {
        var shape = paint.Shape;
        var tableVector = (paintVector + 8);
        var data0 = shape.Type switch {
            SdfShapeType.ConvexPolygon => shape.Data0 with {
                X = BitConverter.UInt32BitsToSingle(value: ((((uint)tableVector) << 4) | (BitConverter.SingleToUInt32Bits(value: shape.Data0.X) & 0xFu))),
            },
            SdfShapeType.Sweep => shape.Data0 with { X = BitConverter.UInt32BitsToSingle(value: (uint)tableVector) },
            _ => shape.Data0,
        };
        var rotation = Quaternion.Normalize(value: paint.Rotation);
        var minimumScale = MathF.Min(x: shape.Scale.X, y: MathF.Min(x: shape.Scale.Y, y: shape.Scale.Z));

        void Row(int row, Vector4 value) => WriteVector4(
            baseIndex: ((paintVector + row) * WordsPerVector),
            w: value.W,
            words: m_words,
            x: value.X,
            y: value.Y,
            z: value.Z
        );
        Row(row: 0, value: new(value: paint.Origin, w: BitConverter.UInt32BitsToSingle(value: (uint)shape.Type)));
        Row(row: 1, value: new(x: rotation.X, y: rotation.Y, z: rotation.Z, w: rotation.W));
        Row(row: 2, value: new(value: shape.Scale, w: minimumScale));
        Row(row: 3, value: data0);
        Row(row: 4, value: shape.Data1);
        Row(row: 5, value: new(value: paint.Color, w: paint.Stroke));
        Row(row: 6, value: new(
            x: BitConverter.UInt32BitsToSingle(value: ((paint.Slot == SdfPaint.WinningFrame) ? PaintWinningFrame : (uint)paint.Slot)),
            y: shape.Bound.Center.X,
            z: shape.Bound.Center.Y,
            w: shape.Bound.Center.Z
        ));
        Row(row: 7, value: new(value: (shape.Bound.IsUnbounded ? new Vector3(value: float.PositiveInfinity) : shape.Bound.HalfExtents), w: 0f));
        for (var index = 0; (index < shape.Table.Count); index++) {
            m_words[((tableVector * WordsPerVector) + index)] = BitConverter.SingleToUInt32Bits(value: shape.Table[index]);
        }
    }
}
