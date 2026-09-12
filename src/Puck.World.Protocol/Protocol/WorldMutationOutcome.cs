using System.Security.Cryptography;

namespace Puck.World.Protocol;

/// <summary>The typed decision made for one submitted mutation after it reaches the ordered domain.</summary>
public enum WorldMutationDecision : byte {
    /// <summary>The mutation was applied to the authoritative in-memory world.</summary>
    Applied,

    /// <summary>The mutation was refused and did not change authoritative state.</summary>
    Refused,
}

/// <summary>The persistence state reported independently from a mutation's decision.</summary>
public enum WorldMutationPersistenceStatus : byte {
    /// <summary>No durable receipt was requested for this outcome.</summary>
    NotRequested,

    /// <summary>A durable receipt has been requested but its publication has not completed.</summary>
    Pending,

    /// <summary>The outcome and any applied state are durably published.</summary>
    Durable,

    /// <summary>The persistence result is uncertain and must be reconciled from the authority root.</summary>
    RecoveryRequired,
}

/// <summary>The coherent authority publication watermark associated with a durable mutation completion.</summary>
/// <param name="RootSequence">The root publication sequence.</param>
/// <param name="CheckpointOrdinal">The checkpoint ordinal named by the publication, when one exists.</param>
/// <param name="JournalSequence">The captured journal sequence, or <see langword="null"/> for a durable refusal
/// that changed no journal state.</param>
/// <param name="Tick">The simulation tick covered by the durable publication.</param>
public readonly record struct WorldDurableWatermark(
    long RootSequence,
    long? CheckpointOrdinal,
    long? JournalSequence,
    ulong Tick
) {
    /// <summary>Validates the watermark's root, checkpoint, and journal sequence shape.</summary>
    /// <returns><see langword="true"/> when the ordinal is valid.</returns>
    public bool IsValid => RootSequence >= 0 && CheckpointOrdinal is not (< -1) && JournalSequence is not (< 0) &&
        (JournalSequence is null || CheckpointOrdinal is not null);
}

