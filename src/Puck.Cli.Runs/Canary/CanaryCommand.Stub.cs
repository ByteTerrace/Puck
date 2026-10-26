using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Cli.PublishRelease;
using Puck.Launcher.Release;

namespace Puck.Cli.Canary;

// A stub-shaped leg's runner: it never launches Puck.World.dll from the shared build path CanaryCommand's other
// legs and puck landing depend on. Each leg gets its own disposable <run>/install/ tree (stub.json, current,
// versions/<v>/...), a leg-private release-source directory signed by a throwaway root/issuing/subject chain
// verified by the ordinary release verifier. It observes installation, activation and successive stub launches,
// concatenating their transcripts before the shared assertion engine runs — so leg.expect authors over the
// combined proof exactly like every other leg's single-process transcript.
public static partial class CanaryCommand {
    // A version label that is not strictly greater is refused by AttestationReleaseVerifier's own version-
    // monotonicity check; both legs stage this as the pre-existing baseline before either boot runs.
    private const string StubBaselineVersion = "1.0.0";
    private const string StubReleaseVersion = "2.0.0";
    private const string StubReleaseChannel = "development";
    private const string StubAppId = "puck.world";

    private static CanaryLegRun RunStubLeg(CanaryManifest manifest, bool discriminating, string worldArtifact, string stubArtifact, CanaryBudget budget) {
        try {
            return (discriminating
                ? RunStubDiscriminatingLegCore(
                    budget: budget,
                    manifest: manifest,
                    stubArtifact: stubArtifact,
                    worldArtifact: worldArtifact
                )
                : RunStubPositiveLegCore(
                    budget: budget,
                    manifest: manifest,
                    stubArtifact: stubArtifact,
                    worldArtifact: worldArtifact
                )
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException)) {
            var leg = (discriminating
                ? manifest.Discriminating
                : manifest.Positive
            );

            return CanaryLegRun.InfrastructureFailure(
                leg: leg,
                runDirectory: "not-created",
                reason: exception.Message.ReplaceLineEndings(replacementText: " ")
            );
        }
    }
    private static CanaryLegRun RunStubPositiveLegCore(CanaryManifest manifest, string worldArtifact, string stubArtifact, CanaryBudget budget) =>
        RunInstalledLeg(manifest, worldArtifact, stubArtifact, budget, broken: false);
    private static CanaryLegRun RunStubDiscriminatingLegCore(CanaryManifest manifest, string worldArtifact, string stubArtifact, CanaryBudget budget) =>
        RunInstalledLeg(manifest, worldArtifact, stubArtifact, budget, broken: true);
    private static CanaryLegRun RunInstalledLeg(CanaryManifest manifest, string worldArtifact, string stubArtifact, CanaryBudget budget, bool broken) {
        var leg = (broken ? manifest.Discriminating : manifest.Positive);
        var run = CreateRunDirectory(id: manifest.Id, leg: leg.Name);
        var install = Path.Combine(path1: run, path2: "install");
        var source = Path.Combine(path1: run, path2: "release");
        var payload = Path.Combine(path1: run, path2: "payload");

        MaterializeStubInstall(installDirectory: install, stubArtifact: stubArtifact);
        CopyDirectoryFiles(payload, Path.GetDirectoryName(path: worldArtifact)!, true);
        var prepared = Path.Combine(path1: payload, path2: "worlds", path3: "debug");
        var authored = (Directory.Exists(path: leg.WorldPath) ? leg.WorldPath : Path.GetDirectoryName(path: leg.WorldPath)!);

        CopyDirectoryFiles(destination: prepared, recursive: true, source: authored);
        if (Puck.Cli.Automation.WorldPrepareCommand.Create().Parse([leg.WorldPath, "--output", prepared]).Invoke() != 0) {
            throw new IOException(message: "world prepare failed for the stub release payload");
        }
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var first = PublishRelease(key: key, payloadDirectory: payload, releaseDirectory: source, sequence: 1, version: StubBaselineVersion);

        WriteSelfUpdateConfigFile(cacheRoot: install, path: Path.Combine(path1: run, path2: "update-config.json"), releaseDirectory: source, trustAnchor: first);
        var options = new UpdateOptions(StubAppId, StubReleaseChannel, install, first, "0.0.0");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Puck.Launcher.LauncherServiceRegistration.AddSelfUpdate(services, options, new DirectoryReleaseSource(root: source));
        using (var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services: services)) {
            var updater = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<UpdateService>(provider: provider);
            var applied = updater.ApplyAsync(DateTimeOffset.UtcNow, budget.Cancellation).GetAwaiter().GetResult();

            if (!applied.Applied) { throw new IOException(message: applied.Detail); }
            File.WriteAllText(Path.Combine(path1: run, path2: "initial-install.log"), applied.Detail);
        }
        if (broken) { File.Delete(path: Path.Combine(path1: payload, path2: "Puck.World.exe")); }
        _ = PublishRelease(key: key, payloadDirectory: payload, releaseDirectory: source, sequence: 2, version: StubReleaseVersion);
        string[] Arguments(string version, string state) => [
            "--world", Path.Combine(install, "versions", version, "worlds", "debug", "debug.world.json"),
            "--update-config-file", Path.Combine(path1: run, path2: "update-config.json"),
            "--state-dir", Path.Combine(path1: run, path2: state), "--headless", "true", "--devices", "none"
        ];
        var baseline = LaunchStub(Arguments(state: "state-baseline", version: StubBaselineVersion),
            (broken ? "update.check\nupdate.apply\nwire.errors\n" : ReadScriptWithSubstitution(leg: leg, runDirectory: run)), install, manifest, budget);

