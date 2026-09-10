using System.Text.Json.Serialization;
using System.Numerics;
using Puck.Abstractions.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>Shape-local cellular relief. Frequency and amplitude use creation units.
/// Contact geometry retains the base shape; the fixed-point VM can evaluate the emitted field independently.</summary>
public sealed record ShapeCellsDocument(float Frequency, float Amplitude, uint Seed,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfCellMode>))] SdfCellMode Mode, float Randomness) {
    /// <summary>The corresponding engine field parameters.</summary>
    [JsonIgnore]
    public SdfCellDisplacement Parameters => new(Frequency, Amplitude, Seed, Mode, Randomness);
    /// <summary>Converts the field-unit outward relief to a conservative primitive-coordinate radius.
    /// Nonuniform primitive scaling and the profile's distance correction can flatten the field.</summary>
    public float PrimitiveReachPadding(Vector3 scale, ShapeFlareDocument? flare) {
        if (Amplitude == 0f) { return 0f; }
        var magnitude = Vector3.Abs(scale);
        var minimum = MathF.Min(magnitude.X, MathF.Min(magnitude.Y, magnitude.Z));
        var maximum = MathF.Max(magnitude.X, MathF.Max(magnitude.Y, magnitude.Z));
        var ratio = MathF.Max(1f, maximum / MathF.Max(minimum, float.Epsilon));
        return Parameters.OutwardReach * ratio * ShapeFlareDocument.ReachFactor(flare);
    }
}
