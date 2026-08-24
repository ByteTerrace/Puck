namespace Puck.Launcher;

/// <summary>The fixed target extent <see cref="OffscreenTickHostedService"/> produces frames at — there is no window
/// to query a live size from, so a composition root supplies it once at registration.</summary>
/// <param name="Width">The render target width in pixels.</param>
/// <param name="Height">The render target height in pixels.</param>
public sealed record OffscreenRenderOptions(uint Width, uint Height);
