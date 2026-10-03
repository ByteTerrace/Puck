namespace Puck.World.Server;

/// <summary>Names why a pull refused to adopt a cloud copy into the owned-world catalog for a reason of the
/// catalog's own state, before any copy is read. <c>world.refusals</c> lists every member.</summary>
public enum WorldOwnedWorldSyncRefusal : byte {
    /// <summary>A tape reading the catalog is recording. A pull would change an owned identity mid-recording, and a tape
    /// never carries an owned document, so the re-drive could not reproduce it.</summary>
    [Refusal(door: "storage.pull", condition: "a tape reading the owned-world catalog is recording", kind: RefusalKind.Verdict)]
    PullWhileRecording,
}
