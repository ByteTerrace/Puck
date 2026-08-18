using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

public readonly record struct WorldSubmittedInput(bool HasIntent, PlayerIntent Intent, PlayerIntent HeldChannels);
/// <summary>Applies the authored and measured participant input holds before submitted input reaches body simulation.</summary>
public sealed class WorldInputHoldRuntime {
    private readonly int[] m_authored;
    private readonly bool[] m_equalized;
    private readonly ParticipantState[] m_participants;

    private int m_maximumSetter = -1;
    private WorldInputHoldSettings m_settings;

    public WorldInputHoldRuntime(WorldInputHoldSettings settings, int capacity) {
        m_participants = new ParticipantState[capacity];
        m_authored = new int[capacity];
        m_equalized = new bool[capacity];
        for (var index = 0; (index < m_participants.Length); index++) {
            m_participants[index] = new ParticipantState();
        }

        Reconfigure(settings: settings);
    }

    private bool IsParticipantOwnSubmission(in IntentSubmission submission) =>
        ((submission.EntityIndex >= 0) && (submission.EntityIndex < m_participants.Length) &&
         (((submission.Principal.Kind == PrincipalKind.Seat) && (submission.Principal.Index == submission.EntityIndex)) ||
          ((submission.Principal.Kind == PrincipalKind.Peer) && (submission.Principal.Index == submission.EntityIndex))));
    private static WorldPrincipal ParticipantPrincipal(WorldPopulation population, int bodyIndex) =>
        ((bodyIndex < population.LocalSeatCount)
            ? WorldPrincipal.Seat(slot: bodyIndex)
            : population.PeerPrincipal(index: bodyIndex)
        );

    public void Apply(WorldPopulation population) {
        var maximum = 0;

        m_maximumSetter = -1;

        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!state.Active) {
                continue;
            }

            state.Target = Math.Min(
                val1: m_settings.CeilingTicks,
                val2: Math.Max(
                    val1: m_authored[bodyIndex],
                    val2: state.Measured
                )
            );

