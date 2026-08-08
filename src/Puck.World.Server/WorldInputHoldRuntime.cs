using System.Text;
using Puck.World.Protocol;

namespace Puck.World.Server;

internal readonly record struct WorldSubmittedInput(bool HasIntent, PlayerIntent Intent, PlayerIntent HeldChannels);

/// <summary>Applies the authored and measured participant input holds before submitted input reaches body simulation.</summary>
internal sealed class WorldInputHoldRuntime {
    private readonly ParticipantState[] m_participants;
    private readonly int[] m_authored;
    private readonly bool[] m_equalized;
    private WorldInputHoldSettings m_settings;
    private int m_maximumSetter = -1;

    public WorldInputHoldRuntime(WorldInputHoldSettings settings, int capacity) {
        m_participants = new ParticipantState[capacity];
        m_authored = new int[capacity];
        m_equalized = new bool[capacity];
        for (var index = 0; (index < m_participants.Length); index++) {
            m_participants[index] = new ParticipantState();
        }

        Reconfigure(settings: settings);
    }

    public void Reconfigure(WorldInputHoldSettings settings) {
        m_settings = settings;
        Array.Fill(array: m_authored, value: settings.DefaultTicks);
        Array.Fill(array: m_equalized, value: settings.EqualizeByDefault);

        foreach (var participant in settings.Participants) {
            if ((uint)participant.BodyIndex >= (uint)m_authored.Length) {
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

    public void PrepareParticipants(WorldPopulation population) {
        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!population.IsHumanOccupied(bodyIndex: bodyIndex)) {
                state.Reset();

                continue;
            }

            var principal = ParticipantPrincipal(population: population, bodyIndex: bodyIndex);

            if (!state.Active || (state.Principal != principal)) {
                state.Reset();
                state.Active = true;
                state.Principal = principal;
            }
        }
    }

    public void ObserveMeasurement(in IntentSubmission submission) {
        if (!IsParticipantOwnSubmission(submission: in submission)) {
            return;
        }

        var state = m_participants[submission.EntityIndex];

        if (state.Active && (state.Principal == submission.Principal)) {
            state.Measured = Math.Clamp(value: submission.MeasuredHoldTicks, min: 0, max: m_settings.CeilingTicks);
        }
    }

    public void Apply(WorldPopulation population) {
        var maximum = 0;
        m_maximumSetter = -1;

        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!state.Active) {
                continue;
            }

            state.Target = Math.Min(val1: m_settings.CeilingTicks, val2: Math.Max(val1: m_authored[bodyIndex], val2: state.Measured));

            if (m_equalized[bodyIndex] && (state.Target > maximum)) {
                maximum = state.Target;
                m_maximumSetter = bodyIndex;
            }
        }

        for (var bodyIndex = 0; (bodyIndex < m_participants.Length); bodyIndex++) {
            var state = m_participants[bodyIndex];

            if (!state.Active || (population.EntryBody(index: bodyIndex) is not { } body)) {
                continue;
            }

            var target = (m_equalized[bodyIndex] ? maximum : state.Target);

            state.MoveApplied(target: target, lowerAfterTicks: m_settings.LowerAfterTicks);

            var current = body.TakeSubmittedInput();
            var selected = state.PushAndSelect(current: current, ticksAgo: state.Applied, ceilingTicks: m_settings.CeilingTicks);

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
            result.Append(value: m_equalized[bodyIndex] ? " equalized" : " independent");
            result.Append(value: ';');
        }

        if (!any) {
            result.Append(value: " none;");
        }

        result.Append(value: " maximum=");
        result.Append(value: ((m_maximumSetter >= 0) ? m_participants[m_maximumSetter].Principal.Describe() : "none"));
        result.Append(value: " ceiling=");
        result.Append(value: m_settings.CeilingTicks);
        result.Append(value: " lower-after=");
        result.Append(value: m_settings.LowerAfterTicks);
        result.Append(value: ']');

        return result.ToString();
    }

    private bool IsParticipantOwnSubmission(in IntentSubmission submission) =>
        ((submission.EntityIndex >= 0) && (submission.EntityIndex < m_participants.Length) &&
         (((submission.Principal.Kind == PrincipalKind.Seat) && (submission.Principal.Index == submission.EntityIndex)) ||
          ((submission.Principal.Kind == PrincipalKind.Peer) && (submission.Principal.Index == submission.EntityIndex))));

    private static WorldPrincipal ParticipantPrincipal(WorldPopulation population, int bodyIndex) =>
        ((bodyIndex < WorldPopulation.LocalSeatCount)
            ? WorldPrincipal.Seat(slot: bodyIndex)
            : population.PeerPrincipal(index: bodyIndex));

    private sealed class ParticipantState {
        private readonly List<WorldSubmittedInput> m_history = [];
        private int m_historyStart;
        private int m_lowerTarget = -1;
        private int m_lowerStableTicks;

        public bool Active { get; set; }
        public WorldPrincipal Principal { get; set; }
        public int Measured { get; set; }
        public int Target { get; set; }
        public int Applied { get; private set; }

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

            while (((long)m_history.Count - m_historyStart) > ((long)ceilingTicks + 1L)) {
                m_historyStart++;
            }

            var selectedIndex = (m_history.Count - 1 - ticksAgo);
            var selected = ((selectedIndex >= m_historyStart) ? m_history[selectedIndex] : default);

            if ((m_historyStart >= 256) && (m_historyStart >= (m_history.Count / 2))) {
                m_history.RemoveRange(index: 0, count: m_historyStart);
                m_historyStart = 0;
            }

            return selected;
        }
    }
}
