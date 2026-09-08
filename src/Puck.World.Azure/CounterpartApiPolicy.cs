namespace Puck.World.Azure;

// Shared API identity and request budget for counterpart publication and resolution.
internal static class CounterpartApiPolicy {
    /// <summary>The platform API's exposed-scope request — read from the app registration's client id
    /// (<c>e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7</c>, <c>src/Puck.Azure.Functions/configuration.json</c>'s own audience);
    /// this repository carries no independent record of the App ID URI, so this is asserted from that client id per
    /// the standard <c>api://{clientId}/{scope}</c> exposed-API convention, not independently verified against a
    /// live app registration.</summary>
    internal const string Scope = "api://e6a7ab9f-19af-4eb0-b23f-a5bde0f90eb7/user_impersonation";

    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);
}
