using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldScopedReplayRuntime {
    private void ValidateCheckpoint(WorldScopedReplayCheckpoint checkpoint) {
        void Refuse(string detail) => throw new InvalidOperationException(message: $"Scoped replay '{Name}' refused: {detail}.");
        if (checkpoint.Name != Name) { Refuse(detail: "scope name differs"); }
        if ((checkpoint.Cursor < 0) || (checkpoint.Cursor > m_source.DurationTicks) || (checkpoint.InputCursor < 0) || (checkpoint.InputCursor > checkpoint.Inputs.Count)) { Refuse(detail: "invalid tape cursor"); }
        if (checkpoint.Recording) {
            if (!checkpoint.Active || checkpoint.Diverged || (checkpoint.InputCursor != 0) || (checkpoint.Hashes.Count != checkpoint.Cursor)) { Refuse(detail: "invalid recording continuation"); }
        } else if ((checkpoint.Hashes.Count != 0) && (checkpoint.Hashes.Count != m_source.DurationTicks)) {
            Refuse(detail: "incomplete hash tape");
        }
        if (checkpoint.Active && ((checkpoint.Start is null) || (checkpoint.Cursor >= m_source.DurationTicks) || (!checkpoint.Recording && (checkpoint.Hashes.Count != m_source.DurationTicks)))) { Refuse(detail: "invalid active tape"); }
        if (!checkpoint.Active && (checkpoint.Cursor != 0) && (checkpoint.Cursor != m_source.DurationTicks)) { Refuse(detail: "invalid inactive tape cursor"); }
        if ((checkpoint.Hashes.Count == 0) && !checkpoint.Recording && ((checkpoint.Inputs.Count != 0) || (checkpoint.Commands.Count != 0) || (checkpoint.Intents.Count != 0) || (checkpoint.Cursor != 0) || (checkpoint.InputCursor != 0) || checkpoint.Diverged)) { Refuse(detail: "inputs without a recording"); }
        if (checkpoint.Start is { } start) {
            if (!start.Rows.SequenceEqual(other: m_orderedRows)) { Refuse(detail: "starting row inventory differs from the declared scope"); }
            if ((checkpoint.Bodies.Count != m_bodies.Count) || !checkpoint.Bodies.Select(selector: body => body.Index).ToHashSet().SetEquals(other: m_bodies)) { Refuse(detail: "starting body inventory differs from the declared scope"); }
            m_server.Arena.ValidateScope(image: start);
            foreach (var body in checkpoint.Bodies) {
                if ((m_server.Body(index: body.Index) is null) || (body.Generation != m_server.Population.Generation(index: body.Index))) { Refuse(detail: "a starting body was replaced"); }
            }
        } else if ((checkpoint.Bodies.Count != 0) || (checkpoint.StartHash != 0) || (checkpoint.Hashes.Count != 0) || (checkpoint.RuleGateHeld.Count != 0) || (checkpoint.InteractionGateHeld.Count != 0)) {
            Refuse(detail: "continuation without a starting image");
        }
        if (!ValidRuleImage(checkpoint: checkpoint)) { Refuse(detail: "rule continuation lies outside the declared scope"); }
        bool Ordered(IEnumerable<int> ticks) {
            var previous = -1;

            foreach (var tick in ticks) {
                if ((tick < previous) || (tick < 0) || (tick >= m_source.DurationTicks) || (checkpoint.Recording && (tick >= checkpoint.Cursor))) { return false; }
                previous = tick;
            }
            return true;
        }
        if (!Ordered(ticks: checkpoint.Inputs.Select(selector: input => input.Tick)) || checkpoint.Inputs.Any(predicate: input => (!Within(mutation: input.Mutation) || !m_source.Seats.Contains(value: input.Mutation.Principal)))) { Refuse(detail: "invalid or unordered cell inputs"); }
        if (!Ordered(ticks: checkpoint.Commands.Select(selector: input => input.Tick)) || checkpoint.Commands.Any(predicate: input =>
            ((input.Command is not (WorldCommand.SnapPose or WorldCommand.PressChannel or WorldCommand.EnqueueSegment or WorldCommand.Stop or WorldCommand.RigidImpulse)) ||
            !m_bodies.Contains(item: input.Command.EntityIndex) || !m_source.Seats.Contains(value: input.Command.Principal)))) { Refuse(detail: "invalid or unordered body commands"); }
        if (!Ordered(ticks: checkpoint.Intents.Select(selector: input => input.Tick)) || checkpoint.Intents.Any(predicate: input => ((input.Intent.Tick != 0) || !m_bodies.Contains(item: input.Intent.EntityIndex) || !m_source.Seats.Contains(value: input.Intent.Principal)))) { Refuse(detail: "invalid or unordered body intents"); }
        if (checkpoint.Active && !checkpoint.Recording && (checkpoint.InputCursor != checkpoint.Inputs.Count(predicate: input => (input.Tick < checkpoint.Cursor)))) { Refuse(detail: "input cursor disagrees with playback"); }
    }
}
