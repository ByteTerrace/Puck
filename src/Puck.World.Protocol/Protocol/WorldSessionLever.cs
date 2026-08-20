namespace Puck.World.Protocol;

/// <summary>
/// A live session lever — one write to a presentation knob that the server grant-checks and the client applies, the
/// same shape <see cref="WorldComposition"/> already uses for live composition overrides (server-gated, client-applied,
/// pushed back through <see cref="IClientSink"/>, synchronous over the loopback, never journaled).
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Writing these knobs directly onto an injected presentation service, bypassing the
/// server, skips the principal check entirely: revoking <c>Mutate</c> over the section a lever folds into would
/// refuse that section's real mutations while the lever still wrote the same values live and persisted them
/// through <c>world.save</c>. Routing the write (not the parsing, not the echo) through <c>Server.WorldServer</c>
/// is what closes that gap.</para>
/// <para><b>The vocabulary is a registration, not an enum.</b> <see cref="Name"/> is the token the <c>world.&lt;knob&gt;</c>
/// verb speaks, and the client's applier holds one registered setter per name (<c>Client.WorldSessionLeverSink</c>,
/// composed by <c>Client.WorldSessionLevers</c>). A new knob is one registration entry and one verb; nothing here and
/// nothing in the codec grows an arm. A name with no registration is refused by name at the applier rather than
/// silently dropped.</para>
/// <para><b>A lever is not a mutation.</b> It changes live state only: the document still owns boot, nothing enters the
/// journal, and no undo entry is minted for a slider. That asymmetry is the point of a lever and is preserved here — the
/// server checks it like a command (exactly as <c>ApplyCommand</c> checks <see cref="WorldCapability.Drive"/>) rather
/// than applying it like a <see cref="WorldMutation"/>.</para>
/// <para><b>Presentation state only — a hard constraint on what may become a lever.</b> The
/// <see cref="A"/>/<see cref="B"/> lanes are IEEE doubles, so a knob the simulation reads would put a float inside the
/// determinism boundary. Every knob carried here writes render, present-pacing, overlay, or audio-mix state that no
/// server type reads: <c>WorldRenderSettings</c> has no consumer under <c>Server/</c>, and <c>PresentPacingControl</c>
/// documents itself as presentation pacing only while the fixed step runs at its own constant rate. <b>A knob the
/// simulation reads is a document mutation, not a lever</b>, and belongs in <see cref="WorldMutation"/> where it is
/// journaled and fixed-point.</para>
/// </remarks>
/// <param name="Section">The document section this lever folds into — and therefore the
/// <see cref="WorldCapability.Mutate"/> subject the server checks it against, so the check subject is a field of the
/// payload rather than something each call site must remember to pass.</param>
/// <param name="Name">The registered knob token to write.</param>
/// <param name="A">The primary value (a level, a tier ordinal, or 0/1 for a toggle).</param>
/// <param name="B">The secondary value for the knobs that carry two (shadow crowd radius); otherwise zero.</param>
/// <param name="Seat">The 0-based local seat a per-seat knob writes, or <c>-1</c> for a session-wide knob. A setter
/// registered for a session-wide name ignores it.</param>
public readonly record struct WorldSessionLever(WorldSection Section, string Name, double A, double B = 0.0, int Seat = -1) {
    /// <summary>The <see cref="Seat"/> value a session-wide lever carries.</summary>
    public const int NoSeat = -1;
}
