namespace Puck.Demo.Viewports;

/// <summary>A stable identifier for a <see cref="Viewport"/>. Trivial today; it becomes the key the
/// split-screen compositor lays out and the jumbotron samples once there is more than one viewport.</summary>
internal readonly record struct ViewportId(int Value);
