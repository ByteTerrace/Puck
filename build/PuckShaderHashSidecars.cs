using System;
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

            File.WriteAllText(
                path: bytecodePath + ".hash",
                contents: $"source:{sourceHash}\nbytecode:{bytecodeHash}\n");
        }

        return !Log.HasLoggedErrors;
    }
}

/// <summary>
/// Independently recomputes both hashes <see cref="PuckWriteShaderHashSidecars"/> writes from whatever is on
/// disk right now and compares them against each committed <c>.hash</c> sidecar — catching a committed
/// bytecode file that is stale relative to its source (an edited <c>.hlsl</c>/<c>.hlsli</c> whose recompiled
/// bytecode and sidecar were never committed) or relative to its own sidecar (bytecode bytes changed without a
/// recompile). Deliberately independent of <see cref="PuckWriteShaderHashSidecars"/>'s own run this pass: on a
/// build where the source changed, the write task above already refreshed the sidecar to match, so this task
/// passes trivially; on a from-clean-checkout build where nothing recompiled (MSBuild's own Inputs/Outputs
/// timestamp check saw no textual change), this task is what actually reads the currently-committed sidecar.
/// </summary>
public sealed class PuckValidateShaderBytecodeFresh : Task {
    /// <summary>Every committed bytecode file (.spv/.dxil); each item's <c>SourcePath</c> metadata names its
    /// matching <c>.hlsl</c> (already confirmed to exist by <c>ValidateShaderBytecodeSources</c>).</summary>
    public ITaskItem[] BytecodeFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The shared <c>ShaderInclude</c> items every source may depend on, in item order.</summary>
    public ITaskItem[] Includes { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        foreach (var bytecode in BytecodeFiles) {
            var bytecodePath = bytecode.GetMetadata(metadataName: "FullPath");
            var sourcePath = bytecode.GetMetadata(metadataName: "SourcePath");

            if (!File.Exists(path: sourcePath)) {
                // ValidateShaderBytecodeSources already refuses a bytecode file with no matching .hlsl; nothing
                // further to check here for it.
                continue;
            }

            var sidecarPath = bytecodePath + ".hash";
            if (!File.Exists(path: sidecarPath)) {
                Log.LogError(
                    message: $"Shader bytecode '{bytecode.ItemSpec}' has no '.hash' sidecar. Recompile (edit and " +
                        "save its source, or delete the bytecode so a rebuild regenerates it) and commit the " +
                        "refreshed bytecode and sidecar together.");
                continue;
            }

            var expectedSourceHash = PuckShaderHashing.HashConcatenated(firstPath: sourcePath, includes: Includes);
            var expectedBytecodeHash = PuckShaderHashing.HashFile(path: bytecodePath);
            var (recordedSourceHash, recordedBytecodeHash) = PuckShaderHashing.ReadSidecar(path: sidecarPath);

            if (!string.Equals(a: recordedSourceHash, b: expectedSourceHash, comparisonType: StringComparison.Ordinal)) {
                Log.LogError(
                    message: $"Shader bytecode '{bytecode.ItemSpec}' is stale relative to its source (or was not " +
                        "recompiled after a source or included .hlsli change). Recompile and commit the refreshed " +
                        "bytecode and '.hash' sidecar.");
            }

            if (!string.Equals(a: recordedBytecodeHash, b: expectedBytecodeHash, comparisonType: StringComparison.Ordinal)) {
                Log.LogError(
                    message: $"Shader bytecode '{bytecode.ItemSpec}' does not match its own committed '.hash' " +
                        "sidecar (the committed bytecode bytes changed without a recompile). Recompile and commit " +
                        "the refreshed bytecode and '.hash' sidecar.");
            }
        }

        return !Log.HasLoggedErrors;
    }
}

/// <summary>Shared hashing helpers for the two shader-hash-sidecar tasks above.</summary>
internal static class PuckShaderHashing {
    /// <summary>Streams <paramref name="firstPath"/> followed by every item in <paramref name="includes"/>, in
    /// order, through one SHA-256 instance — a real byte concatenation, not a hash-of-hashes. Carriage returns are
    /// dropped before hashing so the hash is a function of the committed text, not of the checkout's line-ending
    /// policy (`* text=auto` yields CRLF on Windows and LF elsewhere for the same blob).</summary>
    public static string HashConcatenated(string firstPath, ITaskItem[] includes) {
        using (var sha256 = SHA256.Create()) {
            using (var cryptoStream = new CryptoStream(stream: Stream.Null, transform: sha256, mode: CryptoStreamMode.Write)) {
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
            if (line.StartsWith(value: "source:", comparisonType: StringComparison.Ordinal)) {
                sourceHash = line.Substring(startIndex: "source:".Length).Trim();
            } else if (line.StartsWith(value: "bytecode:", comparisonType: StringComparison.Ordinal)) {
                bytecodeHash = line.Substring(startIndex: "bytecode:".Length).Trim();
            }
        }

        return (sourceHash, bytecodeHash);
    }

    private static void AppendFile(CryptoStream destination, string path) {
        var bytes = File.ReadAllBytes(path: path);
        var count = 0;
        for (var i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\r') {
                bytes[count++] = bytes[i];
            }
        }
        destination.Write(buffer: bytes, offset: 0, count: count);
    }

    private static string ToHex(byte[] bytes) {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++) {
            var b = bytes[i];
            chars[i * 2] = ToNibble(value: (byte)(b >> 4));
            chars[i * 2 + 1] = ToNibble(value: (byte)(b & 0xF));
        }
        return new string(chars);
    }

    private static char ToNibble(byte value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
}
