using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One entry in the server's single ordered domain: a submitted envelope plus the completion its submitter
/// supplied, or a server-authored event.</summary>
public abstract record WorldOrderedEntry {
    /// <summary>One submitted envelope.</summary>
    /// <param name="Envelope">The submission.</param>
    /// <param name="Completion">The submitter's typed result callback, or <see langword="null"/> when the caller
    /// does not need one.</param>
    public sealed record Submission(SubmissionEnvelope Envelope, Action<WorldSubmissionResult>? Completion) : WorldOrderedEntry;
    /// <summary>One server-authored event ordered against the submissions around it.</summary>
    /// <param name="Value">The event.</param>
    /// <param name="Input">Whether the event is an authority input the tape records (see
    /// <see cref="WorldServer.DispatchServerEvent"/>).</param>
    public sealed record ServerEvent(WorldServerEvent Value, bool Input) : WorldOrderedEntry;
}
