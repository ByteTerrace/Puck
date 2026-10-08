using System.Security.Cryptography;
using System.Text;
using Puck.Assets;

namespace Puck.Shaders;

public sealed partial class ShaderBuild {
    /// <summary>The extension of a bytecode file's sidecar, its commit record: <c>a.comp.spv.hash</c>.</summary>
    public const string SidecarExtension = ".hash";

    private const int Attempts = 20;

    private readonly Lock m_publication = new();

    /// <summary>Returns the text of the sidecar a build publishes beside an output.</summary>
    /// <param name="plan">The output's plan.</param>
    /// <param name="bytecode">The output's bytes.</param>
    /// <returns>Three lines: the plan's inputs and key, and the bytes' SHA-256.</returns>
    public static string SidecarOf(ShaderOutputPlan plan, ReadOnlySpan<byte> bytecode) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        return $"inputs:{plan.Inputs}\nkey:{plan.Key}\nbytecode:{Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytecode))}\n";
    }

    // Takes the project's publication lock: within this process a lock, so concurrent compiles publish in turn, and
    // across processes the lock file opened with no sharing, which the operating system releases with the handle however
    // the holder ends, so a crashed build leaves no lock behind. Another process's hold is waited for, up to five
    // minutes. Every hold is a synchronous block, released on the thread that took it.
    private PublicationLock AcquireLock() {
        m_publication.Enter();
        try {
            return new PublicationLock(file: OpenLock(), gate: m_publication);
        } catch {
            m_publication.Exit();

            throw;
        }
    }
    private FileStream OpenLock() {
        var directory = Path.GetDirectoryName(path: m_lockFile);

        if (!string.IsNullOrEmpty(value: directory)) {
            Directory.CreateDirectory(path: directory);
        }

        var deadline = (DateTime.UtcNow + TimeSpan.FromMinutes(minutes: 5));
        var waited = false;

        while (true) {
            try {
                return new FileStream(access: FileAccess.ReadWrite, mode: FileMode.OpenOrCreate, path: m_lockFile, share: FileShare.None);
            } catch (IOException) when ((DateTime.UtcNow < deadline)) {
                if (!waited) {
                    Log(line: $"Waiting for another build's shader publication to finish ('{m_lockFile}').");
                    waited = true;
                }
                Thread.Sleep(millisecondsTimeout: 50);
            }
        }
    }
    // Publishes one pair under the held lock: the old sidecar goes first, the bytecode moves into place whole, and the new
    // sidecar, the commit record, moves in last. A file another process holds (a reader outside the build, an antivirus
    // scan of a file just written) is retried.
    private static void Publish(ShaderBuildOutput output, ShaderOutputPlan plan, byte[] bytecode) {
        var sidecarPath = SidecarOf(output: output);
        var token = Guid.NewGuid().ToString(format: "N");
        var temporary = $"{output.OutputPath}.{token}.tmp";
        var temporarySidecar = $"{sidecarPath}.{token}.tmp";

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: output.OutputPath)!);
        try {
            File.WriteAllBytes(bytes: bytecode, path: temporary);
            File.WriteAllText(contents: SidecarOf(bytecode: bytecode, plan: plan), path: temporarySidecar);
            Retry(operation: () => File.Delete(path: sidecarPath));
            Retry(operation: () => File.Move(destFileName: output.OutputPath, overwrite: true, sourceFileName: temporary));
            Retry(operation: () => File.Move(destFileName: sidecarPath, overwrite: true, sourceFileName: temporarySidecar));
        } finally {
            File.Delete(path: temporary);
            File.Delete(path: temporarySidecar);
        }
    }
    private static void Retry(Action operation) {
        for (var attempt = 1; ; attempt++) {
            try {
                operation();

                return;
            } catch (Exception exception) when (((exception is IOException or UnauthorizedAccessException) && (attempt < Attempts))) {
                Thread.Sleep(millisecondsTimeout: (50 * attempt));
            }
        }
    }
    // Removes bytecode the build wrote whose stage source is gone, and refuses any other bytecode with no source. A
    // bytecode file is a build output exactly when its sidecar records its current bytes; such a file and its sidecar are
    // removed, one line each. Bytecode with no sidecar, or with bytes its sidecar does not record, was not written by the
    // build as it stands, so it is left in place and the build fails naming it. A sidecar whose bytecode and source are
    // both gone is removed once it reads as a sidecar.
    private bool SweepOrphans() {
        var directory = Path.Combine(path1: m_projectDirectory, path2: "Assets", path3: "Shaders");

        if (!Directory.Exists(path: directory)) {
            return true;
        }

        var valid = true;

        using (AcquireLock()) {
            foreach (var extension in ((ReadOnlySpan<string>)["*.spv", "*.dxil"])) {
                foreach (var bytecodePath in Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: extension)) {
                    var sourceName = (Path.GetFileNameWithoutExtension(path: bytecodePath) + ".hlsl");

                    if (File.Exists(path: Path.Combine(path1: Path.GetDirectoryName(path: bytecodePath)!, path2: sourceName))) {
                        continue;
                    }

                    var display = Display(path: bytecodePath);
                    var sidecarPath = (bytecodePath + SidecarExtension);
                    var sidecar = (File.Exists(path: sidecarPath) ? ReadSidecar(path: sidecarPath) : null);

                    if (sidecar is null) {
                        valid = Error(message: $"Shader bytecode '{display}' has no matching HLSL source '{sourceName}' and no '.hash' sidecar, so the build did not write it and leaves it in place. Remove it or add the source.");
                        continue;
                    }
                    if (!string.Equals(a: sidecar.Value.Bytecode, b: HashFile(path: bytecodePath), comparisonType: StringComparison.Ordinal)) {
                        valid = Error(message: $"Shader bytecode '{display}' has no matching HLSL source '{sourceName}' and its bytes are not the ones its '.hash' sidecar records, so the build leaves it in place. Remove it or add the source.");
                        continue;
                    }

                    File.Delete(path: bytecodePath);
                    File.Delete(path: sidecarPath);
                    Log(line: $"Removed orphaned shader bytecode '{display}' and its '.hash' sidecar: its source '{sourceName}' no longer exists.");
                }
            }
            foreach (var extension in ((ReadOnlySpan<string>)["*.spv.hash", "*.dxil.hash"])) {
                foreach (var sidecarPath in Directory.EnumerateFiles(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: extension)) {
                    var bytecodePath = sidecarPath[..^SidecarExtension.Length];
                    var sourceName = (Path.GetFileNameWithoutExtension(path: bytecodePath) + ".hlsl");

                    if (
                        File.Exists(path: bytecodePath) ||
                        File.Exists(path: Path.Combine(path1: Path.GetDirectoryName(path: bytecodePath)!, path2: sourceName)) ||
                        (ReadSidecar(path: sidecarPath) is null)
                    ) {
                        continue;
                    }

                    File.Delete(path: sidecarPath);
                    Log(line: $"Removed orphaned shader sidecar '{Display(path: sidecarPath)}': its bytecode and source '{sourceName}' no longer exist.");
                }
            }
        }

        return valid;
    }
    private static string SidecarOf(ShaderBuildOutput output) => (output.OutputPath + SidecarExtension);

    private sealed class PublicationLock(FileStream file, Lock gate) : IDisposable {
        public void Dispose() {
            file.Dispose();
            gate.Exit();
        }
    }

    // Reads the three fields a sidecar records, or null when the file is absent or is not exactly a sidecar.
    private static (string Inputs, string Key, string Bytecode)? ReadSidecar(string path) {
        string text;

        try {
            text = Encoding.UTF8.GetString(bytes: AtomicFile.ReadAllBytes(path: path));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }

        var fields = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var line in text.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n')) {
            var colon = line.IndexOf(comparisonType: StringComparison.Ordinal, value: ':');

            if ((colon < 0) || !IsHash(value: line[(colon + 1)..]) || !fields.TryAdd(key: line[..colon], value: line[(colon + 1)..])) {
                return null;
            }
        }

        return ((
            (fields.Count == 3) &&
            fields.TryGetValue(key: "inputs", value: out var inputs) &&
            fields.TryGetValue(key: "key", value: out var key) &&
            fields.TryGetValue(key: "bytecode", value: out var bytecode)
        ) ? (inputs, key, bytecode) : null);
    }
    private static bool IsHash(string value) =>
        ((value.Length == 64) && value.All(predicate: static c => (c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))));
    private static string HashFile(string path) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: AtomicFile.ReadAllBytes(path: path)));
}
