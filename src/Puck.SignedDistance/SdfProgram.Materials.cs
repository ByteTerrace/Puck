using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Twenty float4 rows. KEEP IN SYNC with sdfMaterialLoad in sdf-vm.hlsli.
    // 0..3 base shading; 4..7 inset frame/ramp controls; 8..11 radial stops;
    // 12..13 weathering controls; 14..17 two reveal surfaces; 18..19 deposit.
    private void PackMaterials(int materialOffsetVectors, IReadOnlyList<SdfMaterial> materialTable, int materialCount) {
        for (var index = 0; index < materialCount; index++) {
            var m = materialTable[index];
            var materialBase = (materialOffsetVectors + MaterialVectorsPerEntry * index) * WordsPerVector;
            void Row(int row, Vector4 value) => WriteVector4(m_words, materialBase + row * WordsPerVector, value.X, value.Y, value.Z, value.W);
            Row(0, new(m.Albedo, m.Emissive));
            Row(1, new(m.Specular, m.Roughness, m.Sheen, m.Metal));
            Row(2, new(m.Coat, m.Wrap, m.Soften, 0));
            Row(3, new(m.Bounce, 0));
            if (m.Inset is { } inset) {
                var paint = inset.Paint;
                Row(4, new(inset.Origin, inset.Depth));
                var rotation = Quaternion.Normalize(inset.Rotation);
                Row(5, new(rotation.X, rotation.Y, rotation.Z, rotation.W));
                Row(6, new(inset.Ior, paint.Stops.Count, paint.Softness, paint.ModulationAmplitude));
                Row(7, new(paint.ModulationFrequency, BitConverter.UInt32BitsToSingle(paint.Seed), 0, 0));
                for (var stop = 0; stop < paint.Stops.Count; stop++) { Row(8 + stop, new(paint.Stops[stop].Color, paint.Stops[stop].Radius)); }
            }
            if (m.Weathering is { } w) {
                Row(12, new(w.Edge, w.Lines, w.Settle, w.Reach));
                Row(13, new(BitConverter.UInt32BitsToSingle(w.Seed), w.Scale, w.Floor, w.Lane));
                if (w.Under is { } stages) { for (var stage = 0; stage < stages.Count; stage++) {
                    Row(14 + 2 * stage, new(stages[stage].Surface.Color, stages[stage].Threshold));
                    Row(15 + 2 * stage, new(stages[stage].Surface.Roughness, stages[stage].Surface.Metal, stages.Count, 0));
                } }
                if (w.Deposit is { } deposit) {
                    Row(18, new(deposit.Color, 1));
                    Row(19, new(deposit.Roughness, deposit.Metal, 0, 0));
                }
            }
        }
    }
}
