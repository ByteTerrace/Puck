using System.Text.Json.Serialization;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>One data-side addon descriptor the world carries — a World-local row carrying Name/ModulePath/Hash/Fuel/
/// Enabled/Requests, with no Puck.Scripting reference. Consumed when addons mount as principals into
/// <c>Server.WorldAddonRuntime</c>.</summary>
/// <param name="Name">The addon's identifying name — unique within the definition; used by console verbs and logging.</param>
/// <param name="ModulePath">The WASM module file path (machine-local; existence/hash verification is the run path's job).</param>
/// <param name="Hash">The content-address integrity pin (<c>sha256-64/{16 hex}</c>). required — a guest whose module
/// is unpinned makes the state it touches depend on a file on disk, which is a determinism hole before it is a
/// security one.</param>
/// <param name="Fuel">The per-tick fuel budget before a deterministic halt.</param>
/// <param name="Enabled">Whether the addon starts enabled.</param>
/// <param name="Requests">The addon's manifest — what it asks for, as data (see
/// <see cref="Protocol.WorldCapabilityRequest"/>): a designation only, never authority. Deny by default holds
/// regardless of what this names, and so does the converse — this is the left half of requests ∧ grants, so a hold the
/// manifest never names materializes no handle and the guest can never reach it (see
/// <c>Server.WorldAddonRuntime</c>). Null/empty means the row asked for nothing and therefore reaches nothing.
/// Reviewed by an operator before mounting, or by the runtime's own loud mount-time line naming exactly which requested
/// pairs the settled grant table (the permissive seed plus any <see cref="WorldDefinition.Grants"/> row already
/// applied) honors for this addon's principal right now, which it withholds, and which it holds beyond the
/// manifest.</param>
/// <param name="MemoryWatches">The addon's machine-memory watch rows (the fifth event family — see
/// <see cref="WorldAddonMemoryWatch"/>): declared alongside <see cref="Requests"/>, materializing only where the
/// settled grant table also holds <c>Observe/screen:&lt;n&gt;</c> with an event budget for the watched screen (the
/// same requested ∧ granted rule every other capability here already enforces). Null/empty means no watches.</param>
public sealed record WorldAddonRow(string Name, string ModulePath, string Hash, ulong Fuel, bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCapabilityRequest>? Requests = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldAddonMemoryWatch>? MemoryWatches = null);
/// <summary>One machine-memory watch row — an addon's declaration of one byte range on one screen's machine to poll
/// for value-changed edges (the achievements-shaped primitive: works on any ROM with a known memory layout). The
/// address space is the machine's whole bus view (<see cref="Puck.Abstractions.Machines.IMachineMemoryPeek"/>
/// already covers WRAM and external/battery RAM uniformly — a single flat address, never a split
/// WRAM-vs-SRAM shape). Publishes nothing on a headless host: the peek provider is registered only when presentation
/// composes a screen's machine (see <c>Puck.World.WorldScreenBinder</c>'s registration and
/// <c>Server.WorldEventFeed</c>'s own remarks) — the retired <c>arcade.world.json</c> proof world this family was
/// built for was local play, so this is a stated, permanent scope, not a gap to close later. No shipped world
/// authors a memory-watch row today.</summary>
/// <param name="Screen">The engine screen-surface index hosting the watched machine.</param>
/// <param name="Address">The first bus address to watch.</param>
/// <param name="Length">The byte-range length, 1..8 (a watch's changed-value payload is a single zero-extended
/// <c>i64</c> lane on the wire, so a range wider than 8 bytes has nowhere to carry its value and is refused).</param>
public sealed record WorldAddonMemoryWatch(int Screen, int Address, int Length);