            if (
                m_equalized[bodyIndex] &&
                (state.Target > maximum)
            ) {
                maximum = state.Target;
                m_maximumSetter = bodyIndex;
            }
        }

        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (
                !state.Active ||
                (population.EntryBody(index: bodyIndex) is not { } body)
            ) {
                continue;
            }

            var target = (m_equalized[bodyIndex]
                ? maximum
                : state.Target
            );

            state.MoveApplied(
                target: target,
                lowerAfterTicks: m_settings.LowerAfterTicks
            );

            var current = body.TakeSubmittedInput();
            var selected = state.PushAndSelect(
                current: current,
                ticksAgo: state.Applied,
                ceilingTicks: m_settings.CeilingTicks
            );

            body.RestoreSubmittedInput(input: in selected);
        }
    }
    public string Describe() {
        var result = new StringBuilder(value: "[world.input-holds:");
        var any = false;

        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!state.Active) {
                continue;
            }

            any = true;
            result.Append(value: ' ');
            result.Append(value: state.Principal.Describe());
            result.Append(value: " authored=");
            result.Append(value: m_authored[bodyIndex]);
            result.Append(value: " measured=");
            result.Append(value: state.Measured);
            result.Append(value: " applied=");
            result.Append(value: state.Applied);
            result.Append(value: (m_equalized[bodyIndex]
                ? " equalized"
                : " independent"));
            result.Append(value: ';');
        }

        if (!any) {
            result.Append(value: " none;");
        }

        result.Append(value: " maximum=");
        result.Append(value: ((m_maximumSetter >= 0)
            ? m_participants[m_maximumSetter].Principal.Describe()
            : "none"));
        result.Append(value: " ceiling=");
        result.Append(value: m_settings.CeilingTicks);
        result.Append(value: " lower-after=");
        result.Append(value: m_settings.LowerAfterTicks);
        result.Append(value: ']');

        return result.ToString();
    }
    public void ObserveMeasurement(in IntentSubmission submission) {
        if (!IsParticipantOwnSubmission(submission: in submission)) {
            return;
        }

        var state = m_participants[submission.EntityIndex];

        if (
            state.Active &&
            (state.Principal == submission.Principal)
        ) {
            state.Measured = Math.Clamp(
                value: submission.MeasuredHoldTicks,
                min: 0,
                max: m_settings.CeilingTicks
            );
        }
    }
    public void PrepareParticipants(WorldPopulation population) {
        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!population.IsHumanOccupied(bodyIndex: bodyIndex)) {
                state.Reset();

                continue;
            }

            var principal = ParticipantPrincipal(
                bodyIndex: bodyIndex,
                population: population
            );

            if (
                !state.Active ||
                (state.Principal != principal)
            ) {
                state.Reset();
                state.Active = true;
                state.Principal = principal;
            }
        }
    }
    public void Reconfigure(WorldInputHoldSettings settings) {
        m_settings = settings;
        Array.Fill(
            array: m_authored,
            value: settings.DefaultTicks
        );
        Array.Fill(
            array: m_equalized,
            value: settings.EqualizeByDefault
        );

        foreach (var participant in settings.Participants) {
            if (((uint)participant.BodyIndex) >= ((uint)m_authored.Length)) {
                continue;
            }
            m_authored[participant.BodyIndex] = participant.Ticks;
            m_equalized[participant.BodyIndex] = participant.Equalized;
        }
    }
    public void Reset() {
        foreach (var participant in m_participants) {
            participant.Reset();
        }

        m_maximumSetter = -1;
    }

    /// <summary>One participant slot's checkpointed hold state — see <see cref="Capture"/>.</summary>
    public readonly record struct WorldInputHoldParticipantCheckpoint(
        bool Active,
        WorldPrincipal Principal,
        int Measured,
        int Target,
        int Applied,
        int LowerTarget,
        int LowerStableTicks,
        int HistoryStart,
        IReadOnlyList<WorldSubmittedInput> History
    );
    /// <summary>The runtime's own checkpointed state — see <see cref="Capture"/>.</summary>
    public sealed record WorldInputHoldCheckpoint(int MaximumSetter, IReadOnlyList<WorldInputHoldParticipantCheckpoint> Participants);

    /// <summary>Captures every participant slot's live hold state.</summary>
    public WorldInputHoldCheckpoint Capture() {
        var participants = new WorldInputHoldParticipantCheckpoint[m_participants.Length];

        for (var index = 0; (index < m_participants.Length); index++) {
            participants[index] = m_participants[index].Capture();
        }

        return new WorldInputHoldCheckpoint(
            MaximumSetter: m_maximumSetter,
            Participants: participants
        );
    }
    /// <summary>Restores every participant slot's live hold state from a previously captured checkpoint.</summary>
    public void Restore(WorldInputHoldCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        m_maximumSetter = checkpoint.MaximumSetter;

        var count = Math.Min(
            val1: checkpoint.Participants.Count,
            val2: m_participants.Length
        );

        for (var index = 0; (index < count); index++) {
            m_participants[index].Restore(checkpoint: checkpoint.Participants[index]);
        }
    }

    private sealed class ParticipantState {
        private readonly List<WorldSubmittedInput> m_history = [];

        private int m_historyStart;
        private int m_lowerStableTicks;
        private int m_lowerTarget = -1;

        public bool Active { get; set; }
        public int Applied { get; private set; }
        public int Measured { get; set; }
        public WorldPrincipal Principal { get; set; }
        public int Target { get; set; }

        public WorldInputHoldParticipantCheckpoint Capture() => new(
            Active: Active,
            Principal: Principal,
            Measured: Measured,
            Target: Target,
            Applied: Applied,
            LowerTarget: m_lowerTarget,
            LowerStableTicks: m_lowerStableTicks,
            HistoryStart: m_historyStart,
            History: [.. m_history]
        );
        public void Restore(WorldInputHoldParticipantCheckpoint checkpoint) {
            Active = checkpoint.Active;
            Principal = checkpoint.Principal;
            Measured = checkpoint.Measured;
            Target = checkpoint.Target;
            Applied = checkpoint.Applied;
            m_lowerTarget = checkpoint.LowerTarget;
            m_lowerStableTicks = checkpoint.LowerStableTicks;
            m_historyStart = checkpoint.HistoryStart;
            m_history.Clear();
            m_history.AddRange(collection: checkpoint.History);
        }
        public void MoveApplied(int target, int lowerAfterTicks) {
            if (target > Applied) {
                Applied = target;
                m_lowerTarget = -1;
                m_lowerStableTicks = 0;

                return;
            }
            if (target == Applied) {
                m_lowerTarget = -1;
                m_lowerStableTicks = 0;

                return;
            }

            if (target != m_lowerTarget) {
                m_lowerTarget = target;
                m_lowerStableTicks = 1;
            } else if (m_lowerStableTicks < lowerAfterTicks) {
                m_lowerStableTicks++;
            }

            if (m_lowerStableTicks >= lowerAfterTicks) {
                Applied--;
            }
        }
        public WorldSubmittedInput PushAndSelect(WorldSubmittedInput current, int ticksAgo, int ceilingTicks) {
            m_history.Add(item: current);

            while ((((long)m_history.Count) - m_historyStart) > (((long)ceilingTicks) + 1L)) {
                m_historyStart++;
            }

            var selectedIndex = ((m_history.Count - 1) - ticksAgo);
            var selected = ((selectedIndex >= m_historyStart)
                ? m_history[selectedIndex]
                : default
            );

            if (
                (m_historyStart >= 256) &&
                (m_historyStart >= (m_history.Count / 2))
            ) {
                m_history.RemoveRange(
                    count: m_historyStart,
                    index: 0
                );
                m_historyStart = 0;
            }

            return selected;
        }
        public void Reset() {
            Active = false;
            Principal = default;
            Measured = 0;
            Target = 0;
            Applied = 0;
            m_lowerTarget = -1;
            m_lowerStableTicks = 0;
            m_history.Clear();
            m_historyStart = 0;
        }
    }
}
