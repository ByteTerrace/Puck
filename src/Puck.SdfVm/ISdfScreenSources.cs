using System.Numerics;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// What each diegetic screen an <see cref="SdfEngineNode"/> renders shows: the render-graph instance whose image it
/// samples, a source's or a view's, and the light it casts into the room. A screen binds the image the render graph hands
/// the node for that instance when it produces (<see cref="RenderGraphExternalReads"/>), under the lease the node holds
/// until the submission that samples it has finished. Every member runs on the thread that produces frames, once per
/// screen per produced frame, so an implementation answers without allocating.
/// </summary>
public interface ISdfScreenSources {
    /// <summary>Gets the program-declared screen indices the node binds each frame, fixed for the node's lifetime.</summary>
    IReadOnlyList<int> Screens { get; }

    /// <summary>Returns the light a screen casts into the room this frame: its image's average emitted color, normalized
    /// to 0–1, or zero for a screen showing nothing.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The light.</returns>
    Vector3 Light(int screen);
    /// <summary>Returns the name of the render-graph instance a screen samples.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing or text.</returns>
    string? ReadOf(int screen);
}
