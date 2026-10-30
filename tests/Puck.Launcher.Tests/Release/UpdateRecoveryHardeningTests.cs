using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Puck.Assets;
using Puck.Launcher.Release;
using Puck.Launcher.Stub;
using Xunit;

namespace Puck.Launcher.Tests.Release;

public sealed class UpdateRecoveryHardeningTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    private string Current => Path.Combine(path1: m_root.RootPath, path2: "current");
    private string Marker => Path.Combine(path1: m_root.RootPath, path2: "state", path3: "health.json");

    private string Version(string version) => Path.Combine(path1: m_root.RootPath, path2: "versions", path3: version);
    private void PendingHealth(string version = "1.0.1") {
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: Marker)!);
        File.WriteAllText(path: Marker, contents: "pending");
        File.WriteAllText(path: Current, contents: version);
    }

    [Fact]
    public void HealthClearingRequiresTheSelectedExecutableAndTheInstallLease() {
        PendingHealth();
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.0", runningDirectory: Version(version: "1.0.0"));
        Assert.True(condition: File.Exists(path: Marker));
        // Reading the new pointer must not let an old executable report the new version healthy.
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.0"));
        Assert.True(condition: File.Exists(path: Marker));
        using (StubInstall.AcquireUpdateLease(installRoot: m_root.RootPath)) {
            HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.1"));
            Assert.True(condition: File.Exists(path: Marker));
        }
        HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.1", runningDirectory: Version(version: "1.0.1"));
        Assert.False(condition: File.Exists(path: Marker));
    }
    [Fact]
    public async Task ClearHealthy_PrunesExcessVersionsAndUnreferencedObjects_WhileKeepingCurrentLastGoodRecentAndInUseVersions() {
        var store = new ContentAddressedStore(root: Path.Combine(path1: m_root.RootPath, path2: "objects"));
        var pins = new Dictionary<string, ContentPin>(comparer: StringComparer.Ordinal);

        foreach (var version in new[] { "1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5" }) {
            var bytes = Encoding.UTF8.GetBytes(s: $"binary-{version}");

            Directory.CreateDirectory(path: Version(version: version));
            File.WriteAllBytes(path: Path.Combine(path1: Version(version: version), path2: "app.dll"), bytes: bytes);
            pins[version] = store.Put(content: bytes);
        }
        File.WriteAllText(path: Path.Combine(path1: m_root.RootPath, path2: "last-good"), contents: "1.0.1");
        PendingHealth(version: "1.0.5");

        var pipeName = $"puck-prune-{Guid.NewGuid():N}";
        using var signal = new NamedPipeServerStream(pipeName: pipeName, direction: PipeDirection.InOut,
            maxNumberOfServerInstances: 1, transmissionMode: PipeTransmissionMode.Byte, options: PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(fileName: "dotnet") {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(item: typeof(UpdateRecoveryHardeningTests).Assembly.Location);
        start.ArgumentList.Add(item: "--filter-method");
        start.ArgumentList.Add(item: $"{typeof(UpdateRecoveryHardeningTests).FullName}.{nameof(HoldVersionProcessChild)}");
        start.ArgumentList.Add(item: "--explicit");
        start.ArgumentList.Add(item: "only");
        using var child = (Process.Start(startInfo: start) ?? throw new InvalidOperationException(message: "The holder child did not start."));
        var output = child.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var errors = child.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

        await child.StandardInput.WriteLineAsync(buffer: Path.Combine(path1: Version(version: "1.0.0"), path2: "app.dll").AsMemory(),
            cancellationToken: TestContext.Current.CancellationToken);
        await child.StandardInput.WriteLineAsync(buffer: pipeName.AsMemory(),
            cancellationToken: TestContext.Current.CancellationToken);
        child.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 30));
        try {
            var connection = signal.WaitForConnectionAsync(cancellationToken: deadline.Token);
            var exited = child.WaitForExitAsync(cancellationToken: deadline.Token);

            if (await Task.WhenAny(task1: connection, task2: exited) == exited) {
                Assert.Fail(message: $"Holder child exited before locking 1.0.0: {await output}{await errors}");
            }
            await connection;
            var ready = new byte[1];

            Assert.Equal(expected: 1, actual: await signal.ReadAsync(buffer: ready, cancellationToken: deadline.Token));
            Assert.Equal(expected: ((byte)1), actual: ready[0]);

            var firstPrune = HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.5", runningDirectory: Version(version: "1.0.5"));

            Assert.True(condition: firstPrune.Cleared);
            Assert.Equal(expected: ["1.0.5", "1.0.4", "1.0.3", "1.0.1", "1.0.0"], actual: firstPrune.KeptVersions);
            Assert.Equal(expected: ["1.0.2"], actual: firstPrune.DeletedVersions);
            Assert.Equal(expected: 1, actual: firstPrune.DeletedObjects);
            Assert.False(condition: File.Exists(path: Marker));

            foreach (var kept in new[] { "1.0.5", "1.0.4", "1.0.3", "1.0.1", "1.0.0" }) {
                Assert.True(condition: Directory.Exists(path: Version(version: kept)), userMessage: $"Expected version {kept} to be kept.");
                Assert.True(condition: File.Exists(path: Path.Combine(path1: Version(version: kept), path2: "app.dll")), userMessage: $"Expected {kept}/app.dll to remain intact.");
                Assert.True(condition: store.Contains(pin: pins[kept]), userMessage: $"Expected object for {kept} to be retained.");
            }
            Assert.False(condition: Directory.Exists(path: Version(version: "1.0.2")));
            Assert.False(condition: store.Contains(pin: pins["1.0.2"]));

            signal.WriteByte(value: 1);
            signal.Flush();
            await child.WaitForExitAsync(cancellationToken: deadline.Token);
        } finally {
            signal.Dispose();
            if (!child.HasExited) {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);
            }
            _ = await output;
            _ = await errors;
        }

        PendingHealth(version: "1.0.5");
        var secondPrune = HealthGateFile.ClearHealthy(cacheRoot: m_root.RootPath, version: "1.0.5", runningDirectory: Version(version: "1.0.5"));

        Assert.True(condition: secondPrune.Cleared);
        Assert.Equal(expected: ["1.0.5", "1.0.4", "1.0.3", "1.0.1"], actual: secondPrune.KeptVersions);
        Assert.Equal(expected: ["1.0.0"], actual: secondPrune.DeletedVersions);
        Assert.Equal(expected: 1, actual: secondPrune.DeletedObjects);
        Assert.False(condition: File.Exists(path: Marker));
        Assert.False(condition: Directory.Exists(path: Version(version: "1.0.0")));
        Assert.False(condition: store.Contains(pin: pins["1.0.0"]));

        foreach (var kept in new[] { "1.0.5", "1.0.4", "1.0.3", "1.0.1" }) {
            Assert.True(condition: Directory.Exists(path: Version(version: kept)), userMessage: $"Expected version {kept} to be kept after 1.0.0 exited.");
            Assert.True(condition: store.Contains(pin: pins[kept]), userMessage: $"Expected object for {kept} to be retained after 1.0.0 exited.");
        }
    }
    [Fact(Explicit = true)]
    public static async Task HoldVersionProcessChild() {
        var filePath = (await Console.In.ReadLineAsync(cancellationToken: TestContext.Current.CancellationToken))!;
        var pipeName = (await Console.In.ReadLineAsync(cancellationToken: TestContext.Current.CancellationToken))!;
        using var held = new FileStream(path: filePath, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.Read);
        using var signal = new NamedPipeClientStream(serverName: ".", pipeName: pipeName, direction: PipeDirection.InOut);

        signal.Connect(timeout: 30_000);
        signal.WriteByte(value: 1);
        signal.Flush();
        _ = signal.ReadByte();
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ApplyingOverAnUnprovenCurrentPreservesLastGood(bool hasLastGood) {
        PendingHealth();
        var lastGood = Path.Combine(path1: m_root.RootPath, path2: "last-good");

        if (hasLastGood) { File.WriteAllText(contents: "1.0.0", path: lastGood); }
        var content = "new binary"u8.ToArray();

        Directory.CreateDirectory(path: Version(version: "1.0.2"));
        File.WriteAllBytes(path: Path.Combine(path1: Version(version: "1.0.2"), path2: "app.dll"), bytes: content);
        var manifest = new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null,
            Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "app.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length)])],
            Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema,
            Signature: null, StateGeneration: 1, Version: "1.0.2");
        var result = new FileUpdateApplier().Apply(manifest: manifest, rid: "win-x64", cacheRoot: m_root.RootPath);

        Assert.True(condition: result.Applied, userMessage: result.RefusalReason);
        Assert.Equal(expected: "1.0.2", actual: File.ReadAllText(path: Current));
        Assert.Equal(expected: hasLastGood, actual: File.Exists(path: lastGood));
        if (hasLastGood) { Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: lastGood)); }
    }
    [Fact]
    public void ConsecutiveUpdatesBeforeLaunchingNeverPromoteAnUnstartedVersion() {
        Directory.CreateDirectory(path: m_root.RootPath);
        File.WriteAllText(path: Current, contents: "1.0.0");
        var content = "new binary"u8.ToArray();

        foreach (var version in new[] { "1.0.1", "1.0.2" }) {
            Directory.CreateDirectory(path: Version(version: version));
            File.WriteAllBytes(path: Path.Combine(path1: Version(version: version), path2: "app.dll"), bytes: content);
            var manifest = new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null,
                Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "app.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length)])],
                Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema,
                Signature: null, StateGeneration: 1, Version: version);
            var result = new FileUpdateApplier().Apply(manifest: manifest, rid: "win-x64", cacheRoot: m_root.RootPath);

            Assert.True(condition: result.Applied, userMessage: result.RefusalReason);
            Assert.True(condition: File.Exists(path: Marker));
            Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: Path.Combine(path1: m_root.RootPath, path2: "last-good")));
        }
    }
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("garbage")]
    [InlineData("2147483648")]
    [Theory]
    public void UnknownGenerationCannotAuthorizeRollback(string? text) {
        Directory.CreateDirectory(path: Version(version: "1.0.0"));
        if (text is not null) { File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: "state-generation"), contents: text); }
        Assert.ThrowsAny<IOException>(testCode: () => StubInstall.ReadGeneration(installRoot: m_root.RootPath, version: "1.0.0"));
    }
    [InlineData(0)]
    [InlineData(3)]
    [Theory]
    public void KnownGenerationIsReadExactly(int generation) {
        Directory.CreateDirectory(path: Version(version: "1.0.0"));
        File.WriteAllText(path: Path.Combine(path1: Version(version: "1.0.0"), path2: "state-generation"), contents: generation.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected: generation, actual: StubInstall.ReadGeneration(installRoot: m_root.RootPath, version: "1.0.0"));
    }
    public void Dispose() => m_root.Dispose();
}
