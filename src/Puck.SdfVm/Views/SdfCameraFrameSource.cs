using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm.Views;

/// <summary>
/// The frame source of a camera view: the frame a host world renders (<see cref="SdfEngineNode.HostFrame"/>), filmed
/// from one camera. An <see cref="SdfEngineNode"/> of the view's own renders it as an <c>sdf.world</c> instance of a
/// render graph, so the view films the same program, transforms, clock and levers as the world, and the same glyph atlas,
/// screen decals and moving screen surfaces as the host's frame source. The view's producer sets
/// <see cref="HostFrame"/> and <see cref="Camera"/> before each frame it renders.
/// </summary>
/// <param name="host">The host world's frame source, whose glyph atlas, screen decals and screen-surface transforms the
/// view shares.</param>
public sealed class SdfCameraFrameSource(ISdfFrameSource host) : ISdfFrameSource {
    // The program the last captured frame carried, so a frame reports a change only when the program is another one.
    private SdfProgram? m_program;

    /// <summary>Gets or sets the camera the next frame films.</summary>
    public CameraSnapshot Camera { get; set; }
    /// <summary>Gets or sets whether the view skips ambient occlusion even where the host world keeps it.</summary>
    public bool DisableAmbientOcclusion { get; set; }
    /// <summary>Gets or sets whether the view skips soft shadows even where the host world keeps them.</summary>
    public bool DisableSoftShadows { get; set; }
    /// <inheritdoc/>
    public SdfGlyphAtlas? GlyphAtlas => host.GlyphAtlas;
    /// <summary>Gets or sets the frame the host world renders this frame, which the next frame films.</summary>
    public SdfFrame? HostFrame { get; set; }
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => host.ScreenDecals;
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => host.ScreenSurfaceTransforms;

    /// <inheritdoc/>
    /// <remarks>The host's frame with the camera as its one view over the whole output. A view may only add cost
    /// restrictions to the host's levers, never lift one the host set.</remarks>
    /// <exception cref="InvalidOperationException">No <see cref="HostFrame"/> is set.</exception>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var frame = (HostFrame ?? throw new InvalidOperationException(message: "A camera view films the host's frame, and none is set."));
        var changed = !ReferenceEquals(
            objA: frame.Program,
            objB: m_program
        );

        m_program = frame.Program;

        return (frame with {
            DisableAmbientOcclusion = (frame.DisableAmbientOcclusion || DisableAmbientOcclusion),
            DisableSoftShadows = (frame.DisableSoftShadows || DisableSoftShadows),
            ProgramChanged = changed,
            Views = [new SdfViewSnapshot(
                Camera: Camera,
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            )],
        });
    }
    /// <inheritdoc/>
    public void NotifyDeviceLost() => m_program = null;
}
