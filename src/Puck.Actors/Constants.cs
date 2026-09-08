namespace Puck.Actors;

public static class Constants
{
    /// <summary>
    /// Partition 0 (bytrcstp001) is the anchor: Front Door's only blob origin. Every user's
    /// public/ content lives in their oid-named container THERE regardless of home partition,
    /// so published URLs survive migrations and Front Door never needs per-partition routing.
    /// </summary>
    public const int AnchorPartition = 0;
    public const string ClientAssertionCredentialKey = "ClientAssertionCredential";
    public const string InternalApiPolicyName = "InternalApi";
    public const string UserStateStorageName = "user-state";
}

