// Diffs two `world.affordances` captures on EVERYTHING the payload carries: per-verb
// metadata (routing / valueKind / bindable) AND per-channel metadata (shape / consumer).
// A name diff cannot see this class of change (a bindability flip moves no name), so any
// difference in either table FAILS: added and removed rows are listed with their metadata,
// changed rows field by field, and the exit code is 1 the moment either side disagrees.
// Exit 0 means the two captures carry identical verb and channel metadata.
//
//   dotnet run -c Release .runs/afford-diff.cs -- <baseline-capture> <fresh-capture>
//
// Release is not optional: Windows App Control on the reference machine refuses to load
// never-seen Debug binaries (FileLoadException 0x800711C7), and `-c Release` must precede
// the file path or the SDK reads it as a program argument (docs/agent-guide.md).
//
// Each capture is raw stdout from a windowed boot fed `world.affordances`: the narration
// `[world.affordances: <verbs-array>,"channels":<channels-array>]` — two JSON arrays, no
// enclosing object. Everything before the marker is ignored; a capture missing the
// channels array is refused rather than half-diffed.

using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run -c Release .runs/afford-diff.cs -- <baseline-capture> <fresh-capture>");
    return 2;
}

AffordanceTables baseline;
AffordanceTables fresh;

try
{
    baseline = ReadAffordanceTables(path: args[0]);
    fresh = ReadAffordanceTables(path: args[1]);
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"afford-diff: {exception.Message}");
    return 2;
}

Console.WriteLine(
    $"affordances: verbs {baseline.Verbs.Count} -> {fresh.Verbs.Count}; " +
    $"channels {baseline.Channels.Count} -> {fresh.Channels.Count}");

var differences =
    CompareTable(old: baseline.Verbs, fresh: fresh.Verbs, label: "verb") +
    CompareTable(old: baseline.Channels, fresh: fresh.Channels, label: "channel");

if (differences > 0)
{
    Console.Error.WriteLine($"afford-diff: {differences} affordance metadata difference(s).");
    return 1;
}

Console.WriteLine("affordance metadata: identical (verbs and channels)");
return 0;

// The fields kept per row, in the order they are reported. A row's own 'name' keys the
// table and is never compared as a field.
static string[] FieldsFor(string label) => label switch
{
    "verb" => ["routing", "valueKind", "bindable"],
    _ => ["shape", "consumer"],
};

// Returns the first JSON array at or after 'from' along with the index of its closing
// bracket. The capture has trailing text after each array, so the walk tracks string and
// escape state and bracket depth to find the array's own end rather than the line's.
static (JsonDocument Document, int End) ReadJsonArray(string text, int from, string path)
{
    var start = text.IndexOf('[', from);

    if (start < 0)
    {
        throw new InvalidDataException($"no JSON array at/after index {from} in {path}");
    }

    var depth = 0;
    var inString = false;
    var escaped = false;
    var end = -1;

    for (var i = start; i < text.Length; ++i)
    {
        var c = text[i];

        if (escaped)
        {
            escaped = false;

            continue;
        }

        if (inString)
        {
            if (c == '\\') { escaped = true; }
            else if (c == '"') { inString = false; }

            continue;
        }

        if (c == '"') { inString = true; }
        else if (c == '[') { ++depth; }
        else if (c == ']')
        {
            --depth;

            if (depth == 0)
            {
                end = i;

                break;
            }
        }
    }

    if (end < 0)
    {
        throw new InvalidDataException($"unterminated JSON array in {path}");
    }

    return (JsonDocument.Parse(json: text.AsMemory(start, (end - start) + 1)), end);
}

// Builds a name-keyed table from a JSON array of rows, keeping the named fields. Values are
// normalized to strings here so the comparison below is one ordinal string compare per field
// and booleans render the way the payload spells them.
static Dictionary<string, Dictionary<string, string>> ReadTable(JsonDocument array, string[] fields)
{
    var table = new Dictionary<string, Dictionary<string, string>>(comparer: StringComparer.Ordinal);

    foreach (var entry in array.RootElement.EnumerateArray())
    {
        var row = new Dictionary<string, string>(capacity: fields.Length, comparer: StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var property = entry.GetProperty(propertyName: field);

            row[field] = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.GetRawText(),
            };
        }

        table[entry.GetProperty(propertyName: "name").GetString() ?? string.Empty] = row;
    }

    return table;
}

static AffordanceTables ReadAffordanceTables(string path)
{
    var text = File.ReadAllText(path: path);
    var marker = text.IndexOf("world.affordances:", StringComparison.Ordinal);

    if (marker < 0)
    {
        throw new InvalidDataException($"no 'world.affordances:' marker in {path}");
    }

    var (verbs, verbsEnd) = ReadJsonArray(text: text, from: marker, path: path);
    var channelsMarker = text.IndexOf("\"channels\":", verbsEnd, StringComparison.Ordinal);

    if (channelsMarker < 0)
    {
        throw new InvalidDataException($"no '\"channels\":' array after the verbs array in {path}");
    }

    var (channels, _) = ReadJsonArray(text: text, from: channelsMarker, path: path);

    return new AffordanceTables(
        Channels: ReadTable(array: channels, fields: FieldsFor(label: "channel")),
        Verbs: ReadTable(array: verbs, fields: FieldsFor(label: "verb")));
}

static string FormatMetadata(Dictionary<string, string> row, string[] fields) =>
    string.Join(' ', fields.Select(field => $"{field}={row[field]}"));

// Prints +/-/~ rows for one name-keyed table pair and returns the difference count. Names are
// walked in ordinal order so a run's output is stable and diffable against a prior run's.
static int CompareTable(Dictionary<string, Dictionary<string, string>> old, Dictionary<string, Dictionary<string, string>> fresh, string label)
{
    var count = 0;
    var fields = FieldsFor(label: label);
    var oldNames = old.Keys.Order(comparer: StringComparer.Ordinal).ToArray();
    var freshNames = fresh.Keys.Order(comparer: StringComparer.Ordinal).ToArray();

    foreach (var name in freshNames)
    {
        if (!old.ContainsKey(key: name))
        {
            Console.WriteLine($"  +  {label} {name} {FormatMetadata(row: fresh[name], fields: fields)}");
            ++count;
        }
    }

    foreach (var name in oldNames)
    {
        if (!fresh.ContainsKey(key: name))
        {
            Console.WriteLine($"  -  {label} {name} {FormatMetadata(row: old[name], fields: fields)}");
            ++count;
        }
    }

    foreach (var name in oldNames)
    {
        if (!fresh.TryGetValue(key: name, value: out var after))
        {
            continue;
        }

        var before = old[name];

        foreach (var field in fields)
        {
            if (!string.Equals(before[field], after[field], StringComparison.Ordinal))
            {
                Console.WriteLine($"  ~  {label} {name} {field}: {before[field]} -> {after[field]}");
                ++count;
            }
        }
    }

    return count;
}

internal readonly record struct AffordanceTables(
    Dictionary<string, Dictionary<string, string>> Channels,
    Dictionary<string, Dictionary<string, string>> Verbs);
