using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private sealed class SkyNoiseWriter(WorldRenderSkyLayer.Noise layer, in SkyWriterContext context) : SkyWriter<SdfSkyNoiseData>(context) {
        private readonly WorldValueTuple m_coordinates = WorldSkyNumeric.Noise(layer);

        public override bool Write(WorldValueResolver values) {
            var intensity = Domain.Scalar("intensity", layer.Intensity, values, 1f, WorldValueDomain.Nonnegative);
            if (intensity <= 0f) { return false; }
            var coordinates = Domain.Tuple(m_coordinates, values);
            var offset = Domain.Vector("offset", layer.Offset, values, Vector3.Zero, WorldValueDomain.Finite);
            Data = new() {
                ColorLow = Rgb(values, layer.ColorLow, Vector3.Zero), ColorHigh = Rgb(values, layer.ColorHigh, Vector3.One),
                Intensity = intensity, InverseScale = WorldSkyNumeric.Reciprocal(coordinates[0]),
                Offset = new(Periodic(offset.X, SdfVolume.NoisePeriodCells), Periodic(offset.Y, SdfVolume.NoisePeriodCells), Periodic(offset.Z, SdfVolume.NoisePeriodCells)),
                Seed = layer.Seed ?? 0u, Octaves = (uint)(layer.Octaves ?? 4),
                Contrast = Domain.Scalar("contrast", layer.Contrast, values, .5f, WorldValueDomain.Finite),
                Bias = Domain.Scalar("bias", layer.Bias, values, .5f, WorldValueDomain.Finite),
            };
            return true;
        }
    }

    private sealed class SkyPatternWriter(WorldRenderSkyLayer.Pattern layer, in SkyWriterContext context) : SkyWriter<SdfSkyPatternData>(context) {
        public override bool Write(WorldValueResolver values) {
            var intensity = Domain.Scalar("intensity", layer.Intensity, values, 1f, WorldValueDomain.Nonnegative);
            if (intensity <= 0f) { return false; }
            var offset = layer.Offset is { } authored ? Domain.Pair("offset", authored, values, Vector2.Zero, WorldValueDomain.Finite) : Vector2.Zero;
            Data = new() {
                ColorA = Rgb(values, layer.Colors?[0], Vector3.Zero), ColorB = Rgb(values, layer.Colors?[1], Vector3.One),
                Intensity = intensity, Cells = (uint)(layer.Checker?.Cells ?? 24),
                Offset = new(Periodic(offset.X, 2f), Periodic(offset.Y, 2f)),
            };
            return true;
        }
    }

    private sealed class SkyAuroraWriter(WorldRenderSkyLayer.Aurora layer, in SkyWriterContext context) : SkyWriter<SdfSkyAuroraData>(context) {
        private readonly WorldValueTuple m_coordinates = WorldSkyNumeric.Aurora(layer);

        public override bool Write(WorldValueResolver values) {
            var intensity = Domain.Scalar("intensity", layer.Intensity, values, 1f, WorldValueDomain.Nonnegative);
            if (intensity <= 0f) { return false; }
            var coordinates = Domain.Tuple(m_coordinates, values);
            var offset = Domain.Vector("offset", layer.Offset, values, Vector3.Zero, WorldValueDomain.Finite);
            Data = new() {
                Color = Rgb(values, layer.Color, Vector3.One), Intensity = intensity,
                Offset = new(Periodic(offset.X, SdfVolume.NoisePeriodCells), Periodic(offset.Y, SdfVolume.NoisePeriodCells), Periodic(offset.Z, SdfVolume.NoisePeriodCells)),
                InverseScale = WorldSkyNumeric.Reciprocal(coordinates[0]), HeightScale = coordinates[1],
                InverseWidth = WorldSkyNumeric.Reciprocal(coordinates[2]),
                Sharpness = Domain.Scalar("sharpness", layer.Sharpness, values, 2f, WorldValueDomain.Positive),
                Bias = Domain.Scalar("bias", layer.Bias, values, 0f, new(-1d, 1d)),
                Seed = layer.Seed ?? 0u, Octaves = (uint)(layer.Octaves ?? 4),
            };
            return true;
        }
    }
}
