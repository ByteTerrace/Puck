namespace Puck.Scene;

/// <summary>
/// The SAFE, enumerated present-rate tiers a run pins for the window pump's display-aware pacer — the demo/user-facing pacing
/// option layered over the launcher's continuous present-rate knob (<c>LauncherOptions.TargetRenderRate</c>, in Hz).
/// The player picks one of these KNOWN-GOOD cadences, never a free numeric value; the two capped tiers are chosen so the
/// present slot is a WHOLE number of engine ticks (<c>EngineTicks.PerSecond</c> = 50400 divides evenly by both 60 and
/// 120 — 840 and 420 ticks per present slot), keeping the produced-frame-to-tick relationship integral. This is
/// PRESENTATION pacing ONLY: the fixed-step simulation runs at its own rate regardless of the picked tier, so a lower or
/// higher present rate never changes sim state (determinism is untouched). Names + rates are the ONE definition
/// (<see cref="PresentRateTiers"/>); the run-document field validator, the console <c>present-rate</c> verb, and the boot
/// resolution all read them there — mirroring <see cref="WorldRenderScaleTier"/>.
/// </summary>
public enum PresentRateTier {
    /// <summary>60 Hz (the demo default): a 16.67 ms present slot, 840 engine ticks per slot.</summary>
    Sixty,

    /// <summary>120 Hz: an 8.33 ms present slot, 420 engine ticks per slot.</summary>
    OneTwenty,

    /// <summary>Automatic display pacing: use a positively advertised VRR interval (with ceiling guard) when available;
    /// otherwise use the active physical signal rate. The effective rate is not pinned to a whole tick count.</summary>
    Display,
}

/// <summary>
/// The name ↔ tier ↔ target-rate mapping for <see cref="PresentRateTier"/> — the ONE place the safe tier set is defined.
/// The run-document validator (<c>HostDocument.Validate</c>), the demo's live <c>present-rate</c> verb, and the boot
/// resolution all read it, so the accepted names and the fed target Hz never fork. Mirrors <see cref="WorldRenderScaleTiers"/>.
/// </summary>
public static class PresentRateTiers {
    // The target present rate (Hz) each tier feeds the pacer; 0 = automatic display pacing. The two
    // capped rates evenly divide EngineTicks.PerSecond (50400) — 840 and 420 ticks per present slot — so the pacing math
    // stays integral.
    private const uint SixtyHertz = 60U;
    private const uint DisplayHertz = 0U;
    private const uint OneTwentyHertz = 120U;

    /// <summary>The canonical tier names, in ascending-rate order — the valid set the validator and the verb echo.</summary>
    public static readonly IReadOnlyList<string> Names = ["sixty", "one-twenty", "display"];

    /// <summary>The valid names joined for an error / echo message.</summary>
    public static string ValidNames => string.Join(separator: ", ", values: Names);

    /// <summary>Resolves a tier name (case-insensitive, trimmed) to its <see cref="PresentRateTier"/>.</summary>
    /// <param name="name">The tier name (a run-doc value or a typed console argument); null/whitespace is unknown.</param>
    /// <param name="tier">The resolved tier when the return is true (else <see cref="PresentRateTier.Sixty"/>).</param>
    /// <returns>Whether the name named a known tier.</returns>
    public static bool TryParse(string? name, out PresentRateTier tier) {
        switch ((name ?? "").Trim().ToLowerInvariant()) {
            case "sixty":
                tier = PresentRateTier.Sixty;

                return true;
            case "one-twenty":
                tier = PresentRateTier.OneTwenty;

                return true;
            case "display":
                tier = PresentRateTier.Display;

                return true;
            default:
                tier = PresentRateTier.Sixty;

                return false;
        }
    }

    /// <summary>The canonical (lower-case) name of a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The canonical name.</returns>
    public static string Name(PresentRateTier tier) => tier switch {
        PresentRateTier.OneTwenty => "one-twenty",
        PresentRateTier.Display => "display",
        _ => "sixty",
    };

    /// <summary>The target present rate in Hz fed to the pacer (<c>LauncherOptions.TargetRenderRate</c> at boot, the
    /// live pacing control mid-session); 0 means automatic display pacing.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The target Hz (0 = automatic display pacing).</returns>
    public static uint TargetHertz(PresentRateTier tier) => tier switch {
        PresentRateTier.OneTwenty => OneTwentyHertz,
        PresentRateTier.Display => DisplayHertz,
        _ => SixtyHertz,
    };

    /// <summary>The canonical tier name for a live target rate (the inverse of <see cref="TargetHertz"/>) — used by the
    /// <c>present-rate</c> verb to echo the CURRENT tier read back from the pacing control. 0, and any rate that is not a
    /// capped tier, reads as <c>display</c> (automatic).</summary>
    /// <param name="targetHertz">The pacer's live target Hz (0 = automatic).</param>
    /// <returns>The canonical tier name.</returns>
    public static string NameForHertz(double targetHertz) =>
        (Math.Abs(value: (targetHertz - SixtyHertz)) <= 0.0001) ? "sixty"
        : (Math.Abs(value: (targetHertz - OneTwentyHertz)) <= 0.0001) ? "one-twenty"
        : "display";
}
