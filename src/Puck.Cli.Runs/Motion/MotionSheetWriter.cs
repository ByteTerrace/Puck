namespace Puck.Cli.Motion;

/// <summary>Writes a scenario's contact sheets from the camera stills a run wrote.</summary>
public static class MotionSheetWriter {
    /// <summary>Formats a cell's two-line label, each line clipped to what fits the cell: the camera, offset and tick, then the feet, speed and facts.</summary>
    /// <param name="camera">The camera's name.</param>
    /// <param name="still">The still.</param>
    /// <param name="cellWidth">The cell's width, in pixels.</param>
    /// <returns>The two lines, joined by a line feed.</returns>
    public static string CellLabel(string camera, MotionStillResult still, int cellWidth) {
        var heading = MotionSheets.Label(camera: camera, offset: still.Offset, tick: still.Tick);
        var detail = MotionRig.Describe(facts: MotionRig.FactsOf(answer: still.Where), rig: still.Rig);
        var headingFit = MotionSheets.CharactersThatFit(scale: MotionSheets.LabelScale, width: cellWidth);
        var detailFit = MotionSheets.CharactersThatFit(scale: 1, width: cellWidth);

        return $"{((heading.Length > headingFit) ? heading[..headingFit] : heading)}\n{((detail.Length > detailFit) ? detail[..detailFit] : detail)}";
    }
    /// <summary>Composes one sheet per camera and one combined sheet for a scenario, and writes them beside its stills.</summary>
    /// <param name="directory">The run's directory; the sheets go in the scenario's folder under it.</param>
    /// <param name="scenario">The scenario.</param>
    /// <param name="stills">The scenario's stills; a still missing a camera's file is left out of every sheet.</param>
    /// <param name="cameras">The cameras, in row order.</param>
    /// <param name="columns">The most frames in a band.</param>
    /// <param name="cellWidth">The width each frame is scaled to, in pixels.</param>
    /// <param name="problems">Receives what stopped a sheet from being written.</param>
    /// <returns>The sheets' files by name, <c>combined</c> and each camera, as paths relative to <paramref name="directory"/>; empty when no still has every camera.</returns>
    public static IReadOnlyDictionary<string, string> Write(string directory, MotionPlannedScenario scenario, IReadOnlyList<MotionStillResult> stills, IReadOnlyList<string> cameras, int columns, int cellWidth, List<string> problems) {
        var sheets = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var complete = stills.Where(predicate: still => cameras.All(predicate: still.Slots.ContainsKey)).ToArray();

        if ((complete.Length == 0) || (cameras.Count == 0)) {
            return sheets;
        }

        try {
            var frames = new List<IReadOnlyList<MotionImage>>(capacity: complete.Length);
            var labels = new List<IReadOnlyList<string>>(capacity: complete.Length);

            foreach (var still in complete) {
                var row = new List<MotionImage>(capacity: cameras.Count);
                var names = new List<string>(capacity: cameras.Count);

                foreach (var camera in cameras) {
                    row.Add(item: MotionSheets.ScaleToWidth(source: MotionSheets.Read(path: Path.Combine(path1: directory, path2: still.Slots[camera])), width: cellWidth));
                    names.Add(item: CellLabel(camera: camera, cellWidth: cellWidth, still: still));
                }

                frames.Add(item: row);
                labels.Add(item: names);
            }

            var cellHeight = frames[0][0].Height;
            var labelHeight = MotionSheets.LabelHeightFor();
            var clipped = labels;

            WriteSheet(
                directory: directory,
                frames: frames,
                key: "combined",
                labels: clipped,
                layout: MotionSheetLayout.Plan(cameras: cameras.Count, cellHeight: cellHeight, cellWidth: cellWidth, columns: columns, frames: complete.Length, labelHeight: labelHeight),
                path: $"{scenario.Scenario.Name}/sheet.png",
                sheets: sheets
            );

            for (var camera = 0; (camera < cameras.Count); camera++) {
                var index = camera;

                WriteSheet(
                    directory: directory,
                    frames: [.. frames.Select(selector: row => ((IReadOnlyList<MotionImage>)[row[index]]))],
                    key: cameras[camera],
                    labels: [.. clipped.Select(selector: names => ((IReadOnlyList<string>)[names[index]]))],
                    layout: MotionSheetLayout.Plan(cameras: 1, cellHeight: cellHeight, cellWidth: cellWidth, columns: columns, frames: complete.Length, labelHeight: labelHeight),
                    path: $"{scenario.Scenario.Name}/sheet-{cameras[camera]}.png",
                    sheets: sheets
                );
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)) {
            problems.Add(item: $"the contact sheets were not written: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        return sheets;
    }

    private static void WriteSheet(string directory, string key, string path, MotionSheetLayout layout, IReadOnlyList<IReadOnlyList<MotionImage>> frames, IReadOnlyList<IReadOnlyList<string>> labels, Dictionary<string, string> sheets) {
        MotionSheets.Write(image: MotionSheets.Compose(frames: frames, labels: labels, layout: layout), path: Path.Combine(path1: directory, path2: path));
        sheets[key] = path;
    }
}
