using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What reading a world's source for a reload answered.</summary>
/// <param name="Definition">The next revision: the source composed, drawn and settled as an edit, validated by no
/// one. <see langword="null"/> when the source still composes to the pin the authority holds, or was refused.</param>
/// <param name="ContentHash">The content pin of the composition read
/// (<see cref="WorldDefinitionFileSource.ComputeContentHash"/> over its bytes, before any draw); empty on a
/// refusal.</param>
/// <param name="Refusal">Why the source could not be read, or <see langword="null"/>.</param>
public sealed record WorldSourceRead(WorldDefinition? Definition, string ContentHash, string? Refusal = null);

/// <summary>What the reload door prepared off the pump: the delta from the revision the authority holds to the next
/// one, a source unchanged since that revision, or the refusal the read reached.</summary>
/// <param name="ContentHash">The next revision's content pin, which the authority keeps as
/// <see cref="WorldSourceRevision.ContentHash"/> once it accepts the reload; empty on a refusal.</param>
/// <param name="Delta">The delta, or <see langword="null"/> when nothing is sent.</param>
/// <param name="Refusal">The refusal, or <see langword="null"/>.</param>
public sealed record WorldReloadCut(string ContentHash, WorldReloadDelta? Delta, string? Refusal) {
    /// <summary>Gets whether the source still composes to the revision the authority holds, so nothing is sent.</summary>
    public bool Unchanged => ((Refusal is null) && (Delta is null));
    /// <summary>Gets how long cutting the delta took, in milliseconds, for a host's narration of where a reload's time
    /// went.</summary>
    public double DeltaMilliseconds { get; init; }
}

/// <summary>The one reload door: brings a running authority to its source's next revision. It captures the source
/// revision the authority holds on the pump, reads the source and cuts the delta on the thread pool, and submits the
/// delta from the pump as an ordinary reload rebuild, whose verdict settles the reload.</summary>
/// <remarks>The desktop's <c>world.reload</c> and a silo row's published-definition reload both run through it, each
/// with a reader of its own source (a file, the authority store). Nothing that loads, hashes or diffs a source runs on
/// the authority's tick: the tick's share is the merge of the delta over the live document and the install. The source
/// is read as an edit and validated once, by the authority that installs it
/// (<see cref="WorldDefinitionValidator"/> through its rebuild preparation). A revision's content pin is the pin of the
/// composition its reader read, taken before any draw, whatever the source.</remarks>
/// <param name="server">The authority being reloaded.</param>
/// <param name="link">The link the rebuild is submitted through.</param>
/// <param name="echoes">The pending-verb table the authority's verdict answers through.</param>
public sealed class WorldReloadDoor(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes) {
    // Reloads whose reading the pump has not yet taken; pump only.
    private readonly List<Pending> m_reading = [];

    /// <summary>One reload in flight: its reading off the pump, then the authority's verdict.</summary>
    public sealed class Pending {
        private readonly WorldReloadDoor m_door;
        private readonly WorldRebuildOrigin m_origin;
        private readonly Principal m_principal;
        private readonly Func<WorldReloadCut, CommandResult?>? m_taken;
        private readonly string m_verb;
        private BackgroundBuild<WorldReloadCut>? m_build;

        internal Pending(WorldReloadDoor door, string verb, Principal principal, WorldRebuildOrigin origin, Func<WorldReloadCut, CommandResult?>? taken,
            Func<CancellationToken, Task<WorldReloadCut>> cut) {
            m_door = door;
            m_origin = origin;
            m_principal = principal;
            m_taken = taken;
            m_verb = verb;
            m_build = new BackgroundBuild<WorldReloadCut>();
            m_build.Start(build: cut);
            Reading = m_build.Completion!;
        }

        /// <summary>Gets the reading's completion, successful or not: a pump that runs only when posted to posts
        /// <see cref="WorldReloadDoor.Poll"/> from it.</summary>
        public Task Reading { get; }
        /// <summary>Gets the answer reached without submitting anything (a refusal, an unchanged source), which no
        /// authority verdict reports; <see langword="null"/> before the pump takes the reading and for a submitted
        /// reload.</summary>
        public CommandResult? Unsubmitted { get; private set; }
        /// <summary>Gets the reload's settlement: the authority's verdict, or <see cref="Unsubmitted"/>.</summary>
        public CommandSettlement Verdict { get; } = new();

