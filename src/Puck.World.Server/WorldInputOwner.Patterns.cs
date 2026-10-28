using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One consumed event in an authored consumption group.</summary>
public readonly record struct WorldInputClaim(int Group, ulong Event);
public sealed partial class WorldInputOwner {
    private readonly CompiledWorldInputPattern[] m_patterns;
    private readonly ulong[][] m_claimed;
    private readonly int[] m_matchPaths;
    private readonly bool[] m_matchValid;
    private readonly int m_matchWidth;
    private readonly int[] m_matchNext;
    private readonly int[] m_matchPrevious;
    private readonly WorldInputMatch[] m_matchCache;
    private readonly bool[] m_matchCached;

    private ulong m_matchTick;

    /// <summary>Number of predicate checks performed by bounded history matching.</summary>
    public ulong MatchWork { get; private set; }

    /// <summary>Finds the earliest ending match, then latest start, then lexicographic event identities.</summary>
    public bool TryMatch(int patternIndex, WorldInputInterval now, out WorldInputMatch match) {
        match = default;
        if ((((uint)patternIndex) >= ((uint)m_patterns.Length)) || (m_count == 0)) { return false; }
        if (m_matchTick != now.Tick) { Array.Clear(array: m_matchCached); m_matchTick = now.Tick; }
        if (m_matchCached[patternIndex]) { match = m_matchCache[patternIndex]; return (match.First != 0); }
        var found = MatchCore(match: out match, now: now, patternIndex: patternIndex);

        m_matchCache[patternIndex] = match;
        m_matchCached[patternIndex] = true;
        return found;
    }

    private bool MatchCore(int patternIndex, WorldInputInterval now, out WorldInputMatch match) {
        match = default;
        var pattern = m_patterns[patternIndex];

        foreach (var held in pattern.Held) { if (!TryHeld(channel: held, hold: out _)) { return false; } }
        Span<WorldInputMatch> priorities = stackalloc WorldInputMatch[64];
        var priorityCount = 0;

        for (var higher = 0; (higher < patternIndex); higher++) {
            if ((m_patterns[higher].Group == pattern.Group) && TryMatch(match: out var priority, now: now, patternIndex: higher)) { priorities[priorityCount++] = priority; }
        }
        Array.Clear(array: m_matchValid);
        Span<int> group = stackalloc int[16];
        Span<int> candidate = stackalloc int[16];
        var width = 0;

        for (var stageIndex = 0; (stageIndex < pattern.Stages.Length); stageIndex++) {
            var stage = pattern.Stages[stageIndex];
            var priorWidth = width;

            width += stage.Terms.Length;
            for (var term = 0; (term < stage.Terms.Length); term++) {
                var last = -1;
                var offset = (term * m_history.Length);

                for (var index = 0; (index < m_count); index++) {
                    var record = Record(index: index);
                    var eligible = Eligible(record, pattern.Group);

                    for (var priority = 0; (eligible && (priority < priorityCount)); priority++) {
                        if ((record.Id >= priorities[priority].First) && (record.Id <= priorities[priority].Last)) { eligible = false; }
                    }
                    if (eligible && Matches(stage.Terms[term], record)) { last = index; }
                    m_matchPrevious[(offset + index)] = last;
                }
                var next = -1;

                for (var index = (m_count - 1); (index >= 0); index--) {
                    if (m_matchPrevious[(offset + index)] == index) { next = index; }
                    m_matchNext[(offset + index)] = next;
                }
            }
            for (var end = 0; (end < m_count); end++) {
                var ending = Record(index: end);

                if (!Eligible(ending, pattern.Group)) { continue; }
                var endingTerm = -1;

                for (var term = 0; (term < stage.Terms.Length); term++) { if (m_matchPrevious[((term * m_history.Length) + end)] == end) { endingTerm = term; break; } }
                if (endingTerm < 0) { continue; }
                var endTime = ending.Time(policy: m_policy.Judgement);
                var state = ((stageIndex * m_history.Length) + end);

                for (var previous = ((stageIndex == 0) ? -1 : 0); (previous < ((stageIndex == 0) ? 0 : end)); previous++) {
                    var priorState = (((stageIndex - 1) * m_history.Length) + previous);

                    if ((previous >= 0) && !m_matchValid[priorState]) { continue; }
                    var minimumTime = (endTime - stage.Tolerance);
                    var minimumTick = ((previous < 0) ? 0 : Record(index: previous).Admission.Tick);

                    if ((ulong.MaxValue - minimumTick) < stage.MinimumTicks) { continue; }
                    minimumTick += stage.MinimumTicks;
                    if (previous < 0) {
                        // The first stage maximizes its earliest timestamp. Predicates are disjoint,
                        // so each can independently supply its latest event before this fixed end.
                        var latestStart = endTime;

                        for (var term = 0; (term < stage.Terms.Length); term++) {
                            if (term == endingTerm) { continue; }
                            var last = ((end == 0) ? -1 : m_matchPrevious[(((term * m_history.Length) + end) - 1)]);

                            if ((last < 0) || (Record(index: last).Time(policy: m_policy.Judgement) < minimumTime)) { latestStart = FixedQ4816.MinValue; break; }
                            var time = Record(index: last).Time(policy: m_policy.Judgement);

                            if (time < latestStart) { latestStart = time; }
                        }
                        if (latestStart == FixedQ4816.MinValue) { continue; }
                        if (latestStart > minimumTime) { minimumTime = latestStart; }
                    }
                    var complete = true;
                    var lower = (previous + 1);
                    var upper = end;

                    while (lower < upper) {
                        MatchWork++;
                        var middle = (lower + ((upper - lower) / 2));
                        var record = Record(index: middle);

                        if ((record.Admission.Tick < minimumTick) || (record.Time(policy: m_policy.Judgement) < minimumTime)) { lower = (middle + 1); } else { upper = middle; }
                    }
                    for (var term = 0; (term < stage.Terms.Length); term++) {
                        group[term] = ((term == endingTerm) ? end : -1);
                        if (term == endingTerm) { continue; }
                        var next = m_matchNext[((term * m_history.Length) + lower)];

                        if ((next >= lower) && (next < end)) { group[term] = next; }
                        if (group[term] < 0) { complete = false; break; }
                    }
                    if (!complete || (ending.Admission.Tick < minimumTick)) { continue; }
                    group[..stage.Terms.Length].Sort();
                    var first = Record(index: group[0]);

                    if (previous >= 0) {
                        var prior = Record(index: previous);
                        var gap = (first.Admission.Tick - prior.Admission.Tick);

                        if (((first.Time(policy: m_policy.Judgement) - prior.Time(policy: m_policy.Judgement)) > pattern.Gap) ||
                            ((stage.MaximumTicks is { } maxTicks) && (gap > maxTicks))) { continue; }
                        m_matchPaths.AsSpan(length: priorWidth, start: (priorState * m_matchWidth)).CopyTo(destination: candidate);
                    }
                    group[..stage.Terms.Length].CopyTo(destination: candidate[priorWidth..]);
                    if ((endTime - Record(index: candidate[0]).Time(policy: m_policy.Judgement)) > pattern.Duration) { continue; }
                    var destination = m_matchPaths.AsSpan(length: width, start: (state * m_matchWidth));

                    if (!m_matchValid[state] || Better(candidate: candidate[..width], previous: destination)) {
                        candidate[..width].CopyTo(destination: destination);
                        m_matchValid[state] = true;
                    }
                }
                if ((stageIndex != (pattern.Stages.Length - 1)) || !m_matchValid[state]) { continue; }
                if ((pattern.BufferTicks is { } buffer) && ((now.Tick < ending.Admission.Tick) || ((now.Tick - ending.Admission.Tick) > buffer))) { continue; }
                var start = Record(index: m_matchPaths[(state * m_matchWidth)]);

                match = new(Epoch, start.Id, ending.Id, start.Admission.Tick, ending.Admission.Tick, start.Time(policy: m_policy.Judgement), endTime);
                return true;
            }
        }
        return false;
    }

