using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private sealed class SkyCloudsWriter(WorldRenderSkyLayer.Clouds layer, in SkyWriterContext context) : SkyWriter<SdfSkyCloudsData>(context) {
        private readonly WorldValueTuple m_coordinates = WorldSkyNumeric.Clouds(layer);

        public override bool Write(WorldValueResolver values) {
            var coverage = Domain.Scalar("coverage", layer.Coverage, values, 0f, WorldValueDomain.Unit);
            if (coverage <= 0f) { return false; }
            var coordinates = Domain.Tuple(m_coordinates, values);
            Data = new() {
                Coverage = coverage, Color = Rgb(values, layer.Color, Vector3.One), Seed = layer.Seed ?? 0u,
                InverseScale = WorldSkyNumeric.Reciprocal(coordinates[0]), DomeRadius = coordinates[1], Warp = coordinates[2],
                NormalTap = coordinates[3], InverseNormalTap = WorldSkyNumeric.Reciprocal(coordinates[3]), Height = coordinates[4],
                InverseSoftness = WorldSkyNumeric.Reciprocal(coordinates[5]), InverseHorizonFade = WorldSkyNumeric.Reciprocal(coordinates[6]),
                Curl = Domain.Angle("curl", layer.Curl, values, 0f, WorldValueDomain.Finite),
                SelfShadow = Domain.Scalar("selfShadow", layer.SelfShadow, values, .6f, WorldValueDomain.Unit),
                SilverLining = Domain.Scalar("silverLining", layer.SilverLining, values, .5f, WorldValueDomain.Nonnegative),
                Extinction = Domain.Scalar("extinction", layer.Extinction, values, 3.5f, WorldValueDomain.Nonnegative),
                AmbientFloor = Domain.Scalar("ambientFloor", layer.AmbientFloor, values, .45f, WorldValueDomain.Unit),
                SilverExponent = Domain.Scalar("silverExponent", layer.SilverExponent, values, 8f, WorldValueDomain.Nonnegative),
            };
            return true;
        }
        public override bool Moves => Context.Motion is { Moves: true };
        public override void Move(PresentedTick tick) {
            if (Context.Motion is not { } motion) { return; }
            Data.Offset = new(Integral(motion.DriftX, tick, SdfVolume.NoisePeriodCells), Integral(motion.DriftY, tick, SdfVolume.NoisePeriodCells));
            Data.ShearOffset = new(Integral(motion.ShearX, tick, SdfVolume.NoisePeriodCells), Integral(motion.ShearY, tick, SdfVolume.NoisePeriodCells));
            Data.SpinAngle = Integral(motion.Spin, tick, Math.Tau);
        }
    }
}
