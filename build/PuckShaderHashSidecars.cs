using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Writes a "&lt;bytecode-file&gt;.hash" sidecar beside each shader bytecode file compiled this pass, recording
/// a source-side hash (the source <c>.hlsl</c> concatenated with every <c>ShaderInclude</c> item, in item
/// order — a real streamed byte concatenation) and a bytecode-side hash of the compiled file itself.
/// </summary>
/// <remarks>
/// Runs once per build over the whole item list at once, in real C# compiled by <c>RoslynCodeTaskFactory</c>
/// (the same technique <c>PuckArchitectureGate</c> uses in this file's sibling) — not via MSBuild
/// item-metadata batching, whose <c>&lt;Output TaskParameter="..." PropertyName="..."/&gt;</c> property
/// assignment does not reliably re-scope per batch bucket the way item-output accumulation does.
/// </remarks>
public sealed class PuckWriteShaderHashSidecars : Task {
    /// <summary>Every compiled bytecode file (.spv/.dxil) produced this pass; each item's <c>SourcePath</c>
    /// metadata names its originating <c>.hlsl</c>.</summary>
    public ITaskItem[] BytecodeFiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The shared <c>ShaderInclude</c> items every source may depend on, in item order.</summary>
    public ITaskItem[] Includes { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        foreach (var bytecode in BytecodeFiles) {
            var bytecodePath = bytecode.GetMetadata(metadataName: "FullPath");
            var sourcePath = bytecode.GetMetadata(metadataName: "SourcePath");

            if (!File.Exists(path: bytecodePath) || !File.Exists(path: sourcePath)) {
                continue;
            }

            var sourceHash = PuckShaderHashing.HashConcatenated(firstPath: sourcePath, includes: Includes);
            var bytecodeHash = PuckShaderHashing.HashFile(path: bytecodePath);

            // Publish a complete file rather than truncating one a live asset reader may have memory-mapped.
            // Windows refuses truncation of mapped files; replacement also prevents readers seeing half a hash.
            var sidecarPath = (bytecodePath + ".hash");
            var temporaryPath = (((sidecarPath + ".") + Guid.NewGuid().ToString(format: "N")) + ".tmp");

            try {
                File.WriteAllText(contents: $"source:{sourceHash}\nbytecode:{bytecodeHash}\n", path: temporaryPath);
                if (File.Exists(path: sidecarPath)) {
                    File.Replace(destinationBackupFileName: null, destinationFileName: sidecarPath, sourceFileName: temporaryPath);
                } else {
                    File.Move(destFileName: sidecarPath, sourceFileName: temporaryPath);
                }
            } finally {
                if (File.Exists(path: temporaryPath)) {
                    File.Delete(path: temporaryPath);
                }
            }
        }

        return !Log.HasLoggedErrors;
    }
}
/// <summary>
/// Independently recomputes both hashes <see cref="PuckWriteShaderHashSidecars"/> writes from whatever is on
/// disk right now and compares them against each cached <c>.hash</c> sidecar — catching a cached
/// bytecode file that is stale relative to its source (an edited <c>.hlsl</c>/<c>.hlsli</c> whose recompiled
/// bytecode and sidecar were not refreshed) or relative to its own sidecar (bytecode bytes changed without a
/// recompile). Deliberately independent of <see cref="PuckWriteShaderHashSidecars"/>'s own run this pass: on a
/// build where the source changed, the write task above already refreshed the sidecar to match, so this task
/// passes trivially; on an incremental build where nothing recompiled (MSBuild's own Inputs/Outputs
/// timestamp check saw no textual change), this task is what actually reads the cached sidecar.
/// </summary>
public sealed class PuckValidateShaderBytecodeFresh : Task {
    /// <summary>Every cached bytecode file (.spv/.dxil); each item's <c>SourcePath</c> metadata names its
    /// matching <c>.hlsl</c> (already confirmed to exist by <c>ValidateShaderBytecodeSources</c>).</summary>
    public ITaskItem[] BytecodeFiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The shared <c>ShaderInclude</c> items every source may depend on, in item order.</summary>
    public ITaskItem[] Includes { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        foreach (var bytecode in BytecodeFiles) {
            var bytecodePath = bytecode.GetMetadata(metadataName: "FullPath");
            var sourcePath = bytecode.GetMetadata(metadataName: "SourcePath");

            if (!File.Exists(path: sourcePath)) {
                // ValidateShaderBytecodeSources already removed or refused a bytecode file with no matching .hlsl.
                continue;
            }

            var sidecarPath = (bytecodePath + ".hash");

            if (!File.Exists(path: sidecarPath)) {
                Log.LogError(
                    message: (((string)$"Shader bytecode '{bytecode.ItemSpec}' has no '.hash' sidecar. Recompile (edit and save its source, or delete the bytecode so a rebuild regenerates it) to refresh the ") +
                        "bytecode and sidecar together."));
                continue;
            }

            var expectedSourceHash = PuckShaderHashing.HashConcatenated(firstPath: sourcePath, includes: Includes);
            var expectedBytecodeHash = PuckShaderHashing.HashFile(path: bytecodePath);

            var (recordedSourceHash, recordedBytecodeHash) = PuckShaderHashing.ReadSidecar(path: sidecarPath);

            if (!string.Equals(a: recordedSourceHash, b: expectedSourceHash, comparisonType: StringComparison.Ordinal)) {
                Log.LogError(
                    message: (((string)$"Shader bytecode '{bytecode.ItemSpec}' is stale relative to its source (or was not recompiled after a source or included .hlsli change). Recompile to refresh the ") +
                        "bytecode and '.hash' sidecar."));
            }

            if (!string.Equals(a: recordedBytecodeHash, b: expectedBytecodeHash, comparisonType: StringComparison.Ordinal)) {
                Log.LogError(
                    message: (((string)$"Shader bytecode '{bytecode.ItemSpec}' does not match its own cached '.hash' sidecar (the cached bytecode bytes changed without a recompile). Recompile to refresh ") +
                        "the bytecode and '.hash' sidecar."));
            }
        }

        return !Log.HasLoggedErrors;
    }
}

