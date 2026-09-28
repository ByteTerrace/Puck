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
    /// <summary>Hands the binder a captured frame's packed transforms and simulation tick, once the frame is captured.</summary>
    /// <param name="transforms">This frame's packed dynamic transforms, identical to the main engine's.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick);
    /// <summary>Adds each camera view's view to the frame the presentation dresses, after its own views: a camera view is
    /// a view of the world's frame, framed by its registration's camera this frame, so it renders from the world's
    /// residency. Called from the dress, once the frame's transforms are packed and its own views are latched.</summary>
    /// <param name="transforms">This frame's packed dynamic transforms, which a camera's anchors resolve against.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick, which a camera's rig clock reads.</param>
    /// <param name="presentationSeconds">The frame's presentation time, which a camera's rig clock reads.</param>
    /// <param name="views">The frame's views: its own, to which the camera views are appended.</param>
    void FilmViews(DynamicTransform[] transforms, ulong authoritativeTick, float presentationSeconds, List<SdfViewSnapshot> views);
    /// <summary>Gets the live decal-text source at a screen index, or <see langword="null"/> when the slot's current
    /// source is not text.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    WorldScreenSource.Text? TextSourceAt(int index);
}