        File.WriteAllText(Path.Combine(path1: run, path2: "baseline-stdout.log"), baseline.Stdout);
        File.WriteAllText(Path.Combine(path1: run, path2: "baseline-stderr.log"), baseline.Stderr);
        CliProcessResult boot1;

        if (broken) {
            var failed = LaunchStub(Arguments(state: "state-broken", version: StubReleaseVersion), string.Empty, install, manifest, budget);

            boot1 = failed with { Stdout = (baseline.Stdout + failed.Stdout), Stderr = (baseline.Stderr + failed.Stderr) };
        } else {
            boot1 = baseline;
        }
        var boot2 = LaunchStub(Arguments(state: "state-final", version: (broken ? StubBaselineVersion : StubReleaseVersion)),
            "update.status\nworld.status\nwire.errors\n", install, manifest, budget);

        WriteLegConfirmationFixtures(installDirectory: install, runDirectory: run);
        return BuildStubLegRun(boot1: boot1, boot2: boot2, invariants: [
            new(Detail: $"baseline installed through UpdateService and booted: exit {baseline.ExitCode}", Passed: (!baseline.TimedOut && (baseline.ExitCode == 0))),
            new(Detail: $"final stub boot exited {boot2.ExitCode}", Passed: (!boot2.TimedOut && (boot2.ExitCode == 0))),
            new(Detail: "the selected application cleared its health marker", Passed: !File.Exists(path: Path.Combine(path1: install, path2: "state", path3: "health.json")))
        ], leg: leg, runDirectory: run);
    }
    private static CanaryLegRun BuildStubLegRun(CliProcessResult boot1, CliProcessResult boot2, IReadOnlyList<CanaryAssertionResult> invariants, CanaryLeg leg, string runDirectory) {
        File.WriteAllText(
            contents: boot1.Stdout,
            path: Path.Combine(
                path1: runDirectory,
                path2: "boot1-stdout.log"
            )
        );
        File.WriteAllText(
            contents: boot1.Stderr,
            path: Path.Combine(
                path1: runDirectory,
                path2: "boot1-stderr.log"
            )
        );
        File.WriteAllText(
            contents: boot2.Stdout,
            path: Path.Combine(
                path1: runDirectory,
                path2: "boot2-stdout.log"
            )
        );
        File.WriteAllText(
            contents: boot2.Stderr,
            path: Path.Combine(
                path1: runDirectory,
                path2: "boot2-stderr.log"
            )
        );

        var transcript = new CanaryTranscript(
            RunDirectory: runDirectory,
            Stderr: [.. SplitLines(text: boot1.Stderr), .. SplitLines(text: boot2.Stderr)],
            Stdout: [.. SplitLines(text: boot1.Stdout), .. SplitLines(text: boot2.Stdout)]
        );

        return new CanaryLegRun(
            Assertions: CanaryAssertions.Evaluate(
                leg: leg,
                primaryTranscript: transcript
            ),
            AuthorityEndpoint: string.Empty,
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
    // Every stub boot ends the World it launches the way every other leg does: the runner's quit follows the boot's
    // input, and --exit-after-seconds at the leg's own timeout stops a World this runner can no longer kill.
    private static CliProcessResult LaunchStub(IReadOnlyList<string> arguments, string input, string installDirectory, CanaryManifest manifest, CanaryBudget budget) {
        var result = CliProcess.RunCaptured(
            arguments: [
                .. arguments,
                "--exit-after-seconds", manifest.TimeoutSeconds.ToString(provider: CultureInfo.InvariantCulture),
            ],
            cancellationToken: budget.Cancellation,
            fileName: Path.Combine(
                path1: installDirectory,
                path2: "Puck.Launcher.Stub.exe"
            ),
            input: (input + RunnerQuit),
            timeout: TimeSpan.FromSeconds(value: manifest.TimeoutSeconds)
        );

        budget.Tally.StubLaunched(result: result);

        return result;
    }
    private static string ReadScriptWithSubstitution(CanaryLeg leg, string runDirectory) {
        var input = File.ReadAllText(path: leg.ScriptPath)
            .Replace(
            oldValue: "{run}",
            newValue: runDirectory.Replace(
                newChar: '\\',
                oldChar: '/'
            ),
            comparisonType: StringComparison.Ordinal
        );

        return (input.EndsWith(value: '\n')
            ? input
            : (input + Environment.NewLine)
        );
    }
    // <installDirectory>/Puck.Launcher.Stub.exe reads stub.json/current/versions/ from its own directory
    // (AppContext.BaseDirectory) — the stub build output is copied here so nothing this leg does can touch the
    // artifact the other legs in this run share. The build log sits outside that output directory.
    private static void MaterializeStubInstall(string installDirectory, string stubArtifact) {
        Directory.CreateDirectory(path: installDirectory);
        CopyDirectoryFiles(
            destination: installDirectory,
            recursive: false,
            source: Path.GetDirectoryName(path: stubArtifact)!
        );
        File.WriteAllText(
            contents: JsonSerializer.Serialize(value: new StubConfigurationFile(
                AppExecutableFileName: "Puck.World.exe",
                MaxAttempts: 1
            )),
            path: Path.Combine(
                path1: installDirectory,
                path2: "stub.json"
            )
        );
    }
    private static void CopyDirectoryFiles(string destination, string source, bool recursive) {
        foreach (var file in Directory.EnumerateFiles(
            path: source,
            searchOption: (recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly),
            searchPattern: "*"
        )) {
            var relative = Path.GetRelativePath(
                path: file,
                relativeTo: source
            );
            var target = Path.Combine(
                path1: destination,
                path2: relative
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(
                destFileName: target,
                overwrite: true,
                sourceFileName: file
            );
        }
    }
    // Publish the entire prepared application. The ordinary stager alone creates versions/<version>.
    private static ReleaseTrustAnchor PublishRelease(string releaseDirectory, string payloadDirectory, string version, ECDsa key, ulong sequence) {
        File.WriteAllText(Path.Combine(path1: payloadDirectory, path2: "release-version.txt"), version);
        var exit = PublishCommand.Create().Parse([
            "--rid", UpdateService.CurrentRid(), "--input", payloadDirectory,
            "--output", releaseDirectory, "--app", StubAppId, "--channel", StubReleaseChannel,
            "--version", version, "--state-generation", "0"
        ]).Invoke();

        if (exit != 0) { throw new IOException(message: $"puck publish exited {exit}"); }
        var path = Path.Combine(path1: releaseDirectory, path2: StubReleaseChannel, path3: "manifest.json");
        var unsigned = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllBytes(path: path), DocumentJsonOptions.Shared)!;
        var signed = DevelopmentReleaseSigner.Sign(manifest: unsigned, root: key, sequence: sequence);

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(signed.Manifest, DocumentJsonOptions.Shared));
        return signed.TrustAnchor;
    }
    private static void WriteSelfUpdateConfigFile(string cacheRoot, string path, string releaseDirectory, ReleaseTrustAnchor trustAnchor) => File.WriteAllText(
        contents: JsonSerializer.Serialize(value: new SelfUpdateConfigFile(
            CacheRoot: cacheRoot,
            Channel: StubReleaseChannel,
            ReleaseSourceDirectory: releaseDirectory,
            TrustAnchorAlgorithm: trustAnchor.Algorithm,
            TrustAnchorDomain: trustAnchor.Domain,
            TrustAnchorPublicKeySubjectPublicKeyInfoBase64: trustAnchor.PublicKeySubjectPublicKeyInfoBase64
        )),
        path: path
    );
    // The proof a puck.release.manifest.v1-shaped positive.expect can read without parsing any wire format: literal
    // expected-version fixtures beside a snapshot of the install's own current pointer after both boots, checked
    // through the shared filesDiffer assertion. Both fixtures are written into every leg's run directory
    // (regardless of which one that leg's own expect references) because RunSelected re-evaluates the POSITIVE
    // leg's assertions against the DISCRIMINATING leg's transcript to prove the positive observation turns red —
    // that comparison reads files from the discriminating leg's own run directory.
    private static void WriteLegConfirmationFixtures(string installDirectory, string runDirectory) {
        File.Copy(
            destFileName: Path.Combine(
                path1: runDirectory,
                path2: "current-after"
            ),
            overwrite: true,
            sourceFileName: Path.Combine(
                path1: installDirectory,
                path2: "current"
            )
        );
        File.WriteAllText(
            contents: StubBaselineVersion,
            path: Path.Combine(
                path1: runDirectory,
                path2: $"expected-{StubBaselineVersion}.txt"
            )
        );
        File.WriteAllText(
            contents: StubReleaseVersion,
            path: Path.Combine(
                path1: runDirectory,
                path2: $"expected-{StubReleaseVersion}.txt"
            )
        );
    }

    private sealed record StubConfigurationFile(string AppExecutableFileName, int MaxAttempts);
}
