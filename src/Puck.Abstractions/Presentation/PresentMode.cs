namespace Puck.Abstractions.Presentation;

/// <summary>
/// Specifies the backend-neutral swapchain present mode — how presented frames synchronize with the display.
/// Each backend maps it to its native concept (Vulkan <c>VkPresentModeKHR</c>; Direct3D 12 the
/// <c>IDXGISwapChain::Present</c> sync interval and flags) and falls back gracefully when the exact mode is
/// unsupported.
/// </summary>
public enum PresentMode {
    /// <summary>Wait for vertical blank; no tearing. The safe, power-friendly default
    /// (Vulkan <c>FIFO</c>; Direct3D 12 <c>Present(1, 0)</c>).</summary>
    Vsync = 0,
    /// <summary>Low latency without tearing — the newest frame replaces the queued one
    /// (Vulkan <c>MAILBOX</c>; Direct3D 12 flip-discard <c>Present(0, 0)</c>).</summary>
    Mailbox,
    /// <summary>No synchronization; lowest latency, may tear (Vulkan <c>IMMEDIATE</c>; Direct3D 12
    /// <c>Present(0, ALLOW_TEARING)</c> where the display supports it, otherwise <c>Present(0, 0)</c>).</summary>
    Immediate,
    /// <summary>Adaptive presentation: synchronize when on time where the backend supports that distinction, but permit
    /// tearing rather than stuttering when late (Vulkan <c>FIFO_RELAXED</c>; Direct3D 12
    /// <c>Present(0, ALLOW_TEARING)</c> where supported, otherwise a graceful fall back). This policy is independent of
    /// whether the display advertises variable-refresh capability.</summary>
    Adaptive,
}
