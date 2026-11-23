using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldEngagement {
    /// <summary>Derives, for every live body, the body it rides this tick: the body its participant's control set
    /// names while leaving its own body out, when that body's kit carries a rider (<see cref="WorldMount"/>). It reads
    /// only the grant table's application sets and the population's kits, both of which a checkpoint holds, so an
    /// authority restored between two ticks derives what the live one held. The tick calls it before any body
    /// advances.</summary>
    public void ResolveMounts() {
        for (var index = 0; (index < m_population.Capacity); index++) {
            if (m_population.EntryBody(index: index) is null) {
                continue;
            }

            m_population.SetMount(
                mount: MountOf(index: index),
                rider: index
            );
        }
    }
    /// <summary>Reads the body a participant's body rides under its current control set.</summary>
    /// <param name="index">The participant's 0-based body index.</param>
    /// <returns>The ridden body's index, or -1 when the set keeps the participant's own body, names no body, or names
    /// only bodies whose kits carry no rider.</returns>
    public int MountOf(int index) {
        var applications = m_grants.Applications(principal: PrincipalOf(index: index));
        var mount = -1;

        for (var slot = 0; (slot < applications.Count); slot++) {
            var target = applications[slot].Target;

            if (target.Kind != GrantSubjectKind.Body) {
                continue;
            }
            if (target.Value == index) {
                return -1;
            }
            if ((mount < 0) && (m_population.MountFacet(index: target.Value) is not null)) {
                mount = target.Value;
            }
        }

        return mount;
    }
}