/// <summary>The immutable identity binding an operation id to the actor and canonical payload it submitted.</summary>
/// <param name="OperationId">The caller-minted operation id.</param>
/// <param name="Actor">The exact actor stamped by the ingress door.</param>
/// <param name="PayloadDigest">The lowercase SHA-256 digest of the canonical mutation discriminator and leaf bytes.</param>
public readonly record struct WorldMutationBinding(Guid OperationId, WorldPrincipal Actor, string PayloadDigest) {
    /// <summary>Returns whether all binding fields have their canonical shape.</summary>
    public bool IsValid =>
        OperationId != Guid.Empty &&
        Actor.IsCanonical() &&
        IsSha256Hex(PayloadDigest);

    /// <summary>Returns the actor's stable canonical label and payload digest as one diagnostic token.</summary>
    public override string ToString() => $"{Actor.Describe()}:{PayloadDigest}";

    internal static bool IsSha256Hex(string? value) {
        if (value is null || value.Length != 64) {
            return false;
        }

        foreach (var character in value) {
            if (!Uri.IsHexDigit(character) || char.IsUpper(character)) {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The actor/payload-bound decision and persistence facts returned for one operation.</summary>
/// <param name="OperationId">The operation id from the submission envelope.</param>
/// <param name="Actor">The exact actor stamped by ingress.</param>
/// <param name="PayloadDigest">The lowercase SHA-256 digest of the canonical mutation discriminator and leaf bytes.</param>
/// <param name="Decision">Whether authoritative state changed.</param>
/// <param name="Code">A stable machine-readable decision code.</param>
/// <param name="Detail">Human-readable detail for operators and clients.</param>
/// <param name="AffectedGroupRevision">The affected group's revision, when this operation changes membership.</param>
/// <param name="PersistenceStatus">Durability state independent of <paramref name="Decision"/>.</param>
/// <param name="DurableWatermark">The durable journal watermark, when available.</param>
public readonly record struct WorldMutationOutcome(
    Guid OperationId,
    WorldPrincipal Actor,
    string PayloadDigest,
    WorldMutationDecision Decision,
    string Code,
    string Detail,
    long? AffectedGroupRevision,
    WorldMutationPersistenceStatus PersistenceStatus,
    WorldDurableWatermark? DurableWatermark
) {
    /// <summary>Gets whether the mutation was applied.</summary>
    public bool Applied => Decision == WorldMutationDecision.Applied;

    /// <summary>Gets whether the mutation was refused.</summary>
    public bool Refused => Decision == WorldMutationDecision.Refused;

    /// <summary>Gets the actor/payload binding carried by this outcome.</summary>
    public WorldMutationBinding Binding => new(OperationId, Actor, PayloadDigest);

    /// <summary>Returns whether the outcome's identity, decision, strings, and optional facts are valid.</summary>
    public bool IsValid =>
        Binding.IsValid &&
        Enum.IsDefined(Decision) &&
        Enum.IsDefined(PersistenceStatus) &&
        !string.IsNullOrWhiteSpace(Code) &&
        !Code.Any(char.IsWhiteSpace) &&
        Detail is not null &&
        (AffectedGroupRevision is null || AffectedGroupRevision.Value >= 0) &&
        (PersistenceStatus is not (WorldMutationPersistenceStatus.Durable) || DurableWatermark is { IsValid: true }) &&
        (PersistenceStatus is not (WorldMutationPersistenceStatus.NotRequested or WorldMutationPersistenceStatus.Pending) || DurableWatermark is null) &&
        (DurableWatermark is null || DurableWatermark.Value.IsValid);

    /// <summary>Builds an applied outcome.</summary>
    public static WorldMutationOutcome AppliedOutcome(
        WorldMutationBinding binding,
        string code,
        string detail = "",
        long? affectedGroupRevision = null,
        WorldMutationPersistenceStatus persistenceStatus = WorldMutationPersistenceStatus.NotRequested,
        WorldDurableWatermark? durableWatermark = null
    ) => Create(
        binding,
        WorldMutationDecision.Applied,
        code,
        detail,
        affectedGroupRevision,
        persistenceStatus,
        durableWatermark
    );

    /// <summary>Builds a refused outcome. Refusal remains the decision even when its receipt is durable.</summary>
    public static WorldMutationOutcome RefusedOutcome(
        WorldMutationBinding binding,
        string code,
        string detail,
        long? affectedGroupRevision = null,
        WorldMutationPersistenceStatus persistenceStatus = WorldMutationPersistenceStatus.NotRequested,
        WorldDurableWatermark? durableWatermark = null
    ) => Create(
        binding,
        WorldMutationDecision.Refused,
        code,
        detail,
        affectedGroupRevision,
        persistenceStatus,
        durableWatermark
    );

    private static WorldMutationOutcome Create(
        WorldMutationBinding binding,
        WorldMutationDecision decision,
        string code,
        string detail,
        long? affectedGroupRevision,
        WorldMutationPersistenceStatus persistenceStatus,
        WorldDurableWatermark? durableWatermark
    ) {
        if (!binding.IsValid) {
            throw new ArgumentException("The operation binding is not canonical.", nameof(binding));
        }
        if (string.IsNullOrWhiteSpace(code) || code.Any(char.IsWhiteSpace)) {
            throw new ArgumentException("The decision code must be a stable non-whitespace token.", nameof(code));
        }
        if (!Enum.IsDefined(persistenceStatus)) {
            throw new ArgumentOutOfRangeException(nameof(persistenceStatus));
        }
        if ((affectedGroupRevision is < 0) || (durableWatermark is { IsValid: false })) {
            throw new ArgumentException("Outcome durability and revision facts are malformed.", nameof(durableWatermark));
        }

        return new WorldMutationOutcome(
            binding.OperationId,
            binding.Actor,
            binding.PayloadDigest,
            decision,
            code,
            detail ?? string.Empty,
            affectedGroupRevision,
            persistenceStatus,
            durableWatermark
        );
    }
}

/// <summary>Computes the immutable actor-plus-canonical-mutation binding for an envelope.</summary>
public static class WorldMutationBindingFactory {
    /// <summary>Attempts to bind a stamped mutation envelope without trusting client-supplied assertions.</summary>
    public static bool TryCreate(in SubmissionEnvelope envelope, out WorldMutationBinding binding, out string detail) {
        binding = default;
        detail = string.Empty;

        if (envelope.OperationId == Guid.Empty) {
            detail = "operation id is empty";
            return false;
        }
        if (envelope.Payload is not WorldSubmissionPayload.Mutation mutation) {
            detail = "operation binding requires a mutation payload";
            return false;
        }
        if (mutation.Value.Principal != envelope.Principal) {
            detail = "envelope actor does not match the mutation actor";
            return false;
        }
        if (!envelope.Principal.IsCanonical()) {
            detail = "envelope actor is not canonical";
            return false;
        }
        if (!WorldSubmissionCodec.TryEncode(
            envelope.Payload,
            out var kind,
            out var bytes,
            out var failure
        )) {
            detail = failure.ToString();
            return false;
        }

        // Bind the payload's canonical discriminator as well as its leaf bytes. This keeps two polymorphic leaves
        // that happen to share a byte shape from aliasing one operation identity.
        var canonical = new byte[checked(bytes.Length + sizeof(byte))];
        canonical[0] = (byte)kind;
        bytes.CopyTo(canonical.AsSpan(start: sizeof(byte)));

        binding = new WorldMutationBinding(
            envelope.OperationId,
            envelope.Principal,
            Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()
        );
        return true;
    }
}
