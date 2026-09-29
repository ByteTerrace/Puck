using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// Measures a proposed field-value payload without adding a wire contract. Only values whose authored source can
// change need a seed; literal-only fields can always reproduce their own admitted value from the document.
internal static class PresentationSeedCensus {
    public static (int Entries, int Components) Write(string name, WorldDefinition definition) {
        var mirror = ClientFixtures.StateMirror(definition);
        var environment = new WorldEnvironmentResolve();
        _ = environment.Resolve(definition, 0, mirror);
        var theme = new WorldThemeResolve();
        _ = theme.Resolve(definition, 0, mirror);
        var seeds = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var member in typeof(WorldEnvironmentResolve).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)) {
            if (member.GetValue(environment) is WorldValueDomainGroup group) { Read(group, seeds); }
            else if (member.GetValue(environment) is WorldValueDomainGroup[] groups) {
                foreach (var item in groups) { Read(item, seeds); }
            }
        }
        var reads = typeof(WorldThemeResolve).GetField("m_reads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(theme)!;
        Read((WorldValueDomainGroup)reads.GetType().GetProperty("Domains")!.GetValue(reads)!, seeds);
        var json = new JsonObject();
        var components = 0;
        var keyBytes = 0;
        foreach (var (path, values) in seeds) {
            var array = new JsonArray();
            foreach (var value in values) { array.Add(value); }
            json[path] = array;
            components += values.Length;
            keyBytes += Encoding.UTF8.GetByteCount(path);
        }
        TestContext.Current.TestOutputHelper!.WriteLine($"{name}: proposed-seed-entries={seeds.Count}; numeric-binary32-bytes={components * sizeof(float)}; field-path-utf8-bytes={keyBytes}; compact-json-bytes={Encoding.UTF8.GetByteCount(json.ToJsonString())}; paths={string.Join(",", seeds.Keys)}");
        return (seeds.Count, components);
    }

    private static void Read(WorldValueDomainGroup group, SortedDictionary<string, double[]> seeds) {
        var fields = (IDictionary)typeof(WorldValueDomainGroup).GetField("m_fields", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(group)!;
        foreach (DictionaryEntry entry in fields) {
            var value = entry.Value!;
            object? Property(string property) => value.GetType().GetProperty(property)!.GetValue(value);
            var source = (string)Property("Source")!;
            if (!source.Contains("state.", StringComparison.Ordinal) && !source.Contains("clock ", StringComparison.Ordinal)) { continue; }
            var path = (string)Property("Path")!;
            var initial = (double)Property("Initial")!;
            if (Property("FirstPath") is string first && Property("SecondPath") is string second) {
                seeds[first] = [initial];
                seeds[second] = [(double)Property("Second")!];
            } else {
                seeds[path] = Property("Third") is double third ? [initial, (double)Property("Second")!, third]
                    : path.EndsWith(".inkLow/inkHigh", StringComparison.Ordinal)
                    ? [initial, (double)Property("Second")!] : [initial];
            }
        }
    }
}
