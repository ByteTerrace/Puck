namespace Puck.Actors.Grains;

[Flags]
public enum ProvisioningStep
{
    None = 0,
    Identity = 1,
    Storage = 2,
    Keys = 4,
    Finalized = 8,
}

/// <summary>
/// Steps of a partition migration. Each is idempotent and recorded as it completes, so a silo
/// restart or an expired escrow resumes rather than restarts. The container is not homogeneously
/// user-owned, so the protocol is two-actor: the host moves system/ (it is ABAC-permitted on every
/// non-private path), the user moves private/ (only their escrowed assertion can), and public/
/// never moves at all — published content is anchored to the Front Door origin partition.
/// </summary>
[Flags]
public enum MigrationStep
{
    None = 0,
    /// <summary>Destination container exists with the user's access metadata stamped and Frozen cleared (host).</summary>
    Prepared = 1,
    /// <summary>Source has Frozen=True — user writes are ABAC-blocked; reads and deletes still work.</summary>
    Frozen = 2,
    /// <summary>Every system/ blob is at the destination (host copy; needs no escrow).</summary>
    SystemCopied = 4,
    /// <summary>Every private/ blob is at the destination (user copy via the escrowed assertion).</summary>
    PrivateCopied = 8,
    /// <summary>COMMIT POINT: HomePartition points at the destination.</summary>
    Flipped = 16,
    /// <summary>Source system/ blobs removed (host).</summary>
    SystemDrained = 32,
    /// <summary>Source private/ blobs removed (user).</summary>
    PrivateDrained = 64,
}

public static class ProvisioningStatus
{
    public const string Faulted = "Faulted";
    public const string Migrating = "Migrating";
    public const string NotOnboarded = "NotOnboarded";
    public const string Onboarding = "Onboarding";
    public const string Ready = "Ready";
}

/// <summary>
/// The tenant-facing lever for NON-OWNER access to their container's public/ prefix — written to
/// container metadata and enforced by ABAC. Owners are never gated by it, and the system
/// CanRead/CanWrite suspension levers always trump it. A container with no GuestAccess key
/// behaves as <see cref="Read"/> (the ABAC operators encode that default), so stamping is for
/// legibility, not correctness.
/// </summary>
public static class GuestAccessMode
{
    public const string None = "None";
    public const string Read = "Read";
    public const string ReadWrite = "ReadWrite";

    public static bool IsValid(string? value) =>
        (None == value) || (Read == value) || (ReadWrite == value);
}

[GenerateSerializer]
[Immutable]
public sealed record ProvisioningState(
    [property: Id(id: 0)] ProvisioningStep CompletedSteps,
    [property: Id(id: 1)] string Status,
    [property: Id(id: 2)] DateTimeOffset? UpdatedAt,
    [property: Id(id: 3)] string? FaultReason,
    [property: Id(id: 4)] bool StorageReadEnabled,
    [property: Id(id: 5)] bool StorageWriteEnabled,
    [property: Id(id: 6)] int? HomePartition,
    [property: Id(id: 7)] int? MigrationTargetPartition,
    [property: Id(id: 8)] MigrationStep MigrationSteps,
    [property: Id(id: 9)] int? MigrationSourcePartition,
    [property: Id(id: 10)] string GuestAccess
);

/// <summary>
/// Where a user's container actually lives. This is authoritative: the partitioner computes where a
/// user *should* live for the current partition count, but until a migration completes the data is
/// still in its recorded home, so every storage caller must resolve through here rather than
/// recompute.
/// </summary>
[GenerateSerializer]
[Immutable]
public sealed record StorageLocation(
    [property: Id(id: 0)] int Partition,
    [property: Id(id: 1)] string BlobEndpoint,
    [property: Id(id: 2)] bool IsMigrating
);

/// <summary>
/// The user's assertion, DataProtection-protected by the edge (purpose "users.tokens",
/// sub-purposes [oid, discriminator]); the silo can unprotect it only because it shares
/// the edge's DataProtection application.
/// </summary>
[GenerateSerializer]
[Immutable]
public sealed record TokenEscrow(
    [property: Id(id: 0)] string ProtectedAssertion,
    [property: Id(id: 1)] string TokenDiscriminator,
    [property: Id(id: 2)] DateTimeOffset ExpiresAt
);

public interface IUserGrain : IGrainWithGuidKey
{
    Task<ProvisioningState> EnsureProvisionedAsync(TokenEscrow tokenEscrow);
    Task<ProvisioningState> GetProvisioningStateAsync();
    Task<StorageLocation> GetStorageLocationAsync();
    Task<ProvisioningState> SetGuestAccessAsync(string guestAccess);
    Task<ProvisioningState> SetStorageAccessAsync(bool canRead, bool canWrite);
}

