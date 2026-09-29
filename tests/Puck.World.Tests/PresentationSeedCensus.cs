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

        _ = theme.Resolve(definition: definition, mirror: mirror, revision: 0);
        var seeds = new SortedDictionary<string, double[]>(comparer: StringComparer.Ordinal);

        foreach (var member in typeof(WorldEnvironmentResolve).GetFields(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic)) {
            if (member.GetValue(obj: environment) is WorldValueDomainGroup group) { Read(group: group, seeds: seeds); } else if (member.GetValue(obj: environment) is WorldValueDomainGroup[] groups) {
                foreach (var item in groups) { Read(group: item, seeds: seeds); }
            }
        }
        var reads = typeof(WorldThemeResolve).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_reads")!.GetValue(obj: theme)!;

        Read(group: ((WorldValueDomainGroup)reads.GetType().GetProperty(name: "Domains")!.GetValue(obj: reads)!), seeds: seeds);
        var json = new JsonObject();
        var components = 0;
        var keyBytes = 0;

        foreach (var (path, values) in seeds) {
            var array = new JsonArray();

            foreach (var value in values) { array.Add(value: value); }
            json[path] = array;
            components += values.Length;
            keyBytes += Encoding.UTF8.GetByteCount(s: path);
        }
        TestContext.Current.TestOutputHelper!.WriteLine(message: $"{name}: proposed-seed-entries={seeds.Count}; numeric-binary32-bytes={(components * sizeof(float))}; field-path-utf8-bytes={keyBytes}; compact-json-bytes={Encoding.UTF8.GetByteCount(s: json.ToJsonString())}; paths={string.Join(separator: ",", values: seeds.Keys)}");
        return (seeds.Count, components);
    }

    private static void Read(WorldValueDomainGroup group, SortedDictionary<string, double[]> seeds) {
        var fields = ((IDictionary)typeof(WorldValueDomainGroup).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_fields")!.GetValue(obj: group)!);

        foreach (DictionaryEntry entry in fields) {
            var value = entry.Value!;

            object? Property(string property) => value.GetType().GetProperty(name: property)!.GetValue(obj: value);
            var source = ((string)Property(property: "Source")!);

            if (!source.Contains(comparisonType: StringComparison.Ordinal, value: "state.") && !source.Contains(comparisonType: StringComparison.Ordinal, value: "clock ")) { continue; }
            var path = ((string)Property(property: "Path")!);
            var initial = ((double)Property(property: "Initial")!);

            if ((Property(property: "FirstPath") is string first) && (Property(property: "SecondPath") is string second)) {
                seeds[first] = [initial];
                seeds[second] = [((double)Property(property: "Second")!)];
            } else {
                seeds[path] = ((Property(property: "Third") is double third) ? [initial, ((double)Property(property: "Second")!), third]
                    : (path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".inkLow/inkHigh")
                    ? [initial, ((double)Property(property: "Second")!)] : [initial]));
            }
        }
    }
}
