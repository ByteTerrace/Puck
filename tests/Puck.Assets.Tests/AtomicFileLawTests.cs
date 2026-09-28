using System.IO.MemoryMappedFiles;
using System.Text;
using Xunit;

namespace Puck.Assets.Tests;

// AtomicFile is the one temp-then-rename writer. A write either lands whole or leaves the directory as it found it:
// the destination keeps its old bytes and no temporary file survives the failure.
public sealed class AtomicFileLawTests : IDisposable {
    private readonly DirectoryInfo m_directory = Directory.CreateTempSubdirectory(prefix: "puck-atomic-file-");

    private string PathOf(string name) =>
        Path.Combine(
            path1: m_directory.FullName,
            path2: name
        );
    private string[] Entries() =>
        [.. Directory.EnumerateFileSystemEntries(path: m_directory.FullName).Select(selector: static entry => Path.GetFileName(path: entry)).Order(comparer: StringComparer.Ordinal)];

    public void Dispose() =>
        m_directory.Delete(recursive: true);
    [Fact]
    public void ReadAllBytesReadsAFileAnotherHandleHoldsWithDeleteAccess() {
        var path = PathOf(name: "published.bin");

        File.WriteAllBytes(
            bytes: [4, 5, 6],
            path: path
        );

        // A rename in progress holds the file this way; delete-on-close is the portable way to ask for delete access.
        using (var renaming = new FileStream(
            access: FileAccess.Read,
            bufferSize: 1,
            mode: FileMode.Open,
            options: FileOptions.DeleteOnClose,
            path: path,
            share: FileShare.ReadWrite | FileShare.Delete
        )) {
            Assert.Equal(
                expected: [4, 5, 6],
                actual: AtomicFile.ReadAllBytes(path: path)
            );

            if (OperatingSystem.IsWindows()) {
                _ = Assert.Throws<IOException>(testCode: () => File.ReadAllBytes(path: path));
            }
        }
    }
    [Fact]
    public void WriteAllBytesCreatesTheFileAndItsMissingParentDirectory() {
        var path = PathOf(name: "nested/deeper/file.bin");

        AtomicFile.WriteAllBytes(
            bytes: [1, 2, 3],
            path: path
        );

        Assert.Equal(
            expected: [1, 2, 3],
            actual: File.ReadAllBytes(path: path)
        );
        Assert.Equal(
            expected: ["file.bin"],
            actual: Directory.GetFiles(path: Path.GetDirectoryName(path: path)!).Select(selector: static file => Path.GetFileName(path: file))
        );
    }
    [Fact]
    public void WriteAllTextReplacesAnExistingFileWithUtf8WithoutAByteOrderMark() {
        var path = PathOf(name: "pointer");

        File.WriteAllText(
            contents: "old contents that are longer",
            path: path
        );
        AtomicFile.WriteAllText(
            contents: "néw",
            path: path
        );

        Assert.Equal(
            expected: Encoding.UTF8.GetBytes(s: "néw"),
            actual: File.ReadAllBytes(path: path)
        );
        Assert.Equal(
            expected: ["pointer"],
            actual: Entries()
        );
    }
    [Fact]
    public void WriteStreamsTheContentAWriterProduces() {
        var path = PathOf(name: "streamed.txt");

        AtomicFile.Write(
            path: path,
            write: static stream => stream.Write(buffer: "streamed"u8)
        );

        Assert.Equal(
            expected: "streamed",
            actual: File.ReadAllText(path: path)
        );
    }
    [Fact]
    public void AWriterThatThrowsLeavesTheDestinationAndNoTemporaryFile() {
        var path = PathOf(name: "kept.txt");

        File.WriteAllText(
            contents: "kept",
            path: path
        );

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => AtomicFile.Write(
            path: path,
            write: static stream => {
                stream.Write(buffer: "half"u8);

                throw new InvalidOperationException(message: "writer failed");
            }
        ));

