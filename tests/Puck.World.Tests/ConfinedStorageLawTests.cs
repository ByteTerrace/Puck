using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Puck.Storage;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class ConfinedStorageLawTests {
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void HostConfigurationReadsAreBoundedAndRejectLinkedParents() {
        using var directory = new TempWorldDirectory();
        var file = Path.Combine(directory.RootPath, "extensions.json");
        File.WriteAllText(file, "{}");
        Assert.Equal("{}", Encoding.UTF8.GetString(ConfinedFile.ReadAllBytes(file, 2)));
        Assert.Throws<IOException>(() => ConfinedFile.ReadAllBytes(file, 1));
        var link = Path.Combine(directory.RootPath, "linked");
        CreateDirectoryLink(link, directory.RootPath);
        try { Assert.Throws<IOException>(() => ConfinedFile.ReadAllBytes(Path.Combine(link, "extensions.json"), 2)); }
        finally { Directory.Delete(link); }
    }

    [Theory]
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
    public async Task UnsafeLogicalKeysNeverReachTheFilesystem(string key) {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "private"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(target, new(Guid.NewGuid(), key), "data"u8.ToArray(), ObjectBlobWriteMode.CreateOnly, cancellationToken: Cancel).AsTask());
        Assert.False(Directory.Exists(target.RootPath));
    }

    [Fact]
    public async Task LinkedDirectoryCannotRedirectReadWriteOrList() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "private"));
        var id = Guid.NewGuid();
        var parent = Path.Combine(target.RootPath, id.ToString());
        var outside = Path.Combine(directory.RootPath, "outside");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret"), "untouched");
        var link = Path.Combine(parent, "linked");
        CreateDirectoryLink(link, outside);
        try {
            var address = new ObjectBlobAddress(id, "linked/secret");
            await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(target, address, Cancel).AsTask());
            await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(target, address, "changed"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: Cancel).AsTask());
            await Assert.ThrowsAsync<IOException>(() => store.ListAsync(target, id, "", Cancel).AsTask());
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "secret")));
        } finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedFilesCannotExposeOrModifyAnotherFile(bool hardLink) {
        Assert.SkipWhen(OperatingSystem.IsWindows() && !hardLink,
            "Windows file-symlink creation needs an OS privilege; Linux covers this case, Windows separately covers junctions and hard links.");
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "private"));
        var id = Guid.NewGuid();
        var parent = Path.Combine(target.RootPath, id.ToString());
        Directory.CreateDirectory(parent);
        var outside = Path.Combine(directory.RootPath, "secret");
        File.WriteAllText(outside, "untouched");
        var link = Path.Combine(parent, "linked");
        if (hardLink) {
            if (OperatingSystem.IsWindows()) { Assert.True(CreateHardLink(link, outside, 0)); }
            else { Assert.Equal(0, Link(outside, link)); }
        } else { File.CreateSymbolicLink(link, outside); }
        try {
            var address = new ObjectBlobAddress(id, "linked");
            await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(target, address, Cancel).AsTask());
            await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(target, address, "changed"u8.ToArray(), ObjectBlobWriteMode.Overwrite, cancellationToken: Cancel).AsTask());
            Assert.Equal("untouched", File.ReadAllText(outside));
        } finally { File.Delete(link); }
    }

    [Fact]
    public async Task FailedConditionalCreateLeavesNoBlob_AndPublicationHidesStorageMetadata() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(id, "state/value");
        var failed = await store.WriteAsync(target, address, "data"u8.ToArray(), ObjectBlobWriteMode.Overwrite, "missing-token", Cancel);
        Assert.True(failed.PreconditionFailed);
        Assert.Null(await store.ReadAsync(target, address, Cancel));
        Assert.Empty(await store.ListAsync(target, id, "", Cancel));
        var first = await store.WriteAsync(target, address, "original"u8.ToArray(), ObjectBlobWriteMode.CreateOnly, cancellationToken: Cancel);
        var contenders = Enumerable.Range(0, 8).Select(i => Task.Run(async () => await store.WriteAsync(target, address,
            new byte[] { (byte)i }, ObjectBlobWriteMode.Overwrite, first.VersionToken, Cancel), Cancel));
        Assert.Single(await Task.WhenAll(contenders), result => result.Succeeded);
        Assert.Equal(new[] { "state/value" }, await store.ListAsync(target, id, "state", Cancel));
        Assert.Equal(1, (await store.ReadAsync(target, address, Cancel))!.Value.Content.Length);
    }

    [Fact]
    public async Task BackendByteAndTraversalBudgetsAlsoBoundPreexistingData() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "private"), maximumBlobBytes: 4, maximumListEntries: 2);
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(id, "large");
        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(target, address, new byte[5], ObjectBlobWriteMode.CreateOnly, cancellationToken: Cancel).AsTask());
        Assert.False(Directory.Exists(target.RootPath));
        var parent = Path.Combine(target.RootPath, id.ToString());
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(Path.Combine(parent, "large"), new byte[5]);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(target, address, Cancel).AsTask());
        Directory.CreateDirectory(Path.Combine(parent, "unrelated"));
        Directory.CreateDirectory(Path.Combine(parent, "unrelated", "child"));
        await Assert.ThrowsAsync<IOException>(() => store.ListAsync(target, id, "wanted", Cancel).AsTask());
    }

    [Fact]
    public async Task NamespaceCannotChooseAnotherObjectOrEscapeItsPrefix_AndCanBeRevoked() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var id = Guid.NewGuid();
        using var first = new ObjectBlobNamespace(store, target, id, "extension/first", 32, writable: true);
        using var second = new ObjectBlobNamespace(store, target, id, "extension/second", 32, writable: true);
        await first.WriteAsync("memo", "first"u8.ToArray(), cancellationToken: Cancel);
        Assert.Null(await second.ReadAsync("memo", Cancel));
        await Assert.ThrowsAsync<ArgumentException>(() => second.ReadAsync("../first/memo", Cancel).AsTask());
        await Assert.ThrowsAsync<IOException>(() => first.WriteAsync("large", new byte[33], cancellationToken: Cancel).AsTask());
        using var readOnly = new ObjectBlobNamespace(store, target, id, "extension/first", 32);
        using var differentCase = new ObjectBlobNamespace(store, target, id, "extension/FIRST", 32);
        using var ancestorName = new ObjectBlobNamespace(store, target, id, "extension", 32);
        Assert.Null(await differentCase.ReadAsync("memo", Cancel));
        Assert.Null(await ancestorName.ReadAsync("first/memo", Cancel));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => readOnly.WriteAsync("memo", "x"u8.ToArray(), cancellationToken: Cancel).AsTask());
        var active = true;
        using var scoped = first.WithLifetime(() => active);
        Assert.NotNull(await scoped.ReadAsync("memo", Cancel));
        active = false;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scoped.ReadAsync("memo", Cancel).AsTask());
        first.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ReadAsync("memo", Cancel).AsTask());
    }

    [Fact]
    public async Task ConcurrentDirectoryReplacementCannotRedirectAnOpenedRead() {
        using var directory = new TempWorldDirectory();
        var store = PuckStorageTestComposition.BuildStore();
        var target = new DirectoryObjectStorageTarget(Path.Combine(directory.RootPath, "private"));
        var id = Guid.NewGuid();
        var address = new ObjectBlobAddress(id, "slot/value");
        await store.WriteAsync(target, address, "inside"u8.ToArray(), ObjectBlobWriteMode.CreateOnly, cancellationToken: Cancel);
        var slot = Path.Combine(target.RootPath, id.ToString(), "slot");
        var moved = Path.Combine(target.RootPath, id.ToString(), "moved");
        var outside = Path.Combine(directory.RootPath, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "value"), "secret");
        var stagedLink = Path.Combine(target.RootPath, id.ToString(), "junction");
        CreateDirectoryLink(stagedLink, outside);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var swaps = Task.Run(() => {
            while (!stop.IsCancellationRequested) {
                try { Directory.Move(slot, moved); }
                catch (IOException) { Thread.Yield(); continue; }
                try { MoveWithRetry(stagedLink, slot); started.TrySetResult(); Thread.Yield(); }
                finally {
                    if (Directory.Exists(slot)) { MoveWithRetry(slot, stagedLink); }
                    MoveWithRetry(moved, slot);
                }
            }
        }, Cancel);
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Cancel);
            for (var i = 0; i < 200; i++) {
                try {
                    var read = await store.ReadAsync(target, address, Cancel);
                    if (read is not null) { Assert.Equal("inside", Encoding.UTF8.GetString(read.Value.Content.Span)); }
                } catch (IOException) { /* A raced or linked lookup must fail closed. */ }
            }
        } finally { await stop.CancelAsync(); await swaps; Directory.Delete(stagedLink); }
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "value")));
    }

    private static void MoveWithRetry(string source, string destination) {
        var until = Environment.TickCount64 + 5000;
        while (true) {
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (Environment.TickCount64 < until) { Thread.Yield(); }
        }
    }

    private static void CreateDirectoryLink(string name, string target) {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(name, target); return; }
        // NTFS junctions exercise the reparse-point boundary without requiring SeCreateSymbolicLinkPrivilege.
        Directory.CreateDirectory(name);
        using var handle = OpenDirectory(name, 0x40000000, 7, 0, 3, 0x02200000, 0);
        Assert.False(handle.IsInvalid);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[20 + substitute.Length + print.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 0xA0000003u);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BitConverter.TryWriteBytes(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BitConverter.TryWriteBytes(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        Assert.True(SetReparse(handle, 0x900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0));
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
