namespace Puck.Confinement;

/// <summary>What <see cref="InstallTransaction.SelectLaunch"/> decided.</summary>
/// <param name="Version">The version to launch, or <see langword="null"/> when launch is refused.</param>
/// <param name="RolledBackFrom">The version selection moved away from, or <see langword="null"/> when it launches the
/// version that was current.</param>
/// <param name="Refusal">Why nothing may launch, naming the current version's disqualification and each rollback
/// candidate's, or <see langword="null"/> when <paramref name="Version"/> launches.</param>
/// <param name="Notes">What selection recorded on the way: a rejection, a rollback.</param>
public sealed record LaunchDecision(string? Version, string? RolledBackFrom, string? Refusal, IReadOnlyList<string> Notes);
/// <summary>One proven install as rollback selection judged it.</summary>
/// <param name="Proof">The proof of health.</param>
/// <param name="Record">The version directory's install record, or <see langword="null"/> when it holds none.</param>
/// <param name="Disqualification">Why the install may not be selected, or <see langword="null"/> when it may.</param>
public sealed record RollbackCandidate(ProvenInstall Proof, InstallRecord? Record, string? Disqualification);
/// <summary>
/// The install root's update lease (<see cref="InstallLayout.UpdateLockFileName"/>) and the one door to its release record
/// (<see cref="ReleasePolicy"/>) and install records. Every read-modify-write of either happens through a method of this
/// type, which exists only while the lease is held, so no two writers interleave: the stub, an update check or apply,
/// registration, health clearing and pruning all serialize on it.
/// <para>The lease is not reentrant, within a process included: whoever begins a transaction owns and disposes it, and
/// code handed one never begins another. Each operation computes its new record from the snapshot this transaction read
/// (which no other writer can change while the lease is held) and commits it with one durable replacement
/// (<see cref="AtomicFile.WriteAllBytesDurably"/>): before the commit returns nothing has changed, after it everything
/// has.</para>
/// </summary>
public sealed class InstallTransaction : IDisposable {
    private readonly FileStream? m_lease;
    private readonly bool m_ownsRoot;
    private readonly ConfinedDirectory m_root;

    private bool m_disposed;
    private ReleasePolicy? m_policy;

    private InstallTransaction(ConfinedDirectory root, bool ownsRoot, FileStream? lease, string rootPath) {
        m_lease = lease;
        m_ownsRoot = ownsRoot;
        m_root = root;
        RootPath = rootPath;
    }

    /// <summary>Gets the install root's full path.</summary>
    public string RootPath { get; }
    /// <summary>Gets the held install root.</summary>
    /// <exception cref="ObjectDisposedException">The transaction has ended.</exception>
    public ConfinedDirectory Root {
        get {
            ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
            return m_root;
        }
    }
    /// <summary>Gets the release record as this transaction last read or committed it.</summary>
    /// <exception cref="InvalidDataException">The record exists but does not validate.</exception>
    /// <exception cref="ObjectDisposedException">The transaction has ended.</exception>
    public ReleasePolicy Policy => (m_policy ??= ReleasePolicy.Read(root: Root));

    /// <summary>Begins a transaction without waiting, creating the install root when it is missing.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <returns>The transaction, or <see langword="null"/> when another holder has the lease.</returns>
    /// <exception cref="IOException">The root or the lease file cannot be opened, or the lease file is a link.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static InstallTransaction? TryBegin(string installRoot) {
        var root = ConfinedDirectory.OpenAnchor(create: true, path: installRoot)!;

