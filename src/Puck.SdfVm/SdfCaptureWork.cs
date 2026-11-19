using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The residencies a frame captures against those it holds, process-wide. A frame holds every residency an instance
/// resolves to or a recorder keeps, serves each one's upkeep (<see cref="SdfWorldResidency.Upkeep"/>) and captures its
/// host's frame, and captures any other only for an instance the frame reads (<see cref="SdfWorldPasses.PrepareRead"/>)
/// or a recording that reaches it, so <c>held</c> less <c>run</c> over the same frames is the captures the frame order
/// spared. Counts of work, never compared with a wall clock.
/// </summary>
public static class SdfCaptureWork {
    /// <summary>Counts the residencies the SDF package held at each frame's start, one per residency per frame.</summary>
    public static readonly WorkKind Held = new(name: "sdf.captures.held", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the captures residencies ran from their frame sources (<see cref="ISdfFrameSource.CaptureFrame"/>),
    /// at most one a residency a frame; a residency holding its frame for a capture's convergence runs none.</summary>
    public static readonly WorkKind Run = new(name: "sdf.captures.run", unit: "captures", workClass: WorkClass.Pacing);

    /// <summary>Gets the process's capture counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.captures", kinds: [Held, Run]);
}
