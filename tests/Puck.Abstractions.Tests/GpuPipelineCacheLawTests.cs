using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for the neutral half of the persistent pipeline cache. <see cref="GpuPipelineCacheFile"/> names one file per
/// backend, device and content in the same format on every backend, reads a missing file as empty, writes only when a
/// creation missed the cache, replaces a file whole in one rename, and leaves the old file whole when the rename is
/// blocked, never a temporary behind, with the write still owed. Opening a file and every hit on it refresh its
/// last-write time; opening keeps the backend's <see cref="GpuPipelineCacheFile.RetainedFiles"/> most recently written
/// files across all its devices, the opened one always among them, deletes the rest, removes each device directory
/// left empty, and never touches a temporary, another backend's tree, or a file another process holds open. Every
/// timestamp is set explicitly; nothing sleeps. A content key depends on every part and its boundaries.
/// <see cref="GpuPipelineCacheWork"/> counts every created pipeline as exactly one hit or one miss, counts every pruned
/// file, and declares exactly those four kinds.
/// </summary>
public sealed class GpuPipelineCacheLawTests : IDisposable {
    private static readonly GpuDeviceIdentity Vulkan = new(
        AdapterName: "Example GPU 4000",
        ApiVersion: "1.4.303",
        Backend: "vulkan",
        DeviceId: 0x2786U,
        DriverVersion: "566.36",
        DriverVersionRaw: 0x8D8D8000UL,
        VendorId: 0x10DEU
    );
    private static readonly GpuDeviceIdentity DirectX = new(
        AdapterName: "Example GPU 4000",
        ApiVersion: "12_2",
        Backend: "directx",
        DeviceId: 0x2786U,
        DriverVersion: "32.0.15.6636",
        DriverVersionRaw: 0x0020_0000_000F_19ECUL,
        VendorId: 0x10DEU
    );
    // The device directories the pruning laws spread kernel sets over; the first is the Vulkan identity's own.
    private static readonly string[] Devices = [Vulkan.CacheKey, "1002-73bf-0000000000000001", "8086-a7a0-0000000000000002"];
    private static readonly DateTime Epoch = new(
        day: 1,
        hour: 0,
        kind: DateTimeKind.Utc,
        minute: 0,
        month: 1,
        second: 0,
        year: 2026
    );

    // A device directory named in the eight-digit driver format the key no longer uses, and one for an older driver.
    private const string OldKeyFormat = "10de-2786-8d8d8000";
    private const string OldDriver = "10de-2786-000000008d8d7000";

    private readonly string m_directory = Directory.CreateTempSubdirectory(prefix: "puck-pipeline-cache-").FullName;

    private string Root => m_directory.Replace(
        newChar: '/',
        oldChar: '\\'
    );

    private GpuPipelineCacheFile Open(GpuDeviceIdentity identity, string contentKey = "0123456789abcdef", GpuPipelineCacheWork? work = null) =>
        GpuPipelineCacheFile.Open(
            identity: identity,
            store: new GpuPipelineCacheStore(
                contentKey: contentKey,
                directory: m_directory
            ),
            work: (work ?? new GpuPipelineCacheWork(backend: identity.Backend))
        );
    private static string KeyOf(int index) =>
        $"{index:x16}";
    private static long Pruned(GpuPipelineCacheWork work) {
        Assert.True(condition: ((IWorkCounterSource)work).TryRead(kind: GpuWork.PipelineCachePruned, value: out var pruned));

        return pruned;
    }
    // Writes a file under the root and sets its last-write time; returns its full path.
    private string Seed(string relative, DateTime written) {
        var path = Path.Join(
            path1: m_directory,
            path2: relative
        );

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
        File.WriteAllBytes(
            bytes: [1],
            path: path
        );
        File.SetLastWriteTimeUtc(
            lastWriteTimeUtc: written,
            path: path
        );

        return path;
    }
    private static void Write(GpuPipelineCacheFile file, byte[] data) {
        file.Count(cacheHit: false);
        file.Persist(
            serialize: static data => data,
            state: data
        );
    }

