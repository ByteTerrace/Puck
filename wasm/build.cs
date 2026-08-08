// Builds the default addon's release .wasm module, then refreshes the copy Puck.World ships and
// mounts by default. wasm/.cargo/config.toml already pins the default target to
// wasm32-unknown-unknown, so no --target flag is needed here. Building from the workspace root
// builds every member, including puck-stdlib (an rlib with no standalone artifact).
//
//   dotnet run -c Release wasm/build.cs
//
// Release is not optional: Windows App Control on the reference machine refuses to load never-seen
// Debug binaries (FileLoadException 0x800711C7), and `-c Release` must precede the file path or the
// SDK reads it as a program argument (docs/agent-guide.md).
//
// Cargo output: target/wasm32-unknown-unknown/release/puck_addon_default.wasm
// Refreshed copy: ../src/Puck.World/Assets/addons/puck-addon-default.wasm
//
// The committed .wasm's PROVENANCE IS NOT GATE-ENFORCED — no build or Post stage proves the
// committed bytes were built from the Rust beside them. Refreshing it is therefore a DELIBERATE
// step you must remember to take whenever puck-addon-default's (or puck-stdlib's) source changes;
// this script exists so that step is one command instead of a hand-rolled copy.
//
// After running it, paste the printed hash into the `hash` field of EVERY `addons` row pointing at
// the refreshed path — a stale pin makes the host refuse the module rather than silently loading
// it. None of the four shipped worlds declares an addons row (the `default` world that once did was
// retired under the four-world charter), so today those rows live only in hand-authored documents:
// the verification fixtures under docs/verification/, and wasm/puck-addon-hudbuilder/worlds/.
//
// Note that cargo embeds absolute paths, so a rebuild from a different checkout produces different
// bytes and therefore a different hash even with identical sources. Refresh deliberately, and read
// a changed hash as "these are new bytes", never as "the source changed".

#:project ../src/Puck.World.Data/Puck.World.Data.csproj

using System.Diagnostics;

using Puck.World;

// The script's own directory, NOT the caller's: cargo must run against wasm/'s workspace root and
// the refresh target is expressed relative to it, so both are independent of where this is invoked
// from. Directory.SetCurrentDirectory rather than a ProcessStartInfo.WorkingDirectory so the
// relative paths below read the same way the shell scripts this replaces did.
var scriptDirectory = Path.GetDirectoryName(path: SourcePath())!;

Directory.SetCurrentDirectory(path: scriptDirectory);

using (var cargo = Process.Start(startInfo: new ProcessStartInfo(fileName: "cargo", arguments: "build --release") { UseShellExecute = false })!)
{
    await cargo.WaitForExitAsync();

    if (cargo.ExitCode != 0)
    {
        return cargo.ExitCode;
    }
}

var wasmDirectory = Path.Combine(scriptDirectory, "target", "wasm32-unknown-unknown", "release");
var modules = Directory.Exists(path: wasmDirectory)
    ? Directory.GetFiles(path: wasmDirectory, searchPattern: "*.wasm")
    : [];

if (modules.Length == 0)
{
    Console.Error.WriteLine($"Build succeeded but no .wasm file was found under {wasmDirectory}");

    return 1;
}

foreach (var module in modules)
{
    Console.WriteLine($"Built: {module}");
}

var defaultModule = Path.Combine(wasmDirectory, "puck_addon_default.wasm");

if (!File.Exists(path: defaultModule))
{
    Console.Error.WriteLine("puck_addon_default.wasm was not among the built modules — skipping the Puck.World refresh");

    return 1;
}

var targetPath = Path.GetFullPath(
    Path.Combine(scriptDirectory, "..", "src", "Puck.World", "Assets", "addons", "puck-addon-default.wasm"));

File.Copy(sourceFileName: defaultModule, destFileName: targetPath, overwrite: true);

// The pin is the LEADING 64 BITS of the SHA-256, read little-endian off the raw bytes — NOT the
// first 16 hex characters of the big-endian digest string a hashing tool prints. The shell scripts
// this replaces had to hand-roll that reversal and carry a comment warning about it; calling the
// host's OWN implementation means a printed hash cannot disagree with the hash the host computes
// when it decides whether to accept the module.
var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: File.ReadAllBytes(path: targetPath));

Console.WriteLine($"Refreshed: {targetPath}");
Console.WriteLine($"Content hash (paste into WorldDefinition.cs's default WorldAddonRow.Hash): {contentHash}");

return 0;

// A file-based program has no assembly on disk to locate itself by, and the runfile cache means
// AppContext.BaseDirectory points at the cache rather than at wasm/. CallerFilePath is resolved by
// the compiler against the actual source path, which is the one thing that always names this file.
static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
