using Azure;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Puck.Actors.Services;

namespace Puck.Actors.Grains;

[GenerateSerializer]
public sealed class UserGrainState
{
    [Id(id: 0)] public ProvisioningStep CompletedSteps { get; set; }
    [Id(id: 1)] public string Status { get; set; } = ProvisioningStatus.NotOnboarded;
    [Id(id: 2)] public DateTimeOffset? UpdatedAt { get; set; }
    [Id(id: 3)] public string? FaultReason { get; set; }
    [Id(id: 4)] public int RetryAttempts { get; set; }
    [Id(id: 5)] public TokenEscrow? Escrow { get; set; }
    [Id(id: 6)] public string? PublicKeysJson { get; set; }
    // Observability mirror of the container's CanRead/CanWrite metadata (the ABAC source of
    // truth). Default enabled; ABAC always reads the metadata, so any drift can only mislead a
    // reader of this snapshot, never grant access.
    [Id(id: 7)] public bool StorageReadEnabled { get; set; } = true;
    [Id(id: 8)] public bool StorageWriteEnabled { get; set; } = true;
    // Where this user's container actually is. Authoritative — the partitioner says where a user
    // *should* live for the current partition count, but the data only moves when a migration runs,
    // so callers resolve through here. Null until first provisioned.
    [Id(id: 9)] public int? HomePartition { get; set; }
    [Id(id: 10)] public int? MigrationTargetPartition { get; set; }
    [Id(id: 11)] public MigrationStep MigrationSteps { get; set; }
    // Pinned at migration start and kept until completion: after Flipped, HomePartition already
    // points at the destination, so this is the ONLY record of where the drain must run.
    [Id(id: 12)] public int? MigrationSourcePartition { get; set; }
    // Observability mirror of the container's GuestAccess metadata (the ABAC source of truth):
    // the tenant's lever for non-owner access to their public/ prefix.
    [Id(id: 13)] public string GuestAccess { get; set; } = GuestAccessMode.Read;
}

