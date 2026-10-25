using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Attestation;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed partial class UpdateInstallTransactionTests {
    private sealed record ProcessFixture(string Root, ReleaseTrustAnchor Anchor, string Boundary, string Pipe);

    [InlineData("before-reservation")]
    [InlineData("after-reservation")]
    [InlineData("before-pointer")]
    [InlineData("after-pointer")]
    [Theory]
    public async Task KilledInstallProcess_ReleasesItsLeaseAndRecoversOnlyTheAuthorizedManifest(string boundary) {
        var manifest = Manifest();

        Publish(manifest: manifest);
        Directory.CreateDirectory(path: Install);
        File.WriteAllText(path: Current, contents: "1.0.0");
        var fixture = new ProcessFixture(Root: m_root.RootPath, Anchor: m_fixture.TrustAnchor,
            Boundary: boundary, Pipe: $"puck-release-{Guid.NewGuid():N}");
        using var signal = new NamedPipeServerStream(pipeName: fixture.Pipe, direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 1, transmissionMode: PipeTransmissionMode.Byte, options: PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(item: typeof(UpdateInstallTransactionTests).Assembly.Location);
        start.ArgumentList.Add(item: "--filter-method");
        start.ArgumentList.Add(item: $"{typeof(UpdateInstallTransactionTests).FullName}.{nameof(InstallProcessChild)}");
        start.ArgumentList.Add(item: "--explicit");
        start.ArgumentList.Add(item: "only");
        using var child = (Process.Start(startInfo: start) ?? throw new InvalidOperationException(message: "The update child did not start."));
        var output = child.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var errors = child.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

        await child.StandardInput.WriteLineAsync(buffer: JsonSerializer.Serialize(value: fixture, options: DocumentJsonOptions.Shared).AsMemory(),
            cancellationToken: TestContext.Current.CancellationToken);
        child.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 30));
        try {
            var connection = signal.WaitForConnectionAsync(cancellationToken: deadline.Token);
            var exited = child.WaitForExitAsync(cancellationToken: deadline.Token);

            if (await Task.WhenAny(task1: connection, task2: exited) == exited) {
                Assert.Fail(message: $"Updater child exited before reaching {boundary}: {await output}{await errors}");
            }
            await connection;
            var ready = new byte[1];

            Assert.Equal(expected: 1, actual: await signal.ReadAsync(buffer: ready, cancellationToken: deadline.Token));
            Assert.Equal(expected: ((byte)1), actual: ready[0]);
            Assert.Equal(expected: (boundary is "before-pointer" or "after-pointer"), actual: File.Exists(path: Intent));

            // A different process owns the real update lease at this point, not a test substitute lock.
            var concurrent = await Service().ApplyAsync(now: Now, cancellationToken: deadline.Token);

            Assert.False(condition: concurrent.Applied);
            Assert.Contains(expectedSubstring: "busy or unavailable", actualString: concurrent.Detail);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(cancellationToken: deadline.Token);
            Assert.NotEqual(expected: 0, actual: child.ExitCode);
        } finally {
            if (!child.HasExited) {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            _ = await output;
            _ = await errors;
        }

        // The replacement network response cannot replace the old local authorization after a real process death.
        Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
        if (boundary is "before-reservation" or "after-reservation") {
            Assert.False(condition: File.Exists(path: Intent));
            Assert.False(condition: (await Service().ApplyAsync(now: Now.AddHours(hours: 2), cancellationToken: TestContext.Current.CancellationToken)).Applied);
            var fresh = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(condition: fresh.Applied, userMessage: fresh.Detail);
            Assert.Equal(expected: "1.0.2", actual: File.ReadAllText(path: Current));
            return;
        }
        var recovered = await Service(installedVersion: ((boundary == "after-pointer") ? "1.0.1" : "1.0.0"))
            .ApplyAsync(now: Now.AddHours(hours: 2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: recovered.Applied, userMessage: recovered.Detail);
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Current));
        Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: Path.Combine(path1: Install, path2: "last-good")));
        Assert.Equal(expected: Payload, actual: File.ReadAllBytes(path: PayloadPath));
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: Verifier().Verify(manifest: manifest, now: Now, installedVersion: "1.0.0", advanceSequence: false).Accepted);
    }
    // Only the parent above runs this helper; its process is deliberately killed without executing finally blocks.
    [Fact(Explicit = true)]
    public static async Task InstallProcessChild() {
        var input = await Console.In.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fixture = JsonSerializer.Deserialize<ProcessFixture>(json: input, options: DocumentJsonOptions.Shared)!;
        var install = Path.Combine(path1: fixture.Root, path2: "install");
        var source = new DirectoryReleaseSource(root: Path.Combine(path1: fixture.Root, path2: "feed"));
        var trust = new TrustList(entries: [fixture.Anchor.ToTrustListEntry(reach: new HashSet<string> { AttestationReleaseVerifier.Slot }, maximumAge: null)],
            defaultMaximumAge: null, replayAcceptanceHorizon: TimeSpan.FromDays(days: 30));
        var verifier = new AttestationReleaseVerifier(trustList: trust,
            sequenceStore: new FileReleaseSequenceStore(filePath: Path.Combine(path1: install, path2: "sequence")), codec: new CborAttestationCodec());
        var service = new UpdateService(source: source, verifier: new ProcessPausedVerifier(fixture: fixture, inner: verifier),
            stager: new ContentAddressedUpdateStager(source: source,
                cache: new ContentAddressedStore(root: Path.Combine(path1: install, path2: "cache")), cacheRoot: install),
            applier: new ProcessPausedApplier(fixture: fixture),
            options: new UpdateOptions(App: "puck.world", CacheRoot: install, Channel: "stable", InstalledVersion: "1.0.0",
                TrustAnchor: fixture.Anchor, InstallId: new string(c: '0', count: 32)));
        var result = await service.ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Fail(message: $"The process survived its selected interruption: {result.Detail}");
    }

    private static void PauseProcess(ProcessFixture fixture, string boundary) {
        if (fixture.Boundary != boundary) { return; }
        using var signal = new NamedPipeClientStream(serverName: ".", pipeName: fixture.Pipe, direction: PipeDirection.InOut);

        signal.Connect(timeout: 30_000);
        signal.WriteByte(value: 1);
        signal.Flush();
        _ = signal.ReadByte();
        throw new IOException(message: "Parent disconnected without terminating the paused updater.");
    }

    private sealed class ProcessPausedVerifier(IReleaseVerifier inner, ProcessFixture fixture) : IReleaseVerifier {
        public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) =>
            inner.Verify(advanceSequence: advanceSequence, installedVersion: installedVersion, manifest: manifest, now: now);
        public ReleaseVerifyOutcome ReserveInstall(ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion) {
            PauseProcess(boundary: "before-reservation", fixture: fixture);
            var result = inner.ReserveInstall(installedVersion: installedVersion, manifest: manifest, verifiedAt: verifiedAt);

            Assert.True(condition: result.Accepted, userMessage: result.RefusalReason);
            PauseProcess(boundary: "after-reservation", fixture: fixture);
            return result;
        }
    }
    private sealed class ProcessPausedApplier(ProcessFixture fixture) : IUpdateApplier {
        public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, string cacheRoot, bool recoverInterruptedApply = false) {
            PauseProcess(boundary: "before-pointer", fixture: fixture);
            var result = new FileUpdateApplier().Apply(cacheRoot: cacheRoot, manifest: manifest, recoverInterruptedApply: recoverInterruptedApply, rid: rid);

            Assert.True(condition: result.Applied, userMessage: result.RefusalReason);
            PauseProcess(boundary: "after-pointer", fixture: fixture);
            return result;
        }
    }
}
