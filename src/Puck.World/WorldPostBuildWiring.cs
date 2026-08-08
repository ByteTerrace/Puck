using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Puck.Commands;
using Puck.Launcher;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The EVERY-SHAPE post-build wiring step: the affordance vocabulary install,
/// the accepted-session-lever attachment, the outstanding-capture drain (see the end of
/// <see cref="Install"/>), and the server's <see cref="WorldServer.EchoTap"/>/<see cref="WorldServer.SaveEffectTap"/>/
/// <see cref="WorldMachineHost.MachineLifecycleTap"/> closures — moved OUT of the old presentation-only render-root
/// factory so <c>wire.errors</c> stays honest headless (a deferred Simulation-routed refusal is counted regardless of
/// boot shape). Called once from <c>Program.cs</c> right after <c>IHost.Build()</c>, for BOTH boot shapes. The
/// toast/HUD-structure/audio-CUE-listener-placement-lookup halves that only make sense with a renderer resolve their
/// presentation services OPTIONALLY (<see cref="IServiceProvider.GetService"/>, never <c>GetRequiredService</c>) and
/// no-op when absent.
/// </summary>
internal static class WorldPostBuildWiring {
    /// <summary>Installs the affordance vocabulary, attaches the session-lever sink, wires the server's echo/cue
    /// taps, and registers the shutdown drain that reports an armed capture no frame ever served. Safe to call
    /// exactly once, after the container has built but before the host starts.</summary>
    /// <param name="services">The built root service provider.</param>
    public static void Install(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        var consoleRegistry = services.GetRequiredService<CommandRegistry>();

        // The TCP socket door: bound ONLY when host.listen/--listen names an endpoint — a world with no Listen field
        // never opens a socket, exactly like a headless flag never opening a window. Started here (not a factory)
        // so it observes the fully-built container's WorldHostSettings singleton.
        if (services.GetRequiredService<WorldHostSettings>().Listen is { } listen) {
            services.GetRequiredService<WorldTcpHost>().Start(listen: listen);
        }

        // The affordance vocabulary goes live here — the first post-container point on the boot path where the built
        // registry exists (whichever verbs THIS boot shape actually composed). From now on every binding door
        // (player.bind, recomposes, the document validators) refuses a command name the registry does not carry; the
        // sweep re-covers the layers that composed BEFORE this instant (the engine default and the world's boot
        // overlays), so a dead reference in them prints loudly at boot instead of resolving to a silent dead key.
        // Commands only: a channel table belongs to the document that declares it, so every caller supplies its own
        // (WorldSeatBindings holds the boot instance's; the document validator compiles the candidate's).
        WorldAffordances.Install(registry: consoleRegistry);
        services.GetRequiredService<WorldSeatBindings>().ValidateAffordancesLoudly();

        // Seed the seats' context-family states off the boot census once, so a read-back that runs before the first
        // simulation tick reports the joined boot seats truthfully rather than the resolver's cold defaults (the
        // per-tick publish in WorldSimulation takes over from the first step).
        WorldSeatContextSync.Publish(
            seatBindings: services.GetRequiredService<WorldSeatBindings>(),
            roster: services.GetRequiredService<PlayerRoster>(),
            grants: services.GetRequiredService<WorldServer>().Grants,
            anchor: services.GetRequiredService<WorldPerceptionAnchor>()
        );

        // Close the lever path here, where both halves are resolvable in EVERY shape: an accepted lever reaches the
        // client, which either applies it (presentation composed) or drops it per WorldClient's own documented
        // headless contract.
        services.GetRequiredService<WorldClient>().AttachSessionLevers(levers: services.GetRequiredService<WorldSessionLeverSink>());

        // The presentation-only halves of the echo fan-out — resolved ONCE, optionally, so the tap closure below
        // never queries the container per-echo. All null together (headless) or all non-null together (presentation
        // composed): AddWorldPresentation registers every one of them or none of them.
        var toasts = services.GetService<OverlayToastStore>();
        var overlayFeed = services.GetService<WorldOverlayFeed>();
        var editorDrag = services.GetService<WorldEditorDrag>();
        var editorWorkbench = services.GetService<WorldWorkbench>();
        var consoleMirror = services.GetService<WorldConsoleMirror>();
        var audioDirector = services.GetRequiredService<WorldAudioDirector>();
        var definitionSource = services.GetRequiredService<WorldDefinitionSource>();

        services.GetRequiredService<WorldServer>().EchoTap = echo => {
            // world.load/world.reload move what the console considers "the current origin" — but only once the
            // SERVER's own echo confirms the rebuild actually applied (this tap fires from the tick boundary, after
            // every gate — authority, dirty-guard, validation, capacity, solids — has already passed), never eagerly
            // at submit time, when the rebuild might still be refused. world.reset never reaches here with a
            // RebuildOrigin (it targets the base without moving it), so SourcePath is correctly left untouched.
            if (!echo.Rejected && (echo.Kind == WorldEditEchoKind.Rebuild) && (echo.RebuildOrigin is { } origin)) {
                definitionSource.SourcePath = origin;
            }

            // toast/HUD narration is presentation-only; a headless boot simply has nowhere to paint it.
            toasts?.Publish(message: echo.Message, isError: echo.Rejected);
            // The chip wraps but is still bounded; the panel row is the FULL text (up to its 120-column width), so a
            // capacity reason too long for the toast stays readable where the operator is already looking.
            consoleMirror?.RecordEcho(message: echo.Message, refused: echo.Rejected);

            // A world edit is Simulation-routed: the SUBMIT succeeded (the line entered the tick queue) and the
            // server refuses it a tick later, so the registry's own dispatch accounting cannot see it. This tap is
            // the one place both halves meet — count the deferred refusal here so `wire.errors` reports it exactly
            // like a synchronous one, IN EVERY BOOT SHAPE. No double count: a line refused synchronously never
            // reaches the server and so never echoes.
            if (echo.Rejected) {
                consoleRegistry.NoteDeferredRejection();
            }

            // Only applied DOCUMENT edits stamp the act-class tag — grant-table changes narrate as toasts alone.
            // Presentation-only: nothing else reads the HUD act-class tag headless.
            if (!echo.Rejected && (echo.Kind != WorldEditEchoKind.GrantTable)) {
                overlayFeed?.NoteMutationApplied(documentOnly: (echo.Kind == WorldEditEchoKind.DocumentDefaults));
            }

            // A rejected mutation correlates back to the frozen released drag preview that submitted it: the
            // matched seat's overlay retires NOW and the row snaps honestly back, instead of waiting out the
            // deadline. Presentation-only (there is no drag preview headless).
            if (echo.Rejected && (echo.Mutation is { } rejectedMutation)) {
                editorDrag?.NoteRejected(mutation: rejectedMutation);
                // A rejected sculpt commit clears its bench's pending flag WITHOUT flipping clean — the work stays
                // counted as uncommitted (the accept, in WorldWorkbench.Tick, is the only clean edge).
                editorWorkbench?.NoteCommitRejected(mutation: rejectedMutation);
            }

            // THE EDIT-ECHO CUE LANE (the shimmer's audio twin): the same outcome fires its cue token — capability
            // denials as grant.denied, other rejections as mutation.rejected, applied edits as mutation.applied AT
            // the changed row's authored position where the mutation payload carries one. The audio director is
            // CORE, so this runs unconditionally; a headless boot's cues simply accumulate in a queue no device pump
            // ever drains (WorldAudioRenderService is presentation-only).
            if (echo.Denied) {
                audioDirector.SubmitCue(eventToken: WorldAudioCue.GrantDenied, site: null);
            } else if (echo.Kind != WorldEditEchoKind.GrantTable) {
                audioDirector.SubmitCue(
                    eventToken: (echo.Rejected ? WorldAudioCue.MutationRejected : WorldAudioCue.MutationApplied),
                    site: WorldAudioDirector.MutationSite(mutation: echo.Mutation)
                );
            }
        };

        // THE SAVE-EFFECT TAP: a world rule's 'save' effect performs engine I/O directly rather than composing a
        // WorldMutation (see ActionEffect.Save's remarks for why), so WorldServer cannot run it through the ordinary
        // mutation pipeline — and cannot run the CAPTURE itself either: Puck.World.Server references no rendering or
        // input, and WorldSessionCapture.Capture (the world.save fold) needs the live render levers, screen binder,
        // audio director, and pacing control, all composition-root state. This closure runs the IDENTICAL fold
        // WorldMutationCommandModule's own 'world.save' verb runs, to the world's own loaded file (never an authored
        // path — see the effect's remarks on why), and compacts the journal on success exactly like a manual save.
        // A write failure (disk full, the target's directory gone, a read-only file) is caught and narrated on
        // stderr by name; the firing tick is not rolled back, because nothing durable in it depended on the save
        // succeeding.
        var worldServer = services.GetRequiredService<WorldServer>();
        var renderSettings = services.GetRequiredService<WorldRenderSettings>();
        var screenBinder = services.GetRequiredService<WorldScreenBinder>();
        var pacing = services.GetRequiredService<PresentPacingControl>();

        worldServer.SaveEffectTap = tick => {
            var target = definitionSource.SourcePath;

            try {
                var snapshot = WorldSessionCapture.Capture(definition: worldServer.Definition, render: renderSettings, population: worldServer.Population, binder: screenBinder, audio: audioDirector, pacing: pacing, tick: tick);
                var bytes = WorldDefinitionSerialization.Save(definition: snapshot, path: target);

                worldServer.Compact();

                Console.Error.WriteLine(value: $"[world.rule: save effect -> {target} ({bytes} bytes)]");
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)) {
                Console.Error.WriteLine(value: $"[world.rule: save effect refused — could not write {target} ({exception.Message.ReplaceLineEndings(replacementText: " ")})]");
            }
        };

