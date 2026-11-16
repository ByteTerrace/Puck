using Puck.Networking;

namespace Puck.World.Server;

/// <summary>The sections a slot's simulation continuation is cut into, in the order a slot's digest folds them. Each
/// names one unit of state that changes together and is digested alone: a body's or an entry's fixed-size state, one
/// variable-length table, or one group of immutable references.</summary>
/// <remarks>The order is the fold's and the checkpoint's; <see cref="WorldPopulationEntryCheckpoint.VisitSections"/>
/// is the one place it is stated.</remarks>
public enum WorldContinuationSectionKind : byte {
    /// <summary><see cref="BodyPoseSection"/>.</summary>
    Pose,
    /// <summary><see cref="BodyMotionSection"/>.</summary>
    Motion,
    /// <summary><see cref="BodyClocksSection"/>.</summary>
    Clocks,
    /// <summary><see cref="BodyMotionRecencySection"/>.</summary>
    MotionRecency,
    /// <summary><see cref="BodyInputImagesSection"/>.</summary>
    InputImages,
    /// <summary><see cref="BodyInputReadbacksSection"/>.</summary>
    InputReadbacks,
    /// <summary><see cref="BodyActionSection"/>.</summary>
    Action,
    /// <summary><see cref="BodyLaneRecencySection"/>.</summary>
    LaneRecency,
    /// <summary><see cref="BodyRegisterWritesSection"/>.</summary>
    RegisterWrites,
    /// <summary><see cref="BodyDurableInputSection"/>.</summary>
    DurableInput,
    /// <summary><see cref="BodyScriptActorsSection"/>.</summary>
    ScriptActors,
    /// <summary><see cref="BodyTapeSection"/>.</summary>
    Tape,
    /// <summary><see cref="BodyProgramSection"/>.</summary>
    Program,
    /// <summary><see cref="BodyContactsSection"/>.</summary>
    Contacts,
    /// <summary><see cref="EntryAdmissionSection"/>.</summary>
    Admission,
    /// <summary><see cref="EntryDesignationsSection"/>.</summary>
    Designations,
    /// <summary><see cref="EntryLookSection"/>.</summary>
    Look,
    /// <summary><see cref="EntryProducerSection"/>.</summary>
    Producer,
    /// <summary><see cref="EntrySteeringSection"/>.</summary>
    Steering,
    /// <summary><see cref="EntryCadenceSection"/>.</summary>
    Cadence,
    /// <summary><see cref="EntryNavigationSection"/>.</summary>
    Navigation,
    /// <summary><see cref="EntryRouteSection"/>.</summary>
    Route,
}
/// <summary>One section of a slot's simulation continuation: a value whose encoding is a static function of the value
/// alone. The encoder can read nothing but the section it is handed, so a section's digest cannot depend on state
/// outside it. Every one is a section a state holder may own (<see cref="IVersionedSection"/>), which is what puts
/// this project's sections under the DOOR analyzer rules.</summary>
/// <typeparam name="TSelf">The section type.</typeparam>
public interface IContinuationSection<TSelf> : IVersionedSection where TSelf : struct, IContinuationSection<TSelf> {
    /// <summary>Gets which section this is: its place in a slot's fold and the name its counters carry.</summary>
    static abstract WorldContinuationSectionKind Section { get; }
    /// <summary>Gets a value indicating whether a slot's continuation digest folds this section. A section that is
    /// captured and restored without being simulation state (the rendered look) answers <see langword="false"/>.</summary>
    static virtual bool Folded => true;

    /// <summary>Writes the section. Two sections that write the same bytes are the same continuation.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="section">The section.</param>
    static abstract void Write(WireWriter writer, in TSelf section);
    /// <summary>Reads a section <see cref="Write"/> wrote. A malformed value fails the reader; what is consistent
    /// only across sections is the entry reader's to check.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The section.</returns>
    static abstract TSelf Read(ref WireReader reader);
}
/// <summary>Receives each section of a slot, in the continuation's fixed order
/// (<see cref="WorldPopulationEntryCheckpoint.VisitSections"/>).</summary>
public interface IContinuationSectionVisitor {
    /// <summary>Visits one section.</summary>
    /// <typeparam name="TSection">The section type.</typeparam>
    /// <param name="section">The section, which a reading visitor assigns.</param>
    void Visit<TSection>(ref TSection section) where TSection : struct, IContinuationSection<TSection>;
}