        try {
            var lease = root.TryAcquireLock(name: InstallLayout.UpdateLockFileName);

            if (lease is null) { root.Dispose(); return null; }
            return new InstallTransaction(lease: lease, ownsRoot: true, root: root, rootPath: root.Path);
        } catch {
            root.Dispose();
            throw;
        }
    }
    /// <summary>Begins a transaction, waiting for the lease, creating the install root when it is missing.</summary>
    /// <param name="installRoot">The install root.</param>
    /// <param name="timeout">How long to wait; <see cref="Timeout.InfiniteTimeSpan"/> waits until the lease is free.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>The transaction.</returns>
    /// <exception cref="IOException">The wait timed out, or the root or the lease file cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    /// <exception cref="PlatformNotSupportedException">The platform is neither Windows nor Linux x64.</exception>
    public static InstallTransaction Begin(string installRoot, TimeSpan timeout, CancellationToken cancellationToken) {
        var root = ConfinedDirectory.OpenAnchor(create: true, path: installRoot)!;

        try {
            return new InstallTransaction(lease: root.AcquireLock(cancellationToken: cancellationToken, name: InstallLayout.UpdateLockFileName, timeout: timeout),
                ownsRoot: true, root: root, rootPath: root.Path);
        } catch {
            root.Dispose();
            throw;
        }
    }
    /// <summary>Begins a transaction on a root the caller holds and keeps, without waiting.</summary>
    /// <param name="root">The held install root, which the transaction borrows and never disposes.</param>
    /// <returns>The transaction, or <see langword="null"/> when another holder has the lease.</returns>
    /// <exception cref="IOException">The lease file cannot be opened or is a link.</exception>
    public static InstallTransaction? TryBegin(ConfinedDirectory root) {
        ArgumentNullException.ThrowIfNull(argument: root);
        var lease = root.TryAcquireLock(name: InstallLayout.UpdateLockFileName);

        return ((lease is null) ? null : new InstallTransaction(lease: lease, ownsRoot: false, root: root, rootPath: root.Path));
    }
    // Registration already holds the lease on this root; the transaction borrows both.
    internal static InstallTransaction Under(ConfinedDirectory root) => new(lease: null, ownsRoot: false, root: root, rootPath: root.Path);

    /// <summary>Reads a version directory's install record.</summary>
    /// <param name="version">The version directory's name.</param>
    /// <returns>The record, or <see langword="null"/> when the name is not a plain directory name, the directory or its
    /// record is absent, or the record does not validate.</returns>
    /// <exception cref="IOException">The directory or record is a link or cannot be read.</exception>
    public InstallRecord? ReadRecord(string version) {
        if (!InstallLayout.IsPlainName(name: version)) { return null; }
        using var versions = Root.Child(name: InstallLayout.VersionsDirectoryName);
        using var directory = versions?.Child(name: version);

        return ((directory is null) ? null : InstallRecord.TryRead(version: directory));
    }
    /// <summary>Lists the proven installs, most recent first, each with why it may or may not be a rollback target under
    /// the current record: its directory must hold the very install that proved healthy (the same sequence and payload
    /// identity), and it must pass <see cref="ReleasePolicy.Refusal"/>.</summary>
    /// <returns>Every proven install, judged.</returns>
    public IReadOnlyList<RollbackCandidate> RollbackCandidates() => Candidates(policy: Policy);
    /// <summary>Returns the version rollback would select if the current version were disqualified now, or
    /// <see langword="null"/> when none qualifies.</summary>
    /// <returns>The highest-sequence allowed proven install's version other than the current one.</returns>
    public string? RollbackTarget() {
        var current = Policy.Current;

        return RollbackCandidates().FirstOrDefault(predicate: candidate =>
            (candidate.Disqualification is null) && !string.Equals(a: candidate.Proof.Version, b: current, comparisonType: StringComparison.Ordinal))?.Proof.Version;
    }
    /// <summary>Reports whether an install record is the very install a proof of health names.</summary>
    /// <param name="version">The version directory's name.</param>
    /// <param name="record">The version's install record.</param>
    /// <returns><see langword="true"/> when a proof names this version, sequence and payload identity.</returns>
    public bool IsProven(string version, InstallRecord record) {
        ArgumentNullException.ThrowIfNull(argument: record);
        return Policy.ProvenInstalls.Any(predicate: proof => Matches(proof: proof, record: record, version: version));
    }
    /// <summary>Merges an authentic manifest's revocations into the record and commits it, before the manifest's own
    /// eligibility is judged. A name that cannot be a version directory can name no install and is dropped before anything
    /// is written; revocations that can never again name a selectable or admissible version are retired.</summary>
    /// <param name="revoked">The manifest's revoked version names.</param>
    /// <param name="precedence">The release version order admission uses (positive when the first follows the second).</param>
    /// <returns>The refusal when the merged revocations would exceed <see cref="ReleasePolicy.MaximumRevokedVersions"/>, in
    /// which case nothing is written; otherwise <see langword="null"/>.</returns>
    /// <exception cref="IOException">The record cannot be written.</exception>
    public string? MergeRevocations(IEnumerable<string>? revoked, Comparison<string> precedence) {
        ArgumentNullException.ThrowIfNull(argument: precedence);
        var policy = Policy;
        var merged = policy.RevokedVersions.Concat(second: (revoked ?? []).Where(predicate: static version => InstallLayout.IsPlainName(name: version)))
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase).ToList();
        var next = Retire(policy: (policy with { RevokedVersions = merged }), precedence: precedence);

        if (next.RevokedVersions.Count > ReleasePolicy.MaximumRevokedVersions) {
            return $"the install's revocation history would exceed {ReleasePolicy.MaximumRevokedVersions} entries; reinstall to continue";
        }
        Commit(next: next);
        return null;
    }
    /// <summary>Installs a staged, verified version as current: refuses it when <see cref="ReleasePolicy.Refusal"/> does,
    /// when it does not follow the current version, when its name collides (ignoring case) with the current version or the
    /// rollback target, or when the payload history is full; otherwise writes its install record durably with the next
    /// sequence, then commits the record naming it current. The outgoing version's attempt count and proofs are left as
    /// they are. When <paramref name="recovering"/> and the version is already current, only checks that its install
    /// record matches.</summary>
    /// <param name="version">The staged version directory's name.</param>
    /// <param name="rid">The installed payload's runtime identifier.</param>
    /// <param name="stateGeneration">The installed payload's state generation.</param>
    /// <param name="payloadPin">The installed payload's identity.</param>
    /// <param name="files">The payload files' content pins.</param>
    /// <param name="precedence">The release version order admission uses (positive when the first follows the second).</param>
    /// <param name="recovering">Whether this completes a durable install intent, which may already have made the version
    /// current.</param>
    /// <param name="record">The version's install record when it was installed or already current.</param>
    /// <returns>The refusal, or <see langword="null"/> when the version is current.</returns>
    /// <exception cref="IOException">The install record or the release record cannot be written.</exception>
    public string? CommitApply(string version, string rid, int stateGeneration, string payloadPin, IReadOnlyList<string> files, Comparison<string> precedence,
        bool recovering, out InstallRecord? record) {
        ArgumentNullException.ThrowIfNull(argument: files);
        ArgumentNullException.ThrowIfNull(argument: precedence);
        var policy = Policy;

        record = null;
        if (!InstallLayout.IsPlainName(name: version)) { return $"'{version}' is not a version directory name"; }
        if (recovering && string.Equals(a: policy.Current, b: version, comparisonType: StringComparison.Ordinal)) {
            var installed = ReadRecord(version: version);

            if ((installed is null) || (installed.StateGeneration != stateGeneration) || !string.Equals(a: installed.PayloadPin, b: payloadPin, comparisonType: StringComparison.Ordinal)) {
                return "the current install's record does not match its pending intent's state generation and payload";
            }
            record = installed;
            return null;
        }
        if (policy.Refusal(payloadPins: [payloadPin], stateGeneration: stateGeneration, version: version) is { } refusal) { return refusal; }
        if (policy.Current is { } previous) {
            if (precedence(version, previous) <= 0) { return $"version '{version}' does not exceed the current install '{previous}'"; }
            if (string.Equals(a: version, b: previous, comparisonType: StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a: version, b: RollbackTarget(), comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return $"version '{version}' collides case-insensitively with an active install directory";
            }
        }
        if ((policy.RejectedPayloads.Count + policy.LaunchAttempts.Count) >= ReleasePolicy.MaximumPayloadHistory) {
            return $"the install's payload history holds {ReleasePolicy.MaximumPayloadHistory} entries; reinstall to continue";
        }
        var next = Retire(policy: policy, precedence: precedence);

        if (next.RevokedVersions.Count > ReleasePolicy.MaximumRevokedVersions) {
            return $"the install's revocation history exceeds {ReleasePolicy.MaximumRevokedVersions} entries; reinstall to continue";
        }
        var installing = new InstallRecord(Files: [.. files], PayloadPin: payloadPin, Rid: rid, Sequence: (policy.LastSequence + 1), StateGeneration: stateGeneration);

        if (!installing.IsValid) { return $"version '{version}' carries an incomplete install identity"; }
        AtomicFile.WriteAllBytesDurably(bytes: installing.Serialize(),
            path: Path.Combine(path1: RootPath, path2: InstallLayout.VersionsDirectoryName, path3: version, path4: InstallRecord.FileName));
        Commit(next: (next with { Current = version, LastSequence = installing.Sequence }));
        record = installing;
        return null;
    }
    /// <summary>Decides what the stub launches, commits it, and counts the launch: a current version whose payload
    /// reached <paramref name="maxAttempts"/> launches is rejected; a current version that is revoked, rejected, below the
    /// state generation floor or without a valid install record is replaced by the highest-sequence allowed proven install
    /// (<see cref="RollbackCandidates"/>), or launch is refused naming every disqualification. The floor is raised to the
    /// chosen version's generation, one attempt is counted under its payload and <see cref="ReleasePolicy.Current"/> names
    /// it, all in one commit made before the caller starts the process.</summary>
    /// <param name="maxAttempts">The stub's startup attempt budget per payload; at least 1.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAttempts"/> is below 1.</exception>
    /// <exception cref="InvalidDataException">The release record does not validate.</exception>
    /// <exception cref="IOException">A record cannot be read or the release record cannot be written.</exception>
    public LaunchDecision SelectLaunch(int maxAttempts) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 1, value: maxAttempts);
        var policy = Policy;
        var notes = new List<string>();

        if (policy.Current is not { } current) {
            return new LaunchDecision(Notes: notes, Refusal: "no version is current; the install is empty", RolledBackFrom: null, Version: null);
        }
        var record = ReadRecord(version: current);
        var next = policy;
        string? disqualified;

        if (record is null) {
            disqualified = $"versions/{current} holds no valid install record, so its state generation is unknown";
        } else {
            var attempts = policy.AttemptsFor(payloadPin: record.PayloadPin);

            if (attempts >= maxAttempts) {
                next = Reject(payloadPin: record.PayloadPin, policy: next, version: current);
                notes.Add(item: $"version {current} reached {attempts}/{maxAttempts} startup attempts; its payload {record.PayloadPin} is rejected");
            }
            disqualified = next.Refusal(payloadPins: [record.PayloadPin], stateGeneration: record.StateGeneration, version: current);
        }
        var chosen = current;
        var chosenRecord = record;

        if (disqualified is not null) {
            var candidates = Candidates(policy: next);

            if (candidates.FirstOrDefault(predicate: static candidate => (candidate.Disqualification is null)) is not { } target) {
                Commit(next: next);
                var reasons = ((candidates.Count == 0)
                    ? "no proven install exists"
                    : string.Join(separator: "; ", values: candidates.Select(selector: static candidate => $"candidate {candidate.Disqualification}")));

                return new LaunchDecision(Notes: notes, Refusal: $"version '{current}' cannot launch: {disqualified}; rollback refused: {reasons}", RolledBackFrom: null, Version: null);
            }
            chosen = target.Proof.Version;
            chosenRecord = target.Record!;
            notes.Add(item: $"version {current} cannot launch ({disqualified}); rolling back to proven {chosen}");
        }
        Commit(next: (RaiseFloor(policy: next, stateGeneration: chosenRecord!.StateGeneration) with {
            Current = chosen,
            LaunchAttempts = Count(attempts: next.LaunchAttempts, payloadPin: chosenRecord.PayloadPin),
        }));
        return new LaunchDecision(Notes: notes, Refusal: null, RolledBackFrom: (string.Equals(a: chosen, b: current, comparisonType: StringComparison.Ordinal) ? null : current),
            Version: chosen);
    }
    /// <summary>Records that the process registered from <paramref name="version"/> reached startup health: when the
    /// version is current and has a valid install record, its attempt count is cleared and, unless
    /// <see cref="ReleasePolicy.Refusal"/> disqualifies it, its exact install is proven and the floor raised to its
    /// generation, in one commit, so no record holds both a proof and the attempts it cleared.</summary>
    /// <param name="version">The registered process's version directory name.</param>
    /// <returns><see langword="true"/> when the version was current and its health was recorded.</returns>
    /// <exception cref="IOException">A record cannot be read or the release record cannot be written.</exception>
    public bool RecordHealthy(string version) {
        var policy = Policy;

        if (!string.Equals(a: policy.Current, b: version, comparisonType: StringComparison.Ordinal) || (ReadRecord(version: version) is not { } record)) { return false; }
        var next = (policy with { LaunchAttempts = [.. policy.LaunchAttempts.Where(predicate: entry => !string.Equals(a: entry.PayloadPin, b: record.PayloadPin, comparisonType: StringComparison.Ordinal))] });

        if (policy.Refusal(payloadPins: [record.PayloadPin], stateGeneration: record.StateGeneration, version: version) is null) {
            next = RaiseFloor(policy: next, stateGeneration: record.StateGeneration);
            if (!next.ProvenInstalls.Any(predicate: proof => Matches(proof: proof, record: record, version: version))) {
                next = (next with { ProvenInstalls = [.. next.ProvenInstalls, new ProvenInstall(PayloadPin: record.PayloadPin, Sequence: record.Sequence, Version: version)] });
            }
        }
        Commit(next: next);
        return true;
    }
    /// <summary>Admits a process starting from <paramref name="version"/>: its install record must be valid and pass
    /// <see cref="ReleasePolicy.Refusal"/>; the floor is then raised to its generation and committed if it rose, before the
    /// process runs any application code.</summary>
    /// <param name="version">The version directory the process runs from.</param>
    /// <returns>The refusal, or <see langword="null"/> when the process may run.</returns>
    /// <exception cref="InvalidDataException">The release record does not validate.</exception>
    /// <exception cref="IOException">A record cannot be read or the release record cannot be written.</exception>
    public string? AdmitProcess(string version) {
        if (ReadRecord(version: version) is not { } record) { return $"versions/{version} holds no valid install record"; }
        var policy = Policy;

        if (policy.Refusal(payloadPins: [record.PayloadPin], stateGeneration: record.StateGeneration, version: version) is { } refusal) { return refusal; }
        Commit(next: RaiseFloor(policy: policy, stateGeneration: record.StateGeneration));
        return null;
    }
    /// <summary>Commits the record with proofs of installs that are no longer present retired, after a pass removed
    /// versions.</summary>
    /// <exception cref="IOException">The release record cannot be written.</exception>
    public void Compact() => Commit(next: Policy);
    /// <summary>Releases the lease, and the root when this transaction opened it.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_lease?.Dispose();
        if (m_ownsRoot) { m_root.Dispose(); }
    }

    private static bool Matches(ProvenInstall proof, InstallRecord record, string version) =>
        (string.Equals(a: proof.Version, b: version, comparisonType: StringComparison.Ordinal) && (proof.Sequence == record.Sequence) &&
        string.Equals(a: proof.PayloadPin, b: record.PayloadPin, comparisonType: StringComparison.Ordinal));
    private static ReleasePolicy RaiseFloor(ReleasePolicy policy, int stateGeneration) =>
        ((stateGeneration > policy.StateGenerationFloor) ? (policy with { StateGenerationFloor = stateGeneration }) : policy);
    private static ReleasePolicy Reject(ReleasePolicy policy, string payloadPin, string version) => (policy with {
        LaunchAttempts = [.. policy.LaunchAttempts.Where(predicate: entry => !string.Equals(a: entry.PayloadPin, b: payloadPin, comparisonType: StringComparison.Ordinal))],
        RejectedPayloads = (policy.IsRejected(payloadPin: payloadPin) ? policy.RejectedPayloads : [.. policy.RejectedPayloads, new RejectedPayload(PayloadPin: payloadPin, Version: version)]),
    });
    private static LaunchCount[] Count(IReadOnlyList<LaunchCount> attempts, string payloadPin) {
        var found = false;
        var counted = attempts.Select(selector: entry => {
            if (!string.Equals(a: entry.PayloadPin, b: payloadPin, comparisonType: StringComparison.Ordinal)) { return entry; }
            found = true;
            return (entry with { Attempts = checked(entry.Attempts + 1) });
        }).ToList();

        if (!found) { counted.Add(item: new LaunchCount(Attempts: 1, PayloadPin: payloadPin)); }
        return [.. counted];
    }
    private List<RollbackCandidate> Candidates(ReleasePolicy policy) {
        var candidates = new List<RollbackCandidate>();

        foreach (var proof in policy.ProvenInstalls.OrderByDescending(keySelector: static proof => proof.Sequence)) {
            var record = ReadRecord(version: proof.Version);
            var disqualification = ((record is null)
                ? $"'{proof.Version}' is no longer installed"
                : (!Matches(proof: proof, record: record, version: proof.Version)
                    ? $"'{proof.Version}' was reinstalled since it proved healthy"
                    : policy.Refusal(payloadPins: [record.PayloadPin], stateGeneration: record.StateGeneration, version: proof.Version)));

            candidates.Add(item: new RollbackCandidate(Disqualification: disqualification, Proof: proof, Record: record));
        }
        return candidates;
    }
    private List<string> InstalledVersions() {
        using var versions = Root.Child(name: InstallLayout.VersionsDirectoryName);

        return [.. (versions?.Entries() ?? []).Where(predicate: static entry => (entry.Kind == ConfinedEntryKind.Directory)).Select(selector: static entry => entry.Name)];
    }
    // A revoked version can be retired once no directory bears its name and it is at or below every version that could
    // become current: the current one and every installed one. Admission requires a version strictly above the current
    // one, rollback selects only installed ones, and every later install is above some later current, so every future
    // current is at or above today's minimum: the retired version can never again be admitted or selected.
    private ReleasePolicy Retire(ReleasePolicy policy, Comparison<string> precedence) {
        var installed = InstalledVersions();
        var anchors = ((policy.Current is { } current) ? installed.Append(element: current) : installed).ToList();

        if (anchors.Count == 0) { return policy; }
        var kept = policy.RevokedVersions.Where(predicate: version =>
            installed.Contains(value: version, comparer: StringComparer.OrdinalIgnoreCase) || anchors.Any(predicate: anchor => (precedence(version, anchor) > 0))).ToList();

        return ((kept.Count == policy.RevokedVersions.Count) ? policy : (policy with { RevokedVersions = kept }));
    }
    // The one writer of the release record: proofs whose exact install is gone are retired (they can never match again,
    // since sequences are never reused), and the record is replaced durably only when its bytes change.
    private void Commit(ReleasePolicy next) {
        var current = Policy;
        var proven = next.ProvenInstalls.Where(predicate: proof => (ReadRecord(version: proof.Version) is { } record) && Matches(proof: proof, record: record, version: proof.Version)).ToList();

        if (proven.Count != next.ProvenInstalls.Count) { next = (next with { ProvenInstalls = proven }); }
        var bytes = next.Serialize();

        if (bytes.AsSpan().SequenceEqual(other: current.Serialize())) {
            m_policy = next;
            return;
        }
        AtomicFile.WriteAllBytesDurably(bytes: bytes, path: Path.Combine(path1: RootPath, path2: InstallLayout.StateDirectoryName, path3: InstallLayout.ReleasePolicyFileName));
        m_policy = next;
    }
}
