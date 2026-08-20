using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Attestation;
using Puck.Cli.PublishRelease;
using Puck.Launcher.Release;

namespace Puck.Cli.Canary;

// A stub-shaped leg's runner: it never launches Puck.World.dll from the shared build path CanaryCommand's other
// legs and puck landing depend on. Each leg gets its own disposable <run>/install/ tree (stub.json, current,
// versions/<v>/...), a leg-private release-source directory signed by a throwaway root/issuing/subject chain
// minted the same way the federation identities above are, and observes TWO successive stub launches (never one),
// concatenating both boots' transcripts before the shared assertion engine runs — so leg.expect authors over the
// combined proof exactly like every other leg's single-process transcript.
internal static partial class CanaryCommand {
    // A version label that is not strictly greater is refused by AttestationReleaseVerifier's own version-
    // monotonicity check; both legs stage this as the pre-existing baseline before either boot runs.
    private const string StubBaselineVersion = "1.0.0";
    private const string StubReleaseVersion = "2.0.0";
    private const string StubReleaseChannel = "stable";
    private const string StubAppId = "puck.world";

    private static CanaryLegRun RunStubLeg(CanaryManifest manifest, bool discriminating, string worldArtifact, string stubArtifact, Stopwatch suiteClock) {
        try {
            return (discriminating
                ? RunStubDiscriminatingLegCore(manifest: manifest, stubArtifact: stubArtifact, suiteClock: suiteClock, worldArtifact: worldArtifact)
                : RunStubPositiveLegCore(manifest: manifest, stubArtifact: stubArtifact, suiteClock: suiteClock, worldArtifact: worldArtifact)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            var leg = (discriminating ? manifest.Discriminating : manifest.Positive);

            return CanaryLegRun.InfrastructureFailure(leg: leg, runDirectory: "not-created", reason: exception.Message.ReplaceLineEndings(replacementText: " "));
        }
    }
    private static CanaryLegRun RunStubPositiveLegCore(CanaryManifest manifest, string worldArtifact, string stubArtifact, Stopwatch suiteClock) {
        var leg = manifest.Positive;
        var runDirectory = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var installDirectory = Path.Combine(path1: runDirectory, path2: "install");
        var releaseDirectory = Path.Combine(path1: runDirectory, path2: "release");

        MaterializeStubInstall(installDirectory: installDirectory, stubArtifact: stubArtifact);
        StageVersionDirectory(installDirectory: installDirectory, version: StubBaselineVersion, worldArtifact: worldArtifact);
        // The applied version's directory is pre-seeded with the same real Puck.World build the baseline carries
        // (rather than left for the stager to populate) because the signed manifest below names only a small
        // representative payload, not the ~300-file real build — the attestation profile's own payload ceiling
        // (see PublishAndSignThrowawayRelease's own remarks) refuses a claim that large. The stager still verifies
        // and writes its own small file set on top of this seed, exercising the real hash/stage/apply path; the
        // rest of the pre-seeded app is what lets boot 2 actually run.
        StageVersionDirectory(installDirectory: installDirectory, version: StubReleaseVersion, worldArtifact: worldArtifact);
        StubInstallFiles.WritePointer(fileName: "current", installDirectory: installDirectory, value: StubBaselineVersion);

        var (signedManifestBytes, trustAnchor) = PublishAndSignThrowawayRelease(releaseDirectory: releaseDirectory);

        var channelDirectory = Path.Combine(path1: releaseDirectory, path2: StubReleaseChannel);

        Directory.CreateDirectory(path: channelDirectory);
        File.WriteAllBytes(path: Path.Combine(path1: channelDirectory, path2: "manifest.json"), bytes: signedManifestBytes);

        var updateConfigPath = Path.Combine(path1: runDirectory, path2: "update-config.json");

        WriteSelfUpdateConfigFile(cacheRoot: installDirectory, path: updateConfigPath, releaseDirectory: releaseDirectory, trustAnchor: trustAnchor);

        var remaining = CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock);
        var bootTimeout = TimeSpan.FromSeconds(value: Math.Min(val1: manifest.TimeoutSeconds, val2: remaining.TotalSeconds));

        if (bootTimeout <= TimeSpan.Zero) {
            return CanaryLegRun.BudgetExpired(leg: leg, runDirectory: runDirectory);
        }

        var boot1 = LaunchStub(
            arguments: [
                "--world", leg.WorldPath,
                "--update-config-file", updateConfigPath,
                "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", Path.Combine(path1: runDirectory, path2: "state-boot1"),
                "--headless", "true",
            ],
            input: ReadScriptWithSubstitution(leg: leg, runDirectory: runDirectory),
            installDirectory: installDirectory,
            timeout: bootTimeout
        );
        var boot2 = LaunchStub(
            arguments: [
                "--update-config-file", updateConfigPath,
                "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", Path.Combine(path1: runDirectory, path2: "state-boot2"),
                "--headless", "true",
            ],
            input: string.Empty,
            installDirectory: installDirectory,
            timeout: CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock)
        );

