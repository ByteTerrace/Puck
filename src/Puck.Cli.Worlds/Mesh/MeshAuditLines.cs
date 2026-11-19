using System.Globalization;
using System.Text.RegularExpressions;

using Puck.SdfVm;

namespace Puck.Cli.Mesh;

/// <summary>The lines of <c>puck mesh audit</c>'s report that a tool reads back (the sweep's per-exhibit audit): a
/// group's heading and its topology line. Each is written and read here, so the text a reader matches is the text the
/// audit prints; a change to either line changes both sides at once.</summary>
public static partial class MeshAuditLines {
    /// <summary>A group's topology and coverage counts, as its topology line carries them.</summary>
    /// <param name="Folds">The folded edges.</param>
    /// <param name="Flipped">The flipped triangles.</param>
    /// <param name="Slivers">The sliver triangles.</param>
    /// <param name="OpenEdges">The open edges.</param>
    /// <param name="NonManifoldEdges">The non-manifold edges.</param>
    /// <param name="Crossings">The field's surface crossings sampled.</param>
    /// <param name="Uncovered">The crossings no triangle covers.</param>
    public readonly record struct Topology(int Folds, int Flipped, int Slivers, int OpenEdges, int NonManifoldEdges, int Crossings, int Uncovered);

    /// <summary>Returns a group's label: the scene's label, the prototype it is an instance of where the scene names
    /// one, its sealed geometry and whether it was audited posed.</summary>
    /// <param name="scene">The scene's label for the group.</param>
    /// <param name="prototype">The prototype, or <see langword="null"/>.</param>
    /// <param name="geometry">The group's sealed geometry hash.</param>
    /// <param name="posed">Whether the audit posed the group.</param>
    /// <returns>The label.</returns>
    public static string Label(string scene, string? prototype, UInt128 geometry, bool posed) =>
        string.Create(provider: CultureInfo.InvariantCulture, handler: $"{scene}{((prototype is null) ? string.Empty : $" prototype={prototype}")} geometry={geometry:x32}{(posed ? " posed" : string.Empty)}");

