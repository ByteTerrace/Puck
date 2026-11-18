using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldInputRuntime {
    // The proposed seat state is already installed for the ordinary application's tuple preflight. Compose and
    // Dissolve validate before writing; their single route barrier publishes that state and route together.
    private bool TrySetCameraRoute(Owner owner, Principal actor, bool camera, ulong activation, out string reason) {
        reason = "input.mode: actor cannot compose this local seat's camera application";
        if ((owner.Principal.Kind != PrincipalKind.Seat) || (owner.Body != owner.Principal.Index) ||
            (owner.Body < 0) || (owner.Body >= m_host.Population.LocalSeatCount) ||
            !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(index: owner.Body)).IsAllowed) {
            return false;
        }
        var openedScope = !m_observingEvaluation;

        if (openedScope) { BeginActivationScope(); }
        ObserveActivation(activation: activation, principal: owner.Principal);
        try {
            var placement = $"{WorldSeatModeState.CameraPlacementIdPrefix}{owner.Principal.Index}";
            var cameraBody = -1;

            for (var index = 0; (index < m_host.Population.Capacity); index++) {
                if ((m_host.Population.InhabitantPlacementId(index: index) == placement) && (m_host.Body(index: index) is not null)) {
                    cameraBody = index;
                    break;
                }
            }
            if (!camera) {
                // Even a route already removed by an administrative change must validate the new seat tuple.
                if (!TryValidateApplications(owner.Principal, [ControlApplication.OwnBody(owner.Body)], out reason)) { return false; }
                var outcome = m_host.Engagement.Dissolve(actingPrincipal: actor, entityIndex: owner.Body, targetPrincipal: owner.Principal);

                if (outcome == ControlOutcome.Denied) { reason = "input.mode: camera dissolve refused"; return false; }
                if (outcome == ControlOutcome.NotApplied) { ResetRoute(owner.Principal); }
                if (cameraBody >= 0) {
                    _ = m_host.GrantTable.Revoke(owner.Principal, WorldCapability.Drive, GrantSubject.Body(cameraBody), Principal.Console);
                }
                reason = string.Empty;
                return true;
            }
            if (cameraBody < 0) { reason = $"input.mode: no live camera body at '{placement}'"; return false; }
            var target = GrantSubject.Body(index: cameraBody);

            if (!m_host.GrantTable.TryPrepareGrant(new(owner.Principal, WorldCapability.Drive, target, false, Budget: ushort.MaxValue),
                    Principal.Console, out var grant, out reason) ||
                !m_host.CheckEngagePolicy(entityIndex: owner.Body, reason: out reason, target: target) ||
                !m_host.Engagement.Compose(actingPrincipal: actor, entityIndex: owner.Body, exclusive: true, target: target, targetPrincipal: owner.Principal)) {
                if (reason.Length == 0) { reason = "input.mode: camera application or resulting tuple refused"; }
                return false;
            }
            // The local-seat admission grant follows the successful composition. Its inhabited placement
            // has no fixed body index a document could grant. Refused compositions never install this grant.
            grant!.Install();
            reason = string.Empty;
            return true;
        } finally {
            if (openedScope) { EndActivationScope(); }
        }
    }

    // Called while the old document and bodies still stand. Unchanged reloads keep both state and application;
    // a changed input contract or inhabited-placement roster ends the camera through its ordinary finite-state edge.
    internal void PrepareCameraReconfigure(WorldDefinition definition) {
        var current = m_host.Document.Definition;
        if (WorldInputCatalog.SameContract(current, definition) &&
            current.Placements.Where(static row => row.Inhabit is not null).Select(static row => row.Id)
                .SequenceEqual(definition.Placements.Where(static row => row.Inhabit is not null).Select(static row => row.Id))) { return; }
        foreach (var owner in m_ordered.ToArray()) { EndCameraModes(owner); }
    }

    // A crossing and a seat leave end the camera before the body can disappear or its index can be reused.
    internal void DepartBody(int body) {
        if (!m_bodyOwners.TryGetValue(body, out var owners)) { return; }
        foreach (var owner in owners.ToArray()) { EndCameraModes(owner); Reset(owner.Principal); }
    }

    private bool EndCameraModes(Owner owner) {
        if (owner.Controller is not null) { return false; }
        var changed = false;
        foreach (var family in owner.Definition.SeatModes) {
            if (!owner.SeatStates.TryGetValue(family.Name, out var state) ||
                !family.States.Any(row => row.Name == state && row.Target == WorldSeatModeState.CameraTarget)) { continue; }
            if (!TrySetSeatState(owner.Principal, family.Name, family.DefaultState, 0, out var reason, Principal.Console)) {
                throw new InvalidOperationException(reason);
            }
            changed |= state != family.DefaultState;
        }
        return changed;
    }
}
