using System.Globalization;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.Assets.Qr;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.identify</c> — renders the running world's own identity onto a declared screen as a scannable QR code, so a
/// phone pointed at a live session carries that identity away. A composition of two things the engine already has: the
/// canonical content-address pin (<see cref="WorldDefinitionFileSource.ComputeContentHash"/>, over
/// <see cref="WorldDefinitionSerialization.Serialize"/>'s canonical bytes) and the live QR authoring path
/// <c>screen.source &lt;index&gt; qr</c> drives (<see cref="WorldScreenBinder.TryQr"/>). No new document section, no new screen-source kind,
/// no new noun — the payload is minted here and handed to the same encoder, so a code this verb draws and a code
/// <c>screen.source &lt;index&gt; qr</c> draws are the same object with the same read-back.
/// </summary>
/// <remarks>
/// <para>The hash covers the live definition, never the boot file: a mutated world is no longer the document that was
/// hashed at load, so re-serializing on every invocation is the only answer that always tells the truth about the world
/// actually running. It costs one canonical serialization of the definition per invocation — a one-shot cost on an
/// operator verb, never a per-frame one — and it removes the degraded case entirely: there is no world whose identity
/// this verb has to guess at or print stale. The echo says so (<c>hash-covers=live-definition</c>) rather than leaving
/// the reader to assume it matches the file on disk.</para>
/// <para>A separate module from <see cref="ScreenCommandModule"/>: this verb's subject is the world, not a screen — the
/// screen is only where the answer is drawn — and carving by subject is how this project keeps each class under its
/// analyzer ceilings.</para>
/// </remarks>
internal sealed class WorldIdentifyCommandModule(WorldScreenBinder binder, WorldServer server, IServerLink link) : ICommandModule {
    // The payload's URI scheme. Compact (a QR pays for every byte), self-describing (the scheme names the engine, the
    // path segment names the document family), and scanner-friendly: a phone shows it as plain text rather than
    // mangling it, and every character used is URI-unreserved so no encoder mode question arises.
    private const string PayloadScheme = "puck:world/";

    private readonly WorldScreenBinder m_binder = binder;
    private readonly IServerLink m_link = link;
    private readonly WorldServer m_server = server;

    // The world's identity as one URI-shaped token: who it is (documentId), what shape it is (schema), and exactly
    // which bytes it is right now (the canonical content-address pin of the LIVE definition). Deterministic in the
    // definition alone — same document, same payload, on every run and every machine.
    private static string BuildPayload(WorldDefinition definition) {
        var hash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: definition));

        // The id is author-supplied text; escaping it keeps a stray '?' or '&' from re-parsing the payload into a
        // different shape than the one this verb claims to have written.
        return $"{PayloadScheme}{Uri.EscapeDataString(stringToEscape: definition.DocumentId!)}?schema={definition.Schema}&hash={hash}";
    }
    private CommandResult IdentifyHandler(CommandContext context, WireArgs args) {
        if (args.Count is < 1 or > 2) {
            return CommandResult.Error(output: "[world.identify: expected <screenIndex> [ecLevel]]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[world.identify: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        // The same Control-over-the-screen check every screen.* producer verb applies, under whichever identity this
        // dispatch's ingress door stamped — drawing onto someone else's cabinet is drawing onto a cabinet.
        if (!m_server.Grants.Allows(
            principal: principal,
            capability: WorldCapability.Control,
            subject: GrantSubject.Screen(index: index)
        )) {
            return CommandResult.Error(output: $"[world.identify: {principal.Describe()} lacks Control over screen {index} — grant it (world.grant {principal.Describe()} control screen:{index})]");
        }

        var definition = m_server.Definition;

        // No documentId, no identity: a payload carrying only a hash would name the bytes without naming the world,
        // and inventing a stand-in id here would be minting an identity rather than reporting one.
        if (string.IsNullOrWhiteSpace(value: definition.DocumentId)) {
            return CommandResult.Error(output: "[world.identify: the running world carries no documentId — there is no identity to draw; author one and reload]");
        }

        var payload = BuildPayload(definition: definition);

        // A machine on the slot is ejected FIRST, through the ordered domain, exactly as screen.source <index> qr does it — this
        // project never disposes a machine lifetime Server.WorldMachineHost owns.
        if (m_server.Machines.HasMachine(index: index)) {
            m_link.SubmitScreenOp(
                op: new WorldScreenOp.Eject(Index: index),
                principal: principal
            );
        }

        var (ok, message) = m_binder.TryQr(
            index: index,
            payload: payload,
            ecLevel: ((args.Count == 2)
            ? args[1].ToString()
            : null),
            quietZoneModules: null
        );

        if (!ok) {
            return CommandResult.Error(output: $"[world.identify: {message}]");
        }

        // The read-back rule, taken literally: the echo is the binder's OWN record of what it drew, not a restatement
        // of what was asked for — and it carries the payload in full (screen.source <index> qr's own success line elides long
        // payloads), so a scripted session can assert the exact string a scanner will read.
        if (!m_binder.TryReadQr(
            authoring: out var authoring,
            index: index
        )) {
            return CommandResult.Error(output: $"[world.identify: screen {index} accepted the code but reports no QR source]");
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.identify: {index} v{authoring.Version} {QrErrorCorrection.Letter(level: authoring.Level)} mask{authoring.Mask} quietZone={authoring.QuietZoneModules} {authoring.Width}x{authoring.Height} hash-covers=live-definition payload='{authoring.Payload}']"
        ));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.identify",
            description: "Draws the running world's own identity onto a declared screen as a scannable QR code: world.identify <screenIndex> [ecLevel] — [ecLevel] one of L|M|Q|H (default M, the document default). The payload is puck:world/<documentId>?schema=<schema>&hash=sha256-64/<hex>, deterministic in the definition alone (no clock, no counter, no session state): the same world always mints the same payload. The hash is recomputed from the LIVE definition's canonical bytes on every invocation, so a world mutated since boot reports its CURRENT identity rather than the identity of the file it was loaded from — the echo says which document it covers. Drives the same live-QR path screen.source <index> qr does (any existing producer on the slot is cleared; a booted machine ejects first through the ordered domain) and echoes what it drew: version, EC level, mask, quiet zone, rendered extent, and the payload in full. Errors on an undeclared screen, an unrecognized EC-level letter, a world carrying no documentId, or a payload too large for the encoder's supported version range.",
            handler: IdentifyHandler,
            routing: CommandRouting.Simulation
        );
    }
}
