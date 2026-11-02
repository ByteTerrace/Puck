namespace Puck.World.Server;

/// <summary>What a noninterference run concluded.</summary>
public enum WorldDisclosureOutcome : byte {
    /// <summary>No observed principal can tell any variant from the base.</summary>
    Pass,

    /// <summary>Some observed principal tells a variant from the base.</summary>
    Leak,

    /// <summary>A world of the run did not set up what its fixture says (a refused, unanswered or late step, a session
    /// the world did not admit), so the pair is not the pair the fixture means and nothing is judged.</summary>
    Invalid,

    /// <summary>The control differs from the base although their hidden state is equal, so a door answers with
    /// something other than hidden state and nothing is judged.</summary>
    Noise,
}

/// <summary>One variant's comparison with the base.</summary>
/// <param name="Variant">The variant's name.</param>
/// <param name="Leaks">Each recipient that tells it from the base; empty when none can.</param>
public sealed record WorldDisclosurePair(string Variant, IReadOnlyList<WorldDisclosureLeak> Leaks);

/// <summary>A noninterference run's verdict over the transcripts of its worlds.</summary>
/// <param name="Outcome">The conclusion.</param>
/// <param name="Invalid">Each reason a world of the run cannot be judged, naming the world.</param>
/// <param name="Noise">Where the control differs from the base.</param>
/// <param name="Pairs">Each variant's comparison with the base; empty unless the run was judged.</param>
public sealed record WorldDisclosureVerdict(WorldDisclosureOutcome Outcome, IReadOnlyList<string> Invalid, IReadOnlyList<WorldDisclosureLeak> Noise, IReadOnlyList<WorldDisclosurePair> Pairs) {
    /// <summary>Judges a run: invalid when any world's setup failed, noise when the control differs from the base,
    /// otherwise a leak when any recipient tells any variant from the base, and a pass when none can.</summary>
    /// <param name="worlds">The run's worlds by name: the control first, then the base, then each other variant.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentException"><paramref name="worlds"/> names fewer than three worlds (a control, a base and a
    /// variant).</exception>
    public static WorldDisclosureVerdict Judge(IReadOnlyList<(string Name, WorldDisclosureTranscript Transcript)> worlds) {
        ArgumentNullException.ThrowIfNull(argument: worlds);
        if (worlds.Count < 3) { throw new ArgumentException(message: "a run judges a control, a base and at least one variant", paramName: nameof(worlds)); }
        var invalid = worlds.SelectMany(selector: static world => world.Transcript.Invalid.Select(selector: reason => $"{world.Name}: {reason}")).ToArray();

        if (invalid.Length > 0) { return new(Invalid: invalid, Noise: [], Outcome: WorldDisclosureOutcome.Invalid, Pairs: []); }
        var noise = WorldDisclosureComparison.Compare(left: worlds[1].Transcript, right: worlds[0].Transcript);

        if (noise.Count > 0) { return new(Invalid: [], Noise: noise, Outcome: WorldDisclosureOutcome.Noise, Pairs: []); }
        var pairs = worlds.Skip(count: 2).Select(selector: world => new WorldDisclosurePair(Leaks: WorldDisclosureComparison.Compare(left: worlds[1].Transcript, right: world.Transcript),
            Variant: world.Name)).ToArray();

        return new(Invalid: [], Noise: [], Outcome: (pairs.Any(predicate: static pair => (pair.Leaks.Count > 0)) ? WorldDisclosureOutcome.Leak : WorldDisclosureOutcome.Pass), Pairs: pairs);
    }
}