        // THE MACHINE LIFECYCLE CUE LANE: machine boot/fault outcomes fire screen.boot / screen.fault at the screen
        // row's authored face origin. CORE: Server.WorldMachineHost and WorldClient are both core-registered, so
        // this runs unconditionally in EVERY boot shape — a headless boot's screens boot (and step) real machines,
        // so this cue lane is presentation-only only in WHO fires it (the audio director's cue queue accumulates
        // harmlessly with no device pump draining it headless, same as every other cue here). The memory-peek seam
        // that used to be wired here (WorldServer.MachineMemoryPeek) is GONE: Server.WorldMachineHost implements
        // IWorldMachineMemoryPeek directly and is reached through WorldServer.Machines, always present, no wiring
        // needed.
        var audioCueClient = services.GetRequiredService<WorldClient>();

        services.GetRequiredService<WorldMachineHost>().MachineLifecycleTap = (index, faulted) => {
            Vector3? site = null;

            foreach (var screen in audioCueClient.Definition.Screens) {
                if (screen.Index == index) {
                    site = screen.Origin;

                    break;
                }
            }

            audioDirector.SubmitCue(eventToken: (faulted ? WorldAudioCue.ScreenFault : WorldAudioCue.ScreenBoot), site: site);
        };

        // THE CAPTURE-REQUEST DRAIN: world.screenshot arms a readback of the NEXT composed frame, so a run that ends
        // before that frame writes nothing at all. Left alone, the caller's only evidence is the arming echo, which
        // is indistinguishable from a capture that succeeded — the silent-success shape this repository has already
        // been bitten by. Say it out loud instead, at ApplicationStopped (every hosted service has stopped, so the
        // render loop is provably finished and an outstanding request provably never will be served). Presentation-
        // only: a headless boot has no render probe and world.screenshot refuses there anyway.
        if (services.GetService<WorldRenderProbe>() is { } renderProbe) {
            services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(callback: () => {
                if (renderProbe.Render?.PendingCapturePath is { } pending) {
                    Console.Error.WriteLine(value: $"[world.screenshot] WARNING: a capture of {pending} was still pending when the run ended — no frame composed after it was armed, so NO FILE WAS WRITTEN.");
                }
            });
        }
    }
}