        Assert.Equal(
            expected: "writer failed",
            actual: thrown.Message
        );
        Assert.Equal(
            expected: "kept",
            actual: File.ReadAllText(path: path)
        );
        Assert.Equal(
            expected: ["kept.txt"],
            actual: Entries()
        );
    }
    [Fact]
    public void AMoveThatFailsLeavesNoTemporaryFile() {
        // A directory at the destination makes the final rename fail after the temporary file is fully written.
        var path = PathOf(name: "occupied");

        _ = Directory.CreateDirectory(path: path);

        _ = Assert.ThrowsAny<Exception>(testCode: () => AtomicFile.WriteAllBytes(
            bytes: [9, 9, 9],
            path: path
        ));
        Assert.True(condition: Directory.Exists(path: path));
        Assert.Equal(
            expected: ["occupied"],
            actual: Entries()
        );
    }
    [Fact]
    public void AReplacementLandsWhileAReaderHoldsTheOldFileMapped() {
        var path = PathOf(name: "mapped.txt");

        File.WriteAllText(
            contents: "old bytes",
            path: path
        );

        using var stream = new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: path,
            share: FileShare.ReadWrite | FileShare.Delete
        );
        using var mapping = MemoryMappedFile.CreateFromFile(
            access: MemoryMappedFileAccess.Read,
            capacity: 0,
            fileStream: stream,
            inheritability: HandleInheritability.None,
            leaveOpen: true,
            mapName: null
        );
        using var view = mapping.CreateViewStream(
            access: MemoryMappedFileAccess.Read,
            offset: 0,
            size: stream.Length
        );

        AtomicFile.WriteAllText(
            contents: "new bytes",
            path: path
        );

        Assert.Equal(
            expected: "new bytes",
            actual: File.ReadAllText(path: path)
        );

        using var reader = new StreamReader(stream: view);

        Assert.Equal(
            expected: "old bytes",
            actual: reader.ReadToEnd().TrimEnd(trimChar: '\0')
        );
        Assert.Equal(
            expected: ["mapped.txt"],
            actual: Entries()
        );
    }
    // A file mapped as an image can be renamed but not deleted, so replacing one is the interleaving a replace that fails
    // partway reaches under contention, forced: the old file is moved aside and cannot be removed. ReplaceFile would
    // leave it under a name of its own (<name>~RF<hex>.TMP) that nothing finds again; the write names the backup
    // itself, so the one file it leaves is its own temporary, and nothing else beside the destination.
    [Fact]
    public void ReplacingAFileMappedAsAnImageLeavesOnlyTheWritersOwnTemporary() {
        Assert.SkipUnless(
            condition: OperatingSystem.IsWindows(),
            reason: "Only Windows maps a loaded library as an image the file system refuses to delete."
        );

        var path = PathOf(name: "loaded.dll");

        File.Copy(
            destFileName: path,
            sourceFileName: Path.Combine(
                path1: Environment.SystemDirectory,
                path2: "version.dll"
            )
        );

        var library = System.Runtime.InteropServices.NativeLibrary.Load(libraryPath: path);

        try {
            AtomicFile.WriteAllBytes(
                bytes: [9, 9, 9],
                path: path
            );

            Assert.Equal(
                expected: [9, 9, 9],
                actual: File.ReadAllBytes(path: path)
            );

            var entries = Entries();

            Assert.Equal(
                expected: 2,
                actual: entries.Length
            );
            Assert.Equal(
                expected: "loaded.dll",
                actual: entries[0]
            );
            Assert.Matches(
                actualString: entries[1],
                expectedRegexPattern: @"^loaded\.dll\.[0-9a-f]{32}\.replaced\.tmp$"
            );
        } finally {
            System.Runtime.InteropServices.NativeLibrary.Free(handle: library);
        }
    }
    [Fact]
    public void OnUnixAReplacementKeepsTheDestinationsMode() {
        if (OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "a Unix file mode has no meaning on Windows");

            return;
        }

        var path = PathOf(name: "script.sh");
        const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        File.WriteAllText(
            contents: "#!/bin/sh\n",
            path: path
        );
        File.SetUnixFileMode(
            mode: Executable,
            path: path
        );
        AtomicFile.WriteAllText(
            contents: "#!/bin/sh\necho replaced\n",
            path: path
        );

        Assert.Equal(
            expected: Executable,
            actual: File.GetUnixFileMode(path: path)
        );
    }
    [Fact]
    public void OnUnixANamedModeWinsOverTheDestinationsMode() {
        if (OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "a Unix file mode has no meaning on Windows");

            return;
        }

        var path = PathOf(name: "secret.key");
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        File.WriteAllText(
            contents: "public",
            path: path
        );
        File.SetUnixFileMode(
            mode: OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            path: path
        );
        AtomicFile.WriteAllBytes(
            bytes: [7],
            path: path,
            unixCreateMode: OwnerOnly
        );

        Assert.Equal(
            expected: OwnerOnly,
            actual: File.GetUnixFileMode(path: path)
        );
    }
}
