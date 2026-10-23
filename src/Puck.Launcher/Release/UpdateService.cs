using System.Runtime.InteropServices;
using System.Text.Json;
using Puck.Assets;
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
    // Store before reserving the sequence; after a process interruption either side of that write can resume.
    private sealed record InstallIntent(ReleaseManifest Manifest, DateTimeOffset VerifiedAt, string InstalledVersion, string Rid);
    private readonly IUpdateApplier m_applier = applier;
    private readonly UpdateOptions m_options = options;
    private readonly IReleaseSource m_source = source;
    private readonly IUpdateStager m_stager = stager;
    private readonly IReleaseVerifier m_verifier = verifier;

    /// <summary>Gets the resolved operational configuration this service checks against.</summary>
    public UpdateOptions Options => m_options;

    private async Task<UpdateCheckResult> FetchAndVerifyAsync(CancellationToken cancellationToken, DateTimeOffset now) {
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

        var verified = m_verifier.Verify(
            advanceSequence: false,
            installedVersion: m_options.InstalledVersion,
            manifest: manifest,
            now: now
        );

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

    /// <summary>Under an exclusive install-root lease, resumes a pending local install or checks the tracked
    /// channel. Rollout and staging precede a durable exact-manifest intent and replay reservation; the pointer
    /// changes only after reservation. Process interruption resumes that intent, even after signature expiry,
    /// using the current trust policy at its original authorization instant. No network response can replace a
    /// pending intent. The filesystem must protect the install root from untrusted local writers.</summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch/stage.</param>
    public async Task<UpdateApplyOutcome> ApplyAsync(DateTimeOffset now, CancellationToken cancellationToken) {
        try {
            var root = Path.GetFullPath(path: m_options.CacheRoot);
            Directory.CreateDirectory(path: root);
            // Never unlink this file: a persistent inode makes process exit release the only writer lease.
            using var lease = new FileStream(path: Path.Combine(path1: root, path2: "update.lock"),
                mode: FileMode.OpenOrCreate, access: FileAccess.ReadWrite, share: FileShare.None);
            return await ApplyUnderLeaseAsync(root: root, now: now, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) {
            return new UpdateApplyOutcome(Applied: false, Detail: $"install is busy or unavailable; any pending intent is retained: {error.Message}");
        }
    }

    private async Task<UpdateApplyOutcome> ApplyUnderLeaseAsync(string root, DateTimeOffset now, CancellationToken cancellationToken) {
        var intentPath = Path.Combine(path1: root, path2: "install-intent.json");
        var recovering = File.Exists(path: intentPath);
        var rid = CurrentRid();
        InstallIntent intent;

        if (recovering) {
            intent = JsonSerializer.Deserialize<InstallIntent>(json: File.ReadAllText(path: intentPath), options: DocumentJsonOptions.Shared)
                ?? throw new JsonException(message: "install intent deserialized to null");
            if (intent.Manifest is null || string.IsNullOrWhiteSpace(value: intent.InstalledVersion) ||
                intent.Rid != rid || intent.Manifest.App != m_options.App || intent.Manifest.Channel != m_options.Channel || intent.VerifiedAt > now) {
                return new UpdateApplyOutcome(Applied: false, Detail: "pending install intent does not match this app, channel, runtime or authorization time");
            }
            // A changed current trust policy still refuses the intent. Time alone does not revoke local work
            // already authorized while its signature was valid; no fresh network verification uses this instant.
            var resumed = m_verifier.ReserveInstall(manifest: intent.Manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion);
            if (!resumed.Accepted) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"pending install refused: {resumed.RefusalReason}");
            }
        } else {
            var check = await FetchAndVerifyAsync(cancellationToken: cancellationToken, now: now).ConfigureAwait(continueOnCapturedContext: false);
            if (check.Outcome != UpdateCheckOutcome.Available) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"re-verification reported {check.Outcome}: {check.Detail}");
            }
            intent = new InstallIntent(Manifest: check.Manifest!, VerifiedAt: now, InstalledVersion: m_options.InstalledVersion, Rid: rid);
        }

        var manifest = intent.Manifest;
        var currentPath = Path.Combine(path1: root, path2: "current");
        var current = File.Exists(path: currentPath) ? File.ReadAllText(path: currentPath).Trim() : null;
        var alreadyCurrent = recovering && current == manifest.Version;
        if (!alreadyCurrent && !string.IsNullOrEmpty(value: current) &&
            !ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: current)) {
            return new UpdateApplyOutcome(Applied: false, Detail: $"version '{manifest.Version}' does not exceed the current install '{current}'");
        }

        // Never restage the running directory, including when an interrupted apply already swapped current.
        // The applier rehashes it below without modifying its files or last-good pointer.
        if (!alreadyCurrent) {
            var staged = await m_stager.StageAsync(cancellationToken: cancellationToken, manifest: manifest, rid: rid).ConfigureAwait(continueOnCapturedContext: false);
            if (!staged.Staged) {
                return new UpdateApplyOutcome(Applied: false, Detail: staged.RefusalReason ?? "staging refused");
            }
        }

        if (!recovering) {
            AtomicFile.WriteAllText(path: intentPath, contents: JsonSerializer.Serialize(value: intent, options: DocumentJsonOptions.Shared));
            var reserved = m_verifier.ReserveInstall(manifest: manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion);
            if (!reserved.Accepted) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"install reservation refused: {reserved.RefusalReason}");
            }
        }

        var applied = m_applier.Apply(cacheRoot: root, manifest: manifest, rid: rid, recoverInterruptedApply: recovering);
        if (!applied.Applied) {
            return new UpdateApplyOutcome(Applied: false, Detail: applied.RefusalReason ?? "apply refused");
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
    /// <see cref="ApplyAsync"/> still needs to commit.</summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public Task<UpdateCheckResult> CheckAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        FetchAndVerifyAsync(
            cancellationToken: cancellationToken,
            now: now
        );
    /// <summary>Resolves the current process's .NET runtime identifier — the payload key <see cref="IUpdateStager"/> stages.</summary>
    public static string CurrentRid() => RuntimeInformation.RuntimeIdentifier;
}
