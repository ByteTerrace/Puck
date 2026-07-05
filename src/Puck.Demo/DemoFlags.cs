namespace Puck.Demo;

/// <summary>
/// The resolved legacy launch flags, bundled so <see cref="DemoRunDocuments.Synthesize"/> can turn them into a
/// <see cref="Puck.Scene.PuckRunDocument"/>. (The <c>--capture</c> path is threaded separately into the registrar, so it
/// is not carried here.)
/// </summary>
internal sealed record DemoFlags {
    /// <summary>The <c>--backend</c> the live render hosts on (<c>vulkan</c>/<c>directx</c>).</summary>
    public required string Backend { get; init; }
    /// <summary>The <c>--exit-after-seconds</c> auto-exit duration.</summary>
    public required int ExitAfterSeconds { get; init; }
    /// <summary>The <c>--present-mode</c> swapchain present mode.</summary>
    public required string PresentMode { get; init; }
    /// <summary>The <c>--surface-format</c> back-buffer format.</summary>
    public required string SurfaceFormat { get; init; }
    /// <summary>The <c>--validate-overworld</c> pure-CPU determinism + replay self-check for the action demo.</summary>
    public required bool ValidateOverworld { get; init; }
    /// <summary>The <c>--overworld</c> live controller-driven action demo (Vulkan host).</summary>
    public required bool Overworld { get; init; }
    /// <summary>The <c>--rom</c> cartridge path: boot straight into the game — the IMMERSED overworld, one machine per
    /// connecting player (null = not requested).</summary>
    public string? RomPath { get; init; }
    /// <summary>The <c>--rom-exit</c> fourth-wall condition spec (<c>"&lt;0xADDR&gt;&lt;op&gt;&lt;value&gt;"</c>,
    /// e.g. <c>"0xDA22&gt;=1"</c>); null = no instrumentation.</summary>
    public string? RomExit { get; init; }
}
