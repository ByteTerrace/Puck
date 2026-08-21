namespace Puck.World;

/// <summary>The <c>replay.tape</c> door's covered refusal vocabulary — the tape's shape-identity gate
/// (<see cref="WorldReplaySnapshot.Read"/>'s leading magic/shape-token check), its mount-pin gate
/// (<see cref="WorldReplaySnapshot"/>'s <c>VerifyMountedAddons</c>), and its rate pin
/// (<see cref="WorldReplaySnapshot.Drive"/>'s leading simulation-rate check), which together are what keeps a
/// re-drive from silently running a world the tape never recorded. Not the whole codec: the many per-enum "unknown wire value"
/// guards and the plain corruption checks (truncated length prefixes, a duplicate mounted-addon name, an out-of-range
/// seat slot) stay bare <see cref="InvalidDataException"/>s outside this catalog — see
/// <see cref="ReplayRefusalExtensions"/>'s remarks for why this door's v1 scope stops here.</summary>
internal enum ReplayRefusal {
    /// <summary>The leading magic or shape token does not match this build's pinned <c>.puckreplay</c> shape.</summary>
    [Refusal(door: "replay.tape", condition: "the leading magic or shape token does not match this build's pinned .puckreplay shape", kind: RefusalKind.ProtocolFault)]
    ShapeMismatch,

    /// <summary>The recording's own simulation rate disagrees with the rate this build currently runs the
    /// simulation at — a re-drive at the wrong step size would produce a different trajectory that would otherwise
    /// report as an ordinary MISMATCH, never distinguishable from a genuine determinism regression.</summary>
    [Refusal(door: "replay.tape", condition: "the recording's own simulation rate disagrees with the rate this build currently runs the simulation at", kind: RefusalKind.Verdict)]
    RateMismatch,

    /// <summary>The recording pins an addon receipt that did not mount in the replay's fresh world.</summary>
    [Refusal(door: "replay.tape", condition: "the recording pins an addon receipt that did not mount in the replay's fresh world", kind: RefusalKind.Verdict)]
    PinnedAddonNotMounted,

    /// <summary>A pinned addon's module hash disagrees with what mounted for the replay.</summary>
    [Refusal(door: "replay.tape", condition: "a pinned addon's module hash disagrees with what mounted for the replay", kind: RefusalKind.Verdict)]
    AddonModuleMismatch,

    /// <summary>A pinned addon's fuel-per-tick disagrees with what mounted for the replay.</summary>
    [Refusal(door: "replay.tape", condition: "a pinned addon's fuel-per-tick disagrees with what mounted for the replay", kind: RefusalKind.Verdict)]
    AddonFuelMismatch,

    /// <summary>A recorded <c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>'s CAS content hash disagrees with
    /// what the replay drive resolved (its own base for Reset, a fresh re-read of the path hint for Load/Reload).</summary>
    [Refusal(door: "replay.tape", condition: "a recorded rebuild's CAS content hash disagrees with what the replay drive resolved", kind: RefusalKind.Verdict)]
    RebuildContentMismatch,

    /// <summary>A recorded <c>world.load</c>/<c>world.reload</c>'s pinned path could not be re-read for the replay
    /// drive (missing, unreadable, or no longer a valid document).</summary>
    [Refusal(door: "replay.tape", condition: "a recorded world.load/world.reload's pinned path could not be re-read for the replay drive", kind: RefusalKind.Verdict)]
    RebuildSourceUnavailable,

    /// <summary>The recording pins <c>rateHz</c> 0 (a static world with no step width) but carries recorded ticks
    /// anyway — a rate-0 tape's own invariant is zero recorded ticks, because <c>NoteTick</c> never fires while the
    /// boot world never steps; a tape violating that invariant cannot honestly derive a step width to re-drive at.</summary>
    [Refusal(door: "replay.tape", condition: "the recording pins rateHz 0 but carries recorded ticks anyway", kind: RefusalKind.Verdict)]
    RateZeroCarriesTicks,

    /// <summary>A recorded same-process transfer's own content signature disagrees with what its decoded fields
    /// recompute. This entry sits outside the pose hash's coverage, so this check is the only thing on the tape that
    /// would ever catch a tampered byte in one — never a plausible-looking ordinary trajectory mismatch.</summary>
    [Refusal(door: "replay.tape", condition: "a recorded transfer's content signature disagrees with what its own decoded fields recompute", kind: RefusalKind.Verdict)]
    TransferEventTampered,

    /// <summary>A recorded mutation's accept/refuse outcome disagrees with what the replay's own apply pipeline
    /// produced for the identical mutation, at the identical tick position — either direction (accepted live but
    /// now refused, or the reverse). Once acceptance can depend on module bytes on disk (addon preparation), this
    /// disagreement is a real determinism finding that a later-tick pose comparison alone could never surface.</summary>
    [Refusal(door: "replay.tape", condition: "a recorded mutation's accept/refuse outcome disagrees with what the replay's own apply pipeline produced for it", kind: RefusalKind.Verdict)]
    MutationOutcomeMismatch,
}
/// <summary>Constructs this door's <see cref="InvalidDataException"/>s tagged with the <see cref="ReplayRefusal"/>
/// each throw site names. <see cref="InvalidDataException"/> is sealed (unlike <c>SdfDocumentException</c> elsewhere
/// in this catalog, it cannot be subclassed to carry a required <see cref="ReplayRefusal"/> constructor parameter),
/// so <see cref="Raise"/> — a convention, not a type-system guarantee — is what every covered throw site routes
/// through instead; the reason rides in <see cref="Exception.Data"/> under a private key, recoverable via
/// <see cref="ReasonOf"/>. Every existing <c>catch (Exception e) when (e is InvalidDataException ...)</c> in
/// <see cref="WorldReplayTape"/>/<c>Puck.World.WorldReplayCommandModule</c> keeps working unchanged, because the raised
/// exception's runtime type is still exactly <see cref="InvalidDataException"/>.
/// <para>V1 scope: only the shape-identity check and the mount-pin gate are covered — the doors named in
/// <see cref="ReplayRefusal"/>'s remarks. The codec's remaining ~15 "unknown wire value"/corruption throws stay
/// untagged <see cref="InvalidDataException"/>s: they are mechanically identical in shape (a byte in a fixed slot
/// naming nothing the pinned wire set declares, or a length-prefixed field that does not fit the remaining bytes)
/// across roughly a dozen unrelated enum types, so tagging all of them would be bulk transcription rather than a
/// coherent door.</para></summary>
internal static class ReplayRefusalExtensions {
    private const string ReasonKey = "Puck.World.ReplayRefusal";

    /// <summary>Constructs the tagged <see cref="InvalidDataException"/> for <paramref name="reason"/>.</summary>
    /// <param name="reason">Which of this door's finite refusal reasons fired.</param>
    /// <param name="message">The refusal, with enough tape context to locate the disagreement.</param>
    /// <returns>The exception to throw.</returns>
    public static InvalidDataException Raise(this ReplayRefusal reason, string message) {
        var exception = new InvalidDataException(message: $"{reason}: {message}");

        exception.Data[ReasonKey] = reason;

        return exception;
    }
    /// <summary>Recovers the <see cref="ReplayRefusal"/> a caught exception was raised with, if any.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <returns>The reason, or <see langword="null"/> when the exception was not raised via <see cref="Raise"/>.</returns>
    public static ReplayRefusal? ReasonOf(this Exception exception) {
        return ((exception.Data[ReasonKey] is ReplayRefusal reason)
            ? reason
            : null
        );
    }
}
