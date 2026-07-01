using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>Specifies how a live camera frame is fit into its viewport rect.</summary>
public enum CameraFit {
    /// <summary>Scales the whole camera frame to the viewport rect (stretches to the rect's aspect, no crop).</summary>
    Sample,
    /// <summary>Center-crops the camera frame to the rect's aspect so it fills the rect with no distortion.</summary>
    Fill,
}

/// <summary>
/// A LIVE hardware camera (webcam / capture device) as a viewport content source — a <see cref="ViewportSource"/> kind
/// interchangeable with a virtual SDF camera at the viewport seam. The requested dimensions and frame-rate are hints
/// (the device negotiates a nearby supported mode; <c>0</c> means "device default"); <see cref="Fit"/> chooses how the
/// captured frame maps into the viewport rect.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LiveCameraSource : ViewportSource {
    /// <summary>An optional device identifier/name selecting a specific camera; <see langword="null"/>/empty opens the default device.</summary>
    public string? DeviceId { get; init; }
    /// <summary>How the camera frame is fit into the viewport rect.</summary>
    public CameraFit Fit { get; init; } = CameraFit.Sample;
    /// <summary>The requested capture frame-rate hint, in frames per second (<c>0</c> = device default).</summary>
    public int RequestedFps { get; init; }
    /// <summary>The requested capture height hint, in pixels (<c>0</c> = device default).</summary>
    public int RequestedHeight { get; init; }
    /// <summary>The requested capture width hint, in pixels (<c>0</c> = device default).</summary>
    public int RequestedWidth { get; init; }

    internal override void Validate(string path, ValidationErrors errors) {
        errors.RequireRange(path: $"{path}.requestedWidth", name: "requestedWidth", range: new FloatRange(Maximum: 16384f, Minimum: 0f), value: RequestedWidth);
        errors.RequireRange(path: $"{path}.requestedHeight", name: "requestedHeight", range: new FloatRange(Maximum: 16384f, Minimum: 0f), value: RequestedHeight);
        errors.RequireRange(path: $"{path}.requestedFps", name: "requestedFps", range: new FloatRange(Maximum: 1000f, Minimum: 0f), value: RequestedFps);
    }
}
