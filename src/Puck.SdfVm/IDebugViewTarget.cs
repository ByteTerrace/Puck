namespace Puck.SdfVm;

/// <summary>
/// A producer whose SDF debug view mode can be set — the seam the demo's <c>debug.view</c> command and the
/// parity harness drive without depending on the concrete producer.
/// </summary>
public interface IDebugViewTarget {
    /// <summary>Gets or sets the active debug view mode (see <see cref="DebugViewModes"/>); 0 is the final image.</summary>
    int DebugMode { get; set; }
}
