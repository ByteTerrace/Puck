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
/// composition its reader read, taken before any draw, whatever the source.
/// <para>A door runs one reload at a time. A reload begun while another is still reading or awaiting its verdict waits
/// for that verdict, then captures the revision the authority holds and reads the source, so its delta is always cut
/// from the last revision the authority accepted and the source it reads is the one saved last. Two reloads cut from
/// one revision would race: once the earlier installs, the later one, holding the newer source, is refused as cut from
/// a stale base, and the newest save never applies. The queue is the door's own (<see cref="Begin"/> links each reload
/// behind the one begun before it), so no caller can start a reading around it.</para></remarks>
/// <param name="server">The authority being reloaded.</param>
/// <param name="link">The link the rebuild is submitted through.</param>
/// <param name="echoes">The pending-verb table the authority's verdict answers through.</param>
public sealed class WorldReloadDoor(WorldServer server, IServerLink link, WorldDeferredVerbEchoes echoes) {
    // Reloads whose reading the pump has not yet taken; pump only.
    private readonly List<Pending> m_reading = [];
    // The reload begun last, which the next one waits behind until its verdict settles; pump only.
    private Pending? m_last;

    /// <summary>One reload in flight: its turn behind the reload begun before it, its reading off the pump, then the
    /// authority's verdict.</summary>
    public sealed class Pending {
        private readonly WorldReloadDoor m_door;
        private readonly WorldRebuildOrigin m_origin;
        private readonly Principal m_principal;
        private readonly Func<WorldReloadCut, CommandResult?>? m_taken;
        private readonly string m_verb;
        private readonly Func<WorldSourceRevision, CancellationToken, Task<WorldReloadCut>> m_cut;
        private readonly TaskCompletionSource m_read = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<string?>? m_explain;
        // The reload this one waits behind until its verdict settles, or null once its turn has come.
        private Pending? m_ahead;
        private BackgroundBuild<WorldReloadCut>? m_build;
        private bool m_submitted;
        // A refusal awaiting its explanation off the pump, and that explanation; the refusal is written wherever the
        // authority's verdict settles, and settled on the pump once the explanation is in.
        private Explanation? m_explaining;

        private sealed record Explanation(CommandResult Refusal, bool Submitted, Task<string?> Explaining);

        internal Pending(WorldReloadDoor door, string verb, Principal principal, WorldRebuildOrigin origin, Func<WorldReloadCut, CommandResult?>? taken,
            Func<WorldSourceRevision, CancellationToken, Task<WorldReloadCut>> cut, Pending? ahead, Func<string?>? explain) {
            m_door = door;
            m_origin = origin;
            m_principal = principal;
            m_taken = taken;
            m_verb = verb;
            m_cut = cut;
            m_ahead = ahead;
            m_explain = explain;
            _ = TryStartReading();
        }

        /// <summary>Gets the reading's completion, successful or not: a pump that runs only when posted to posts
        /// <see cref="WorldReloadDoor.Poll"/> from it. A reload queued behind another starts reading only on a pump
        /// call made after that one settled.</summary>
        public Task Reading => m_read.Task;
        /// <summary>Gets whether this reload still waits for the verdict of the reload begun before it on the same door
        /// before it captures a revision and reads its source.</summary>
        public bool Queued => (m_ahead is not null);
        /// <summary>Gets the answer reached without submitting anything (a refusal, an unchanged source), which no
        /// authority verdict reports; <see langword="null"/> before the pump takes the reading and for a submitted
        /// reload.</summary>
        public CommandResult? Unsubmitted { get; private set; }
        /// <summary>Gets the explanation a refusal's verdict carries after its own text (a restart the running build
        /// owes), or <see langword="null"/>. A session that prints the authority's own refusal line, not the verdict,
        /// prints this beside it.</summary>
        public string? Note { get; private set; }
        /// <summary>Gets the reload's settlement: the authority's verdict, or <see cref="Unsubmitted"/>, each followed by
        /// <see cref="Note"/> when a refusal carries one.</summary>
        public CommandSettlement Verdict { get; } = new();

        // On the pump: once the reload ahead has settled, captures the revision the authority holds now and starts the
        // reading against it. Answers whether the reading has started.
        private bool TryStartReading() {
            if (m_build is not null) { return true; }
            if (m_ahead is { Verdict.IsSettled: false }) { return false; }
            m_ahead = null;

            var revision = m_door.m_server.SourceRevision;
            var build = new BackgroundBuild<WorldReloadCut>();

            build.Start(build: token => m_cut(arg1: revision, arg2: token));
            m_build = build;
            _ = build.Completion!.ContinueWith(continuationAction: _ => m_read.TrySetResult(), scheduler: TaskScheduler.Default);
            return true;
        }

