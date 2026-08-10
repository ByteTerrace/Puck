namespace Puck.World.Protocol;

/// <summary>Which live presentation knob a <see cref="WorldSessionLever"/> writes. A new knob adds a member here and a
/// switch arm at the applier — never a new record type, so the lever vocabulary grows as data exactly like the grant
/// model's subjects do.</summary>
/// <remarks>A preset is a composition of levers, never a lever with more lanes; otherwise every preset earns another
/// payload shape and the two-lane vocabulary stops being closed.</remarks>
public enum WorldLeverKind : byte {
    /// <summary>The audio mix master gain (<c>world.volume</c>); folds into <c>audio.masterGain</c>.</summary>
    MasterVolume,

    /// <summary>Soft-shadow reach in <see cref="WorldSessionLever.A"/> and crowd radius in
    /// <see cref="WorldSessionLever.B"/> (<c>world.shadows</c>).</summary>
    Shadows,

    /// <summary>Ambient occlusion on/off as 0/1 (<c>world.ao</c>).</summary>
    AmbientOcclusion,

    /// <summary>Ambient-occlusion quality tier ordinal (<c>world.ao-quality</c>).</summary>
    AmbientOcclusionQuality,

    /// <summary>Far-bound culling on/off as 0/1 (<c>world.far-field bound</c>).</summary>
    FarBound,

    /// <summary>Shadow far-exit on/off as 0/1.</summary>
    ShadowFarExit,

    /// <summary>Shadow accumulation on/off as 0/1 (<c>world.shadow.accumulate</c>).</summary>
    ShadowAccumulation,

    /// <summary>Shadow mask tier ordinal (<c>world.shadow-mask</c>).</summary>
    ShadowMask,

    /// <summary>Shadow march tier ordinal (<c>world.shadow-march</c>).</summary>
    ShadowMarch,

    /// <summary>Render scale (<c>world.render-scale</c>).</summary>
    RenderScale,

    /// <summary>Upscale sharpness (<c>world.upscale-sharpness</c>).</summary>
    UpscaleSharpness,

    /// <summary>Target present rate in Hz, 0 meaning automatic display pacing (<c>world.target</c>).</summary>
    TargetHertz,
}

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
/// <para><b>A lever is not a mutation.</b> It changes live state only: the document still owns boot, nothing enters the
/// journal, and no undo entry is minted for a slider. That asymmetry is the point of a lever and is preserved here — the
/// server checks it like a command (exactly as <c>ApplyCommand</c> checks <see cref="WorldCapability.Drive"/>) rather
/// than applying it like a <see cref="WorldMutation"/>.</para>
/// <para><b>Presentation state only — a hard constraint on what may become a lever.</b> The
/// <see cref="A"/>/<see cref="B"/> lanes are IEEE doubles, so a knob the simulation reads would put a float inside the
/// determinism boundary. Every knob carried here writes render, present-pacing, or audio-mix state that no server type
/// reads: <c>WorldRenderSettings</c> has no consumer under <c>Server/</c>, and <c>PresentPacingControl</c> documents
/// itself as presentation pacing only while the fixed step runs at its own constant rate. <b>A knob the simulation
/// reads is a document mutation, not a lever</b>, and belongs in <see cref="WorldMutation"/> where it is journaled and
/// fixed-point.</para>
/// </remarks>
/// <param name="Section">The document section this lever folds into — and therefore the
/// <see cref="WorldCapability.Mutate"/> subject the server checks it against, so the check subject is a field of the
/// payload rather than something each call site must remember to pass.</param>
/// <param name="Kind">Which knob to write.</param>
/// <param name="A">The primary value (a level, a tier ordinal, or 0/1 for a toggle).</param>
/// <param name="B">The secondary value for the knobs that carry two (shadow crowd radius); otherwise zero.</param>
public readonly record struct WorldSessionLever(WorldSection Section, WorldLeverKind Kind, double A, double B = 0.0);