        /// <summary>Takes the reading once it has finished and submits it. Call on the pump.</summary>
        /// <returns>Whether the reading is still running.</returns>
        public bool Advance() {
            if (m_build is not { } build) { return false; }
            if (!build.TryTake(result: out var taken, error: out var error)) { return true; }
            m_build = null;

            var cut = (taken ?? new WorldReloadCut(ContentHash: string.Empty, Delta: null, Refusal: (error?.Message ?? "the source read was cancelled")));
            var result = (m_taken?.Invoke(arg: cut) ?? Submit(cut: cut));

            if (result.Settlement is { } settlement) {
                settlement.OnSettled(continuation: verdict => Verdict.Settle(result: verdict));
            } else {
                Unsubmitted = result;
                Verdict.Settle(result: result);
            }
            return false;
        }
        /// <summary>Advances the reload and answers whether it is still unsettled: the predicate a text session holds
        /// its later lines on.</summary>
        /// <returns>Whether the verdict is still owed.</returns>
        public bool Holding() {
            _ = Advance();
            return !Verdict.IsSettled;
        }

        private CommandResult Submit(WorldReloadCut cut) {
            if (cut.Refusal is { } refusal) { return CommandResult.Error(output: $"[{m_verb}: {refusal}]"); }
            if (cut.Unchanged) { return new CommandResult(Output: $"[{m_verb}: the source is unchanged since the revision the authority holds ({cut.ContentHash})]"); }
            return m_door.m_link.SubmitRebuild(
                echoes: m_door.m_echoes,
                principal: m_principal,
                request: new WorldRebuildRequest(ContentHash: cut.ContentHash, Definition: null, Delta: cut.Delta, Force: false, Kind: WorldRebuildKind.Reload, Origin: m_origin),
                verb: m_verb
            );
        }
    }

    private readonly WorldDeferredVerbEchoes m_echoes = echoes;
    private readonly IServerLink m_link = link;

    /// <summary>Begins a reload. Call on the pump: the source revision captured here is the one the delta is cut
    /// from, and the authority merges that delta over whatever its session changed since.</summary>
    /// <param name="verb">The submitting verb, exactly as its response line spells it.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="origin">Where the source is read from, as the rebuild names it.</param>
    /// <param name="read">Reads the source as an edit on the thread pool, given the revision the authority holds; a
    /// source that still composes to that revision's pin answers no definition. It may check the next revision and
    /// answer a refusal.</param>
    /// <param name="taken">Runs on the pump when the reading is taken, before anything is submitted: answers the
    /// result the reload settles with in place of the door's own (a host's refusal, its own unchanged line), or
    /// <see langword="null"/> for the door to submit the delta. <see langword="null"/> for none.</param>
    /// <returns>The reload in flight.</returns>
    public Pending Begin(string verb, Principal principal, WorldRebuildOrigin origin, Func<WorldSourceRevision, CancellationToken, ValueTask<WorldSourceRead>> read,
        Func<WorldReloadCut, CommandResult?>? taken = null) {
        ArgumentNullException.ThrowIfNull(argument: read);

        var revision = server.SourceRevision;
        var pending = new Pending(cut: token => CutAsync(read: read, revision: revision, token: token), door: this, origin: origin, principal: principal, taken: taken, verb: verb);

        m_reading.Add(item: pending);
        return pending;
    }
    /// <summary>Submits every reload whose reading has finished. Call on the pump.</summary>
    public void Poll() {
        if (m_reading.Count != 0) { _ = m_reading.RemoveAll(match: static pending => !pending.Advance()); }
    }

    // On the thread pool: reads the source and cuts its delta from the captured revision. The delta is cut between two
    // revisions of the source, never against the live document.
    private static async Task<WorldReloadCut> CutAsync(WorldSourceRevision revision, Func<WorldSourceRevision, CancellationToken, ValueTask<WorldSourceRead>> read, CancellationToken token) {
        var source = await read(arg1: revision, arg2: token).ConfigureAwait(continueOnCapturedContext: false);

        if (source.Refusal is { } refusal) { return new WorldReloadCut(ContentHash: string.Empty, Delta: null, Refusal: refusal); }
        if (source.Definition is not { } next) { return new WorldReloadCut(ContentHash: source.ContentHash, Delta: null, Refusal: null); }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try {
            return new WorldReloadCut(ContentHash: source.ContentHash, Delta: WorldReloadDelta.Between(previous: revision.Definition, next: next), Refusal: null) {
                DeltaMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalMilliseconds,
            };
        } catch (InvalidDataException exception) {
            return new WorldReloadCut(ContentHash: source.ContentHash, Delta: null, Refusal: exception.Message);
        }
    }
}
