namespace Puck.Hosting;

/// <summary>The cost gate for a camera's scatter pyramid. Off has no resources or dispatches.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<BloomQuality>))]
public enum BloomQuality { Off, Low, High }
/// <summary>Presentation-only optical scatter. Radius is a fraction of the output's shorter side.
/// A positive cutoff selects light in exposed HDR space and therefore depends on exposure.</summary>
/// <param name="Scatter">The fraction of direct light scattered, in [0, 1].</param>
/// <param name="Radius">The normalized radius, in [0.001, 1].</param>
/// <param name="Cutoff">An optional soft luminance cutoff; zero keeps the filter linear.</param>
[method: System.Text.Json.Serialization.JsonConstructor]
public readonly record struct CameraBloom(float Scatter = 0f, float Radius = 0.08f, float Cutoff = 0f) {
    /// <summary>The unauthored lens: scatter disabled, with the default radius ready for a session override.</summary>
    public CameraBloom() : this(0f, 0.08f, 0f) { }
}
