#!/usr/bin/env dotnet

using System.Security.Cryptography;
using static Puck.AutomationProcess;

if (!OperatingSystem.IsLinux() || !File.ReadAllLines("/etc/os-release").Contains("VERSION_ID=\"24.04\"")) {
    throw new PlatformNotSupportedException("This repository's QUIC runner setup requires Ubuntu 24.04.");
}
const string Checksum = "c13f01ac7c3001b51a9281d40dde666db5e037e05512840c319832f7852bfec4";
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var bytes = await client.GetByteArrayAsync("https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb");
if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(Checksum, StringComparison.Ordinal)) {
    throw new InvalidDataException("The Microsoft package-source archive differs from its pinned checksum.");
}
var archive = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath(), $"puck-packages-{Guid.NewGuid():N}.deb");
try {
    await File.WriteAllBytesAsync(archive, bytes);
    await RunAsync("sudo", ["-n", "dpkg", "-i", archive]);
    await RunAsync("sudo", ["-n", "apt-get", "update"]);
    await RunAsync("sudo", ["-n", "apt-get", "install", "-y", "--no-install-recommends", "libmsquic"]);
} finally { File.Delete(archive); }