        WriteLegConfirmationFixtures(installDirectory: installDirectory, runDirectory: runDirectory);

        return BuildStubLegRun(
            boot1: boot1,
            boot2: boot2,
            invariants: [
                new(Detail: $"boot 1 (check + apply) exited 0 (actual {boot1.ExitCode})", Passed: (!boot1.TimedOut && (boot1.ExitCode == 0))),
                new(Detail: "boot 1 completed within the leg timeout", Passed: !boot1.TimedOut),
                new(Detail: $"boot 2 (the stub relaunches on the applied version) exited 0 (actual {boot2.ExitCode})", Passed: (!boot2.TimedOut && (boot2.ExitCode == 0))),
                new(Detail: "boot 2 completed within the leg timeout", Passed: !boot2.TimedOut),
            ],
            leg: leg,
            runDirectory: runDirectory
        );
    }
    private static CanaryLegRun RunStubDiscriminatingLegCore(CanaryManifest manifest, string worldArtifact, string stubArtifact, Stopwatch suiteClock) {
        var leg = manifest.Discriminating;
        var runDirectory = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var installDirectory = Path.Combine(path1: runDirectory, path2: "install");

        MaterializeStubInstall(installDirectory: installDirectory, stubArtifact: stubArtifact);
        StageVersionDirectory(installDirectory: installDirectory, version: StubBaselineVersion, worldArtifact: worldArtifact);

        // The broken candidate is pre-applied directly (this leg never drives update.check/update.apply — that
        // plumbing is the positive leg's own proof): its version directory exists but carries no Puck.World.exe at
        // all, so the stub itself refuses to launch it, and its state-generation never exceeds the baseline's, so
        // StubDecisionTable's revert path is the generation-safe one.
        var brokenVersionDirectory = StubInstallFiles.VersionDirectory(installDirectory: installDirectory, version: StubReleaseVersion);

        Directory.CreateDirectory(path: brokenVersionDirectory);
        File.WriteAllText(path: Path.Combine(path1: brokenVersionDirectory, path2: "state-generation"), contents: "0");
        StubInstallFiles.WritePointer(fileName: "current", installDirectory: installDirectory, value: StubReleaseVersion);
        StubInstallFiles.WritePointer(fileName: "last-good", installDirectory: installDirectory, value: StubBaselineVersion);

        var remaining = CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock);
        var bootTimeout = TimeSpan.FromSeconds(value: Math.Min(val1: manifest.TimeoutSeconds, val2: remaining.TotalSeconds));

        if (bootTimeout <= TimeSpan.Zero) {
            return CanaryLegRun.BudgetExpired(leg: leg, runDirectory: runDirectory);
        }

        var boot1 = LaunchStub(
            // --world is never read: the stub refuses the missing executable before starting any child process.
            arguments: [
                "--world", leg.WorldPath,
                "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", Path.Combine(path1: runDirectory, path2: "state-boot1"),
                "--headless", "true",
            ],
            input: ReadScriptWithSubstitution(leg: leg, runDirectory: runDirectory),
            installDirectory: installDirectory,
            timeout: bootTimeout
        );
        var boot2 = LaunchStub(
            arguments: [
                "--exit-after-seconds", manifest.Seconds.ToString(provider: CultureInfo.InvariantCulture),
                "--state-dir", Path.Combine(path1: runDirectory, path2: "state-boot2"),
                "--headless", "true",
            ],
            input: string.Empty,
            installDirectory: installDirectory,
            timeout: CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock)
        );

        WriteLegConfirmationFixtures(installDirectory: installDirectory, runDirectory: runDirectory);

        return BuildStubLegRun(
            boot1: boot1,
            boot2: boot2,
            invariants: [
                new(Detail: $"boot 1 (the broken candidate) refuses to launch (actual exit {boot1.ExitCode})", Passed: (!boot1.TimedOut && (boot1.ExitCode == 2))),
                new(Detail: "boot 1 completed within the leg timeout", Passed: !boot1.TimedOut),
                new(Detail: $"boot 2 (the stub reverts to last-good) exited 0 (actual {boot2.ExitCode})", Passed: (!boot2.TimedOut && (boot2.ExitCode == 0))),
                new(Detail: "boot 2 completed within the leg timeout", Passed: !boot2.TimedOut),
            ],
            leg: leg,
            runDirectory: runDirectory
        );
    }
    private static CanaryLegRun BuildStubLegRun(CliProcessResult boot1, CliProcessResult boot2, IReadOnlyList<CanaryAssertionResult> invariants, CanaryLeg leg, string runDirectory) {
        File.WriteAllText(contents: boot1.Stdout, path: Path.Combine(path1: runDirectory, path2: "boot1-stdout.log"));
        File.WriteAllText(contents: boot1.Stderr, path: Path.Combine(path1: runDirectory, path2: "boot1-stderr.log"));
        File.WriteAllText(contents: boot2.Stdout, path: Path.Combine(path1: runDirectory, path2: "boot2-stdout.log"));
        File.WriteAllText(contents: boot2.Stderr, path: Path.Combine(path1: runDirectory, path2: "boot2-stderr.log"));

        var transcript = new CanaryTranscript(
            RunDirectory: runDirectory,
            Stderr: [.. SplitLines(text: boot1.Stderr), .. SplitLines(text: boot2.Stderr)],
            Stdout: [.. SplitLines(text: boot1.Stdout), .. SplitLines(text: boot2.Stdout)]
        );

        return new CanaryLegRun(
            Assertions: CanaryAssertions.Evaluate(leg: leg, primaryTranscript: transcript),
            AuthorityTranscripts: ImmutableEmptyAuthorityTranscripts,
            ExitCode: boot2.ExitCode,
            InfrastructureError: null,
            Invariants: invariants,
            Leg: leg,
            RunDirectory: runDirectory,
            TimedOut: (boot1.TimedOut || boot2.TimedOut),
            Transcript: transcript
        );
    }
    private static CliProcessResult LaunchStub(IReadOnlyList<string> arguments, string input, string installDirectory, TimeSpan timeout) =>
        CliProcess.RunCaptured(fileName: Path.Combine(path1: installDirectory, path2: "Puck.Launcher.Stub.exe"), arguments: arguments, input: input, timeout: timeout);
    private static string ReadScriptWithSubstitution(CanaryLeg leg, string runDirectory) {
        var input = File.ReadAllText(path: leg.ScriptPath)
            .Replace(oldValue: "{run}", newValue: runDirectory.Replace(newChar: '\\', oldChar: '/'), comparisonType: StringComparison.Ordinal);

        return (input.EndsWith(value: '\n') ? input : (input + Environment.NewLine));
    }
    // <installDirectory>/Puck.Launcher.Stub.exe reads stub.json/current/versions/ from its own directory
    // (AppContext.BaseDirectory) — the stub build output is copied here rather than launched from the shared
    // build path, so nothing this leg does can touch the artifact every other canary and puck landing depend on.
    private static void MaterializeStubInstall(string installDirectory, string stubArtifact) {
        Directory.CreateDirectory(path: installDirectory);
        CopyDirectoryFiles(destination: installDirectory, recursive: false, source: Path.GetDirectoryName(path: stubArtifact)!);
        File.WriteAllText(
            contents: JsonSerializer.Serialize(value: new StubConfigurationFile(AppExecutableFileName: "Puck.World.exe", MaxAttempts: 1)),
            path: Path.Combine(path1: installDirectory, path2: "stub.json")
        );
    }
    // A full copy of the one built Puck.World output under versions/<version>/, never the shared build directory
    // itself.
    private static void StageVersionDirectory(string installDirectory, string version, string worldArtifact) {
        var versionDirectory = StubInstallFiles.VersionDirectory(installDirectory: installDirectory, version: version);

        Directory.CreateDirectory(path: versionDirectory);
        CopyDirectoryFiles(destination: versionDirectory, recursive: true, source: Path.GetDirectoryName(path: worldArtifact)!);
        File.WriteAllText(contents: "0", path: Path.Combine(path1: versionDirectory, path2: "state-generation"));
    }
    private static void CopyDirectoryFiles(string destination, string source, bool recursive) {
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: (recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly), searchPattern: "*")) {
            var relative = Path.GetRelativePath(path: file, relativeTo: source);
            var target = Path.Combine(path1: destination, path2: relative);

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(destFileName: target, overwrite: true, sourceFileName: file);
        }
    }
    // The channel-tree layout DirectoryReleaseSource reads, signed by a throwaway root/issuing/subject chain minted
    // fresh per leg the same way GenerateFederationIdentity mints one for federation — never an unsigned bypass.
    // The published payload is a small marker file set, not the ~300-file real Puck.World build: the attestation
    // profile caps a claim's payload at 49152 bytes (AttestationResourceLimits), and a full build's canonical file
    // list alone already exceeds it. RunStubPositiveLegCore pre-seeds the applied version's directory with the real
    // build separately, so the small signed payload still exercises the real stage/verify/apply path against files
    // that land on top of a directory that can actually run.
    private static (byte[] SignedManifestBytes, ReleaseTrustAnchor TrustAnchor) PublishAndSignThrowawayRelease(string releaseDirectory) {
        var payloadDirectory = Path.Combine(path1: releaseDirectory, path2: "payload");

        Directory.CreateDirectory(path: payloadDirectory);
        File.WriteAllText(contents: $"puck.world {StubReleaseVersion}", path: Path.Combine(path1: payloadDirectory, path2: "release-notes.txt"));

        var dryRunDirectory = Path.Combine(path1: releaseDirectory, path2: "dry-run");
        // ContentAddressedUpdateStager stages the payload whose rid matches this RUNNING process's own
        // RuntimeInformation.RuntimeIdentifier (UpdateService.CurrentRid()) — a fixture rid would never match.
        var publishExit = PublishCommand.Run(args: [
            "--rid", UpdateService.CurrentRid(),
            "--input", payloadDirectory,
            "--out", dryRunDirectory,
            "--app", StubAppId,
            "--channel", StubReleaseChannel,
            "--version", StubReleaseVersion,
            "--state-generation", "0",
        ]);

        if (publishExit != 0) {
            throw new IOException(message: $"puck publish exited {publishExit} building the self-update canary's throwaway release.");
        }

        var unsignedBytes = File.ReadAllBytes(path: Path.Combine(path1: dryRunDirectory, path2: StubReleaseChannel, path3: "manifest.json"));
        var unsigned = (JsonSerializer.Deserialize<ReleaseManifest>(utf8Json: unsignedBytes, options: DocumentJsonOptions.Shared)
            ?? throw new IOException(message: "puck publish wrote a manifest that deserialized to null."));

        Directory.CreateDirectory(path: Path.Combine(path1: releaseDirectory, path2: "objects"));
        CopyDirectoryFiles(destination: Path.Combine(path1: releaseDirectory, path2: "objects"), recursive: true, source: Path.Combine(path1: dryRunDirectory, path2: "objects"));

        using var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var rootId = KeyId.ForRoot(algorithm: AttestationAlgorithms.EcdsaP256Sha256, subjectPublicKeyInfo: rootSpki);

        using var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var issuingId = KeyId.ForIssuing(algorithm: AttestationAlgorithms.EcdsaP256Sha256, domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki);

        using var subjectKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSpki = subjectKey.ExportSubjectPublicKeyInfo();
        var subjectId = KeyId.ForSubject(algorithm: AttestationAlgorithms.EcdsaP256Sha256, domain: rootId.Domain, subject: StubAppId, subjectPublicKeyInfo: subjectSpki);

        var codec = new CborAttestationCodec();
        var canonical = ReleaseCanonicalizer.Canonicalize(document: unsigned);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var notBefore = (now - 60);
        var notAfter = (now + 3600);
        var rootToIssuing = AttestationSigner.SignKeyBinding(codec: codec, domain: rootId.Domain, notAfter: notAfter, notBefore: notBefore, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, signerKey: rootKey, targetId: issuingId, targetSubjectPublicKeyInfo: issuingSpki);
        var issuingToSubject = AttestationSigner.SignKeyBinding(codec: codec, domain: rootId.Domain, notAfter: notAfter, notBefore: notBefore, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, signerKey: issuingKey, targetId: subjectId, targetSubjectPublicKeyInfo: subjectSpki);
        var claim = AttestationSigner.SignClaim(audience: null, claimBytes: canonical.Bytes, codec: codec, domain: rootId.Domain, notAfter: notAfter, notBefore: notBefore, purpose: AttestationReleaseVerifier.Purpose, sequence: 1, signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256, signerKey: subjectKey, subject: StubAppId);
        var signature = new ReleaseSignature(
            Chain: [Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: rootToIssuing)), Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: issuingToSubject))],
            Claim: Convert.ToBase64String(inArray: codec.EncodeAttestation(attestation: claim))
        );
        var signed = (unsigned with { Signature = signature });
        var trustAnchor = new ReleaseTrustAnchor(
            Algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            Domain: rootId.Domain,
            PublicKeySubjectPublicKeyInfoBase64: Convert.ToBase64String(inArray: rootSpki)
        );

        return (JsonSerializer.SerializeToUtf8Bytes(value: signed, options: DocumentJsonOptions.Shared), trustAnchor);
    }
    private static void WriteSelfUpdateConfigFile(string cacheRoot, string path, string releaseDirectory, ReleaseTrustAnchor trustAnchor) => File.WriteAllText(
        contents: JsonSerializer.Serialize(value: new SelfUpdateConfigFile(
            CacheRoot: cacheRoot,
            ReleaseSourceDirectory: releaseDirectory,
            TrustAnchorAlgorithm: trustAnchor.Algorithm,
            TrustAnchorDomain: trustAnchor.Domain,
            TrustAnchorPublicKeySubjectPublicKeyInfoBase64: trustAnchor.PublicKeySubjectPublicKeyInfoBase64
        )),
        path: path
    );
    // The proof a puck.release.v1-shaped positive.expect can read without parsing any wire format: literal
    // expected-version fixtures beside a snapshot of the install's own current pointer after both boots, checked
    // through the shared filesDiffer assertion. Both fixtures are written into every leg's run directory
    // (regardless of which one that leg's own expect references) because RunSelected re-evaluates the POSITIVE
    // leg's assertions against the DISCRIMINATING leg's transcript to prove the positive observation turns red —
    // that comparison reads files from the discriminating leg's own run directory.
    private static void WriteLegConfirmationFixtures(string installDirectory, string runDirectory) {
        File.Copy(destFileName: Path.Combine(path1: runDirectory, path2: "current-after"), overwrite: true, sourceFileName: Path.Combine(path1: installDirectory, path2: "current"));
        File.WriteAllText(contents: StubBaselineVersion, path: Path.Combine(path1: runDirectory, path2: $"expected-{StubBaselineVersion}.txt"));
        File.WriteAllText(contents: StubReleaseVersion, path: Path.Combine(path1: runDirectory, path2: $"expected-{StubReleaseVersion}.txt"));
    }

    private sealed record StubConfigurationFile(string AppExecutableFileName, int MaxAttempts);
}

// The pointer/version-directory layout Puck.Launcher.Stub.StubInstall reads and Puck.Launcher.Release.FileUpdateApplier
// writes — this runner is neither (it references neither project's install-layout type) and instead writes the same
// file shapes directly, exactly the same "shared by convention, not by reference" posture StubInstall's own remarks
// describe for why the stub and the applier don't share code either.
file static class StubInstallFiles {
    public static string VersionDirectory(string installDirectory, string version) =>
        Path.Combine(path1: installDirectory, path2: "versions", path3: version);
    public static void WritePointer(string fileName, string installDirectory, string value) =>
        File.WriteAllText(contents: value, path: Path.Combine(path1: installDirectory, path2: fileName));
}
