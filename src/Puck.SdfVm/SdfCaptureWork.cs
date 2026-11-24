using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The residencies a frame captures against those it holds, process-wide. A frame holds every residency an instance
/// resolves to or a recorder keeps, serves each one's upkeep (<see cref="SdfWorldResidency.Upkeep"/>) and captures its
/// host's frame, and captures any other only for an instance the frame reads (<see cref="SdfWorldPasses.PrepareRead"/>),
/// a recording that reaches it, or the minimum a residency nothing reads is owed (<see cref="Minimum"/>), so
/// <c>held</c> less <c>run</c> over the same frames is the captures the frame order spared. Counts of work, never
/// compared with a wall clock.
/// </summary>
public static class SdfCaptureWork {
    /// <summary>Counts the residencies the SDF package held at each frame's start, one per residency per frame.</summary>
    public static readonly WorkKind Held = new(name: "sdf.captures.held", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the captures residencies ran from their frame sources (<see cref="ISdfFrameSource.CaptureFrame"/>),
    /// at most one a residency a frame; a residency holding its frame for a capture's convergence runs none.</summary>
    public static readonly WorkKind Run = new(name: "sdf.captures.run", unit: "captures", workClass: WorkClass.Pacing);
    /// <summary>Counts the residencies a frame's start prepared for their minimum
    /// (<see cref="SdfWorldResidency.OwesPreparation"/>) before any reader asked: every owed residency on a frame that
    /// rates nothing, and at most one a frame otherwise (<see cref="SdfWorldPasses.BeginFrame"/>).</summary>
    public static readonly WorkKind Minimum = new(name: "sdf.captures.minimum", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the owed residencies a frame's start left for a later frame's minimum, one per residency per
    /// frame; a reader of the frame still prepares such a residency.</summary>
    public static readonly WorkKind MinimumHeld = new(name: "sdf.captures.minimum-held", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts routed captures whose composition packed transforms and draw journals in a reader round.</summary>
    public static readonly WorkKind CapturePacks = new(name: "sdf.captures.capture-packs", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts those capture packs that ran on a job-pool worker.</summary>
    public static readonly WorkKind CapturePacksOnWorker = new(name: "sdf.captures.capture-packs-on-worker", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the residencies whose frames were packed as partitions of one fork of the job pool
    /// (<see cref="SdfWorldPasses.ReadsPrepared"/>), the forking thread's own share included.</summary>
    public static readonly WorkKind PackedBeside = new(name: "sdf.captures.packed-beside", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts those of <see cref="PackedBeside"/> a job-pool worker packed, as against the frame thread.</summary>
    public static readonly WorkKind PackedOnWorker = new(name: "sdf.captures.packed-on-worker", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the packs a frame handed to the residency's own lane once its host had captured
    /// (<see cref="SdfWorldResidency.PackAhead"/>), which run beside the readers' capture rounds.</summary>
    public static readonly WorkKind PackedAhead = new(name: "sdf.captures.packed-ahead", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts the joins of a pack run ahead that found it still running, so the frame thread waited for it.</summary>
    public static readonly WorkKind PackAheadWaits = new(name: "sdf.captures.pack-ahead-waits", unit: "joins", workClass: WorkClass.Pacing);
    /// <summary>Counts the residencies whose uploads were recorded as partitions of one fork of the job pool
    /// (<see cref="SdfWorldPasses.RendersScheduled"/>), the forking thread's own share included.</summary>
    public static readonly WorkKind UploadedBeside = new(name: "sdf.captures.uploaded-beside", unit: "residencies", workClass: WorkClass.Pacing);
    /// <summary>Counts those of <see cref="UploadedBeside"/> whose upload a job-pool worker recorded, as against the
    /// frame thread.</summary>
    public static readonly WorkKind UploadedOnWorker = new(name: "sdf.captures.uploaded-on-worker", unit: "residencies", workClass: WorkClass.Pacing);

    /// <summary>Counts the residencies whose background GPU work a frame's upkeep stepped (<see cref="SdfGpuWork.Pump"/>):
    /// against <see cref="Held"/>, the upkeep visits only a residency with a completed job, a client's offer, a new
    /// upload, a held frame or a budget hold the passing of time lifts.</summary>
    public static readonly WorkKind Pumped = new(name: "sdf.captures.pumped", unit: "residencies", workClass: WorkClass.Pacing);

    /// <summary>Gets the process's capture counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.captures", kinds: [Held, Run, Minimum, MinimumHeld, CapturePacks, CapturePacksOnWorker, PackedBeside, PackedOnWorker, PackedAhead, PackAheadWaits, UploadedBeside, UploadedOnWorker, Pumped]);
}
