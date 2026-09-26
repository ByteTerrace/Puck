using Puck.Abstractions.Machines;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The narrow slice of the composition root's screen binder a frame source drives per frame. Declared here
/// so a Client-side type can hold the binder without naming the root's concrete type.</summary>
public interface IWorldScreenPresenter {
    /// <summary>Gets the audio machine bound to a screen index, or <see langword="null"/> when the slot's current
    /// source is not a machine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    IAudioMachine? AudioMachine(int index);
    /// <summary>Gets a named machine's audio output, or <see langword="null"/> when the instance or output is not live.</summary>
    /// <param name="instance">The declared machine instance name.</param>
    /// <param name="output">The provider audio output name.</param>
    IAudioMachine? AudioOutput(string instance, string output);
    /// <summary>Drops every device-owned upload while preserving CPU sessions, machine simulation, declarations, and view
    /// registrations.</summary>
    void NotifyDeviceLost();
    /// <summary>Publishes the screens' content for a produced frame before the render graph schedules it: the capture
    /// gate's answer for the frame, the fills, the shared feeds and every screen's mapping.</summary>
    /// <param name="context">The host's frame context, whose host resolves the live GPU device.</param>
    void Publish(in Puck.Hosting.FrameContext context);
    /// <summary>Reconciles the camera views against a mutated camera list.</summary>
    /// <param name="cameras">The mutated camera list (the live definition's cameras).</param>
    void ReconcileCameras(IReadOnlyList<WorldCamera> cameras);
    /// <summary>Reconciles the declared-screen slot table against a mutated screen list.</summary>
    /// <param name="screens">The mutated screen list (the live definition's screens).</param>
    void ReconcileScreens(IReadOnlyList<WorldScreen> screens);
    /// <summary>Hands the binder what a captured frame presents, which the camera views filming it read: the packed
    /// transforms their anchors resolve against and the simulation tick their rigs' clocks read.</summary>
    /// <param name="transforms">This frame's packed dynamic transforms, identical to the main engine's.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick);
    /// <summary>Gets the live decal-text source at a screen index, or <see langword="null"/> when the slot's current
    /// source is not text.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    WorldScreenSource.Text? TextSourceAt(int index);
}
