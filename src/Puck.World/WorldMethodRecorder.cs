using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Reflection;

namespace Puck.World;

/// <summary>
/// Records every Puck method the runtime compiles in this process and writes them, at exit, as
/// <c>methods.&lt;pid&gt;.txt</c> in the run's state root: one <c>&lt;assembly&gt;&#9;&lt;metadata token, hex&gt;</c> line
/// each. <c>puck affected --record</c> maps the lines to source files through the build's PDBs to learn what each
/// canary executes. A build carries the recorder only when built with <c>-p:PuckRecordMethods=true</c>, which stamps
/// the <see cref="MetadataKey"/> assembly metadata this class looks for; every other build records nothing.
/// </summary>
internal sealed class WorldMethodRecorder : EventListener {
    /// <summary>The assembly-metadata key a recording build carries.</summary>
    public const string MetadataKey = "PuckRecordMethods";

    // The JIT keyword of the runtime's own event source, whose MethodLoadVerbose events name each compiled method.
    private const EventKeywords JitKeyword = ((EventKeywords)0x10);
    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";

    private readonly ConcurrentDictionary<(string Assembly, int Token), byte> m_methods = new();

    private WorldMethodRecorder() { }

    /// <summary>Gets a value indicating whether this build records methods.</summary>
    public static bool IsBuiltIn => typeof(WorldMethodRecorder).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(predicate: static attribute => (
        (attribute.Key == MetadataKey) &&
        (attribute.Value == "true")
    ));

    /// <summary>Starts recording when this build carries the recorder, writing the record when the process exits.</summary>
    /// <param name="stateRoot">Resolves the run's state root at exit, once the command line has named it; a run that
    /// ended before the command line resolved one records nothing.</param>
    public static void StartIfBuiltIn(Func<Server.WorldStateRoot?> stateRoot) {
        if (!IsBuiltIn) {
            return;
        }

        var recorder = new WorldMethodRecorder();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            if (stateRoot() is { } root) {
                recorder.Write(directory: root.FullPath);
            }
        };
    }

    private void Write(string directory) {
        Dispose();
        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllLines(
            contents: m_methods.Keys
                .Select(selector: static method => $"{method.Assembly}\t{method.Token.ToString(format: "x8", provider: CultureInfo.InvariantCulture)}")
                .Order(comparer: StringComparer.Ordinal),
            path: Path.Combine(
                path1: directory,
                path2: $"methods.{Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}.txt"
            )
        );
    }

    /// <inheritdoc/>
    protected override void OnEventSourceCreated(EventSource eventSource) {
        if (eventSource.Name == RuntimeSource) {
            EnableEvents(
                eventSource: eventSource,
                level: EventLevel.Verbose,
                matchAnyKeyword: JitKeyword
            );
        }
    }
    /// <inheritdoc/>
    protected override void OnEventWritten(EventWrittenEventArgs eventData) {
        if (
            (eventData.EventName is null) ||
            !eventData.EventName.StartsWith(comparisonType: StringComparison.Ordinal, value: "MethodLoadVerbose") ||
            (eventData.PayloadNames is null) ||
            (eventData.Payload is null)
        ) {
            return;
        }

        var index = eventData.PayloadNames.IndexOf(value: "MethodID");

        if (index < 0) {
            return;
        }

        MethodBase? method;

        try {
            method = MethodBase.GetMethodFromHandle(handle: RuntimeMethodHandle.FromIntPtr(value: ((nint)Convert.ToUInt64(value: eventData.Payload[index], provider: CultureInfo.InvariantCulture))));
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or InvalidOperationException)) {
            return;
        }

        var assembly = method?.Module.Assembly.GetName().Name;

        if (
            (method is not null) &&
            (assembly is not null) &&
            assembly.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck.")
        ) {
            _ = m_methods.TryAdd(key: (assembly, method.MetadataToken), value: 0);
        }
    }
}