    /// <summary>Returns a measured group's heading line, without its line end.</summary>
    /// <param name="ordinal">The group's ordinal in the audit.</param>
    /// <param name="label">The group's label (<see cref="Label"/>).</param>
    /// <param name="report">The group's report.</param>
    /// <param name="cell">The mesh's cell, in millimetres, as text.</param>
    /// <param name="boxCentre">The mesh's box centre in world space, as text, or <see langword="null"/>.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static string Heading(int ordinal, string label, SdfMeshAuditReport report, string cell, string? boxCentre) {
        ArgumentNullException.ThrowIfNull(argument: report);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"group {ordinal} {label}: {report.Triangles} triangles, {report.Vertices} vertices, cell {cell} mm, score {report.Score:0.0}{((boxCentre is null) ? string.Empty : $", box centre {boxCentre}")}");
    }

    /// <summary>Reads a measured group's heading line.</summary>
    /// <param name="line">The line.</param>
    /// <param name="prototype">The prototype the group is an instance of.</param>
    /// <param name="score">The group's score.</param>
    /// <returns>Whether the line is the heading of a measured group that names a prototype.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public static bool TryReadHeading(string line, out string prototype, out double score) {
        ArgumentNullException.ThrowIfNull(argument: line);

        if (HeadingPattern().Match(input: line) is { Success: true } match) {
            prototype = match.Groups["prototype"].Value;
            score = double.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups["score"].Value);
            return true;
        }

        prototype = string.Empty;
        score = 0d;
        return false;
    }

    /// <summary>Returns a measured group's topology line, without its line end.</summary>
    /// <param name="report">The group's report.</param>
    /// <returns>The line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static string TopologyLine(SdfMeshAuditReport report) {
        ArgumentNullException.ThrowIfNull(argument: report);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"  topology: {report.Folds} folds ({report.CreaseFolds} on the field's creases), {report.Flipped} flipped, {report.Slivers} slivers, {report.OpenEdges} open edges, {report.NonManifoldEdges} non-manifold edges; surface: {report.Crossings} crossings, {report.MissingCrossings} uncovered");
    }

    /// <summary>Returns the classes of a measured group's defects, each with the cells' refinement flags its items lie in.</summary>
    /// <param name="tallies">The report's defect tallies (<see cref="SdfMeshAuditReport.Defects"/>).</param>
    /// <returns>The classes with items, as text; <see langword="null"/> when no class has an item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tallies"/> is <see langword="null"/>.</exception>
    public static string? DefectText(IReadOnlyList<SdfMeshDefectTally> tallies) {
        ArgumentNullException.ThrowIfNull(argument: tallies);

        var parts = tallies
            .Where(predicate: static tally => (tally.Count > 0))
            .Select(selector: static tally => {
                var flags = new[] { ("not-scanned", tally.NotScanned), ("scanned-nothing-flagged", tally.LatticeUnrefined), ("unflagged", tally.Unflagged), ("ring", tally.Ring), ("measured", tally.Measured), ("no-map", tally.Unknown), ("thin", tally.Thin) }
                    .Where(predicate: static flag => (flag.Item2 > 0))
                    .Select(selector: static flag => $"{flag.Item1} {flag.Item2}");

                return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{ClassName(defect: tally.Class)} {tally.Count} [{string.Join(separator: ", ", values: flags)}]");
            })
            .ToArray();

        return ((parts.Length == 0) ? null : string.Join(separator: "; ", values: parts));
    }

    /// <summary>Returns a class's name as the report prints it.</summary>
    /// <param name="defect">The class.</param>
    /// <returns>The name.</returns>
    public static string ClassName(SdfMeshDefectClass defect) => defect switch {
        SdfMeshDefectClass.OffSurfaceVertex => "off-surface-vertex",
        SdfMeshDefectClass.CreaseSliver => "crease-sliver",
        SdfMeshDefectClass.NarrowFace => "narrow-face",
        SdfMeshDefectClass.ShortTip => "short-tip",
        _ => "other",
    };

    /// <summary>Reads a measured group's topology line.</summary>
    /// <param name="line">The line.</param>
    /// <param name="topology">The counts it carries.</param>
    /// <returns>Whether the line is a topology line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public static bool TryReadTopology(string line, out Topology topology) {
        ArgumentNullException.ThrowIfNull(argument: line);

        if (TopologyPattern().Match(input: line) is { Success: true } match) {
            topology = new Topology(
                Crossings: Count(match: match, name: "crossings"),
                Flipped: Count(match: match, name: "flipped"),
                Folds: Count(match: match, name: "folds"),
                NonManifoldEdges: Count(match: match, name: "nonmanifold"),
                OpenEdges: Count(match: match, name: "open"),
                Slivers: Count(match: match, name: "slivers"),
                Uncovered: Count(match: match, name: "uncovered")
            );
            return true;
        }

        topology = default;
        return false;
    }

    private static int Count(Match match, string name) => int.Parse(provider: CultureInfo.InvariantCulture, s: match.Groups[name].Value);

    [GeneratedRegex(pattern: @"^group \d+ .*\bprototype=(?<prototype>\S+) .*: \d+ triangles, .*\bscore (?<score>\d+(?:\.\d+)?)", options: RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();
    [GeneratedRegex(pattern: @"^  topology: (?<folds>\d+) folds(?: \(\d+ on the field's creases\))?, (?<flipped>\d+) flipped, (?<slivers>\d+) slivers, (?<open>\d+) open edges, (?<nonmanifold>\d+) non-manifold edges; surface: (?<crossings>\d+) crossings, (?<uncovered>\d+) uncovered", options: RegexOptions.CultureInvariant)]
    private static partial Regex TopologyPattern();
}
