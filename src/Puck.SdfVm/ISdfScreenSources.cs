using Puck.Commands;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>What a screen's face is, which decides how the screen shading draws its picture. Each value IS the one the
/// screen's mapping record carries to the kernels (<c>ScreenMappingData.state.w</c>, <c>shade/sdf-environment.hlsli</c>).</summary>
public enum SdfScreenFace : byte {
    /// <summary>An opening onto another place (a window): its picture is the world beyond it, shown at that world's own
    /// luminance with no display treatment.</summary>
    Opening = 0,
    /// <summary>A display: its picture as its source made it, filling the face, at the display's luminance.</summary>
    Display = 1,
    /// <summary>A cathode-ray tube's display: its picture inset in the tube's bezel and drawn through its glass
    /// (scanlines, an aperture grille, a bright-pixel bloom), which a framed view's screen fidelity fades out.</summary>
    Crt = 2,
}
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
    /// <summary>Returns the luminance a screen's picture shows at: the peak luminance of a white picture on a display, in
    /// nits. A screen drawn as an opening shows the luminance beyond it as it is, whatever this says.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The peak luminance, in nits; <see cref="SdfScreenEmission.DefaultLuminance"/> unless the screen's row
    /// authors one.</returns>
    float LuminanceOf(int screen) => SdfScreenEmission.DefaultLuminance;
    /// <summary>Returns what a screen's face is: an opening, a display, or a CRT display.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The screen's face.</returns>
    SdfScreenFace FaceOf(int screen);
    /// <summary>Returns the mapping a screen publishes, which the screen shading draws its face from.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The screen's surface mapping, or <see langword="null"/> when it publishes none: it then shades as unbound
    /// glass.</returns>
    SourceMapping? MappingOf(int screen);
    /// <summary>Returns the ripple a screen shows this frame, for an opening that opts in.</summary>
    /// <param name="screen">The program-declared screen index.</param>
    /// <param name="ripple">The screen's clock and contacts, or <see langword="default"/> when it does not ripple.</param>
    /// <returns><see langword="true"/> when the screen ripples; the default is a screen that never does.</returns>
    bool TryRippleOf(int screen, out SdfScreenRipple ripple) {
        ripple = default;

        return false;
    }
    /// <summary>Returns the name of the render-graph instance a screen samples in one view of the residency's frame.</summary>
    /// <param name="view">The view's index in the residency's frame (<see cref="SdfWorldView.View"/>).</param>
    /// <param name="screen">The program-declared screen index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing or text in that view.</returns>
    string? ReadOf(int view, int screen);
}
