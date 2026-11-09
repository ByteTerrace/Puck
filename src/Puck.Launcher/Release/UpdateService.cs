using Puck.Confinement;
using System.Runtime.InteropServices;
using System.Text.Json;
using Puck.Assets.Documents;

namespace Puck.Launcher.Release;

/// <summary>The result of an <see cref="UpdateService.CheckAsync"/> call.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Manifest">The fetched manifest, when <paramref name="Outcome"/> is <see cref="UpdateCheckOutcome.Available"/> or <see cref="UpdateCheckOutcome.OutsideRollout"/>.</param>
/// <param name="Detail">A human-readable detail — the source's refusal reason, the verifier's refusal reason, or the resolved version/file-count summary.</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseManifest? Manifest, string Detail);
/// <summary>What an <see cref="UpdateService.CheckAsync"/> call found.</summary>
public enum UpdateCheckOutcome {
    /// <summary>The source has nothing newer, or nothing at all, for this channel.</summary>
    UpToDate,
    /// <summary>A newer, verified manifest is available and this install falls inside its rollout percentage.</summary>
    Available,
    /// <summary>A newer, verified manifest exists but this install falls outside its rollout percentage.</summary>
    OutsideRollout,
    /// <summary>The source could not be reached, or the fetched manifest failed to parse or verify.</summary>
    Refused,
}
/// <summary>The result of an <see cref="UpdateService.ApplyAsync"/> call.</summary>
/// <param name="Applied">Whether the pointer swap happened.</param>
/// <param name="Detail">A human-readable detail — the check/stage/apply refusal reason, or the applied version and
/// what it replaced.</param>
public sealed record UpdateApplyOutcome(bool Applied, string Detail);
/// <summary>
/// Ties an <see cref="IReleaseSource"/>, an <see cref="IReleaseVerifier"/>, an <see cref="IUpdateStager"/>, an
/// <see cref="IUpdateApplier"/>, and <see cref="UpdateOptions"/> together for the <c>update.status</c>/
/// <c>update.check</c>/<c>update.apply</c> console surface.
/// </summary>
/// <param name="source">The release transport.</param>
/// <param name="verifier">The signature/replay/version verifier.</param>
/// <param name="stager">Delta-downloads and stages a verified manifest's payload.</param>
/// <param name="applier">Swaps the install's <c>current</c> pointer to a staged version.</param>
/// <param name="options">The resolved operational configuration.</param>
public sealed class UpdateService(IReleaseSource source, IReleaseVerifier verifier, IUpdateStager stager, IUpdateApplier applier, UpdateOptions options) {
    // This is local authorization state, protected by the same install-root ownership as the replay store.
    // Only a successfully reserved sequence may create resumable authorization. A crash between reservation
    // and intent persistence consumes that sequence; it cannot turn a refused reservation into a future install.
    private sealed record InstallIntent(ReleaseManifest Manifest, DateTimeOffset VerifiedAt, string InstalledVersion, string Rid);

    private readonly IUpdateApplier m_applier = applier;
    private readonly UpdateOptions m_options = options;
    private readonly IReleaseSource m_source = source;
    private readonly IUpdateStager m_stager = stager;
    private readonly IReleaseVerifier m_verifier = verifier;

    /// <summary>Gets the resolved operational configuration this service checks against.</summary>
    public UpdateOptions Options => m_options;

