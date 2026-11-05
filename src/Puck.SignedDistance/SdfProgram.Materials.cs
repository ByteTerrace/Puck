using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // MaterialVectorsPerEntry float4 rows. KEEP IN SYNC with sdfMaterialLoad in shade/sdf-material.hlsli.
    // 0..3 base shading; 4..7 inset frame/ramp controls; 8..11 radial stops;
    // 12..13 weathering controls; 14..17 two reveal surfaces; 18..19 deposit; 26 the pattern lattice's turn;
    // 20 the paints and the emissive lane: (first paint's vector, paint count, emissive lane + 1 or 0 for a static
    // emissive, 0) as uint bits; 21 the glass: (absorption per world unit, index of refraction), zero for an opaque
    // material, so a positive index says glass; 22..25 the material's own cel style (SdfToon.WriteRows; KEEP IN SYNC with
    // sdfToonStyle in shade/sdf-toon.hlsli), all zero to inherit the world's default, a mode of -1 for a style that
    // keeps the material continuous.
    // Bleed occupies 2.w/7.zw, and indirect receive is 3.w. Weathering pattern XYZ uses
    // 15.w/17.w/19.z; noise coverage is 19.w; reveal softness is 17.z. The second octave's frequency ratio is 15.z and
    // its gain 18.w (zero reads one octave); the turn is row 26 as a unit quaternion (x, y, z, w). A reveal stage is
    // present when its threshold (14.w, 16.w) is positive, and a deposit when the settle share (12.z) is.
    // 27..30 the flow layer (SdfFlow; KEEP IN SYNC with sdfApplyFlow in shade/shade-layers.hlsli): 27 (velocity,
    // pattern scale), 28 (stretch, cycles per clock period, tilt, coverage), 29 (revealed colour, softness), 30 (revealed
    // roughness, revealed metal, seed bits, control bits: the lane plus one, or zero, in the low byte and the world-frame
    // flag at bit 8); all zero for a material with no flow, which a zero scale says.
    private void PackMaterials(int materialOffsetVectors, int paintOffsetVectors, IReadOnlyList<SdfMaterial> materialTable, int materialCount) {
        var paintCursor = paintOffsetVectors;
        Span<Vector4> toonRows = stackalloc Vector4[4];

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
                    z: BitConverter.UInt32BitsToSingle(value: ((m.EmissiveLane is { } lane) ? (((uint)lane) + 1u) : 0u)),
                    w: 0f
                )
            );
            foreach (var paint in paints) {
                PackPaint(paint: paint, paintVector: paintCursor);
                paintCursor += PaintVectorsPerEntry;
            }
            if (m.Toon is { } toon) {
                toon.WriteRows(inkW: 0f, mode: (toon.Enabled ? toon.Mode : -1f), rows: toonRows);
                for (var row = 0; (row < toonRows.Length); row++) {
                    Row(row: (22 + row), value: toonRows[row]);
                }
            }
            if (m.Flow is { } flow) {
                var revealed = (flow.Surface ?? default);

                Row(row: 27, value: new(value: flow.Velocity, w: flow.Scale));
                Row(row: 28, value: new(flow.Stretch, flow.CyclesPerClock, flow.Tilt, flow.Coverage));
                Row(row: 29, value: new(value: revealed.Color, w: flow.Softness));
                Row(
                    row: 30,
                    value: new(
                        revealed.Roughness,
                        revealed.Metal,
                        BitConverter.UInt32BitsToSingle(value: flow.Seed),
                        BitConverter.UInt32BitsToSingle(value: FlowControl(flow: flow))
                    )
                );
            }
            if (m.Glass is { } glass) {
                Row(
                    row: 21,
                    value: new(
                        value: glass.Absorption(tint: m.Albedo),
                        w: glass.Ior
                    )
                );
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
                                0,
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
                            w: 0
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
                // Pattern XYZ and noise coverage use existing reserved components; material stride stays fixed.
                var pattern = w.PatternScale ?? Vector3.One;
                m_words[materialBase + 15 * WordsPerVector + 3] = BitConverter.SingleToUInt32Bits(pattern.X);
                m_words[materialBase + 17 * WordsPerVector + 3] = BitConverter.SingleToUInt32Bits(pattern.Y);
                m_words[materialBase + 19 * WordsPerVector + 2] = BitConverter.SingleToUInt32Bits(pattern.Z);
                m_words[materialBase + 19 * WordsPerVector + 3] = BitConverter.SingleToUInt32Bits(w.Noise);
                m_words[materialBase + 17 * WordsPerVector + 2] = BitConverter.SingleToUInt32Bits(w.Softness);
                // A second octave exists when the gain word is positive; a single octave leaves both words zero.
                if (w.Octaves >= 2) {
                    m_words[materialBase + 15 * WordsPerVector + 2] = BitConverter.SingleToUInt32Bits(w.OctaveRatio);
                    m_words[materialBase + 18 * WordsPerVector + 3] = BitConverter.SingleToUInt32Bits(w.OctaveGain);
                }
                var turn = Quaternion.Normalize(value: (w.Turn ?? Quaternion.Identity));

                Row(row: 26, value: new(x: turn.X, y: turn.Y, z: turn.Z, w: turn.W));
            }
        }
    }
    // A flow's control word (row 30.w): its lane plus one, or zero for a constant flow, and FlowWorldFrame for a
    // velocity authored in world space.
    private static uint FlowControl(SdfFlow flow) =>
        (((flow.Lane is { } lane) ? (((uint)lane) + 1u) : 0u) | ((flow.Frame == SdfFlowFrame.World) ? FlowWorldFrame : 0u));
    // PaintVectorsPerEntry float4 rows. KEEP IN SYNC with sdfApplyPaints in shade/shade-layers.hlsli.
    // 0 frame origin, shape type (uint bits); 1 frame rotation; 2 residual scale, its smallest component;
    // 3..4 the shape's data vectors, a side-table shape's offset naming row 8; 5 color, stroke;
    // 6 (dynamic slot as uint bits, bound center); 7 (bound half-extents, infinite when unbounded, 0); 8.. the side table.
    private void PackPaint(SdfPaint paint, int paintVector) {
        var shape = paint.Shape;
        var tableVector = (paintVector + 8);
        var data0 = shape.Type switch {
            SdfShapeType.ConvexPolygon => shape.Data0 with {
                X = BitConverter.UInt32BitsToSingle(value: SdfPrismProfile.PackConvexReference(count: ((int)(BitConverter.SingleToUInt32Bits(value: shape.Data0.X) & ((1u << SdfPrismProfile.ConvexCountBits) - 1u)) + 1), tableVector: tableVector)),
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
