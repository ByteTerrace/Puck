using System.Text.Json.Serialization;

namespace Puck.World;

public abstract partial record WorldRenderSkyLayer {
    public sealed partial record Stars {
        /// <summary>The fraction of lattice cells containing a star, in [0, 1]. Absent is 0.08.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? Sparsity { get; init; }
        /// <summary>The star center's minimum distance from a cell wall, in [0, 0.5] cells. Absent is 0.3.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? Inset { get; init; }
        /// <summary>The peak angular radius as a fraction of the cell's angular pitch. Absent is 0.12.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? RadiusFraction { get; init; }
        /// <summary>The faintest luminosity as a fraction of the peak, in (0, 1]. Absent is 0.125.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? LuminosityFloor { get; init; }
        /// <summary>The faintest radius as a fraction of the peak radius, in [0, 1]. Absent is 0.6.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? RadiusFloor { get; init; }
        /// <summary>Seven linear RGB spectrum colors, from warm to cool. Absent retains the established
        /// seven-color star palette; the spectrum's colors may bind or key, but its count is structural.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<BindableColor>? Spectrum { get; init; }
    }
}
