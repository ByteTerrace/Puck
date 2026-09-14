using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Puck.Storage;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ConfinedStorageLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LongPathsSupportAtomicWritesReadBackAndListing() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var id = Guid.NewGuid();
        var key = (string.Join(
            separator: '/',
            values: Enumerable.Repeat(
                new string(
                    c: 'a',
                    count: 60
                ),
                5
            )
        ) + "/root.json");

        Assert.True(condition: (Path.Combine(
            path1: target.RootPath,
            path2: id.ToString(),
            path3: key
        ).Length > 260));
        var address = new ObjectBlobAddress(
            Key: key,
            ObjectId: id
        );
        var first = await store.WriteAsync(
            target,
            address,
            "original"u8.ToArray(),
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Cancel
        );

        Assert.True(condition: first.Succeeded);
        var second = await store.WriteAsync(
            target,
            address,
            "replacement"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            first.VersionToken,
            Cancel
        );

        Assert.True(condition: second.Succeeded);
        Assert.Equal(
            "replacement",
            Encoding.UTF8.GetString(bytes: (await store.ReadAsync(
                target,
                address,
                Cancel
            ))!.Value.Content.Span)
        );
        Assert.Equal(
            new[] { key },
            await store.ListAsync(
                target,
                id,
                "",
                Cancel
            )
        );
        Assert.Equal(
            "replacement",
            Encoding.UTF8.GetString(bytes: ConfinedFile.ReadAllBytes(
                Path.Combine(
                    path1: target.RootPath,
                    path2: id.ToString(),
                    path3: key
                ),
                100
            ))
        );
    }
    [Fact]
    public void HostConfigurationReadsAreBoundedAndRejectLinkedParents() {
        using var directory = new TempWorldDirectory();
        var file = Path.Combine(
            path1: directory.RootPath,
            path2: "extensions.json"
        );

        File.WriteAllText(
            contents: "{}",
            path: file
        );
        Assert.Equal(
            "{}",
            Encoding.UTF8.GetString(bytes: ConfinedFile.ReadAllBytes(
                maximumBytes: 2,
                path: file
            ))
        );
        Assert.Throws<IOException>(testCode: () => ConfinedFile.ReadAllBytes(
            maximumBytes: 1,
            path: file
        ));
        var link = Path.Combine(
            path1: directory.RootPath,
            path2: "linked"
        );

        CreateDirectoryLink(
            name: link,
            target: directory.RootPath
        );
        try { Assert.Throws<IOException>(testCode: () => ConfinedFile.ReadAllBytes(
            Path.Combine(
                path1: link,
                path2: "extensions.json"
            ),
            2
        )); } finally { Directory.Delete(path: link); }
    }
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("C:/escape")]
    [InlineData("nested/C:escape")]
    [InlineData("file:stream")]
    [InlineData("CON.txt")]
    [InlineData("aux")]
    [InlineData("LPT1")]
    [InlineData("COM¹.log")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData(" nested/value")]
    [InlineData(".puck-lock")]
    [InlineData("nested/.puck-file")]
    [Theory]
    public async Task UnsafeLogicalKeysNeverReachTheFilesystem(string key) {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "private"
        ));

        await Assert.ThrowsAsync<ArgumentException>(testCode: () => store.WriteAsync(
            target,
            new(
                Guid.NewGuid(),
                key
            ),
            "data"u8.ToArray(),
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Cancel
        ).AsTask());
        Assert.False(condition: Directory.Exists(path: target.RootPath));
    }
    [Fact]
    public async Task LinkedDirectoryCannotRedirectReadWriteOrList() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "private"
        ));
        var id = Guid.NewGuid();
        var parent = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString()
        );
        var outside = Path.Combine(
            path1: directory.RootPath,
            path2: "outside"
        );

        Directory.CreateDirectory(path: parent);
        Directory.CreateDirectory(path: outside);
        File.WriteAllText(
            Path.Combine(
                path1: outside,
                path2: "secret"
            ),
            "untouched"
        );
        var link = Path.Combine(
            path1: parent,
            path2: "linked"
        );

        CreateDirectoryLink(
            name: link,
            target: outside
        );
        try {
            var address = new ObjectBlobAddress(
                Key: "linked/secret",
                ObjectId: id
            );

            await Assert.ThrowsAsync<IOException>(testCode: () => store.ReadAsync(
                target,
                address,
                Cancel
            ).AsTask());
            await Assert.ThrowsAsync<IOException>(testCode: () => store.WriteAsync(
                target,
                address,
                "changed"u8.ToArray(),
                ObjectBlobWriteMode.Overwrite,
                cancellationToken: Cancel
            ).AsTask());
            await Assert.ThrowsAsync<IOException>(testCode: () => store.ListAsync(
                target,
                id,
                "",
                Cancel
            ).AsTask());
            Assert.Equal(
                "untouched",
                File.ReadAllText(path: Path.Combine(
                    path1: outside,
                    path2: "secret"
                ))
            );
        } finally { Directory.Delete(path: link); }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task LinkedFilesCannotExposeOrModifyAnotherFile(bool hardLink) {
        Assert.SkipWhen(
            condition: (OperatingSystem.IsWindows() && !hardLink),
            reason: "Windows file-symlink creation needs an OS privilege; Linux covers this case, Windows separately covers junctions and hard links."
        );
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "private"
        ));
        var id = Guid.NewGuid();
        var parent = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString()
        );

        Directory.CreateDirectory(path: parent);
        var outside = Path.Combine(
            path1: directory.RootPath,
            path2: "secret"
        );

        File.WriteAllText(
            contents: "untouched",
            path: outside
        );
        var link = Path.Combine(
            path1: parent,
            path2: "linked"
        );

        if (hardLink) {
            if (OperatingSystem.IsWindows()) { Assert.True(condition: CreateHardLink(
                existing: outside,
                name: link,
                security: 0
            )); } else { Assert.Equal(
                0,
                Link(
                    existing: outside,
                    name: link
                )
            ); }
        } else { File.CreateSymbolicLink(
            path: link,
            pathToTarget: outside
        ); }
        try {
            var address = new ObjectBlobAddress(
                Key: "linked",
                ObjectId: id
            );

            await Assert.ThrowsAsync<IOException>(testCode: () => store.ReadAsync(
                target,
                address,
                Cancel
            ).AsTask());
            await Assert.ThrowsAsync<IOException>(testCode: () => store.WriteAsync(
                target,
                address,
                "changed"u8.ToArray(),
                ObjectBlobWriteMode.Overwrite,
                cancellationToken: Cancel
            ).AsTask());
            Assert.Equal(
                "untouched",
                File.ReadAllText(path: outside)
            );
        } finally { File.Delete(path: link); }
    }
    [Fact]
    public async Task FailedConditionalCreateLeavesNoBlob_AndPublicationHidesStorageMetadata() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(
            Key: "state/value",
            ObjectId: id
        );
        var failed = await store.WriteAsync(
            target,
            address,
            "data"u8.ToArray(),
            ObjectBlobWriteMode.Overwrite,
            "missing-token",
            Cancel
        );

        Assert.True(condition: failed.PreconditionFailed);
        Assert.Null(value: await store.ReadAsync(
            target,
            address,
            Cancel
        ));
        Assert.Empty(collection: await store.ListAsync(
            target,
            id,
            "",
            Cancel
        ));
        var first = await store.WriteAsync(
            target,
            address,
            "original"u8.ToArray(),
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Cancel
        );
        var contenders = Enumerable.Range(
            count: 8,
            start: 0
        ).Select(selector: i => Task.Run(
            async () => await store.WriteAsync(
                target,
                address,
                new byte[] { ((byte)i) },
                ObjectBlobWriteMode.Overwrite,
                first.VersionToken,
                Cancel
            ),
            Cancel
        ));

        Assert.Single(
            collection: await Task.WhenAll(tasks: contenders),
            predicate: result => result.Succeeded
        );
        Assert.Equal(
            new[] { "state/value" },
            await store.ListAsync(
                target,
                id,
                "state",
                Cancel
            )
        );
        Assert.Equal(
            1,
            (await store.ReadAsync(
                target,
                address,
                Cancel
            ))!.Value.Content.Length
        );
    }
    [Fact]
    public async Task BackendByteAndTraversalBudgetsAlsoBoundPreexistingData() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(
            Path.Combine(
                path1: directory.RootPath,
                path2: "private"
            ),
            maximumBlobBytes: 4,
            maximumListEntries: 2
        );
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(
            Key: "large",
            ObjectId: id
        );

        await Assert.ThrowsAsync<IOException>(testCode: () => store.WriteAsync(
            target,
            address,
            new byte[5],
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Cancel
        ).AsTask());
        Assert.False(condition: Directory.Exists(path: target.RootPath));
        var parent = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString()
        );

        Directory.CreateDirectory(path: parent);
        File.WriteAllBytes(
            Path.Combine(
                path1: parent,
                path2: "large"
            ),
            new byte[5]
        );
        await Assert.ThrowsAsync<IOException>(testCode: () => store.ReadAsync(
            target,
            address,
            Cancel
        ).AsTask());
        Directory.CreateDirectory(path: Path.Combine(
            path1: parent,
            path2: "unrelated"
        ));
        Directory.CreateDirectory(path: Path.Combine(
            path1: parent,
            path2: "unrelated",
            path3: "child"
        ));
        await Assert.ThrowsAsync<IOException>(testCode: () => store.ListAsync(
            target,
            id,
            "wanted",
            Cancel
        ).AsTask());
    }
    [Fact]
    public async Task NamespaceCannotChooseAnotherObjectOrEscapeItsPrefix_AndCanBeRevoked() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var id = Guid.NewGuid();
        using var first = new ObjectBlobNamespace(
            store,
            target,
            id,
            "extension/first",
            32,
            writable: true
        );
        using var second = new ObjectBlobNamespace(
            store,
            target,
            id,
            "extension/second",
            32,
            writable: true
        );

        await first.WriteAsync(
            "memo",
            "first"u8.ToArray(),
            cancellationToken: Cancel
        );
        Assert.Null(value: await second.ReadAsync(
            "memo",
            Cancel
        ));
        await Assert.ThrowsAsync<ArgumentException>(testCode: () => second.ReadAsync(
            "../first/memo",
            Cancel
        ).AsTask());
        await Assert.ThrowsAsync<IOException>(testCode: () => first.WriteAsync(
            "large",
            new byte[33],
            cancellationToken: Cancel
        ).AsTask());
        using var readOnly = new ObjectBlobNamespace(
            store,
            target,
            id,
            "extension/first",
            32
        );
        using var differentCase = new ObjectBlobNamespace(
            store,
            target,
            id,
            "extension/FIRST",
            32
        );
        using var ancestorName = new ObjectBlobNamespace(
            store,
            target,
            id,
            "extension",
            32
        );

        Assert.Null(value: await differentCase.ReadAsync(
            "memo",
            Cancel
        ));
        Assert.Null(value: await ancestorName.ReadAsync(
            "first/memo",
            Cancel
        ));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(testCode: () => readOnly.WriteAsync(
            "memo",
            "x"u8.ToArray(),
            cancellationToken: Cancel
        ).AsTask());
        var active = true;
        using var scoped = first.WithLifetime(isActive: () => active);

        Assert.NotNull(value: await scoped.ReadAsync(
            "memo",
            Cancel
        ));
        active = false;
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => scoped.ReadAsync(
            "memo",
            Cancel
        ).AsTask());
        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => first.ReadAsync(
            "memo",
            Cancel
        ).AsTask());
    }
    [Fact]
    public async Task ConcurrentDirectoryReplacementCannotRedirectAnOpenedRead() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(
            path1: directory.RootPath,
            path2: "private"
        ));
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(
            Key: "slot/value",
            ObjectId: id
        );

        await store.WriteAsync(
            target,
            address,
            "inside"u8.ToArray(),
            ObjectBlobWriteMode.CreateOnly,
            cancellationToken: Cancel
        );
        var slot = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString(),
            path3: "slot"
        );
        var moved = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString(),
            path3: "moved"
        );
        var outside = Path.Combine(
            path1: directory.RootPath,
            path2: "outside"
        );

        Directory.CreateDirectory(path: outside);
        File.WriteAllText(
            Path.Combine(
                path1: outside,
                path2: "value"
            ),
            "secret"
        );
        var stagedLink = Path.Combine(
            path1: target.RootPath,
            path2: id.ToString(),
            path3: "junction"
        );

        CreateDirectoryLink(
            name: stagedLink,
            target: outside
        );
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Cancel);
        var started = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        // This blocking race loop needs its own thread, even when other tests occupy the pool.
        var swaps = Task.Factory.StartNew(
            action: () => {
            while (!stop.IsCancellationRequested) {
                try { Directory.Move(
                    destDirName: moved,
                    sourceDirName: slot
                ); } catch (IOException) { Thread.Yield(); continue; }
                try { MoveWithRetry(
                    destination: slot,
                    source: stagedLink
                ); started.TrySetResult(); Thread.Yield(); } finally {
                    if (Directory.Exists(path: slot)) { MoveWithRetry(
                        destination: stagedLink,
                        source: slot
                    ); }
                    MoveWithRetry(
                        destination: slot,
                        source: moved
                    );
                }
            }
        },
            cancellationToken: Cancel,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        try {
            await started.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 5),
                Cancel
            );
            for (var i = 0; (i < 200); i++) {
                try {
                    var read = await store.ReadAsync(
                        target,
                        address,
                        Cancel
                    );

                    if (read is not null) { Assert.Equal(
                        "inside",
                        Encoding.UTF8.GetString(bytes: read.Value.Content.Span)
                    ); }
                } catch (IOException) { /* A raced or linked lookup must fail closed. */ }
            }
        } finally { await stop.CancelAsync(); await swaps; Directory.Delete(path: stagedLink); }
        Assert.Equal(
            "secret",
            File.ReadAllText(path: Path.Combine(
                path1: outside,
                path2: "value"
            ))
        );
    }

    private static void MoveWithRetry(string source, string destination) {
        var until = (Environment.TickCount64 + 5000);

        while (true) {
            try { Directory.Move(
                destDirName: destination,
                sourceDirName: source
            ); return; } catch (IOException) when ((Environment.TickCount64 < until)) { Thread.Yield(); }
        }
    }
    private static void CreateDirectoryLink(string name, string target) {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(
            path: name,
            pathToTarget: target
        ); return; }
        // NTFS junctions exercise the reparse-point boundary without requiring SeCreateSymbolicLinkPrivilege.
        Directory.CreateDirectory(path: name);
        using var handle = OpenDirectory(
            access: 0x40000000,
            disposition: 3,
            flags: 0x02200000,
            name: name,
            security: 0,
            share: 7,
            template: 0
        );

        Assert.False(condition: handle.IsInvalid);
        var substitute = Encoding.Unicode.GetBytes(s: ("\\??\\" + Path.GetFullPath(path: target)));
        var print = Encoding.Unicode.GetBytes(s: Path.GetFullPath(path: target));
        var buffer = new byte[((20 + substitute.Length) + print.Length)];

        BitConverter.TryWriteBytes(
            destination: buffer.AsSpan(start: 0),
            value: 0xA0000003u
        );
        BitConverter.TryWriteBytes(
            destination: buffer.AsSpan(start: 4),
            value: checked((ushort)(buffer.Length - 8))
        );
        BitConverter.TryWriteBytes(
            destination: buffer.AsSpan(start: 10),
            value: checked((ushort)substitute.Length)
        );
        BitConverter.TryWriteBytes(
            destination: buffer.AsSpan(start: 12),
            value: checked((ushort)(substitute.Length + 2))
        );
        BitConverter.TryWriteBytes(
            destination: buffer.AsSpan(start: 14),
            value: checked((ushort)print.Length)
        );
        substitute.CopyTo(
            array: buffer,
            index: 16
        );
        print.CopyTo(
            array: buffer,
            index: (18 + substitute.Length)
        );
        Assert.True(condition: SetReparse(
            handle,
            0x900A4,
            buffer,
            ((uint)buffer.Length),
            0,
            0,
            out _,
            0
        ));
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string name, string existing, nint security);
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Link(string existing, string name);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetReparse(SafeFileHandle handle, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
