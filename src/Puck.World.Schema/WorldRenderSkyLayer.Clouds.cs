using System.Text.Json.Serialization;

namespace Puck.World;

public abstract partial record WorldRenderSkyLayer {
    public sealed partial record Clouds {
        /// <summary>The dome center's depth below the viewer, in layer units with unit height overhead. Absent is 6.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? DomeRadius { get; init; }
        /// <summary>The shaping field's displacement of the density field, in cells. Absent is 0.6.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? Warp { get; init; }
        /// <summary>The heightfield rise per unit thickness, in cells. Absent is 0.7.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? Height { get; init; }
        /// <summary>The positive normal and self-shadow sample spacing, in cells. Absent is 0.18.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? NormalTap { get; init; }
        /// <summary>The darkening from a taller light-facing neighbor, in [0, 1]. Absent is 0.6.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? SelfShadow { get; init; }
        /// <summary>The strength of each light's thin-edge highlight. Absent is 0.5.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? SilverLining { get; init; }
        /// <summary>The extinction per unit thickness. Zero is transparent; absent is 3.5.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? Extinction { get; init; }
        /// <summary>The positive local Y-direction interval over which coverage fades at the horizon, at most one.
        /// Absent is 0.05.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? HorizonFade { get; init; }
        /// <summary>The fraction of color lit by the supplied ambient field, in [0, 1]. Absent is 0.45.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? AmbientFloor { get; init; }
        /// <summary>The forward highlight exponent. Absent is 8.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindableScalar? SilverExponent { get; init; }
    }
}
