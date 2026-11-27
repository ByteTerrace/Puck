using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World;

public sealed partial class WorldRenderLeverCommandModule {
    // The world.occluders read-back: the count, then one line an occluder of the frame's table, as the kernels read it.
    private static string DescribeOccluders(SdfFrame frame) {
        var occluders = frame.Occluders;
        var kept = (((frame.Views.Count > 0) && (occluders.Count > 0))
            ? SdfLightSelection.SelectOccluders(camera: frame.Views[0].Camera, far: frame.FarDistance, occluders: occluders)
            : UInt128.Zero);
        var text = new StringBuilder();

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"count={occluders.Count}/{SdfOccluders.MaxOccluders} view0-keeps={UInt128.PopCount(value: kept)}");
        for (var index = 0; (index < occluders.Count); index++) {
            var occluder = occluders[index];

            _ = text.Append(value: Environment.NewLine);
            _ = text.Append(provider: CultureInfo.InvariantCulture,
                handler: $"#{index} source={(occluders.SourceOf(index: index) ?? "unnamed")} {occluder.Shape.ToString().ToLowerInvariant()} center={Format(value: occluder.Center)}");
            _ = occluder.Shape switch {
                SdfOccluderShape.Capsule => text.Append(provider: CultureInfo.InvariantCulture, handler: $" radius={occluder.Radius:0.###} half-segment={Format(value: occluder.AxisX)}"),
                SdfOccluderShape.Box => text.Append(provider: CultureInfo.InvariantCulture, handler: $" half-axes={Format(value: occluder.AxisX)},{Format(value: occluder.AxisY)},{Format(value: occluder.AxisZ)}"),
                _ => text.Append(provider: CultureInfo.InvariantCulture, handler: $" radius={occluder.Radius:0.###}"),
            };
            _ = text.Append(provider: CultureInfo.InvariantCulture,
                handler: $" bound={occluder.BoundingRadius:0.###} reach={occluder.Reach:0.###} strength={occluder.Strength:0.###} shadow={occluder.Shadow:0.###} softness={occluder.Softness:0.###} own-slots={((occluder.OwnSlots == 0u) ? "none" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{occluder.OwnSlot}+{occluder.OwnSlots}"))} view0={((((kept >> index) & UInt128.One) != UInt128.Zero) ? "kept" : "culled")}");
        }

        return text.ToString();

        static string Format(Vector3 value) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"({value.X:0.###},{value.Y:0.###},{value.Z:0.###})");
    }
}
