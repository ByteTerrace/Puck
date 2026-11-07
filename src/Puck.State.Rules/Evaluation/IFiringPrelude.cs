namespace Puck.State.Rules;

/// <summary>What a host records into a firing's scope the moment it opens, before any effect runs: simulation state the
/// host owns beside the arena (a decision's choice and timers) that the firing decided, written through the journal so
/// the scope's rewind restores it with the cells.</summary>
public interface IFiringPrelude {
    /// <summary>Records the state into the open scope.</summary>
    /// <param name="journal">The journal of the arena the firing writes, with its scope open.</param>
    void Record(ArenaJournal journal);
}
