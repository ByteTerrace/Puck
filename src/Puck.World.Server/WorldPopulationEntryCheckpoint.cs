namespace Puck.World.Server;

/// <summary>What a slot's checkpoint carries beside its sections: state of the slot's continuation that no section
/// holds, because another store owns it or because it is a relation to the process that captured it. No slot digest
/// folds any of it; a continuation fold reads each part afresh beside the slots
/// (<see cref="WorldPopulation.AppendContinuationHash(ref Puck.Maths.Fnv1aHash)"/>).</summary>
/// <param name="ContactFieldObservationCurrent">Whether a resting body had observed the contact field as it stood at
/// capture; always <see langword="true"/> for an awake body, which overwrites its observation before any read. The
/// counters behind it are process-local, so this relation is what a restore preserves.</param>
/// <param name="Profile">The profile the body is seated on, or <see langword="null"/> for a profileless body. The
/// identity owns it and can change it with no write to the body.</param>
/// <param name="Registers">The body's register file as the arena's slot lanes hold it: one raw value per slot of the
/// kit's compiled definitions. The arena owns the values and folds them itself.</param>
public readonly record struct WorldPopulationEntryTail(
    bool ContactFieldObservationCurrent,
    WorldIdentityProjection? Profile,
    ReadOnlyMemory<long> Registers
);
/// <summary>One entity-table slot's checkpointed simulation state (<see cref="WorldPopulation.Capture"/>): the slot's
/// sections, each encoded by its own static encoder, and a tail. Read-back-only outcome strings and the resolved look
/// row are left for the next write to the slot to set.</summary>
public struct WorldPopulationEntryCheckpoint {
    /// <summary>The slot's population index.</summary>
    public int Index;
    /// <summary>The sections the slot's body owns.</summary>
    public WorldBodySections Body;
    /// <summary>Who occupies the slot and under what admission.</summary>
    public EntryAdmissionSection Admission;
    /// <summary>The targets the occupant has designated.</summary>
    public EntryDesignationsSection Designations;
    /// <summary>How the occupant is drawn; captured and restored, never folded.</summary>
    public EntryLookSection Look;
    /// <summary>The producer and flock runtime.</summary>
    public EntryProducerSection Producer;
    /// <summary>The cached steering image.</summary>
    public EntrySteeringSection Steering;
    /// <summary>The motion and steering cadence.</summary>
    public EntryCadenceSection Cadence;
    /// <summary>The cached route.</summary>
    public EntryNavigationSection Navigation;
    /// <summary>The cached route's path.</summary>
    public EntryRouteSection Route;
    /// <summary>The state no section holds.</summary>
    public WorldPopulationEntryTail Tail;

    /// <summary>Visits every section of an entry in the continuation's order. This is the one statement of that order
    /// and of which sections a slot has: the checkpoint's writer and reader and the slot digest all walk it, so a
    /// section cannot be encoded without being digested, or read in another order than it was written.</summary>
    /// <typeparam name="TVisitor">The visitor type.</typeparam>
    /// <param name="entry">The entry.</param>
    /// <param name="visitor">The visitor.</param>
    public static void VisitSections<TVisitor>(ref WorldPopulationEntryCheckpoint entry, ref TVisitor visitor) where TVisitor : IContinuationSectionVisitor, allows ref struct {
        visitor.Visit(section: ref entry.Body.Pose);
        visitor.Visit(section: ref entry.Body.Motion);
        visitor.Visit(section: ref entry.Body.Clocks);
        visitor.Visit(section: ref entry.Body.MotionRecency);
        visitor.Visit(section: ref entry.Body.InputImages);
        visitor.Visit(section: ref entry.Body.InputReadbacks);
        visitor.Visit(section: ref entry.Body.Action);
        visitor.Visit(section: ref entry.Body.LaneRecency);
        visitor.Visit(section: ref entry.Body.RegisterWrites);
        visitor.Visit(section: ref entry.Body.DurableInput);
        visitor.Visit(section: ref entry.Body.ScriptActors);
        visitor.Visit(section: ref entry.Body.Tape);
        visitor.Visit(section: ref entry.Body.Program);
        visitor.Visit(section: ref entry.Body.Contacts);
        visitor.Visit(section: ref entry.Admission);
        visitor.Visit(section: ref entry.Designations);
        visitor.Visit(section: ref entry.Look);
        visitor.Visit(section: ref entry.Producer);
        visitor.Visit(section: ref entry.Steering);
        visitor.Visit(section: ref entry.Cadence);
        visitor.Visit(section: ref entry.Navigation);
        visitor.Visit(section: ref entry.Route);
    }
}
