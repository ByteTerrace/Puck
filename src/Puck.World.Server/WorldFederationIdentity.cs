using Puck.Networking;

namespace Puck.World.Server;

/// <summary>One row's own federation signing identity: the authenticator it signs outbound frames with and the claim
/// subject it signs as. Every desktop row shares the process authenticator and the boot subject; a hosted row signs
/// under its own key and its own <c>host.authority</c>.</summary>
/// <param name="Authenticator">The application-level authority proof and admission policy.</param>
/// <param name="Subject">The authority claim subject.</param>
/// <param name="Network">The shared peer transport owner; null only for local-only instances.</param>
public readonly record struct WorldFederationIdentity(IAuthenticator Authenticator, string Subject, WorldPeerNetwork? Network = null);