/// <summary>
/// Removes shader bytecode the build wrote whose <c>.hlsl</c> source is gone, and refuses any other bytecode
/// with no source. The build owns what it writes: a <c>.spv</c> or <c>.dxil</c> left behind by a deleted source is
/// ignored build output, and a checkout that built before the deletion must build again without a person deleting it.
/// <para>A bytecode file is a build output exactly when its <c>.hash</c> sidecar, which only
/// <see cref="PuckWriteShaderHashSidecars"/> writes, records its current bytes. Such a file and its sidecar are
/// removed, one message line each. Bytecode with no sidecar, or with bytes its sidecar does not record, was not
/// written by the build as it stands, so it is left in place and the build fails naming it. A sidecar whose bytecode
/// and source are both gone is removed too, once it reads as a sidecar.</para>
/// </summary>
public sealed class PuckRemoveOrphanedShaderBytecode : Task {
    /// <summary>Every bytecode file (.spv/.dxil) on disk under the project's shader globs.</summary>
    public ITaskItem[] BytecodeFiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>Every bytecode sidecar (.spv.hash/.dxil.hash) on disk under the project's shader directories.</summary>
    public ITaskItem[] Sidecars { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The bytecode files this task removed, so the caller can drop them from its item list.</summary>
    [Output]
    public ITaskItem[] Removed { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        var removed = new List<ITaskItem>();

        foreach (var bytecode in BytecodeFiles) {
            var bytecodePath = bytecode.GetMetadata(metadataName: "FullPath");
            var sourceName = (Path.GetFileNameWithoutExtension(path: bytecodePath) + ".hlsl");

            if (!File.Exists(path: bytecodePath) || File.Exists(path: Path.Combine(path1: Path.GetDirectoryName(path: bytecodePath)!, path2: sourceName))) {
                continue;
            }

            var display = bytecode.ItemSpec.Replace(newChar: '/', oldChar: '\\');
            var sidecarPath = (bytecodePath + ".hash");

            if (!File.Exists(path: sidecarPath)) {
                Log.LogError(message: $"Shader bytecode '{display}' has no matching HLSL source '{sourceName}' and no '.hash' sidecar, so the build did not write it and leaves it in place. Remove it or add the source.");
                continue;
            }

            var (_, recordedBytecodeHash) = PuckShaderHashing.ReadSidecar(path: sidecarPath);

            if (!string.Equals(a: recordedBytecodeHash, b: PuckShaderHashing.HashFile(path: bytecodePath), comparisonType: StringComparison.Ordinal)) {
                Log.LogError(message: $"Shader bytecode '{display}' has no matching HLSL source '{sourceName}' and its bytes are not the ones its '.hash' sidecar records, so the build leaves it in place. Remove it or add the source.");
                continue;
            }

            File.Delete(path: bytecodePath);
            File.Delete(path: sidecarPath);
            removed.Add(item: bytecode);
            Log.LogMessage(importance: MessageImportance.High, message: $"Removed orphaned shader bytecode '{display}' and its '.hash' sidecar: its source '{sourceName}' no longer exists.");
        }

        foreach (var sidecar in Sidecars) {
            var sidecarPath = sidecar.GetMetadata(metadataName: "FullPath");
            // "<stem>.spv.hash": the bytecode path drops ".hash", the source stem drops the bytecode extension too.
            var bytecodePath = sidecarPath.Substring(length: (sidecarPath.Length - ".hash".Length), startIndex: 0);
            var sourceName = (Path.GetFileNameWithoutExtension(path: bytecodePath) + ".hlsl");

            if (!File.Exists(path: sidecarPath) || File.Exists(path: bytecodePath) || File.Exists(path: Path.Combine(path1: Path.GetDirectoryName(path: bytecodePath)!, path2: sourceName))) {
                continue;
            }

            var (recordedSourceHash, recordedBytecodeHash) = PuckShaderHashing.ReadSidecar(path: sidecarPath);

            if (!PuckShaderHashing.IsHash(value: recordedSourceHash) || !PuckShaderHashing.IsHash(value: recordedBytecodeHash)) {
                continue;
            }

            File.Delete(path: sidecarPath);
            Log.LogMessage(importance: MessageImportance.High, message: $"Removed orphaned shader sidecar '{sidecar.ItemSpec.Replace(newChar: '/', oldChar: '\\')}': its bytecode and source '{sourceName}' no longer exist.");
        }

        Removed = removed.ToArray();

        return !Log.HasLoggedErrors;
    }
}

/// <summary>Shared hashing helpers for the shader-hash-sidecar tasks above.</summary>
internal static class PuckShaderHashing {
    /// <summary>Streams <paramref name="firstPath"/> followed by every item in <paramref name="includes"/>, in
    /// order, through one SHA-256 instance — a real byte concatenation, not a hash-of-hashes. Carriage returns are
    /// dropped before hashing so the hash is a function of the committed text, not of the checkout's line-ending
    /// policy (`* text=auto` yields CRLF on Windows and LF elsewhere for the same blob).</summary>
    public static string HashConcatenated(string firstPath, ITaskItem[] includes) {
        using (var sha256 = SHA256.Create()) {
            using (var cryptoStream = new CryptoStream(mode: CryptoStreamMode.Write, stream: Stream.Null, transform: sha256)) {
                AppendFile(destination: cryptoStream, path: firstPath);
                foreach (var include in includes) {
                    AppendFile(destination: cryptoStream, path: include.GetMetadata(metadataName: "FullPath"));
                }
            }

            return ToHex(bytes: sha256.Hash);
        }
    }
    /// <summary>Hashes one file's raw bytes.</summary>
    public static string HashFile(string path) {
        using (var sha256 = SHA256.Create()) {
            using (var stream = File.OpenRead(path: path)) {
                return ToHex(bytes: sha256.ComputeHash(inputStream: stream));
            }
        }
    }
    /// <summary>Reads a two-line "source:&lt;hex&gt;" / "bytecode:&lt;hex&gt;" sidecar.</summary>
    public static (string SourceHash, string BytecodeHash) ReadSidecar(string path) {
        var sourceHash = "";
        var bytecodeHash = "";

        foreach (var line in File.ReadAllLines(path: path)) {
            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "source:")) {
                sourceHash = line.Substring(startIndex: "source:".Length).Trim();
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "bytecode:")) {
                bytecodeHash = line.Substring(startIndex: "bytecode:".Length).Trim();
            }
        }

        return (sourceHash, bytecodeHash);
    }

    /// <summary>True when <paramref name="value"/> is a SHA-256 in the lowercase hex <see cref="ToHex"/> writes.</summary>
    public static bool IsHash(string value) {
        if (value.Length != 64) {
            return false;
        }

        foreach (var c in value) {
            if (!(((c >= '0') && (c <= '9')) || ((c >= 'a') && (c <= 'f')))) {
                return false;
            }
        }

        return true;
    }

    private static void AppendFile(CryptoStream destination, string path) {
        var bytes = File.ReadAllBytes(path: path);
        var count = 0;

        for (var i = 0; (i < bytes.Length); i++) {
            if (bytes[i] != ((byte)'\r')) {
                bytes[count++] = bytes[i];
            }
        }
        destination.Write(buffer: bytes, count: count, offset: 0);
    }
    private static string ToHex(byte[] bytes) {
        var chars = new char[(bytes.Length * 2)];

        for (var i = 0; (i < bytes.Length); i++) {
            var b = bytes[i];

            chars[(i * 2)] = ToNibble(value: ((byte)(b >> 4)));
            chars[((i * 2) + 1)] = ToNibble(value: ((byte)(b & 0xF)));
        }
        return new string(value: chars);
    }
    private static char ToNibble(byte value) => ((char)((value < 10) ? ('0' + value) : ('a' + (value - 10))));
}
