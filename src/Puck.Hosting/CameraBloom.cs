namespace Puck.Hosting;

/// <summary>The cost gate for a camera's scatter pyramid. Off has no resources or dispatches.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<BloomQuality>))]
public enum BloomQuality { Off, Low, High }
/// <summary>Presentation-only optical scatter. Radius is a fraction of the output's shorter side.
/// A positive cutoff selects light in exposed HDR space and therefore depends on exposure.</summary>
/// <param name="Scatter">The fraction of direct light scattered, in [0, 1].</param>
/// <param name="Radius">The normalized radius, in (0, 1].</param>
/// <param name="Cutoff">An optional soft luminance cutoff; zero keeps the filter linear.</param>
public sealed record CameraBloom(float Scatter = 0f, float Radius = 0.08f, float Cutoff = 0f);
