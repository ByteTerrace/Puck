using Puck.Abstractions.Counting;

namespace Puck.World.Client;

/// <summary>Actual creation posing work across the composed World, including authored placements; their surface probes count in <see cref="WorldFieldQueryWork"/>.</summary>
public static class WorldCreationWork {
    /// <summary>Bytes copied to retain the previous articulated pose before packing or carrying it.</summary>
    public static WorkKind PreviousPoseBytes { get; } = new(name: "sdf.creations.previous-pose.bytes-copied", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Articulated pose packs, including packs retaining all their slots.</summary>
    public static WorkKind Packs { get; } = new(name: "sdf.creations.packs", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Live registrations considered by a pose pack.</summary>
    public static WorkKind Visited { get; } = new(name: "sdf.creations.registrations.visited", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Registrations whose authored pose programs ran.</summary>
    public static WorkKind Posed { get; } = new(name: "sdf.creations.registrations.posed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Registrations retaining their pose because no input or continuation owed work.</summary>
    public static WorkKind Retained { get; } = new(name: "sdf.creations.registrations.retained", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Registrations whose pose evaluation was held by the existing rate mechanism.</summary>
    public static WorkKind RateHeld { get; } = new(name: "sdf.creations.registrations.rate-held", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Placed registrations, rather than body-rooted registrations, whose pose programs ran.</summary>
    public static WorkKind PlacementsPosed { get; } = new(name: "sdf.creations.placements.posed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Transform slots owned by evaluated registrations.</summary>
    public static WorkKind SlotsPosed { get; } = new(name: "sdf.creations.slots.posed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Transform slots retained without authored pose evaluation.</summary>
    public static WorkKind SlotsRetained { get; } = new(name: "sdf.creations.slots.retained", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>New driver state initializations.</summary>
    public static WorkKind DriverInitializations { get; } = new(name: "sdf.creations.drivers.initializations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Driver rows evaluated.</summary>
    public static WorkKind DriverEvaluations { get; } = new(name: "sdf.creations.drivers.evaluations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Positive-time nonzero-cadence driver integrations with positive weight.</summary>
    public static WorkKind DriverIntegrations { get; } = new(name: "sdf.creations.drivers.integrations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Driver integrations that actually changed their float phase.</summary>
    public static WorkKind DriverPhaseChanges { get; } = new(name: "sdf.creations.drivers.phase-changes", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Evaluated registrations with ongoing authored driver work.</summary>
    public static WorkKind DriverContinuations { get; } = new(name: "sdf.creations.drivers.continuations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Local static geometry variants emitted or replayed during a visual program build.</summary>
    public static WorkKind StaticTemplates { get; } = new(name: "sdf.creations.static.templates", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Retained static occurrences published during a visual program build.</summary>
    public static WorkKind StaticCopies { get; } = new(name: "sdf.creations.static.copies", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Process totals; read twice to measure a window. Posed, retained and rate-held partition visited registrations.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.creations", kinds: [Visited, Posed, Retained, RateHeld, PlacementsPosed, SlotsPosed, SlotsRetained, DriverInitializations, DriverEvaluations, DriverIntegrations, DriverPhaseChanges, DriverContinuations, StaticTemplates, StaticCopies, PreviousPoseBytes, Packs]);
}
