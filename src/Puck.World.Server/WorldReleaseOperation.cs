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
/// <summary>Whether public, federation, and console admission is open for a group.</summary>
public enum WorldReleaseAdmissionState { Closed, Open }
/// <summary>Named result returned by guarded release-group writes.</summary>
public enum WorldReleaseOperationOutcomeKind { Ok, Missing, AlreadyExists, PreconditionFailed, Conflict, Failed }
