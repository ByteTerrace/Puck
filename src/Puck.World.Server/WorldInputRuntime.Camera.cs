using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    // The proposed seat state is already installed for the ordinary application's tuple preflight. Compose and
    // Dissolve validate before writing; their single route barrier publishes that state and route together.
    private bool TrySetCameraRoute(Owner owner, Principal actor, bool camera, ulong activation, out string reason) {
        reason = "input.mode: actor cannot compose this local seat's camera application";
        if (owner.Principal.Kind != PrincipalKind.Seat || owner.Body != owner.Principal.Index ||
            owner.Body < 0 || owner.Body >= m_host.Population.LocalSeatCount ||
            !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(owner.Body)).IsAllowed) {
            return false;
        }
        var openedScope = !m_observingEvaluation;
        if (openedScope) { BeginActivationScope(); }
        ObserveActivation(owner.Principal, activation);
        try {
            if (!camera) {
                if (m_host.Engagement.Dissolve(owner.Body, actor, owner.Principal) == ControlOutcome.Denied) { return false; }
                reason = string.Empty;
                return true;
            }
            var placement = $"{WorldSeatModeState.CameraPlacementIdPrefix}{owner.Principal.Index}";
            var cameraBody = -1;
            for (var index = 0; index < m_host.Population.Capacity; index++) {
                if (m_host.Population.InhabitantPlacementId(index) == placement && m_host.Body(index) is not null) {
                    cameraBody = index;
                    break;
                }
            }
            if (cameraBody < 0) { reason = $"input.mode: no live camera body at '{placement}'"; return false; }
            var target = GrantSubject.Body(cameraBody);
            if (!m_host.CheckEngagePolicy(owner.Body, target, out reason) ||
                !m_host.Engagement.Compose(owner.Body, target, true, actor, owner.Principal)) {
                if (reason.Length == 0) { reason = "input.mode: camera application or resulting tuple refused"; }
                return false;
            }
            // Same local-seat admission grant as player.camera, with the same lifetime. Its inhabited placement
            // has no fixed body index a document could grant. Refused compositions never install this grant.
            m_host.Grant(new(owner.Principal, WorldCapability.Drive, target, false), Principal.Console);
            reason = string.Empty;
            return true;
        } finally {
            if (openedScope) { EndActivationScope(); }
        }
    }
}
