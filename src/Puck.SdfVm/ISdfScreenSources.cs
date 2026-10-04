using Puck.Commands;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// What each diegetic screen an <see cref="SdfWorldResidency"/> renders shows: the render-graph instance whose image it
/// samples in each view, a source's or a view's, the mapping its face is drawn from, and the light it casts into the
/// room. Views of one residency render one world from different eyes, so what a screen shows is each view's own: a
/// portal seen by a seat standing in the world and the same portal seen through another portal show two images, each
/// rendered from its own eye. A screen binds the image the render graph hands the view's node for that instance when it
/// produces (<see cref="RenderGraphExternalReads"/>), under the lease the node holds until the submission that samples
/// it has finished. Every member runs on the thread that produces frames, once per screen (and view) per produced frame,
/// so an implementation answers without allocating.
/// </summary>
public interface ISdfScreenSources {
    /// <summary>Gets the program-declared screen indices the node binds each frame, fixed for the node's lifetime.</summary>
    IReadOnlyList<int> Screens { get; }

    /// <summary>Returns whether this screen's acquired image can emit light. A camera viewing its own world does not
    /// emit; independent sources and views of other worlds do. The GPU reduces the acquired image itself.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>Whether the bound image participates in the shared emission reduction.</returns>
    bool Emits(int screen);
    /// <summary>Returns the mapping a screen publishes, which the screen shading draws its face from.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The screen's surface mapping, or <see langword="null"/> when it publishes none: it then shades as unbound
    /// glass.</returns>
    SourceMapping? MappingOf(int screen);
    /// <summary>Returns the name of the render-graph instance a screen samples in one view of the residency's frame.</summary>
    /// <param name="view">The view's index in the residency's frame (<see cref="SdfWorldView.View"/>).</param>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing or text in that view.</returns>
    string? ReadOf(int view, int screen);
}
