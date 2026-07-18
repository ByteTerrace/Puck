using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Hosting;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The demo's data-driven root-node registrar + headless utilities. Every run — whether from <c>--run</c> or
/// synthesized from CLI options (<see cref="DemoRunDocuments.Synthesize"/>) — arrives here as a
/// <see cref="PuckRunDocument"/>: <see cref="RegisterRunDocument"/> installs its validation gate or its
/// composition-graph producer. Kept out of <c>Program</c> so the entry point stays loosely coupled.
/// </summary>
internal static class DemoRunRegistrar {
    /// <summary>Writes CLI parse errors, such as an unrecognized option or invalid value, to stderr.
    /// Kept out of the top-level <c>Program</c> so the entry point does not couple to the parser's error types.</summary>
    /// <param name="parseResult">The command-line parse result.</param>
    /// <returns><see langword="true"/> when the parse was clean; <see langword="false"/> when errors were reported (the
    /// caller should exit non-zero rather than fall through to the default showcase).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parseResult"/> is <see langword="null"/>.</exception>
    public static bool ReportParseErrors(ParseResult parseResult) {
        ArgumentNullException.ThrowIfNull(parseResult);

        if (parseResult.Errors.Count == 0) {
            return true;
        }

        foreach (var error in parseResult.Errors) {
            Console.Error.WriteLine(value: $"[args] {error.Message}");
        }

        return false;
    }

