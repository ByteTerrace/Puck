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
    private readonly IUpdateApplier m_applier = applier;
    private readonly UpdateOptions m_options = options;
    private readonly IReleaseSource m_source = source;
    private readonly IUpdateStager m_stager = stager;
    private readonly IReleaseVerifier m_verifier = verifier;

    /// <summary>Gets the resolved operational configuration this service checks against.</summary>
    public UpdateOptions Options => m_options;

    /// <summary>Fetches and verifies the current channel manifest, and evaluates the rollout bucket. Performs no
    /// download or staging, and never commits the manifest's sequence to the durable replay high-water mark — a
    /// read-only inspection that can be repeated without permanently consuming the one sequence number an
    /// <see cref="ApplyAsync"/> still needs to commit.</summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public Task<UpdateCheckResult> CheckAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        FetchAndVerifyAsync(advanceSequence: false, cancellationToken: cancellationToken, now: now);
    /// <summary>Resolves the current process's .NET runtime identifier — the payload key <see cref="IUpdateStager"/> stages.</summary>
    public static string CurrentRid() => RuntimeInformation.RuntimeIdentifier;
    /// <summary>Re-verifies the tracked channel — committing the manifest's sequence to the durable replay
    /// high-water mark this time, unlike <see cref="CheckAsync"/> — and, only when a verified, in-rollout newer
    /// version is found, stages and applies this process's runtime identifier's payload. Refuses without staging,
    /// applying, or committing anything when the re-verification does not report
    /// <see cref="UpdateCheckOutcome.Available"/>.</summary>
    /// <param name="now">The check instant, captured once by the caller.</param>
    /// <param name="cancellationToken">Cancels the fetch/stage.</param>
    public async Task<UpdateApplyOutcome> ApplyAsync(DateTimeOffset now, CancellationToken cancellationToken) {
        var check = await FetchAndVerifyAsync(advanceSequence: true, cancellationToken: cancellationToken, now: now).ConfigureAwait(continueOnCapturedContext: false);

        if (check.Outcome != UpdateCheckOutcome.Available) {
            return new UpdateApplyOutcome(Applied: false, Detail: $"re-verification reported {check.Outcome}: {check.Detail}");
        }

        var manifest = check.Manifest!;
        var rid = CurrentRid();
        var staged = await m_stager.StageAsync(cancellationToken: cancellationToken, manifest: manifest, rid: rid).ConfigureAwait(continueOnCapturedContext: false);

        if (!staged.Staged) {
            return new UpdateApplyOutcome(Applied: false, Detail: (staged.RefusalReason ?? "staging refused"));
        }

        var applied = m_applier.Apply(cacheRoot: m_options.CacheRoot, manifest: manifest, rid: rid);

        if (!applied.Applied) {
            return new UpdateApplyOutcome(Applied: false, Detail: (applied.RefusalReason ?? "apply refused"));
        }

        return new UpdateApplyOutcome(Applied: true, Detail: $"version {manifest.Version} applied, replacing {(applied.PreviousVersion ?? "(first install)")} — takes effect at the stub's next launch");
    }

    private async Task<UpdateCheckResult> FetchAndVerifyAsync(bool advanceSequence, CancellationToken cancellationToken, DateTimeOffset now) {
        var fetch = await m_source.TryGetLatestManifestAsync(channel: m_options.Channel, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!fetch.Found) {
            return new UpdateCheckResult(Detail: (fetch.RefusalReason ?? "no manifest found"), Manifest: null, Outcome: UpdateCheckOutcome.UpToDate);
        }

        ReleaseManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize<ReleaseManifest>(utf8Json: fetch.ManifestBytes, options: DocumentJsonOptions.Shared)
                ?? throw new JsonException(message: "manifest deserialized to null"));
        } catch (JsonException exception) {
            return new UpdateCheckResult(Detail: $"manifest did not parse: {exception.Message}", Manifest: null, Outcome: UpdateCheckOutcome.Refused);
        }

        if (!string.Equals(a: manifest.App, b: m_options.App, comparisonType: StringComparison.Ordinal)) {
            return new UpdateCheckResult(Detail: $"manifest names app '{manifest.App}', expected '{m_options.App}'", Manifest: null, Outcome: UpdateCheckOutcome.Refused);
        }

        var verified = m_verifier.Verify(advanceSequence: advanceSequence, installedVersion: m_options.InstalledVersion, manifest: manifest, now: now);

        if (!verified.Accepted) {
            return new UpdateCheckResult(Detail: (verified.RefusalReason ?? "refused"), Manifest: null, Outcome: UpdateCheckOutcome.Refused);
        }

        var installId = (m_options.InstallId ?? ReleaseRolloutBucket.MintOrLoad(cacheRoot: m_options.CacheRoot));

        if (!ReleaseRolloutBucket.IsIncluded(installId: installId, percent: manifest.Rollout.Percent)) {
            return new UpdateCheckResult(Detail: $"version {manifest.Version} verified but outside the {manifest.Rollout.Percent}% rollout", Manifest: manifest, Outcome: UpdateCheckOutcome.OutsideRollout);
        }

        var fileCount = manifest.Payloads.Sum(selector: payload => payload.Files.Count);

        return new UpdateCheckResult(Detail: $"version {manifest.Version} verified, {manifest.Payloads.Count} rid(s), {fileCount} file(s) total", Manifest: manifest, Outcome: UpdateCheckOutcome.Available);
    }
}
