#!/usr/bin/env dotnet
#:property PublishAot=false

using System.IO.Compression;
using System.Security.Cryptography;
using static Puck.AutomationProcess;

try {
    if (args is ["-h" or "--help"]) { Console.WriteLine(value: "Install the pinned DXC into .tmp/dxc and export its bin directory to GITHUB_PATH."); return 0; }
    if (args.Length != 0) { throw new ArgumentException(message: "InstallDxc.cs accepts no arguments."); }
    var windows = OperatingSystem.IsWindows();

    if (!windows && !OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException(message: "CI supports Windows and Linux x64."); }
    var root = (Puck.RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));
    var destination = Path.Combine(path1: root, path2: ".tmp/dxc");

    if (Directory.Exists(path: destination)) { throw new IOException(message: $"Use a fresh DXC directory: {destination}"); }
    var asset = (windows ? "dxc_2026_07_29.zip" : "linux_dxc_2026_07_29.x86_x64.tar.gz");
    var expected = (windows ? "a1dfb116ba3eeae6a1582291b53a8e7bf65ad760676bd3194685c8f7367cd241" : "55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54");

    Directory.CreateDirectory(path: destination);
    var archive = Path.Combine(path1: destination, path2: asset);
    using var http = new HttpClient();
    var bytes = await http.GetByteArrayAsync(requestUri: $"https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/{asset}");

    if (Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)) != expected) { throw new InvalidDataException(message: "DXC archive checksum mismatch."); }
    await File.WriteAllBytesAsync(path: archive, bytes: bytes);
    if (windows) { ZipFile.ExtractToDirectory(destinationDirectoryName: destination, sourceArchiveFileName: archive); } else {
        await RunAsync(executable: "tar", arguments: ["-xzf", archive, "-C", destination, "--strip-components=1"]);
    }
    var bin = Path.Combine(path1: destination, path2: (windows ? "bin/x64" : "bin"));

    await RunAsync(executable: Path.Combine(path1: bin, path2: (windows ? "dxc.exe" : "dxc")), arguments: ["--version"]);
    if (Environment.GetEnvironmentVariable(variable: "GITHUB_PATH") is { Length: > 0 } path) { File.AppendAllText(contents: (bin + "\n"), path: path); }
    Console.WriteLine(value: $"DXC installed at {bin}");
    return 0;
} catch (Exception error) {
    Console.Error.WriteLine(value: $"dxc: {error.Message}");
    return 1;
}
