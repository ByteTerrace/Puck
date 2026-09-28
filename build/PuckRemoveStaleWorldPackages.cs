using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Removes every file under the World's deployed package store (<c>Assets/worlds/packages</c> in the output directory)
/// that this build no longer deploys, and then every directory the removal leaves empty.
/// </summary>
/// <remarks>
/// The store under the output directory is written only by the build: the tree run's report ships each package's files
/// there, and nothing in the source tree maps to it. A copy never deletes, so a package a source stops naming would stay
/// deployed and a World would still find it. The deployed set is every content item's target path, so a file the build
/// copies for any reason is kept.
/// </remarks>
public sealed class PuckRemoveStaleWorldPackages : Task {
    /// <summary>The project's output directory, which content target paths are relative to.</summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;
    /// <summary>The package store's path relative to <see cref="OutputDirectory"/>.</summary>
    [Required]
    public string PackageDirectory { get; set; } = string.Empty;
    /// <summary>Every content item this build deploys; each item's <c>TargetPath</c> metadata names its deployed
    /// path relative to <see cref="OutputDirectory"/>.</summary>
    public ITaskItem[] Deployed { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        var output = Path.GetFullPath(path: OutputDirectory);
        var store = Path.GetFullPath(path: Path.Combine(
            path1: output,
            path2: PackageDirectory
        ));

        if (!Directory.Exists(path: store)) {
            return true;
        }

        // Windows paths compare without case; the task also compiles for .NET Framework MSBuild, which lacks
        // OperatingSystem.
        var comparer = ((Path.DirectorySeparatorChar == '\\')
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal
        );
        var deployed = new HashSet<string>(comparer: comparer);

        foreach (var item in Deployed) {
            var targetPath = item.GetMetadata(metadataName: "TargetPath");

            if (targetPath.Length != 0) {
                deployed.Add(item: Path.GetFullPath(path: Path.Combine(
                    path1: output,
                    path2: targetPath
                )));
            }
        }

        foreach (var file in Directory.GetFiles(
            path: store,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            if (deployed.Contains(item: file)) {
                continue;
            }

            File.Delete(path: file);
            Log.LogMessage(
                importance: MessageImportance.High,
                message: $"Removed stale world package file '{file.Substring(startIndex: output.Length).TrimStart('\\', '/').Replace(newChar: '/', oldChar: '\\')}': this build no longer deploys it."
            );
        }

        RemoveEmptyDirectories(directory: store);

        return !Log.HasLoggedErrors;
    }

    // Removes every directory below this one that holds no file, deepest first; the store itself stays.
    private static void RemoveEmptyDirectories(string directory) {
        foreach (var child in Directory.GetDirectories(path: directory)) {
            RemoveEmptyDirectories(directory: child);

            if (Directory.GetFileSystemEntries(path: child).Length == 0) {
                Directory.Delete(path: child);
            }
        }
    }
}
