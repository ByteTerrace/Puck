namespace Puck.World.Server;

/// <summary>Names the <c>world.undo</c> refusals a caller can tell apart by code. <c>world.refusals</c> lists every
/// member.</summary>
public enum WorldUndoRefusal : byte {
    /// <summary>The undo reaches past a bounded journal's horizon: the entries it would remove have already compacted
    /// into the base, so no replay can recover the document before them. Refused whole, never clamped to fewer.</summary>
    [Refusal(door: "world.undo", condition: "the undo reaches past the horizon host.journalDepth bounds the journal to", kind: RefusalKind.Verdict)]
    PastHorizon,
}
