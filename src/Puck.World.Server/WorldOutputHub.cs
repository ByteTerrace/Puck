using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The server's multi-subscriber output publication point — two lanes, per the design's §1.6. The TYPED lane fans a
/// tick's output out to every attached <see cref="IClientSink"/> SYNCHRONOUSLY on the tick thread: each subscriber
/// receives the borrowed <see cref="WorldSnapshot"/> (its <see cref="WorldSnapshot.Entries"/> memory wraps a reused
/// server-owned array — see <see cref="WorldServer"/>'s own remarks) and must fully consume or copy it before its
/// <see cref="IClientSink.DeliverSnapshot"/> call returns, because the next tick's snapshot overwrites the same
/// backing array. <see cref="WorldServer.EmitSnapshot"/> only returns once every typed subscriber has done exactly
/// that. The ENCODED lane is a SCAFFOLD ONLY — <see cref="SubscribeEncoded"/> exists so a future socket connection
/// could register without a second wiring pass, but nothing is produced or delivered on it: the P7 TCP transport
/// (<see cref="Server.WorldTcpHost"/>) uses its own strictly request-then-response wire instead
/// (<see cref="Server.WorldTcpWireFormat"/>) and never subscribes here, so this lane and
/// <see cref="SubscribeEncoded"/> remain unused.
/// </summary>
/// <remarks>Replaces the single overwriting <c>AttachSink</c>/<c>m_sink</c> field this server used to carry — play-
/// and-host (a local sink plus N future connections, plus the tape) is first-class here: every <see cref="Subscribe"/>
/// call ADDS a subscriber, it never displaces one already attached.</remarks>
public sealed class WorldOutputHub {
    private readonly List<IClientSink> m_typed = new();
    // SCAFFOLD list only — see the class remarks. Nothing ever iterates or delivers to this; the P7 TCP transport
    // does not subscribe here.
    private readonly List<object> m_encoded = new();

    /// <summary>Gets a value indicating whether at least one typed-lane subscriber is attached — lets a caller skip building a snapshot/answer
    /// nobody would receive (mirrors the old <c>m_sink is null</c> short-circuit).</summary>
    public bool HasTypedSubscribers => (m_typed.Count > 0);

    /// <summary>Adds a typed-lane subscriber. Delivered every subsequent tick's output SYNCHRONOUSLY, on the tick
    /// thread, until the process ends — there is no unsubscribe today (loopback never detaches its one local sink).</summary>
    /// <param name="sink">The subscriber to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Subscribe(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        m_typed.Add(item: sink);
    }

    /// <summary>Registers an encoded-lane subscriber. SCAFFOLD — no byte encoding happens (see the class remarks):
    /// the P7 TCP transport does not use this seam, and nothing else calls this method today.</summary>
    /// <param name="subscriber">The encoded-lane subscriber to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subscriber"/> is <see langword="null"/>.</exception>
    public void SubscribeEncoded(object subscriber) {
        ArgumentNullException.ThrowIfNull(argument: subscriber);

        m_encoded.Add(item: subscriber);
    }

    /// <summary>Fans a tick's snapshot out to every typed subscriber, synchronously, before returning.</summary>
    /// <param name="snapshot">The tick snapshot.</param>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        foreach (var sink in m_typed) {
            sink.DeliverSnapshot(snapshot: in snapshot);
        }
    }

    /// <summary>Fans a composed query answer out to every typed subscriber.</summary>
    /// <param name="answer">The answer.</param>
    public void DeliverAnswer(in QueryAnswer answer) {
        foreach (var sink in m_typed) {
            sink.DeliverAnswer(answer: in answer);
        }
    }

    /// <summary>Fans the live world definition out to every typed subscriber (once per step with at least one applied
    /// edit, or a definition swap).</summary>
    /// <param name="definition">The definition now live on the server.</param>
    public void DeliverDefinition(WorldDefinition definition) {
        foreach (var sink in m_typed) {
            sink.DeliverDefinition(definition: definition);
        }
    }

    /// <summary>Fans an accepted live window-composition override out to every typed subscriber.</summary>
    /// <param name="composition">The composition override.</param>
    public void DeliverComposition(WorldComposition composition) {
        foreach (var sink in m_typed) {
            sink.DeliverComposition(composition: composition);
        }
    }

    /// <summary>Fans an accepted live session lever out to every typed subscriber.</summary>
    /// <param name="lever">The accepted lever write.</param>
    public void DeliverSessionLever(WorldSessionLever lever) {
        foreach (var sink in m_typed) {
            sink.DeliverSessionLever(lever: lever);
        }
    }
}
