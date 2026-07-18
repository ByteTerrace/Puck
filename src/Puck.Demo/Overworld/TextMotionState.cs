namespace Puck.Demo.Overworld;

/// <summary>
/// The demo-global reduced-motion switch for text enrichment — the accessibility gate the <c>text.motion</c> console
/// verb flips and the diegetic terminal's <see cref="ConsoleFeed"/> reads. When off, every motion-class effect
/// (shake/wave/pulse/jitter/dissolve) settles to rest and reveals complete instantly, while the static delight layer
/// (colour, weight) is unaffected — the WCAG reduced-motion contract. It is process-global on purpose (an
/// accessibility preference is one setting for the whole session), and presentation-only: the simulation and its hash
/// never see it, matching every other console echo in the greenfield demo.
/// </summary>
internal static class TextMotionState {
    // Default on: motion is opt-OUT, per the delight doctrine (constant motion is opt-in as a posture, but the switch
    // starts enabled so authored effects animate until a player asks for calm).
    private static volatile bool s_motionEnabled = true;

    /// <summary>Whether motion-class text effects animate this session.</summary>
    public static bool MotionEnabled => s_motionEnabled;

    /// <summary>Sets the reduced-motion switch.</summary>
    /// <param name="enabled">Whether motion-class effects should animate.</param>
    public static void SetMotionEnabled(bool enabled) =>
        s_motionEnabled = enabled;
}