    public void Dispose() => Directory.Delete(
        path: m_directory,
        recursive: true
    );
    [Fact]
    public void EveryBackendNamesItsFileInOneFormat() {
        // Vendor and device in four hexadecimal digits, the driver version in sixteen on both backends: Vulkan's 32-bit
        // driverVersion is zero-padded to the width of Direct3D's 64-bit user-mode driver version.
        Assert.Equal(
            expected: "10de-2786-000000008d8d8000",
            actual: Vulkan.CacheKey
        );
        Assert.Equal(
            expected: "10de-2786-00200000000f19ec",
            actual: DirectX.CacheKey
        );
        Assert.Equal(
            expected: $"{Root}/vulkan/10de-2786-000000008d8d8000/0123456789abcdef.bin",
            actual: Open(identity: Vulkan).Path
        );
        Assert.Equal(
            expected: $"{Root}/directx/10de-2786-00200000000f19ec/0123456789abcdef.bin",
            actual: Open(identity: DirectX).Path
        );
    }
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("key.bin")]
    [Theory]
    public void AKeyThatIsNotOneSegmentIsRefused(string segment) {
        Assert.ThrowsAny<ArgumentException>(testCode: () => new GpuPipelineCacheStore(
            contentKey: segment,
            directory: m_directory
        ));
        Assert.ThrowsAny<ArgumentException>(testCode: () => Open(identity: Vulkan with { Backend = segment }));
    }
    [Fact]
    public void WithoutAStoreTheCacheIsKeptInMemoryOnly() {
        var file = GpuPipelineCacheFile.Open(
            identity: Vulkan,
            store: null,
            work: new GpuPipelineCacheWork(backend: "vulkan")
        );
        var serialized = 0;

        file.Count(cacheHit: false);
        file.Persist(
            serialize: _ => {
                serialized++;

                return new byte[] { 1 };
            },
            state: 0
        );

        Assert.Null(@object: file.Path);
        Assert.Null(@object: file.Read());
        Assert.Equal(
            actual: serialized,
            expected: 0
        );
        Assert.Empty(collection: Directory.GetFileSystemEntries(path: m_directory));
    }
    [Fact]
    public void OnlyAMissedCreationOwesAWrite() {
        var file = Open(identity: Vulkan);
        var serialized = 0;

        ReadOnlyMemory<byte> Serialize(int _) {
            serialized++;

            return new byte[] { 7 };
        }

        file.Count(cacheHit: true);
        file.Persist(
            serialize: Serialize,
            state: 0
        );
        Assert.Equal(
            actual: serialized,
            expected: 0
        );
        Assert.Null(@object: file.Read());

        file.Count(cacheHit: false);
        file.Persist(
            serialize: Serialize,
            state: 0
        );
        file.Persist(
            serialize: Serialize,
            state: 0
        );
        Assert.Equal(
            actual: serialized,
            expected: 1
        );
        Assert.Equal(
            actual: file.Read(),
            expected: [7]
        );
    }
    [Fact]
    public void AMissingFileReadsAsEmptyAndAWriteReplacesTheFileWhole() {
        var file = Open(
            contentKey: "key",
            identity: Vulkan
        );

        Assert.Null(@object: file.Read());
        Write(
            data: [1, 2, 3, 4, 5, 6],
            file: file
        );
        Assert.Equal(
            actual: file.Read(),
            expected: [1, 2, 3, 4, 5, 6]
        );

        // A second writer's shorter cache replaces the first whole; nothing of the longer file survives.
        Write(
            data: [9, 8],
            file: file
        );
        Assert.Equal(
            actual: file.Read(),
            expected: [9, 8]
        );
        Assert.Equal(
            actual: Directory.GetFiles(path: Path.GetDirectoryName(path: file.Path)!).Select(selector: Path.GetFileName),
            expected: ["key.bin"]
        );
    }
    [Fact]
    public void AWriteThatCannotReplaceTheFileLeavesItWholeAndStaysOwed() {
        var file = Open(
            contentKey: "key",
            identity: DirectX
        );
        var path = file.Path!;

        Write(
            data: [1],
            file: file
        );

        // Another process holding the file without sharing it for deletion blocks the rename: the old file stays whole,
        // no temporary is left behind, and the write stays owed, so the next Persist writes it without another miss.
        using (new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: path,
            share: FileShare.Read
        )) {
            Write(
                data: [2],
                file: file
            );
        }

        Assert.Equal(
            actual: file.Read(),
            expected: [1]
        );
        Assert.Single(collection: Directory.GetFiles(path: Path.GetDirectoryName(path: path)!));
        file.Persist(
            serialize: static data => data,
            state: new byte[] { 3 }
        );
        Assert.Equal(
            actual: file.Read(),
            expected: [3]
        );
    }
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Theory]
    public void OpeningOneOfEightKernelSetsEvictsNone(int deviceCount) {
        // Eight kernel sets spread over one to three devices, the opened one the least recently written of them all.
        var files = Enumerable.Range(
            count: GpuPipelineCacheFile.RetainedFiles,
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Devices[(index % deviceCount)]}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: index)
        )).ToArray();
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan,
            work: work
        );

        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: files
        );
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 0L
        );
    }
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    [InlineData(6, 0)]
    [Theory]
    public void ANinthKernelSetEvictsTheLeastRecentlyWrittenOther(int opened, int evicted) {
        // Nine kernel sets over three devices, kernel set i written i minutes after the epoch; the opened one is on the
        // Vulkan identity's device and counts as the most recent whatever its time.
        var files = Enumerable.Range(
            count: (GpuPipelineCacheFile.RetainedFiles + 1),
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Devices[(index % 3)]}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: index)
        )).ToArray();
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: opened),
            identity: Vulkan,
            work: work
        );

        for (var index = 0; (index < files.Length); index++) {
            Assert.Equal(
                actual: File.Exists(path: files[index]),
                expected: (index != evicted)
            );
        }

        Assert.Equal(
            actual: Pruned(work: work),
            expected: 1L
        );
    }
    [Fact]
    public void AKernelSetNotYetWrittenHoldsItsPlace() {
        // Eight kernel sets are on disk and the opened ninth is not: its first miss writes it, so the oldest goes now.
        var files = Enumerable.Range(
            count: GpuPipelineCacheFile.RetainedFiles,
            start: 1
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: index)
        )).ToArray();
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan,
            work: work
        );

        Assert.False(condition: File.Exists(path: files[0]));
        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: files[1..]
        );
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 1L
        );
    }
    [Fact]
    public void EqualTimesFallToTheOrdinallyLesserPath() {
        var files = Enumerable.Range(
            count: (GpuPipelineCacheFile.RetainedFiles + 1),
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: index)}.bin",
            written: Epoch
        )).ToArray();
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan,
            work: work
        );

        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: files[..^1]
        );
        Assert.False(condition: File.Exists(path: files[^1]));
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 1L
        );
    }
    [Fact]
    public void AStaleDeviceDirectoryAgesOutAndIsRemoved() {
        // The eight current kernel sets are all newer than the files an old key format and an old driver left behind.
        var current = Enumerable.Range(
            count: GpuPipelineCacheFile.RetainedFiles,
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: (10 + index))
        )).ToArray();

        _ = Seed(
            relative: $"vulkan/{OldKeyFormat}/{KeyOf(index: 0)}.bin",
            written: Epoch
        );
        _ = Seed(
            relative: $"vulkan/{OldDriver}/{KeyOf(index: 1)}.bin",
            written: Epoch.AddMinutes(value: 1)
        );

        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 7),
            identity: Vulkan,
            work: work
        );

        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: current
        );
        Assert.Equal(
            actual: Directory.GetDirectories(path: Path.Join(path1: m_directory, path2: "vulkan")).Select(selector: Path.GetFileName),
            expected: [Vulkan.CacheKey]
        );
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 2L
        );
    }
    [Fact]
    public void ATemporaryIsNeverDeletedAndKeepsItsDirectory() {
        // A temporary another process is still renaming into place is not a cache file, however old it is.
        _ = Enumerable.Range(
            count: GpuPipelineCacheFile.RetainedFiles,
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: (10 + index))
        )).ToArray();

        var stale = Seed(
            relative: $"vulkan/{OldKeyFormat}/{KeyOf(index: 0)}.bin",
            written: Epoch
        );
        var renaming = Seed(
            relative: $"vulkan/{OldKeyFormat}/{KeyOf(index: 1)}.bin.4242-0.tmp",
            written: Epoch
        );
        var besideOpened = Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: 0)}.bin.4242-1.tmp",
            written: Epoch
        );
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan,
            work: work
        );

        Assert.False(condition: File.Exists(path: stale));
        Assert.True(condition: File.Exists(path: renaming));
        Assert.True(condition: File.Exists(path: besideOpened));
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 1L
        );
    }
    [Fact]
    public void AnotherBackendsTreeIsNeverTouched() {
        // More than the cap on Direct3D 12, all older than every Vulkan file, and an empty Direct3D 12 device directory.
        var directx = Enumerable.Range(
            count: (GpuPipelineCacheFile.RetainedFiles + 1),
            start: 0
        ).Select(selector: index => Seed(
            relative: $"directx/{Devices[(index % 2)]}/{KeyOf(index: index)}.bin",
            written: Epoch.AddDays(value: -1).AddMinutes(value: index)
        )).ToArray();
        var empty = Directory.CreateDirectory(path: Path.Join(
            path1: m_directory,
            path2: $"directx/{DirectX.CacheKey}"
        )).FullName;

        _ = Enumerable.Range(
            count: GpuPipelineCacheFile.RetainedFiles,
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: index)
        )).ToArray();

        var work = new GpuPipelineCacheWork(backend: "vulkan");

        _ = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan,
            work: work
        );

        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: directx
        );
        Assert.True(condition: Directory.Exists(path: empty));
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 0L
        );
    }
    [Fact]
    public void OpeningAndEveryHitRefreshTheFile() {
        var path = Seed(
            relative: $"vulkan/{Vulkan.CacheKey}/{KeyOf(index: 0)}.bin",
            written: Epoch
        );
        var before = DateTime.UtcNow;
        var file = Open(
            contentKey: KeyOf(index: 0),
            identity: Vulkan
        );

        Assert.InRange(
            actual: File.GetLastWriteTimeUtc(path: path),
            high: DateTime.UtcNow,
            low: before
        );

        File.SetLastWriteTimeUtc(
            lastWriteTimeUtc: Epoch,
            path: path
        );
        before = DateTime.UtcNow;
        file.Count(cacheHit: true);
        Assert.InRange(
            actual: File.GetLastWriteTimeUtc(path: path),
            high: DateTime.UtcNow,
            low: before
        );
    }
    [Fact]
    public void AFileAnotherProcessHoldsOpenIsLeftInPlace() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Only Windows refuses to delete a file another process holds open.");
        }

        var files = Enumerable.Range(
            count: (GpuPipelineCacheFile.RetainedFiles + 1),
            start: 0
        ).Select(selector: index => Seed(
            relative: $"vulkan/{Devices[(index % 2)]}/{KeyOf(index: index)}.bin",
            written: Epoch.AddMinutes(value: index)
        )).ToArray();
        var work = new GpuPipelineCacheWork(backend: "vulkan");

        // The least recently written file is due to go, but a holder that does not share it for deletion keeps it.
        using (new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: files[1],
            share: FileShare.Read
        )) {
            _ = Open(
                contentKey: KeyOf(index: 0),
                identity: Vulkan,
                work: work
            );
        }

        Assert.All(
            action: static file => Assert.True(condition: File.Exists(path: file)),
            collection: files
        );
        Assert.Equal(
            actual: Pruned(work: work),
            expected: 0L
        );
    }
    [Fact]
    public void AContentKeyDependsOnEveryPartAndItsBoundaries() {
        var key = GpuPipelineCacheStore.ContentKeyOf(parts: [new byte[] { 1, 2 }, new byte[] { 3 }]);

        Assert.Equal(expected: 16, actual: key.Length);
        Assert.Equal(
            actual: GpuPipelineCacheStore.ContentKeyOf(parts: [new byte[] { 1, 2 }, new byte[] { 3 }]),
            expected: key
        );
        Assert.NotEqual(
            actual: GpuPipelineCacheStore.ContentKeyOf(parts: [new byte[] { 1 }, new byte[] { 2, 3 }]),
            expected: key
        );
        Assert.NotEqual(
            actual: GpuPipelineCacheStore.ContentKeyOf(parts: [new byte[] { 1, 2 }, new byte[] { 4 }]),
            expected: key
        );
        Assert.All(
            action: static character => Assert.True(condition: (char.IsAsciiDigit(c: character) || ((character >= 'a') && (character <= 'f')))),
            collection: key
        );
    }
    [Fact]
    public void EveryCreatedPipelineIsOneHitOrOneMiss() {
        var work = new GpuPipelineCacheWork(backend: "vulkan");
        IWorkCounterSource source = work;

        Assert.Equal(
            actual: source.WorkKinds.ToArray(),
            expected: [GpuWork.PipelinesCreated, GpuWork.PipelineCacheHits, GpuWork.PipelineCacheMisses, GpuWork.PipelineCachePruned]
        );

        work.Count(cacheHit: true);
        work.Count(cacheHit: false);
        work.Count(cacheHit: true);
        work.CountPruned();

        Assert.True(condition: source.TryRead(kind: GpuWork.PipelinesCreated, value: out var created));
        Assert.True(condition: source.TryRead(kind: GpuWork.PipelineCacheHits, value: out var hits));
        Assert.True(condition: source.TryRead(kind: GpuWork.PipelineCacheMisses, value: out var misses));
        Assert.True(condition: source.TryRead(kind: GpuWork.PipelineCachePruned, value: out var pruned));
        Assert.Equal(actual: created, expected: 3L);
        Assert.Equal(actual: hits, expected: 2L);
        Assert.Equal(actual: misses, expected: 1L);
        Assert.Equal(actual: pruned, expected: 1L);
        Assert.False(condition: source.TryRead(kind: GpuWork.Dispatches, value: out var undeclared));
        Assert.Equal(actual: undeclared, expected: 0L);
        Assert.Equal(expected: "gpu.pipeline-cache.hits", actual: GpuWork.PipelineCacheHits.Name);
        Assert.Equal(expected: "gpu.pipeline-cache.misses", actual: GpuWork.PipelineCacheMisses.Name);
        Assert.Equal(expected: "gpu.pipeline-cache.pruned", actual: GpuWork.PipelineCachePruned.Name);
        Assert.Equal(expected: WorkClass.Pacing, actual: GpuWork.PipelineCachePruned.Class);
    }
}
