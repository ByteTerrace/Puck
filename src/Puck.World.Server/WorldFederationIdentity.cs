using Puck.Networking;

namespace Puck.World.Server;

/// <summary>One row's own federation signing identity: the authenticator it signs outbound frames with and the claim
/// subject it signs as. Every desktop row shares the process authenticator and the boot subject; a hosted row signs
/// under its own key and its own <c>host.authority</c>.</summary>
public readonly record struct WorldFederationIdentity(IAuthenticator Authenticator, string Subject);
