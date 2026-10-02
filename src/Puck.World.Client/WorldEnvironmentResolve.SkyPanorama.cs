using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private sealed class SkyPanoramaWriter(WorldRenderSkyLayer.Panorama layer, in SkyWriterContext context) : SkyWriter<SdfSkyPanoramaData>(context) {
        public override bool Write(WorldValueResolver values) {
            var intensity = Domain.Scalar("intensity", layer.Intensity, values, 1f, WorldValueDomain.Nonnegative);
            if (intensity <= 0f) { return false; }
            Data = new() {
                Tint = Rgb(values, layer.Tint, Vector3.One), Intensity = intensity,
                SourceIndex = (uint)Context.Index, Filter = (uint)(layer.Filter ?? GpuSamplerFilter.Linear),
            };
            return true;
        }
    }
}