    // Fetches without the install lease (a network wait must not hold back the stub), then verifies under a transaction:
    // the caller's when it holds one, otherwise one begun here for verification alone, so a check's revocation merge is
    // serialized with every other writer of the release record.
    private async Task<UpdateCheckResult> FetchAndVerifyAsync(CancellationToken cancellationToken, DateTimeOffset now, InstallTransaction? transaction) {
        var fetch = await m_source.TryGetLatestManifestAsync(
            channel: m_options.Channel,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!fetch.Found) {
            return new UpdateCheckResult(
                Detail: (fetch.RefusalReason ?? "no manifest found"),
                Manifest: null,
                Outcome: UpdateCheckOutcome.UpToDate
            );
        }

        ReleaseManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize<ReleaseManifest>(
                utf8Json: fetch.ManifestBytes,
                options: DocumentJsonOptions.Shared
            )
                ?? throw new JsonException(message: "manifest deserialized to null"));
        } catch (JsonException exception) {
            return new UpdateCheckResult(
                Detail: $"manifest did not parse: {exception.Message}",
                Manifest: null,
                Outcome: UpdateCheckOutcome.Refused
            );
        }

        if (!string.Equals(
            a: manifest.App,
            b: m_options.App,
            comparisonType: StringComparison.Ordinal
        )) {
            return new UpdateCheckResult(
                Detail: $"manifest names app '{manifest.App}', expected '{m_options.App}'",
                Manifest: null,
                Outcome: UpdateCheckOutcome.Refused
            );
        }

        if (!string.Equals(a: manifest.Channel, b: m_options.Channel, comparisonType: StringComparison.Ordinal)) {
            return new UpdateCheckResult(
                Detail: $"manifest names channel '{manifest.Channel}', expected '{m_options.Channel}'",
                Manifest: null,
                Outcome: UpdateCheckOutcome.Refused
            );
        }

        ReleaseVerifyOutcome verified;

        if (transaction is not null) {
            verified = m_verifier.Verify(advanceSequence: false, installedVersion: m_options.InstalledVersion, manifest: manifest, now: now, transaction: transaction);
        } else {
            try {
                using var check = InstallTransaction.Begin(cancellationToken: cancellationToken, installRoot: Path.GetFullPath(path: m_options.CacheRoot), timeout: CheckLeaseWait);

                verified = m_verifier.Verify(advanceSequence: false, installedVersion: m_options.InstalledVersion, manifest: manifest, now: now, transaction: check);
            } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException)) {
                return new UpdateCheckResult(Detail: $"install is busy or unavailable: {error.Message}", Manifest: null, Outcome: UpdateCheckOutcome.Refused);
            }
        }

        if (!verified.Accepted) {
            return new UpdateCheckResult(
                Detail: (verified.RefusalReason ?? "refused"),
                Manifest: null,
                Outcome: UpdateCheckOutcome.Refused
            );
        }

        var installId = (m_options.InstallId ?? ReleaseRolloutBucket.MintOrLoad(cacheRoot: m_options.CacheRoot));

        if (!ReleaseRolloutBucket.IsIncluded(
            installId: installId,
            percent: manifest.Rollout.Percent
        )) {
            return new UpdateCheckResult(
                Detail: $"version {manifest.Version} verified but outside the {manifest.Rollout.Percent}% rollout",
                Manifest: manifest,
                Outcome: UpdateCheckOutcome.OutsideRollout
            );
        }

        var fileCount = manifest.Payloads.Sum(selector: payload => payload.Files.Count);

        return new UpdateCheckResult(
            Detail: $"version {manifest.Version} verified, {manifest.Payloads.Count} rid(s), {fileCount} file(s) total",
            Manifest: manifest,
            Outcome: UpdateCheckOutcome.Available
        );
    }

    /// <summary>Under one <see cref="InstallTransaction"/> on the install root, resumes a pending local install or checks
    /// the tracked channel. Rollout and staging precede replay reservation, then a durable exact-manifest intent; the
    /// release record selects the version only after reservation. Process interruption resumes that intent, even after
    /// signature expiry, using the current trust policy at its original authorization instant. Terminal refusal retires an
    /// intent without lowering replay marks. No network response can replace a resumable pending intent. The filesystem
    /// must protect the install root from untrusted local writers.</summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch/stage.</param>
    /// <returns>Whether a version was applied, and why not.</returns>
    public async Task<UpdateApplyOutcome> ApplyAsync(DateTimeOffset now, CancellationToken cancellationToken) {
        try {
            using var transaction = (InstallTransaction.TryBegin(installRoot: Path.GetFullPath(path: m_options.CacheRoot))
                ?? throw new IOException(message: $"another process holds {InstallLayout.UpdateLockFileName}"));

            return await ApplyUnderLeaseAsync(cancellationToken: cancellationToken, now: now, transaction: transaction).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or PlatformNotSupportedException)) {
            return new UpdateApplyOutcome(Applied: false, Detail: $"install is busy or unavailable; any pending intent is retained: {error.Message}");
        }
    }

    private async Task<UpdateApplyOutcome> ApplyUnderLeaseAsync(InstallTransaction transaction, DateTimeOffset now, CancellationToken cancellationToken) {
        var intentPath = Path.Combine(path1: transaction.RootPath, path2: InstallLayout.InstallIntentFileName);
        var recovering = File.Exists(path: intentPath);
        var rid = CurrentRid();
        InstallIntent intent;

        UpdateApplyOutcome Refusal(string detail, ReleaseDecision decision = ReleaseDecision.TerminalRefusal) {
            // Retirement affects only the intent. The verifier's durable replay watermark is never rewound.
            if (decision == ReleaseDecision.TerminalRefusal) { File.Delete(path: intentPath); }
            return new UpdateApplyOutcome(Applied: false, Detail: detail);
        }

        if (recovering) {
            try {
                intent = (JsonSerializer.Deserialize<InstallIntent>(json: File.ReadAllText(path: intentPath), options: DocumentJsonOptions.Shared)
                    ?? throw new JsonException(message: "install intent deserialized to null"));
            } catch (JsonException error) {
                return Refusal(detail: $"invalid pending install intent: {error.Message}");
            }
            if ((intent.Manifest is null) || string.IsNullOrWhiteSpace(value: intent.InstalledVersion) ||
                (intent.Rid != rid) || (intent.Manifest.App != m_options.App) || (intent.Manifest.Channel != m_options.Channel)) {
                return Refusal(detail: "pending install intent does not match this app, channel or runtime");
            }
            if (intent.VerifiedAt > now) {
                return Refusal(detail: "waiting for the clock to reach the pending install's authorization time", decision: ReleaseDecision.TransientRefusal);
            }
            // A changed current trust policy still refuses the intent. Time alone does not revoke local work
            // already authorized while its signature was valid; no fresh network verification uses this instant.
            // The release record's admission runs here too: an intent whose version was revoked or whose payload was
            // rejected since it was written is refused terminally and retired.
            var resumed = m_verifier.ReserveInstall(manifest: intent.Manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion, transaction: transaction);

            if (!resumed.Accepted) {
                return Refusal(detail: $"pending install refused: {resumed.RefusalReason}", decision: resumed.Decision);
            }
        } else {
            var check = await FetchAndVerifyAsync(cancellationToken: cancellationToken, now: now, transaction: transaction).ConfigureAwait(continueOnCapturedContext: false);

            if (check.Outcome != UpdateCheckOutcome.Available) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"re-verification reported {check.Outcome}: {check.Detail}");
            }
            intent = new InstallIntent(Manifest: check.Manifest!, VerifiedAt: now, InstalledVersion: m_options.InstalledVersion, Rid: rid);
        }

        var manifest = intent.Manifest;
        var current = transaction.Policy.Current;
        var rollbackTarget = transaction.RollbackTarget();
        var alreadyCurrent = (recovering && (current == manifest.Version));

        if (!alreadyCurrent && !string.IsNullOrEmpty(value: current)) {
            if ((manifest.MinimumSupported is { } minimumSupported) &&
                ReleaseVersion.IsStrictlyGreaterThan(left: minimumSupported, right: current)) {
                return Refusal(detail: $"current install '{current}' is below this release's minimumSupported floor '{minimumSupported}'");
            }
            if (!ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: current)) {
                return Refusal(detail: $"version '{manifest.Version}' does not exceed the current install '{current}'");
            }
        }
        if ((!alreadyCurrent && !string.IsNullOrEmpty(value: current) && string.Equals(a: manifest.Version, b: current, comparisonType: StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(value: rollbackTarget) && string.Equals(a: manifest.Version, b: rollbackTarget, comparisonType: StringComparison.OrdinalIgnoreCase))) {
            return Refusal(detail: $"version '{manifest.Version}' collides case-insensitively with an active install directory");
        }

        // Never restage the running directory, including when an interrupted apply already made it current.
        // The applier rehashes it below without modifying its files.
        if (!alreadyCurrent) {
            var staged = await m_stager.StageAsync(cancellationToken: cancellationToken, manifest: manifest, rid: rid).ConfigureAwait(continueOnCapturedContext: false);

            if (!staged.Staged) {
                return new UpdateApplyOutcome(Applied: false, Detail: (staged.RefusalReason ?? "staging refused"));
            }
        }

        if (!recovering) {
            var reserved = m_verifier.ReserveInstall(manifest: manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion, transaction: transaction);

            if (!reserved.Accepted) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"install reservation refused: {reserved.RefusalReason}");
            }
            AtomicFile.WriteAllBytesDurably(bytes: JsonSerializer.SerializeToUtf8Bytes(value: intent, options: DocumentJsonOptions.Shared), path: intentPath);
        }

        var applied = m_applier.Apply(manifest: manifest, recoverInterruptedApply: recovering, rid: rid, transaction: transaction);

        if (!applied.Applied) {
            return Refusal(detail: (applied.RefusalReason ?? "apply refused"));
        }

        // If interrupted before deletion, recovery rehashes the now-current version and completes this cleanup.
        // The replay mark remains, so a later response with the same sequence is still refused.
        File.Delete(path: intentPath);
        return new UpdateApplyOutcome(
            Applied: true,
            Detail: $"version {manifest.Version} applied, replacing {(applied.PreviousVersion ?? "(first install)")} — takes effect at the stub's next launch"
        );
    }

    /// <summary>Fetches and verifies the current channel manifest, and evaluates the rollout bucket. Performs no
    /// download or staging, and never commits the manifest's sequence to the durable replay high-water mark — a
    /// read-only inspection that can be repeated without permanently consuming the one sequence number an
    /// <see cref="ApplyAsync"/> still needs to commit.
    /// <para>Verification runs under an <see cref="InstallTransaction"/> begun for it alone (waiting up to
    /// <see cref="CheckLeaseWait"/>), so the revocations an authentic manifest carries are merged into the release record
    /// without racing the stub, an apply or another check; a busy install reports <see cref="UpdateCheckOutcome.Refused"/>
    /// and writes nothing.</para></summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>What the check found.</returns>
    public Task<UpdateCheckResult> CheckAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        FetchAndVerifyAsync(cancellationToken: cancellationToken, now: now, transaction: null);
    /// <summary>Gets how long a check waits for the install root's update lease before reporting the install busy.</summary>
    public static TimeSpan CheckLeaseWait { get; } = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>Resolves the current process's .NET runtime identifier — the payload key <see cref="IUpdateStager"/> stages.</summary>
    public static string CurrentRid() => RuntimeInformation.RuntimeIdentifier;
}
