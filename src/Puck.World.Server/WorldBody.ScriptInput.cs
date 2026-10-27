using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    private readonly Principal[] m_scriptActors = new Principal[ActionLaneCount];
    private readonly Principal[] m_pendingScriptActors = new Principal[ActionLaneCount];
    private readonly Principal[] m_inputScriptActorScratch = new Principal[ActionLaneCount];
    private List<Principal>? m_viewTapeActors;
    private List<Principal>? m_scriptActorScratch;
    private PlayerIntent m_scriptAccepted;

    // The existing tape and lane timers own durations. This pass only admits their current observations and
    // substitutes accepted values before the motion/action fold; a refused script never drives through its history.
    private PlayerIntent AdmitScriptInput(PlayerIntent tape, Principal tapeActor, ulong tick, int body) {
        m_scriptAccepted = default;
        var runtime = m_stateLane?.Input;
        if (runtime is null) { return tape; }
        var actors = m_scriptActorScratch;
        actors?.Clear();
        if (tapeActor != default) { (actors ??= m_scriptActorScratch = []).Add(tapeActor); }
        for (var ordinal = 0; ordinal < ActionLaneCount; ordinal++) {
            if (m_laneTimers[ordinal] == 0) { continue; }
            actors ??= m_scriptActorScratch = [];
            if (!actors.Contains(m_scriptActors[ordinal])) { actors.Add(m_scriptActors[ordinal]); }
        }
        var acceptedTape = default(PlayerIntent);
        if (actors is not null) {
            foreach (var actor in actors) {
                var image = actor == tapeActor ? tape : default;
                for (var ordinal = 0; ordinal < ActionLaneCount; ordinal++) {
                    if (m_laneTimers[ordinal] > 0 && m_scriptActors[ordinal] == actor) {
                        image = image.WithChannel(ordinal, m_channelShapes[ordinal] == ChannelShape.Binary
                            ? m_channelTimerValues[ordinal] >= m_channelThresholds[ordinal] ? FixedQ4816.One : FixedQ4816.Zero
                            : m_channelTimerValues[ordinal]);
                    }
                }
                var accepted = runtime.AdmitScriptSnapshot(actor, body, image, tick);
                if (actor == tapeActor) { acceptedTape = accepted; }
                for (var ordinal = 0; ordinal < ActionLaneCount; ordinal++) {
                    if (m_laneTimers[ordinal] > 0 && m_scriptActors[ordinal] == actor) {
                        m_scriptAccepted = m_scriptAccepted.WithChannel(ordinal, accepted[ordinal]);
                    }
                }
            }
        }
        runtime.CompleteBodyScripts(body, tick);
        return acceptedTape;
    }
}
