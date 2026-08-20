using Puck.Physics.Motion;

namespace Puck.World.Protocol;

/// <summary>One tick-stamped foreign durable-state submission — the one door both a numeric operand and a text
/// operand cross, submitter-agnostic. The door's contract (grants + <see cref="DocumentWriteMask"/>) never varies;
/// only who submits does — the sim itself, per-tick, for numeric <c>Counter</c>/<c>Timer</c> outputs
/// (<c>Server.WorldServer.Step</c>), or a player-initiated text delivery. One shape rather than a sibling
/// text-submission door keeps the admission predicate singular (see <c>Server.WorldOwnedWorlds.Decide</c>'s
/// remarks).</summary>
/// <param name="SourceDocumentId">The asking document.</param>
/// <param name="OwnerDocumentId">The owning document.</param>
/// <param name="Tick">The source tick.</param>
/// <param name="Slot">The state row name.</param>
/// <param name="Kind">The requested operation. A <see cref="Text"/> submission admits only
/// <see cref="WorldDocumentWriteKind.Set"/> — <see cref="WorldDocumentWriteKind.Add"/> refuses by name at the door
/// (no concatenation-by-stealth), regardless of what the recipient's write mask admits.</param>
/// <param name="StorageKind">The durable slot's numeric representation. Ignored when <see cref="Text"/> is set —
/// the same asymmetry <see cref="WorldStateCell"/>'s own <c>Value</c>/<c>Text</c> pair carries: a string cannot
/// ride a numeric lane by any honest encoding, so a text submission carries its operand in the second field rather
/// than reusing the first.</param>
/// <param name="Value">The raw numeric operand. Ignored when <see cref="Text"/> is set.</param>
/// <param name="Text">The text operand for a submission against a <see cref="CellKind.Text"/> slot row, or
/// <see langword="null"/> for a numeric submission. Capped at the SAME
/// <see cref="WorldStateCapacity.MaxTextValueLength"/> refusal every other text-cell write door enforces.</param>
public readonly record struct WorldDocumentSubmission(string SourceDocumentId, string OwnerDocumentId, ulong Tick, string Slot, WorldDocumentWriteKind Kind, ActionStateKind StorageKind, long Value, string? Text = null);
/// <summary>The owning authority's visible submission verdict.</summary>
/// <param name="Submission">The request.</param>
/// <param name="Accepted">Whether it applied.</param>
/// <param name="Reason">Why it applied or was refused.</param>
public readonly record struct WorldDocumentSubmissionReceipt(WorldDocumentSubmission Submission, bool Accepted, string Reason);
