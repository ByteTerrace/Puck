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
    /// <summary>The <c>--fuzz-seed</c> (negative disables fuzzing; pairs with <see cref="ValidateWorld"/>).</summary>
    public required int FuzzSeed { get; init; }
    /// <summary>The <c>--present-mode</c> swapchain present mode.</summary>
    public required string PresentMode { get; init; }
    /// <summary>The <c>--produce</c> backend the world/showcase renders on (for a Vulkan host).</summary>
    public required string Produce { get; init; }
    /// <summary>The <c>--surface-format</c> back-buffer format.</summary>
    public required string SurfaceFormat { get; init; }
    /// <summary>The <c>--validate</c> cross-backend graphics parity gate.</summary>
    public required bool Validate { get; init; }
    /// <summary>The <c>--validate-mini-action</c> pure-CPU determinism + replay self-check for the action demo.</summary>
    public required bool ValidateMiniAction { get; init; }
    /// <summary>The <c>--validate-determinism</c> pure-CPU fixed-point + engine command-snapshot record/replay self-check.</summary>
    public required bool ValidateDeterminism { get; init; }
    /// <summary>The <c>--mini-action</c> live controller-driven action demo (Vulkan host).</summary>
    public required bool MiniAction { get; init; }
    /// <summary>The <c>--camera</c> live camera content source: a Direct3D 12-produced feed the Vulkan host imports zero-copy and presents.</summary>
    public required bool Camera { get; init; }
    /// <summary>The <c>--validate-camera-live</c> real-webcam hardware bring-up gate (opens the default device and dumps a frame).</summary>
    public required bool ValidateCameraLive { get; init; }
    /// <summary>The <c>--validate-camera-gpu</c> GPU-resident zero-copy camera tier gate (DXVA → shared targets → Vulkan import).</summary>
    public required bool ValidateCameraGpu { get; init; }
    /// <summary>The <c>--validate-world</c> cross-backend compute-world parity gate.</summary>
    public required bool ValidateWorld { get; init; }
    /// <summary>The <c>--validate-world-child</c> compute-world parity gate with a hosted child viewport.</summary>
    public required bool ValidateWorldChild { get; init; }
    /// <summary>The <c>--world</c> single-viewport compute world.</summary>
    public required bool World { get; init; }
    /// <summary>The <c>--world-child</c> split compute world with a hosted child in the bottom-right viewport.</summary>
    public required bool WorldChild { get; init; }
    /// <summary>The <c>--world-rt</c> ray-query world.</summary>
    public required bool WorldRt { get; init; }
    /// <summary>The <c>--world-split</c> 2x2 split-screen compute world.</summary>
    public required bool WorldSplit { get; init; }
}
