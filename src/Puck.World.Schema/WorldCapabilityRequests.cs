namespace Puck.World.Protocol;

/// <summary>Manifest reach shared by WASM guests and host-composed extensions. Requests never grant authority.</summary>
public static class WorldCapabilityRequests {
    /// <summary>Tests exact subject reach or an authored wildcard for the requested capability.</summary>
    /// <param name="requests">The manifest; null and empty request nothing.</param>
    /// <param name="capability">The capability being exercised.</param>
    /// <param name="subject">The concrete subject.</param>
    public static bool Contains(IReadOnlyList<WorldCapabilityRequest>? requests, WorldCapability capability, GrantSubject subject) {
        if (requests is null) { return false; }
        for (var index = 0; index < requests.Count; index++) {
            var request = requests[index];
            if (request.Capability == capability && (request.Subject == subject || request.Subject.Kind == GrantSubjectKind.All)) {
                return true;
            }
        }
        return false;
    }
}
