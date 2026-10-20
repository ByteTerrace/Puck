using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>The content a machine's authored <see cref="WorldMachine.ContentFolder"/> offers: the files directly in that
/// folder its engine can mount, by file name in ordinal order. An engine that prepares content through a provider
/// (<see cref="IMachineValidationCatalog.HasContentProvider"/>) offers the files the provider recognizes, so a
/// cartridge source kept beside its compiled document is not listed; an engine that mounts raw bytes offers every file.
/// Subfolders are not searched, and nothing outside the folder is listed.</summary>
public static class WorldMachineContentFolder {
    /// <summary>One file the folder offers.</summary>
    /// <param name="Name">The file name, as <c>machine.swap</c> takes it.</param>
    /// <param name="ContentPath">The path the machine's content operation mounts, relative to the document directory
    /// the folder is authored against, spelled with <c>/</c>.</param>
    public readonly record struct Entry(string Name, string ContentPath);

    /// <summary>Lists the content a machine's folder offers.</summary>
    /// <param name="machine">The machine declaration.</param>
    /// <param name="documentDirectory">The directory of the document the folder is relative to, or
    /// <see langword="null"/> for a document with none.</param>
    /// <param name="catalog">The host's machine catalog, which decides what the machine's engine can mount.</param>
    /// <param name="entries">The offered files in ordinal file-name order; empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the machine names a folder that resolves and exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> or <paramref name="catalog"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryList(WorldMachine machine, string? documentDirectory, IMachineValidationCatalog catalog,
        out IReadOnlyList<Entry> entries, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: catalog);
        entries = [];

        if (machine.ContentFolder is not { } folder) {
            reason = $"machine '{machine.Name}' names no contentFolder";
            return false;
        }
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: documentDirectory,
            path: folder,
            reason: out var unresolved,
            resolved: out var resolved
        )) {
            reason = $"machine '{machine.Name}' contentFolder {unresolved}";
            return false;
        }
        if (!Directory.Exists(path: resolved)) {
            reason = $"machine '{machine.Name}' contentFolder '{folder}' is not a folder at '{resolved}'";
            return false;
        }

        var provided = catalog.HasContentProvider(engineId: machine.Engine);
        var listed = new List<Entry>();

        foreach (var file in Directory.EnumerateFiles(path: resolved)) {
            var name = Path.GetFileName(path: file);

            if (
                provided &&
                !catalog.CanPrepare(
                    contentPath: name,
                    engineId: machine.Engine
                )
            ) {
                continue;
            }
            listed.Add(item: new(
                ContentPath: $"{folder.TrimEnd('/')}/{name}",
                Name: name
            ));
        }
        listed.Sort(comparison: static (left, right) => string.CompareOrdinal(
            strA: left.Name,
            strB: right.Name
        ));
        entries = listed;
        reason = string.Empty;
        return true;
    }
    /// <summary>Finds the offered file a console argument names: its whole file name, or the part of it before the
    /// first <c>.</c> when exactly one offered file has that stem.</summary>
    /// <param name="entries">The offered files, as <see cref="TryList"/> returns them.</param>
    /// <param name="name">The file name or stem to find.</param>
    /// <param name="entry">The named file on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when exactly one offered file answers the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> or <paramref name="name"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryFind(IReadOnlyList<Entry> entries, string name, out Entry entry, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: entries);
        ArgumentNullException.ThrowIfNull(argument: name);

        foreach (var candidate in entries) {
            if (string.Equals(
                a: candidate.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                entry = candidate;
                reason = string.Empty;
                return true;
            }
        }

        var stems = entries.Where(predicate: candidate => string.Equals(
            a: Stem(name: candidate.Name),
            b: name,
            comparisonType: StringComparison.Ordinal
        )).ToArray();

        if (stems.Length == 1) {
            entry = stems[0];
            reason = string.Empty;
            return true;
        }
        entry = default;
        reason = ((stems.Length == 0)
            ? $"'{name}' names no file the folder offers ({Describe(entries: entries)})"
            : $"'{name}' names {stems.Length} files ({Describe(entries: stems)}); give the whole file name"
        );
        return false;
    }

    private static string Describe(IReadOnlyList<Entry> entries) => ((entries.Count == 0)
        ? "none"
        : string.Join(
            separator: ", ",
            values: entries.Select(selector: static entry => entry.Name)
        ));
    private static string Stem(string name) {
        var dot = name.IndexOf(value: '.');

        return ((dot < 0)
            ? name
            : name[..dot]
        );
    }
}
