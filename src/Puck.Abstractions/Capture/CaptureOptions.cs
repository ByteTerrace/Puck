namespace Puck.Abstractions.Capture;

/// <summary>
/// Backend-neutral configuration for a capture session: its cadence, frame budget, and output location.
/// </summary>
public sealed class CaptureOptions {
    /// <summary>Gets or sets the file-name prefix used when composing an output path under
    /// <see cref="OutputDirectory"/>.</summary>
    public string FileNamePrefix { get; set; } = "puck";
    /// <summary>Gets or sets the capture output cadence, in frames per second.</summary>
    public int FrameRate { get; set; } = 30;
    /// <summary>Gets or sets the maximum number of frames to capture; zero means unbounded.</summary>
    public int MaxFrames { get; set; }
    /// <summary>Gets or sets the directory captures are written to when <see cref="OutputPath"/> is unset.</summary>
    public string OutputDirectory { get; set; } = "captures";
    /// <summary>Gets or sets an explicit output file path, overriding <see cref="OutputDirectory"/> and
    /// <see cref="FileNamePrefix"/>.</summary>
    public string? OutputPath { get; set; }
}
