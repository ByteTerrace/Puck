using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private sealed class SkyStarsWriter(WorldRenderSkyLayer.Stars layer, in SkyWriterContext context) : SkyWriter<SdfSkyStarsData>(context) {
        private readonly WorldValueTuple m_radius = WorldSkyNumeric.Stars(layer);

        public override bool Write(WorldValueResolver values) {
            var brightness = Domain.Scalar("brightness", layer.Brightness, values, 0f, WorldValueDomain.Nonnegative);
            var sparsity = Domain.Scalar("sparsity", layer.Sparsity, values, .08f, WorldValueDomain.Unit);
            if (brightness <= 0f || sparsity <= 0f) { return false; }
            var radius = Domain.Tuple(m_radius, values);
            Data = new() {
                Density = radius[1], Brightness = brightness, Sparsity = sparsity, Seed = layer.Seed ?? 0u,
                Inset = Domain.Scalar("inset", layer.Inset, values, .3f, new(0d, .5d)),
                AngularRadius = WorldSkyNumeric.AngularRadius(radius[0], radius[1]),
                LuminosityFloor = Domain.Scalar("luminosityFloor", layer.LuminosityFloor, values, .125f, new(0d, 1d, MinimumOpen: true)),
                RadiusFloor = Domain.Scalar("radiusFloor", layer.RadiusFloor, values, .6f, WorldValueDomain.Unit),
                TwinkleShare = Domain.Scalar("twinkle.share", layer.Twinkle?.Share, values, 0f, WorldValueDomain.Unit),
                TwinkleDepth = Domain.Scalar("twinkle.depth", layer.Twinkle?.Depth, values, 0f, WorldValueDomain.Unit),
                Spectrum0 = Spectrum(values, 0, new(1f, .71f, .42f)),
                Spectrum1 = Spectrum(values, 1, new(1f, .82f, .64f)),
                Spectrum2 = Spectrum(values, 2, new(1f, .89f, .81f)),
                Spectrum3 = Spectrum(values, 3, new(1f, .98f, .99f)),
                Spectrum4 = Spectrum(values, 4, new(.89f, .91f, 1f)),
                Spectrum5 = Spectrum(values, 5, new(.79f, .85f, 1f)),
                Spectrum6 = Spectrum(values, 6, new(.71f, .80f, 1f)),
            };
            return true;
        }
        private Vector3 Spectrum(WorldValueResolver values, int index, Vector3 fallback) => Rgb(values, layer.Spectrum?[index], fallback);
        public override bool Moves => Context.Motion?.Twinkle is not null && Data.TwinkleShare > 0f && Data.TwinkleDepth > 0f;
        public override void Move(PresentedTick tick) {
            var phase = Integral(Context.Motion?.Twinkle, tick, 1d);
            Data.TwinklePhase = phase < 0f ? phase + 1f : phase;
        }
    }
}
