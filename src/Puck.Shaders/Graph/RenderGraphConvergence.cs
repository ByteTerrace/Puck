using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

/// <summary>One converging capture as the runtime counts it: the request and the samples the runtime has counted toward
/// it. A sample counts only when the captured instance renders over inputs that are ready for capture (no stand-in and no
/// tainted read), so a package that samples on the capture's behalf takes its sample index from <see cref="Samples"/>
/// rather than from its own renders, and a frame the runtime does not count renders the same sample again.</summary>
public sealed class RenderGraphConvergence {
    /// <summary>Initializes a new instance of the <see cref="RenderGraphConvergence"/> class.</summary>
    /// <param name="request">The converging capture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public RenderGraphConvergence(FrameCaptureRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        Request = request;
    }

    /// <summary>Gets the converging capture, whose completion ends the frozen interval.</summary>
    public FrameCaptureRequest Request { get; }
    /// <summary>Gets the number of samples the runtime has counted toward the capture: the index of the sample a
    /// contributing render takes now. The capture is served on the render taken at <c>Converge - 1</c>.</summary>
    public int Samples { get; private set; }
    /// <summary>Gets whether the capture still converges: it asks for samples and has not completed.</summary>
    public bool IsActive => ((Request.Converge > 0) && !Request.Completion.IsCompleted);

    /// <summary>Counts one sample: the captured instance rendered over inputs ready for capture.</summary>
    public void Count() => Samples++;
}
