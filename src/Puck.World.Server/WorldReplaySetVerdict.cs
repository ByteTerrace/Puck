using System.Text;

namespace Puck.World;

/// <summary>One authority's own replay verdict inside a set of tapes.</summary>
/// <param name="Instance">The recorded row's instance identity.</param>
/// <param name="Authority">The recorded row's authority identity.</param>
/// <param name="Verdict">The authority's live-versus-replay verdict.</param>
public readonly record struct WorldReplayAuthorityVerdict(string Instance, string Authority, WorldReplayVerdict Verdict);
/// <summary>One crossing's verdict inside a set of tapes: verified only when the source's departure and the
/// destination's arrival are both on tapes in the set and both tapes replay to the state their authorities reached.
/// A crossing whose other half is on a remote authority, or on a row nothing taped, is never verified.</summary>
/// <param name="TransferId">The source-scoped transfer id.</param>
/// <param name="Source">The source's authority identity.</param>
/// <param name="Target">The destination's authority identity.</param>
/// <param name="Verified">Whether both halves replayed.</param>
/// <param name="Reason">Why the crossing is not verified, or empty when it is.</param>
public readonly record struct WorldReplayCrossingVerdict(ulong TransferId, string Source, string Target, bool Verified, string Reason);
/// <summary>The verdict for a recording and every companion taped beside it: each authority's own trajectory, and
/// every crossing between them paired by its handoff token. A set passes only when every authority matches and every
/// crossing on any of its tapes is verified, so a tape that departs a traveler to a remote or untaped authority never
/// reports as passing, however exactly its own trajectory replays.</summary>
/// <param name="Primary">The recording's own verdict.</param>
/// <param name="Companions">Each companion authority's verdict, in recorded order.</param>
/// <param name="Crossings">Every crossing a tape in the set departed or landed, in pairing order.</param>
public sealed record WorldReplaySetVerdict(WorldReplayVerdict Primary, IReadOnlyList<WorldReplayAuthorityVerdict> Companions, IReadOnlyList<WorldReplayCrossingVerdict> Crossings) {
    /// <summary>Gets whether every authority matched, ignoring crossings.</summary>
    public bool Match => (Primary.Match && Companions.All(predicate: static companion => companion.Verdict.Match));
    /// <summary>Gets whether every authority matched and every crossing is verified.</summary>
    public bool Passing => (Match && Crossings.All(predicate: static crossing => crossing.Verified));

    /// <summary>Renders the verdict on one line: the recording's own verdict, then each companion's, then each
    /// crossing's.</summary>
    /// <returns>The verdict text.</returns>
    public string Describe() {
        var text = new StringBuilder(value: Primary.Describe());

        foreach (var companion in Companions) {
            _ = text.Append(value: $" | authority '{companion.Instance}' {companion.Verdict.Describe()}");
        }
        foreach (var crossing in Crossings) {
            _ = text.Append(value: (crossing.Verified
                ? $" | crossing transfer={crossing.TransferId} '{crossing.Source}' -> '{crossing.Target}' verified"
                : $" | crossing transfer={crossing.TransferId} '{crossing.Source}' -> '{crossing.Target}' NOT VERIFIED ({crossing.Reason})"));
        }

        return text.ToString();
    }
    /// <summary>Pairs every crossing the tapes of one set carry. A departure is a source tape's committed transfer;
    /// an arrival is a destination tape's landed cohort; the two halves share the source authority and the
    /// source-scoped transfer id.</summary>
    /// <param name="tapes">Every tape in the set with its own verdict.</param>
    /// <returns>Every crossing's verdict: each departure in tape order, then each arrival no departure claimed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tapes"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldReplayCrossingVerdict> PairCrossings(IReadOnlyList<(WorldReplaySnapshot Tape, WorldReplayVerdict Verdict)> tapes) {
        ArgumentNullException.ThrowIfNull(argument: tapes);

        var crossings = new List<WorldReplayCrossingVerdict>();
        var paired = new HashSet<(string Source, ulong TransferId)>();

        int Find(string authority) {
            for (var index = 0; (index < tapes.Count); index++) {
                if (string.Equals(
                    a: tapes[index].Tape.Authority,
                    b: authority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return index;
                }
            }
            return -1;
        }
        static bool Carries(WorldReplaySnapshot tape, Func<WorldReplayEntry, bool> match) =>
            tape.Ticks.Any(predicate: tick => tick.Authority.Any(predicate: match));

        foreach (var (tape, verdict) in tapes) {
            foreach (var tick in tape.Ticks) {
                foreach (var entry in tick.Authority) {
                    if (entry is not WorldReplayEntry.Transfer { DepartedSlots.Count: > 0 } departure) {
                        continue;
                    }

                    var key = (tape.Authority, departure.TransferId);
                    var target = Find(authority: departure.Target);
                    string reason;

                    if (departure.TargetRemote) {
                        reason = $"its arrival is on remote authority '{departure.Target}', whose tape is not in this set";
                    } else if (target < 0) {
                        reason = $"its arrival is on authority '{departure.Target}', which no tape in this set recorded";
                    } else if (!Carries(
                        match: candidate => ((candidate is WorldReplayEntry.Arrival arrival) && (arrival.TransferId == departure.TransferId) && string.Equals(
                            a: arrival.SourceAuthority,
                            b: tape.Authority,
                            comparisonType: StringComparison.Ordinal
                        )),
                        tape: tapes[target].Tape
                    )) {
                        reason = $"the tape of '{departure.Target}' holds no arrival for it";
                    } else if (!verdict.Match || !tapes[target].Verdict.Match) {
                        reason = "a tape holding one of its halves does not replay to the state its authority reached";
                    } else {
                        reason = string.Empty;
                    }

                    _ = paired.Add(item: key);
                    crossings.Add(item: new WorldReplayCrossingVerdict(
                        Reason: reason,
                        Source: tape.Authority,
                        Target: departure.Target,
                        TransferId: departure.TransferId,
                        Verified: (reason.Length == 0)
                    ));
                }
            }
        }

        foreach (var (tape, _) in tapes) {
            foreach (var tick in tape.Ticks) {
                foreach (var entry in tick.Authority) {
                    if (
                        (entry is not WorldReplayEntry.Arrival arrival) ||
                        paired.Contains(item: (arrival.SourceAuthority, arrival.TransferId))
                    ) {
                        continue;
                    }

                    var source = Find(authority: arrival.SourceAuthority);

                    crossings.Add(item: new WorldReplayCrossingVerdict(
                        Reason: ((source < 0)
                            ? $"its departure is on authority '{arrival.SourceAuthority}', whose tape is not in this set"
                            : $"the tape of '{arrival.SourceAuthority}' holds no departure for it"),
                        Source: arrival.SourceAuthority,
                        Target: tape.Authority,
                        TransferId: arrival.TransferId,
                        Verified: false
                    ));
                }
            }
        }

        return crossings;
    }
}
