namespace Puck.World.Server;

/// <summary>Durable phases of one deployment-group release transaction.</summary>
public enum WorldReleaseOperationPhase {
    Prepare,
    Drain,
    Activate,
    Verify,
    Commit,
    Recover,
    RecoverActivate,
    Finalized,
}
/// <summary>What a deployment does with its group's pending operation (<see cref="WorldReleaseCoordinator.Succession"/>).</summary>
public enum WorldReleaseSuccession {
    /// <summary>The group holds no unfinished operation; the deployment begins its own, accepting an admitted one.</summary>
    Begin,
    /// <summary>The pending operation is the deployment's own release; the deployment resumes it.</summary>
    Resume,
    /// <summary>The pending operation is another release's unfinished one; the deployment supersedes it first.</summary>
    Supersede,
}
/// <summary>Whether public, federation, and console admission is open for a group.</summary>
public enum WorldReleaseAdmissionState { Closed, Open }
/// <summary>Named result returned by guarded release-group writes.</summary>
public enum WorldReleaseOperationOutcomeKind { Ok, Missing, AlreadyExists, PreconditionFailed, Conflict, Failed }