    /// <summary>Whether a run document's graph CAN host on Direct3D 12 on this OS. When false, a
    /// <c>host.backend:"directx"</c> request degrades to a Vulkan host.</summary>
    /// <param name="document">The validated run document.</param>
    /// <returns><see langword="true"/> when the document's graph can host on Direct3D 12.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool HostDirectXAvailable(PuckRunDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);
    }

    /// <summary>Loads and validates a data-driven run document (the <c>--run</c> path) up front, so its host section
    /// can drive the window/launcher/presentation before the host is built. A bad document is reported here as an
    /// actionable, source-attributed error (and <see langword="null"/> returned) rather than a downstream GPU crash.</summary>
    /// <param name="runPath">The path to the run document.</param>
    /// <returns>The validated document, or <see langword="null"/> when it could not be loaded/validated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runPath"/> is <see langword="null"/>.</exception>
    public static PuckRunDocument? LoadRunDocument(string runPath) {
        ArgumentNullException.ThrowIfNull(runPath);

        try {
            var document = RunDocument.Load(path: runPath);
            var summary = ((document.Validation is ValidationDocument validation) ? $"validation gate '{validation.Gate}'" : $"{document.Graph?.GetType().Name} graph");

            Console.Error.WriteLine(value: $"[run] '{runPath}': {summary}, {(document.Scene?.Objects?.Count ?? 0)} object(s), {(document.Viewports?.Count ?? 0)} viewport(s)");

            return document;
        } catch (Exception exception) when (((exception is RunDocumentValidationException) || (exception is JsonException) || (exception is IOException) || (exception is NotSupportedException) || (exception is UnauthorizedAccessException) || (exception is ArgumentException))) {
            Console.Error.WriteLine(value: $"[run] failed to load '{runPath}':{Environment.NewLine}{exception.Message}");

            return null;
        }
    }

    /// <summary>Reports (to stderr) the reason a document's graph cannot run on the RESOLVED host (see
    /// <see cref="GraphBuilder.UnsupportedReason"/>) — the entry point's pre-flight, shaped like
    /// <see cref="ReportParseErrors"/> so <c>Program</c> stays coupled to this registrar alone.</summary>
    /// <param name="document">The validated run document.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12.</param>
    /// <returns><see langword="true"/> when the graph is unsupported (the caller should exit 2).</returns>
    public static bool ReportGraphUnsupported(PuckRunDocument document, bool hostsOnDirectX) {
        if (GraphBuilder.UnsupportedReason(document: document, hostsOnDirectX: hostsOnDirectX) is { } reason) {
            Console.Error.WriteLine(value: $"[run] {reason}");

            return true;
        }

        return false;
    }

    /// <summary>Registers the root render node from an already-loaded run document: its graph selects the producer and
    /// its scene + viewports drive the injected frame source.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="document">The validated run document.</param>
    /// <param name="capturePath">An optional CLI capture path; overrides the graph node's own capture field when set.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (drives per-node host placement).</param>
    /// <param name="width">The resolved render width in pixels (from <c>host.size</c>).</param>
    /// <param name="height">The resolved render height in pixels (from <c>host.size</c>).</param>
    /// <returns>The shared parity result a validation gate fills (its <c>ExitCode</c> is propagated by the entry
    /// point); for a live render run it is unused and left at its default.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
    public static ParityResult RegisterRunDocument(IServiceCollection services, PuckRunDocument document, string? capturePath, bool hostsOnDirectX, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        var result = new ParityResult();

        if (document.Validation is ValidationDocument validation) {
            // A validation run installs a self-contained gate (the composition graph is ignored) and propagates its
            // exit code; the gate renders offscreen at a fixed 960x600, so capture/size do not apply.
            WarnOffscreenNoOp(capturePath: capturePath, gate: "validation", height: height, width: width);

            services.AddSingleton<IRenderNode>(implementationFactory: sp => CreateValidationNode(result: result, validation: validation));

            return result;
        }

        services.AddSingleton<IRenderNode>(implementationFactory: sp => GraphBuilder.Build(
            capturePath: capturePath,
            document: document,
            height: height,
            hostsOnDirectX: hostsOnDirectX,
            serviceProvider: sp,
            width: width
        ));

        return result;
    }

    // A validation gate renders OFFSCREEN at a fixed 960x600 and writes its own artifacts, so a CLI --capture
    // path and a host.size are no-ops for it; surface that rather than silently dropping them. "Non-default size"
    // compares against the resolved defaults (HostSettings), never literals, so a default bump cannot false-positive.
    private static void WarnOffscreenNoOp(string gate, string? capturePath, uint width, uint height) {
        if ((capturePath is not null) || (width != HostSettings.DefaultWidth) || (height != HostSettings.DefaultHeight)) {
            Console.Error.WriteLine(value: $"[run] a {gate} gate renders offscreen at a fixed 960x600 and writes its own artifacts; --capture and host.size are ignored.");
        }
    }

    // Maps a validation gate name to its node. 'overworld' is the CPU determinism self-check over the actual Overworld
    // sim — the one gate with no Puck.Post equivalent, kept in the demo on purpose. (The cross-backend engine gates —
    // parity/export/compute/reverse-share/indirect/resample/viewports/pixelate/capture/camera/genlock/cli-determinism/
    // world/fuzz — are Puck.Post POST stages; the POST is their single home.)
    private static IRenderNode CreateValidationNode(ValidationDocument validation, ParityResult result) {
        return validation.Gate.ToLowerInvariant() switch {
            "overworld" => new Overworld.OverworldDeterminismNode(result: result),
            _ => throw new NotSupportedException(message: $"Unsupported validation gate '{validation.Gate}'."),
        };
    }

    /// <summary>Writes the run-document JSON Schema to a path (the headless <c>--emit-schema</c> utility): the schema
    /// is exported from the SAME source-gen options that read documents, so it cannot drift from the model.</summary>
    /// <param name="path">The output path; parent directories are created.</param>
    /// <returns>0 on success.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public static int EmitSchema(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var schema = RunDocumentSchema.Export();
        var directory = Path.GetDirectoryName(path: path);

        if (!string.IsNullOrEmpty(value: directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(contents: schema, path: path);
        Console.WriteLine(value: $"[emit-schema] wrote the run-document schema ({schema.Length} chars) to '{path}'.");

        return 0;
    }

    // The live overworld — the demo's opening experience: a controller-driven player box in a room with bootable console
    // stands, simulated deterministically and rendered through the compute SDF world path (the player rides the
    // per-frame dynamic-transform buffer; each booted console lights a GamingBrick pane). An immersed document
    // (OverworldNode.Immersed, the --rom path) opens INSIDE the machines instead — the fourth-wall boot. The overworld
    // document resolves to a Vulkan host today; the backend still flows through so the shared render builder owns
    // the choice.
    internal static IRenderNode CreateOverworldRootNode(IServiceProvider serviceProvider, IReadOnlyList<GamingBrickSource> consoles, IReadOnlyList<CartridgeSource> library, string? capturePath, Overworld.IAddonControlHost? addons = null, bool hostsOnDirectX = false, bool immersed = false, string? world = null, long? cell = null, string? revealedRenderScale = null, uint width = HostSettings.DefaultWidth, uint height = HostSettings.DefaultHeight) {
        return new Overworld.OverworldRenderNode(addons: addons, bootWorld: world, capturePath: capturePath, consoles: consoles, height: height, hostsOnDirectX: hostsOnDirectX, immersed: immersed, library: library, revealedRenderScale: revealedRenderScale, serviceProvider: serviceProvider, spawnCell: cell, width: width);
    }
}