        /// <summary>Starts the reading once the reload ahead has settled, takes it once it has finished and submits it,
        /// and settles a refusal once its explanation is in. Call on the pump.</summary>
        /// <returns>Whether the verdict is still owed.</returns>
        public bool Advance() {
            if (!m_submitted) {
                if (!TryStartReading()) { return true; }

                var build = m_build!;

                if (!build.TryTake(result: out var taken, error: out var error)) { return true; }
                m_submitted = true;
                m_build = null;

                var cut = (taken ?? new WorldReloadCut(ContentHash: string.Empty, Delta: null, Refusal: (error?.Message ?? "the source read was cancelled")));
                var result = (m_taken?.Invoke(arg: cut) ?? Submit(cut: cut));

                if (result.Settlement is { } settlement) {
                    settlement.OnSettled(continuation: verdict => Conclude(verdict: verdict, submitted: true));
                } else {
                    Conclude(verdict: result, submitted: false);
                }
            }
            if ((Volatile.Read(location: ref m_explaining) is { Explaining.IsCompleted: true } explained)) {
                Volatile.Write(location: ref m_explaining, value: null);
                Settle(verdict: explained.Refusal, submitted: explained.Submitted, note: (explained.Explaining.IsCompletedSuccessfully ? explained.Explaining.Result : null));
            }
            return !Verdict.IsSettled;
        }
        /// <summary>Advances the reload and answers whether it is still unsettled: the predicate a text session holds
        /// its later lines on.</summary>
        /// <returns>Whether the verdict is still owed.</returns>
        public bool Holding() => Advance();

        // Settles a verdict, or for a refusal the host can explain, asks for the explanation on the thread pool and leaves
        // the settlement to the pump call that finds it in. Runs wherever the authority's verdict settles.
        private void Conclude(CommandResult verdict, bool submitted) {
            if (verdict.IsError && (m_explain is { } explain)) {
                Volatile.Write(location: ref m_explaining, value: new Explanation(Explaining: Task.Run(function: explain), Refusal: verdict, Submitted: submitted));
                return;
            }
            Settle(verdict: verdict, note: null, submitted: submitted);
        }
        private void Settle(CommandResult verdict, bool submitted, string? note) {
            var settled = ((note is null) ? verdict : (verdict with { Output = $"{verdict.Output}\n[{m_verb}: {note}]" }));

            Note = note;
            if (!submitted) { Unsubmitted = settled; }
            Verdict.Settle(result: settled);
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
    private readonly WorldServer m_server = server;

    /// <summary>Begins a reload. Call on the pump. The revision the delta is cut from is captured when the reading
    /// starts: here when no other reload of this door is in flight, otherwise on the pump call after the reload begun
    /// before it settles. The authority merges that delta over whatever its session changed since.</summary>
    /// <param name="verb">The submitting verb, exactly as its response line spells it.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="origin">Where the source is read from, as the rebuild names it.</param>
    /// <param name="read">Reads the source as an edit on the thread pool, given the revision the authority holds; a
    /// source that still composes to that revision's pin answers no definition. It may check the next revision and
    /// answer a refusal.</param>
    /// <param name="taken">Runs on the pump when the reading is taken, before anything is submitted: answers the
    /// result the reload settles with in place of the door's own (a host's refusal, its own unchanged line), or
    /// <see langword="null"/> for the door to submit the delta. <see langword="null"/> for none.</param>
    /// <param name="explain">Runs on the thread pool when the reload is refused, by its reading or by the authority:
    /// answers a sentence the refusal carries after its own text (a restart the running build owes before the source can
    /// apply), or <see langword="null"/> for none. <see langword="null"/> to explain nothing.</param>
    /// <returns>The reload in flight.</returns>
    public Pending Begin(string verb, Principal principal, WorldRebuildOrigin origin, Func<WorldSourceRevision, CancellationToken, ValueTask<WorldSourceRead>> read,
        Func<WorldReloadCut, CommandResult?>? taken = null, Func<string?>? explain = null) {
        ArgumentNullException.ThrowIfNull(argument: read);

        var ahead = ((m_last is { Verdict.IsSettled: false } last) ? last : null);
        var pending = new Pending(ahead: ahead, cut: (revision, token) => CutAsync(read: read, revision: revision, token: token), door: this, explain: explain, origin: origin,
            principal: principal, taken: taken, verb: verb);

        m_last = pending;
        m_reading.Add(item: pending);
        return pending;
    }
    /// <summary>Starts every queued reload whose turn has come and submits every reload whose reading has finished.
    /// Call on the pump.</summary>
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
