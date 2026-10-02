using System.Numerics;
using Puck.Shaders;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private sealed class SkyGradientWriter : SkyWriter<SdfSkyGradientData> {
        private readonly WorldRenderSkyLayer.Gradient m_layer;
        private readonly WorldValueDomainGroup[] m_stops;

        public SkyGradientWriter(WorldRenderSkyLayer.Gradient layer, in SkyWriterContext context) : base(context) {
            m_layer = layer;
            m_stops = new WorldValueDomainGroup[layer.Stops?.Count ?? 0];
            for (var index = 0; index < m_stops.Length; index++) {
                var stop = layer.Stops![index];
                var identity = stop.Name ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                m_stops[index] = new(context.Owner.Domains, context.Definition, context.Path + ".stops[" + identity + "]");
            }
        }

        public override bool Write(WorldValueResolver values) {
            var stops = m_layer.Stops;
            Data = new() { FirstStop = (uint)Context.FirstStop, StopCount = (uint)m_stops.Length };
            for (var index = 0; index < m_stops.Length; index++) {
                var stop = stops![index];
                Context.Stops.Rows[Context.FirstStop + index] = new() {
                    Color = Rgb(values, stop.Color, Vector3.One),
                    Elevation = m_stops[index].Scalar("elevation", stop.Elevation, values, 0f, new(-1d, 1d)),
                };
            }
            return m_stops.Length != 0;
        }
    }
}
