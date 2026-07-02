namespace Puck.Abstractions.Capture;

/// <summary>
/// Backend-neutral configuration for a capture session: whether it runs, its cadence relative to the source,
/// how many frames it keeps, and where it writes.
/// </summary>
public sealed class CaptureOptions {
    /// <summary>Gets or sets whether capture is active.</summary>
    public bool Enabled { get; set; }
    /// <summary>Gets or sets the capture output cadence, in frames per second.</summary>
    public int FrameRate { get; set; } = 30;
    /// <summary>Gets or sets the rate frames arrive from the source, in frames per second; with
    /// <see cref="FrameRate"/> it sets the keep-one-in-N frame step.</summary>
    public int SourceFrameRate { get; set; } = 60;
    /// <summary>Gets or sets the maximum number of frames to capture; zero means unbounded.</summary>
    public int MaxFrames { get; set; }
    /// <summary>Gets or sets the directory captures are written to when <see cref="OutputPath"/> is unset.</summary>
    public string OutputDirectory { get; set; } = "captures";
    /// <summary>Gets or sets an explicit output file path, overriding <see cref="OutputDirectory"/> and
    /// <see cref="FileNamePrefix"/>.</summary>
    public string? OutputPath { get; set; }
    /// <summary>Gets or sets the file-name prefix used when composing an output path under
    /// <see cref="OutputDirectory"/>.</summary>
    public string FileNamePrefix { get; set; } = "puck";
}
