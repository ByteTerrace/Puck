using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's screen binder a frame source drives per frame. Declared here
/// so a Client-side type can hold the binder without naming the root's concrete type.</summary>
public interface IWorldScreenPresenter {
    /// <summary>Gets the audio machine bound to a screen index, or <see langword="null"/> when the slot's current
    /// source is not a machine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    IAudioMachine? AudioMachine(int index);
    /// <summary>Drops every device-owned upload and offscreen view while preserving CPU sessions, machine
    /// simulation, declarations, and view registrations.</summary>
    void NotifyDeviceLost();
    /// <summary>Publishes this tick's declared-screen content to the device.</summary>
    /// <param name="tick">The world's completed-step ordinal driving deterministic pattern animation.</param>
    /// <param name="elapsedTicks">The exact completed simulation time in engine ticks, used by feed deadlines.</param>
    /// <param name="deviceContext">The live GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    void Publish(ulong tick, ulong elapsedTicks, IGpuDeviceContext deviceContext, IGpuComputeServices gpu);
    /// <summary>Reconciles the offscreen camera-view pool against a mutated camera list.</summary>
    /// <param name="cameras">The mutated camera list (the live definition's cameras).</param>
    void ReconcileCameras(IReadOnlyList<WorldCamera> cameras);
    /// <summary>Reconciles the declared-screen slot table against a mutated screen list.</summary>
    /// <param name="screens">The mutated screen list (the live definition's screens).</param>
    void ReconcileScreens(IReadOnlyList<WorldScreen> screens);
    /// <summary>Renders every registered offscreen camera view for this frame.</summary>
    /// <param name="context">The host frame's render context.</param>
    /// <param name="program">The compiled SDF program the room renders.</param>
    /// <param name="revision">The program's revision counter — each offscreen engine re-uploads only when it advances.</param>
    /// <param name="transforms">This frame's packed dynamic transforms, identical to the main engine's.</param>
    /// <param name="time">The frame's content clock (seconds).</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    /// <param name="hostFrame">The frame the room is rendering this frame.</param>
    void RenderViews(in Puck.Hosting.FrameContext context, SdfProgram program, int revision, IReadOnlyList<DynamicTransform> transforms, float time, ulong authoritativeTick, SdfFrame hostFrame);
    /// <summary>Gets the live decal-text source at a screen index, or <see langword="null"/> when the slot's current
    /// source is not text.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    WorldScreenSource.Text? TextSourceAt(int index);
}
