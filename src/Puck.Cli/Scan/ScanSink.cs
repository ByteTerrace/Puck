namespace Puck.Cli.Scan;

// The analyzer output contract: stream a lone analyzer's JSONL to stdout, or write
// <OutDir>/<name>.jsonl (+ <name>.grouped.json) for a batch.
internal static class ScanSink {
    public static void Emit(string name, string jsonl, string grouped, ScanOptions options) {
        if (options.SingleStdout) {
            Console.Out.Write(value: jsonl);

            return;
        }

        Directory.CreateDirectory(path: options.OutDirectory);

        var jsonlPath = Path.Combine(path1: options.OutDirectory, path2: $"{name}.jsonl");

        File.WriteAllText(path: jsonlPath, contents: jsonl);
        Console.Error.WriteLine(value: $"scan: wrote {jsonlPath}");

        if (options.Grouped) {
            var groupedPath = Path.Combine(path1: options.OutDirectory, path2: $"{name}.grouped.json");

            File.WriteAllText(path: groupedPath, contents: grouped);
            Console.Error.WriteLine(value: $"scan: wrote {groupedPath}");
        }
    }
}
