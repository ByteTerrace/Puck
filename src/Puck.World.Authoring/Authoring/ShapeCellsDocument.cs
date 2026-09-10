using System.Text.Json.Serialization;
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
}