    /// <summary>Consumes the exact range selected at preflight; call only after its rule transaction commits.</summary>
    public bool Consume(int pattern, WorldInputMatch match) {
        if ((((uint)pattern) >= ((uint)m_patterns.Length)) || (match.Epoch > Epoch) || (match.First == 0) || (match.Last < match.First) || (match.Last > m_eventId)) { return false; }
        var claims = m_claimed[m_patterns[pattern].Group];

        Array.Clear(array: m_matchCached);
        var found = false;

        for (var index = 0; (index < m_count); index++) {
            var record = Record(index: index);

            if ((record.Epoch != match.Epoch) || (record.Id < match.First) || (record.Id > match.Last)) { continue; }
            claims[((int)(record.Id % ((ulong)claims.Length)))] = record.Id;
            found = true;
        }
        for (var address = 0; (address < m_holds.Length); address++) {
            var hold = m_holds[address];

            if ((hold.Press >= match.First) && (hold.Press <= match.Last)) { m_holds[address] = hold with { Consumed = true }; found = true; }
        }
        return found;
    }
    /// <summary>Whether two prepared effects compete for one authored consumption group.</summary>
    public bool SharesConsumption(int first, int second) => (m_patterns[first].Group == m_patterns[second].Group);

    private bool Better(ReadOnlySpan<int> candidate, ReadOnlySpan<int> previous) {
        var time = Record(index: candidate[0]).Time(policy: m_policy.Judgement).CompareTo(other: Record(index: previous[0]).Time(policy: m_policy.Judgement));

        if (time != 0) { return (time > 0); }
        return (candidate.SequenceCompareTo(other: previous) < 0);
    }
    private bool Eligible(WorldInputRecord record, int group) => ((record.Epoch == Epoch) &&
        (m_claimed[group][((int)(record.Id % ((ulong)m_history.Length)))] != record.Id));
    private bool Matches(WorldInputPatternTerm term, WorldInputRecord record) {
        MatchWork++;
        return ((record.Event.Channel == term.Channel) && (record.Event.Kind == term.Kind) && (record.Crossing == term.Crossing) &&
            ((term.RawValue is null) || (term.RawValue == record.Event.RawValue)));
    }
}