public sealed class UserGrain(
    IDataProtectionProvider dataProtectionProvider,
    ILogger<UserGrain> logger,
    IPartitionResolver partitionResolver,
    [PersistentState(stateName: "user", storageName: Constants.UserStateStorageName)] IPersistentState<UserGrainState> state,
    IUserProvisioningService userProvisioningService
) : Grain, IUserGrain, IRemindable
{
    private const int MaxRetryAttempts = 20;
    private const string MigrationRetryReminderName = "migration-retry";
    private const string RetryReminderName = "provisioning-retry";

    private static readonly TimeSpan MigrationTickDueTime = TimeSpan.FromSeconds(seconds: 1);
    private static readonly TimeSpan MigrationTickPeriod = TimeSpan.FromSeconds(seconds: 2);
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(minutes: 1);
    private static readonly TimeSpan TimerDueTime = TimeSpan.FromSeconds(seconds: 29);
    private static readonly TimeSpan TimerPeriod = TimeSpan.FromSeconds(seconds: 31);

    // Listing position within the current copy step. In-memory only: a silo restart re-enumerates
    // from the front and skips already-copied blobs, which is cheap.
    private string? migrationContinuationToken;
    private IGrainTimer? migrationTimer;
    private IGrainTimer? retryTimer;

    private string UserObjectId => this.GetPrimaryKey().ToString(format: "D");

    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        // A silo restart mid-onboarding: the reminder is durable, but re-arm the fast local
        // timer too so the propagation probe is not stuck at reminder granularity.
        if ((ProvisioningStatus.Onboarding == state.State.Status) && (state.State.Escrow is not null)) {
            ArmRetryTimer();
        }

        // A silo restart mid-migration: host-only steps proceed without an escrow; a user step
        // with a dead escrow pauses cleanly until the next sign-in refreshes it.
        if ((ProvisioningStatus.Migrating == state.State.Status) && (state.State.MigrationTargetPartition is not null)) {
            ArmMigrationTimer();
        }

        return Task.CompletedTask;
    }

    public async Task<ProvisioningState> EnsureProvisionedAsync(TokenEscrow tokenEscrow) {
        if (state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Finalized)) {
            // Already provisioned — but the partition count may have grown since, in which case this
            // sign-in is the opportunity to move the user. Migration can only copy private/ while a
            // live assertion is in hand (the platform is ABAC-denied on private/), so each sign-in
            // deposits a fresh escrow and the migration runs off the request path on a grain timer.
            await StartOrResumeMigrationAsync(tokenEscrow: tokenEscrow);

            return Snapshot();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(argument: tokenEscrow.ProtectedAssertion);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: tokenEscrow.TokenDiscriminator);

        if (DateTimeOffset.UtcNow >= tokenEscrow.ExpiresAt) {
            throw new InvalidOperationException(message: "Escrowed assertion is already expired.");
        }

        state.State.Escrow = tokenEscrow;
        state.State.FaultReason = null;
        state.State.RetryAttempts = 0;
        state.State.Status = ProvisioningStatus.Onboarding;

        await AdvanceAsync(cancellationToken: CancellationToken.None);

        return Snapshot();
    }
    public Task<ProvisioningState> GetProvisioningStateAsync() =>
        Task.FromResult(result: Snapshot());
    public Task<StorageLocation> GetStorageLocationAsync() {
        // Fall back to the computed partition only for a user with no recorded home yet (never
        // provisioned): that is where onboarding will place them.
        var partition = (state.State.HomePartition ?? partitionResolver.GetPartition(userObjectId: this.GetPrimaryKey()));

        return Task.FromResult(result: new StorageLocation(
            BlobEndpoint: partitionResolver.GetBlobEndpoint(partition: partition).ToString(),
            IsMigrating: (state.State.MigrationTargetPartition is not null),
            Partition: partition
        ));
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (RetryReminderName == reminderName) {
            await AdvanceAsync(cancellationToken: CancellationToken.None);
        }
        else if (MigrationRetryReminderName == reminderName) {
            if ((ProvisioningStatus.Migrating == state.State.Status) && (state.State.MigrationTargetPartition is not null)) {
                ArmMigrationTimer();
            }
            else {
                await StopMigrationRetriesAsync();
            }
        }
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken) {
        if (state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Finalized)) {
            await StopRetriesAsync();

            return;
        }

        var escrow = state.State.Escrow;

        if (escrow is null) {
            await FaultAsync(reason: "Escrowed assertion is missing; a new sign-in is required to resume.");

            return;
        }

        if (DateTimeOffset.UtcNow >= escrow.ExpiresAt) {
            state.State.Escrow = null;

            await FaultAsync(reason: "Escrowed assertion expired before directory propagation completed; a new sign-in is required to resume.");

            return;
        }

        try {
            // First contact fixes the home partition to wherever the partitioner points today;
            // afterwards the recorded home only changes by completing a migration.
            state.State.HomePartition ??= partitionResolver.GetPartition(userObjectId: this.GetPrimaryKey());

            var homePartition = state.State.HomePartition!.Value;

            // Identity and storage are independent of each other; run them in parallel.
            var stepTasks = new List<Task>(capacity: 2);

            if (!state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Identity)) {
                stepTasks.Add(item: userProvisioningService.StampIdentityAsync(
                    cancellationToken: cancellationToken,
                    userObjectId: UserObjectId
                ));
            }

            if (!state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Storage)) {
                stepTasks.Add(item: userProvisioningService.CreateStorageAsync(
                    cancellationToken: cancellationToken,
                    partition: homePartition,
                    userObjectId: UserObjectId
                ));

                // Published content is anchored: the user's public/ prefix lives in their container
                // on the Front Door origin partition regardless of home, so that container must
                // exist from day one.
                if (Constants.AnchorPartition != homePartition) {
                    stepTasks.Add(item: userProvisioningService.CreateStorageAsync(
                        cancellationToken: cancellationToken,
                        partition: Constants.AnchorPartition,
                        userObjectId: UserObjectId
                    ));
                }
            }

            if (0 != stepTasks.Count) {
                await Task.WhenAll(tasks: stepTasks);

                state.State.CompletedSteps |= (ProvisioningStep.Identity | ProvisioningStep.Storage);
                state.State.UpdatedAt = DateTimeOffset.UtcNow;

                await state.WriteStateAsync();
            }

            if (!state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Keys)) {
                // This both escrows the keys and verifies ABAC propagation: the OBO write
                // fails with 403 until the directory attribute is visible to storage.
                var publicKeys = await userProvisioningService.EscrowKeysAsync(
                    cancellationToken: cancellationToken,
                    partition: homePartition,
                    userAssertion: escrow.Unprotect(
                        dataProtectionProvider: dataProtectionProvider,
                        userObjectId: UserObjectId
                    ),
                    userObjectId: UserObjectId
                );

                state.State.CompletedSteps |= ProvisioningStep.Keys;
                state.State.PublicKeysJson = publicKeys.ToJsonString();
            }

            await userProvisioningService.FinalizeAsync(
                cancellationToken: cancellationToken,
                partition: homePartition,
                userObjectId: UserObjectId
            );

            if (Constants.AnchorPartition != homePartition) {
                await userProvisioningService.FinalizeAsync(
                    cancellationToken: cancellationToken,
                    partition: Constants.AnchorPartition,
                    userObjectId: UserObjectId
                );
            }

            state.State.CompletedSteps |= ProvisioningStep.Finalized;
            state.State.Escrow = null;
            state.State.FaultReason = null;
            state.State.Status = ProvisioningStatus.Ready;
            state.State.UpdatedAt = DateTimeOffset.UtcNow;

            await state.WriteStateAsync();
            await StopRetriesAsync();

            logger.LogInformation(
                args: UserObjectId,
                message: "User {UserObjectId} provisioning completed."
            );
        }
        catch (CryptographicException e) {
            // The escrow cannot be unprotected (key-ring or application-name mismatch, or an
            // oid that does not match the grain key); retrying cannot fix it.
            state.State.Escrow = null;

            await FaultAsync(reason: $"Escrowed assertion could not be unprotected: {e.Message}");
        }
        catch (Exception e) {
            state.State.RetryAttempts += 1;

            var isPropagationDelay = ((e as RequestFailedException)?.Status is 403);

            if (MaxRetryAttempts <= state.State.RetryAttempts) {
                await FaultAsync(reason: $"Provisioning retry attempts exhausted; last error: {e.Message}");

                return;
            }

            logger.Log(
                args: [UserObjectId, state.State.RetryAttempts,],
                exception: (isPropagationDelay ? null : e),
                logLevel: (isPropagationDelay ? LogLevel.Information : LogLevel.Warning),
                message: "User {UserObjectId} provisioning attempt {RetryAttempts} will be retried."
            );

            state.State.Status = ProvisioningStatus.Onboarding;
            state.State.UpdatedAt = DateTimeOffset.UtcNow;

            await state.WriteStateAsync();
            await this.RegisterOrUpdateReminder(
                dueTime: ReminderPeriod,
                period: ReminderPeriod,
                reminderName: RetryReminderName
            );
            ArmRetryTimer();
        }
    }
    private void ArmMigrationTimer() {
        migrationTimer ??= this.RegisterGrainTimer(
            callback: ExecuteMigrationStepAsync,
            options: new() {
                DueTime = MigrationTickDueTime,
                Period = MigrationTickPeriod,
            }
        );
    }
    private void ArmRetryTimer() {
        retryTimer ??= this.RegisterGrainTimer(
            callback: AdvanceAsync,
            options: new() {
                DueTime = TimerDueTime,
                Period = TimerPeriod,
            }
        );
    }
    /// <summary>
    /// One bounded slice of the migration, driven by a grain timer so long copies never pin a
    /// sign-in request or starve the grain's other callers. Every step is idempotent and recorded
    /// on completion; the guard is the presence of MigrationTargetPartition, never a comparison of
    /// HomePartition against the computed partition — after Flipped those are equal by design.
    /// </summary>
    private async Task ExecuteMigrationStepAsync(CancellationToken cancellationToken) {
        var sourcePartition = state.State.MigrationSourcePartition;
        var targetPartition = state.State.MigrationTargetPartition;

        if ((sourcePartition is null) || (targetPartition is null)) {
            await StopMigrationRetriesAsync();

            return;
        }

        try {
            var steps = state.State.MigrationSteps;

            if (!steps.HasFlag(flag: MigrationStep.Prepared)) {
                await userProvisioningService.CreateStorageAsync(
                    cancellationToken: cancellationToken,
                    partition: targetPartition.Value,
                    userObjectId: UserObjectId
                );
                // Repair whatever a previous cycle may have left on an already-existing container:
                // carry the user's current suspension state onto the destination and thaw it.
                await userProvisioningService.SetStorageAccessAsync(
                    canRead: state.State.StorageReadEnabled,
                    canWrite: state.State.StorageWriteEnabled,
                    cancellationToken: cancellationToken,
                    partition: targetPartition.Value,
                    userObjectId: UserObjectId
                );
                await userProvisioningService.SetStorageFrozenAsync(
                    cancellationToken: cancellationToken,
                    frozen: false,
                    partition: targetPartition.Value,
                    userObjectId: UserObjectId
                );
                await userProvisioningService.SetGuestAccessAsync(
                    cancellationToken: cancellationToken,
                    guestAccess: state.State.GuestAccess,
                    partition: targetPartition.Value,
                    userObjectId: UserObjectId
                );
                await RecordMigrationStepAsync(step: MigrationStep.Prepared);

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.Frozen)) {
                // Freeze writes on the source via the dedicated lever: reads stay up (the copy runs
                // as the user), deletes stay up (the drain runs as the user), and the CanRead/
                // CanWrite suspension levers are untouched.
                await userProvisioningService.SetStorageFrozenAsync(
                    cancellationToken: cancellationToken,
                    frozen: true,
                    partition: sourcePartition.Value,
                    userObjectId: UserObjectId
                );

                migrationContinuationToken = null;

                await RecordMigrationStepAsync(step: MigrationStep.Frozen);

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.SystemCopied)) {
                var batch = await userProvisioningService.CopyPrefixAsHostAsync(
                    cancellationToken: cancellationToken,
                    continuationToken: migrationContinuationToken,
                    prefix: "system/",
                    sourcePartition: sourcePartition.Value,
                    targetPartition: targetPartition.Value,
                    userObjectId: UserObjectId
                );

                migrationContinuationToken = batch.ContinuationToken;

                if (batch.IsCompleted) {
                    await RecordMigrationStepAsync(step: MigrationStep.SystemCopied);
                }

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.PrivateCopied)) {
                var userAssertion = TryGetLiveAssertion();

                if (userAssertion is null) {
                    await PauseMigrationAsync(reason: "escrowed assertion is expired or missing; the next sign-in resumes");

                    return;
                }

                var batch = await userProvisioningService.CopyPrefixAsUserAsync(
                    cancellationToken: cancellationToken,
                    continuationToken: migrationContinuationToken,
                    prefix: "private/",
                    sourcePartition: sourcePartition.Value,
                    targetPartition: targetPartition.Value,
                    userAssertion: userAssertion,
                    userObjectId: UserObjectId
                );

                migrationContinuationToken = batch.ContinuationToken;

                if (batch.IsCompleted) {
                    await RecordMigrationStepAsync(step: MigrationStep.PrivateCopied);
                }

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.Flipped)) {
                // The commit point: everything is at the destination, so the recorded home moves.
                state.State.HomePartition = targetPartition.Value;

                await state.WriteStateAsync();
                // Suspension may have changed mid-copy; restamp the new home from the mirror.
                await userProvisioningService.SetStorageAccessAsync(
                    canRead: state.State.StorageReadEnabled,
                    canWrite: state.State.StorageWriteEnabled,
                    cancellationToken: cancellationToken,
                    partition: targetPartition.Value,
                    userObjectId: UserObjectId
                );
                await RecordMigrationStepAsync(step: MigrationStep.Flipped);

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.SystemDrained)) {
                var batch = await userProvisioningService.DeletePrefixAsHostAsync(
                    cancellationToken: cancellationToken,
                    partition: sourcePartition.Value,
                    prefix: "system/",
                    userObjectId: UserObjectId
                );

                if (batch.IsCompleted) {
                    await RecordMigrationStepAsync(step: MigrationStep.SystemDrained);
                }

                return;
            }

            if (!steps.HasFlag(flag: MigrationStep.PrivateDrained)) {
                var userAssertion = TryGetLiveAssertion();

                if (userAssertion is null) {
                    await PauseMigrationAsync(reason: "escrowed assertion is expired or missing; the next sign-in resumes");

                    return;
                }

                var batch = await userProvisioningService.DeletePrefixAsUserAsync(
                    cancellationToken: cancellationToken,
                    partition: sourcePartition.Value,
                    prefix: "private/",
                    userAssertion: userAssertion,
                    userObjectId: UserObjectId
                );

                if (batch.IsCompleted) {
                    await RecordMigrationStepAsync(step: MigrationStep.PrivateDrained);
                }

                return;
            }

            // Thaw the drained source shell: when the source is the anchor it still holds the
            // user's public/ content and must accept publishes again; elsewhere it is an empty
            // container left ready for a possible future migration back.
            await userProvisioningService.SetStorageFrozenAsync(
                cancellationToken: cancellationToken,
                frozen: false,
                partition: sourcePartition.Value,
                userObjectId: UserObjectId
            );

            state.State.Escrow = null;
            state.State.MigrationSourcePartition = null;
            state.State.MigrationSteps = MigrationStep.None;
            state.State.MigrationTargetPartition = null;
            state.State.Status = ProvisioningStatus.Ready;
            state.State.UpdatedAt = DateTimeOffset.UtcNow;

            await state.WriteStateAsync();
            await StopMigrationRetriesAsync();

            logger.LogInformation(
                args: [UserObjectId, sourcePartition.Value, targetPartition.Value,],
                message: "User {UserObjectId} migration from partition {SourcePartition} to {TargetPartition} completed."
            );
        }
        catch (CryptographicException e) {
            state.State.Escrow = null;

            await PauseMigrationAsync(reason: $"escrowed assertion could not be unprotected: {e.Message}");
        }
        catch (Exception e) {
            state.State.RetryAttempts += 1;

            if (MaxRetryAttempts <= state.State.RetryAttempts) {
                await PauseMigrationAsync(reason: $"retry attempts exhausted; last error: {e.Message}");

                return;
            }

            logger.LogWarning(
                args: [UserObjectId, state.State.MigrationSteps, state.State.RetryAttempts,],
                exception: e,
                message: "User {UserObjectId} migration step after {MigrationSteps} failed (attempt {RetryAttempts}); the timer retries."
            );

            state.State.UpdatedAt = DateTimeOffset.UtcNow;

            await state.WriteStateAsync();
        }
    }
    private async Task FaultAsync(string reason) {
        state.State.FaultReason = reason;
        state.State.Status = ProvisioningStatus.Faulted;
        state.State.UpdatedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();
        await StopRetriesAsync();

        logger.LogError(
            args: [UserObjectId, reason,],
            message: "User {UserObjectId} provisioning faulted: {FaultReason}"
        );
    }
    /// <summary>
    /// Stops driving the migration without abandoning it: the recorded steps and the pinned
    /// source/target stay in place, Status stays Migrating, and the next sign-in (with its fresh
    /// escrow) resumes from the last completed step. Until Flipped the user's home is still the
    /// source, afterwards the destination — they are never unreachable while paused.
    /// </summary>
    private async Task PauseMigrationAsync(string reason) {
        state.State.Status = ProvisioningStatus.Migrating;
        state.State.UpdatedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();
        await StopMigrationRetriesAsync();

        logger.LogWarning(
            args: [UserObjectId, state.State.MigrationSteps, reason,],
            message: "User {UserObjectId} migration paused after {MigrationSteps}: {Reason}"
        );
    }
    private async Task RecordMigrationStepAsync(MigrationStep step) {
        state.State.MigrationSteps |= step;
        state.State.UpdatedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();
    }
    /// <summary>
    /// The tenant's own lever: how much of their public/ prefix non-owners get (None | Read |
    /// ReadWrite; the substrate default is Read). Stamped everywhere their public surface can
    /// exist — home, anchor, and an in-flight migration destination — while the system
    /// CanRead/CanWrite suspension levers remain untouched and always trump this via ABAC.
    /// </summary>
    public async Task<ProvisioningState> SetGuestAccessAsync(string guestAccess) {
        if (!GuestAccessMode.IsValid(value: guestAccess)) {
            throw new InvalidOperationException(message: $"Guest access must be one of: {GuestAccessMode.None}, {GuestAccessMode.Read}, {GuestAccessMode.ReadWrite}.");
        }

        if (!state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Finalized)) {
            throw new InvalidOperationException(message: "Guest access can only be changed for a fully provisioned user.");
        }

        foreach (var partition in GetOwnedPartitions()) {
            await userProvisioningService.SetGuestAccessAsync(
                cancellationToken: CancellationToken.None,
                guestAccess: guestAccess,
                partition: partition,
                userObjectId: UserObjectId
            );
        }

        state.State.GuestAccess = guestAccess;
        state.State.UpdatedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();

        logger.LogInformation(
            args: [UserObjectId, guestAccess],
            message: "User {UserObjectId} guest access set: {GuestAccess}."
        );

        return Snapshot();
    }
    /// <summary>
    /// Every container that is or will be this user's: their home, the anchor holding their
    /// public/ content, and — once it exists — an in-flight migration destination (before
    /// Prepared, that step stamps the mirrors itself).
    /// </summary>
    private HashSet<int> GetOwnedPartitions() {
        var partitions = new HashSet<int> {
            state.State.HomePartition!.Value,
            Constants.AnchorPartition,
        };

        if (state.State.MigrationSteps.HasFlag(flag: MigrationStep.Prepared) &&
            (state.State.MigrationTargetPartition is int migrationTargetPartition)
        ) {
            partitions.Add(item: migrationTargetPartition);
        }

        return partitions;
    }
    public async Task<ProvisioningState> SetStorageAccessAsync(bool canRead, bool canWrite) {
        // Suspension acts on a real container; a user still onboarding has no stable storage to
        // gate, so refuse until provisioning has finalized.
        if (!state.State.CompletedSteps.HasFlag(flag: ProvisioningStep.Finalized)) {
            throw new InvalidOperationException(message: "Storage access can only be changed for a fully provisioned user.");
        }

        foreach (var partition in GetOwnedPartitions()) {
            await userProvisioningService.SetStorageAccessAsync(
                canRead: canRead,
                canWrite: canWrite,
                cancellationToken: CancellationToken.None,
                partition: partition,
                userObjectId: UserObjectId
            );
        }

        state.State.StorageReadEnabled = canRead;
        state.State.StorageWriteEnabled = canWrite;
        state.State.UpdatedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();

        logger.LogInformation(
            args: [UserObjectId, canRead, canWrite],
            message: "User {UserObjectId} storage access set: CanRead={CanRead}, CanWrite={CanWrite}."
        );

        return Snapshot();
    }
    private ProvisioningState Snapshot() =>
        new(
            CompletedSteps: state.State.CompletedSteps,
            FaultReason: state.State.FaultReason,
            GuestAccess: state.State.GuestAccess,
            HomePartition: state.State.HomePartition,
            MigrationSourcePartition: state.State.MigrationSourcePartition,
            MigrationSteps: state.State.MigrationSteps,
            MigrationTargetPartition: state.State.MigrationTargetPartition,
            Status: state.State.Status,
            StorageReadEnabled: state.State.StorageReadEnabled,
            StorageWriteEnabled: state.State.StorageWriteEnabled,
            UpdatedAt: state.State.UpdatedAt
        );
    /// <summary>
    /// Starts a migration if the partitioner now disagrees with the recorded home, or refreshes
    /// the escrow of one already in flight. Returns promptly in every case — the actual work runs
    /// on a grain timer in bounded slices.
    /// </summary>
    private async Task StartOrResumeMigrationAsync(TokenEscrow tokenEscrow) {
        var homePartition = state.State.HomePartition;

        if (homePartition is null) {
            return;
        }

        var isInFlight = (state.State.MigrationTargetPartition is not null);

        if (!isInFlight) {
            var computedPartition = partitionResolver.GetPartition(userObjectId: this.GetPrimaryKey());

            if (computedPartition == homePartition.Value) {
                return;
            }

            // A suspended user's copy would be ABAC-denied at the destination (its metadata
            // mirrors the suspension), so hold off; the first sign-in after reinstatement migrates.
            if (!state.State.StorageReadEnabled || !state.State.StorageWriteEnabled) {
                logger.LogInformation(
                    args: [UserObjectId, computedPartition,],
                    message: "User {UserObjectId} migration to partition {TargetPartition} deferred: storage access is suspended."
                );

                return;
            }

            if (DateTimeOffset.UtcNow >= tokenEscrow.ExpiresAt) {
                return; // A later sign-in will carry a live assertion.
            }

            try {
                _ = tokenEscrow.Unprotect(
                    dataProtectionProvider: dataProtectionProvider,
                    userObjectId: UserObjectId
                );
            }
            catch (CryptographicException e) {
                logger.LogWarning(
                    args: UserObjectId,
                    exception: e,
                    message: "User {UserObjectId} migration skipped: escrow could not be unprotected."
                );

                return;
            }

            state.State.Escrow = tokenEscrow;
            state.State.MigrationSourcePartition = homePartition.Value;
            state.State.MigrationSteps = MigrationStep.None;
            state.State.MigrationTargetPartition = computedPartition;
            state.State.RetryAttempts = 0;
            state.State.Status = ProvisioningStatus.Migrating;

            await state.WriteStateAsync();

            logger.LogInformation(
                args: [UserObjectId, homePartition.Value, computedPartition],
                message: "User {UserObjectId} migrating from partition {SourcePartition} to {TargetPartition}."
            );
        }
        else {
            // Refresh the escrow of an in-flight migration when this sign-in's is usable; an
            // expired or unreadable one changes nothing — host-only steps run without it.
            if (DateTimeOffset.UtcNow < tokenEscrow.ExpiresAt) {
                try {
                    _ = tokenEscrow.Unprotect(
                        dataProtectionProvider: dataProtectionProvider,
                        userObjectId: UserObjectId
                    );

                    state.State.Escrow = tokenEscrow;
                }
                catch (CryptographicException e) {
                    logger.LogWarning(
                        args: UserObjectId,
                        exception: e,
                        message: "User {UserObjectId} migration escrow refresh skipped: escrow could not be unprotected."
                    );
                }
            }

            state.State.RetryAttempts = 0;
            state.State.Status = ProvisioningStatus.Migrating;

            await state.WriteStateAsync();
        }

        await this.RegisterOrUpdateReminder(
            dueTime: ReminderPeriod,
            period: ReminderPeriod,
            reminderName: MigrationRetryReminderName
        );
        ArmMigrationTimer();
    }
    private async Task StopMigrationRetriesAsync() {
        migrationTimer?.Dispose();
        migrationTimer = null;

        var reminder = await this.GetReminder(reminderName: MigrationRetryReminderName);

        if (reminder is not null) {
            await this.UnregisterReminder(reminder: reminder);
        }
    }
    private async Task StopRetriesAsync() {
        retryTimer?.Dispose();
        retryTimer = null;

        var reminder = await this.GetReminder(reminderName: RetryReminderName);

        if (reminder is not null) {
            await this.UnregisterReminder(reminder: reminder);
        }
    }
    private string? TryGetLiveAssertion() {
        var escrow = state.State.Escrow;

        return ((escrow is null) || (DateTimeOffset.UtcNow >= escrow.ExpiresAt))
            ? null
            : escrow.Unprotect(
                dataProtectionProvider: dataProtectionProvider,
                userObjectId: UserObjectId
            );
    }
}

