using System.Numerics;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// What each diegetic screen an <see cref="SdfEngineNode"/> renders shows: the render-graph source instance whose image
/// it samples, or an image the host renders itself, and the light it casts into the room. A screen that reads a source
/// instance binds the image the render graph hands the node for that instance when it produces
/// (<see cref="RenderGraphExternalReads"/>), under the lease the node holds until the submission that samples it has
/// finished; a screen that reads none binds <see cref="Rendered"/>. Every member runs on the thread that produces frames,
/// once per screen per produced frame, so an implementation answers without allocating.
/// </summary>
public interface ISdfScreenSources {
    /// <summary>Gets the program-declared screen indices the node binds each frame, fixed for the node's lifetime.</summary>
    IReadOnlyList<int> Screens { get; }

    /// <summary>Returns the light a screen casts into the room this frame: its image's average emitted color, normalized
    /// to 0–1, or zero for a screen showing nothing.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The light.</returns>
    Vector3 Light(int screen);
    /// <summary>Returns the name of the render-graph source instance a screen samples.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows an image the host renders itself
    /// (<see cref="Rendered"/>) or nothing.</returns>
    string? ReadOf(int screen);
    /// <summary>Acquires the image of a screen that samples no source instance for one submitted frame. The node holds
    /// the lease until that submission has finished and adds the wait it carries to that submission.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The lease, or one of a zero handle for a screen showing nothing this frame, which the engine shades with
    /// its procedural screen material.</returns>
    GpuImageLease Rendered(int screen);
}
