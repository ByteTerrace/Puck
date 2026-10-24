namespace Puck.SdfVm;

/// <summary>The evidence behind a view's current exposure meter reading.</summary>
public enum SdfExposureMeterSource {
    /// <summary>No current measurement: the scene is unavailable or the camera is inside opaque geometry.</summary>
    None,
    /// <summary>Visible irradiance lattice probes about the camera.</summary>
    Probes,
    /// <summary>The irradiance tracer sampled the camera itself because no lattice probe was visible.</summary>
    CameraTrace,
}
