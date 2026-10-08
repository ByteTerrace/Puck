using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Puck.Abstractions.Tests;

/// <summary>Build output is hard-linked where nothing rewrites it in place, and only there (<c>Directory.Build.props</c>).
/// This assembly's own bin is a built tree like every other: each project-reference assembly in it is a copy-local
/// link, so it shares its file with the referenced project's bin; and no file in it shares its file with a compiler
/// output under any project's <c>obj</c>, because the compiler truncates and rewrites those in place, which would
/// rewrite every linked copy, a kept World build included. Windows reports a file's identity through its handle; other
/// platforms skip.</summary>
public sealed partial class BuildOutputLinkLawTests {
    private static readonly string Bin = AppContext.BaseDirectory;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    // The file's volume and index, which every name of one file shares, and how many names it has.
    private static (ulong Volume, ulong Index, uint Links) Identity(string path) {
        using var handle = File.OpenHandle(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandle(file: handle, information: out var information)) {
            throw new IOException(message: $"Cannot read the identity of {path}: error {Marshal.GetLastPInvokeError()}.");
        }

        return (information.Volume, (((ulong)information.IndexHigh) << 32) | information.IndexLow, information.Links);
    }
    // The repository root: the nearest ancestor of the bin holding Puck.slnx.
    private static string Repository() {
        for (var directory = new DirectoryInfo(path: Bin); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(message: $"No repository encloses {Bin}.");
    }
    // The compiler's own output of a Puck assembly in this bin: obj/<configuration>/<framework>/<name>.dll of its project.
    private static string? CompilerOutput(string repository, string assembly) {
        var name = Path.GetFileNameWithoutExtension(path: assembly);
        var framework = new DirectoryInfo(path: Bin).Name;
        var configuration = new DirectoryInfo(path: Bin).Parent!.Name;

        foreach (var root in ((string[])["src", "tests"])) {
            var candidate = Path.Combine(paths: [repository, root, name, "obj", configuration, framework, (name + ".dll")]);

            if (File.Exists(path: candidate)) {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public void ProjectReferenceAssembliesAreLinkedToTheirProjectsBin() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "File identities are read through the Windows file API.");

        var repository = Repository();
        var maths = Path.Combine(path1: Bin, path2: "Puck.Maths.dll");
        var producer = Path.Combine(paths: [repository, "src", "Puck.Maths", "bin", new DirectoryInfo(path: Bin).Parent!.Name, new DirectoryInfo(path: Bin).Name, "Puck.Maths.dll"]);
        var local = Identity(path: maths);
        var produced = Identity(path: producer);

        Assert.True(
            condition: ((local.Volume, local.Index) == (produced.Volume, produced.Index)),
            userMessage: $"{maths} is a copy of {producer}, not a link to it: copy-local hard links are off."
        );
    }
    [Fact]
    public void NoOutputSharesItsFileWithACompilerOutput() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "File identities are read through the Windows file API.");

        var repository = Repository();
        var shared = new List<string>();
        var checkedCount = 0;

        foreach (var assembly in Directory.EnumerateFiles(path: Bin, searchPattern: "Puck.*.dll")) {
            if (CompilerOutput(assembly: assembly, repository: repository) is not { } output) {
                continue;
            }

            checkedCount++;
            var bin = Identity(path: assembly);
            var obj = Identity(path: output);

            if ((bin.Volume, bin.Index) == (obj.Volume, obj.Index)) {
                shared.Add(item: $"{assembly} is {output}");
            }
        }

        Assert.True(condition: (checkedCount > 1), userMessage: $"Found no compiler output to compare under {repository}.");
        Assert.True(
            condition: (shared.Count == 0),
            userMessage: $"Build outputs share their file with a compiler output the next compile rewrites in place:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: shared)}"
        );
    }
}
