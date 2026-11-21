using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Presentation;

/// <summary>One open interval of a viewer's right to read a stream or route. It exposes no live answer: only its
/// owning <see cref="DeliverySpanSource"/> ends it, and only a <see cref="DeliveryLatch"/> asks whether it was still
/// open when a frame latched.</summary>
public sealed class DeliverySpan {
    private static readonly Lock Gate = new();
    private static long NextEpoch;

    private long m_endedAt;

    private DeliverySpan(long endedAt) => m_endedAt = endedAt;

    /// <summary>Gets a span that has already ended, which no <see cref="DeliveryLatch"/> accepts.</summary>
    public static DeliverySpan Ended { get; } = new(endedAt: 0L);

    internal static DeliverySpan MintOpen() => new(endedAt: long.MaxValue);
    internal static long CaptureEpoch() {
        lock (Gate) {
            return ++NextEpoch;
        }
    }

    internal void End() {
        lock (Gate) {
            if (m_endedAt == long.MaxValue) {
                Volatile.Write(location: ref m_endedAt, value: ++NextEpoch);
            }
        }
    }
    internal bool IsCurrentAt(long epoch) => (
        (epoch > 0L) &&
        (Volatile.Read(location: ref m_endedAt) > epoch)
    );
}

/// <summary>The one writer of a reader's or route's current <see cref="DeliverySpan"/>: advancing or closing ends the
/// span that was open and mints its replacement or leaves <see cref="DeliverySpan.Ended"/>.</summary>
public sealed class DeliverySpanSource {
    private readonly Lock m_gate = new();
    private DeliverySpan m_span;

    /// <summary>Initializes a new instance of the <see cref="DeliverySpanSource"/> class.</summary>
    /// <param name="open">Whether the initial span starts open; <see langword="false"/> starts at
    /// <see cref="DeliverySpan.Ended"/>.</param>
    public DeliverySpanSource(bool open = true) =>
        m_span = (open ? DeliverySpan.MintOpen() : DeliverySpan.Ended);

    /// <summary>Gets the current span.</summary>
    public DeliverySpan Span {
        get {
            lock (m_gate) {
                return m_span;
            }
        }
    }

    /// <summary>Ends the current span and mints the next open span.</summary>
    /// <returns>The newly minted open span.</returns>
    public DeliverySpan Advance() {
        lock (m_gate) {
            m_span.End();
            m_span = DeliverySpan.MintOpen();

            return m_span;
        }
    }
    /// <summary>Ends the current span and leaves <see cref="DeliverySpan.Ended"/> until <see cref="Advance"/> opens a
    /// new span.</summary>
    public void Close() {
        lock (m_gate) {
            m_span.End();
            m_span = DeliverySpan.Ended;
        }
    }
}

/// <summary>A frame's immutable point-in-time snapshot of span currency: every span ended before <see cref="Capture"/>
/// answers ended, and any cut accepted after <see cref="Capture"/> takes effect at the next latch. The default value is
/// unlatched and refuses every read.</summary>
public readonly struct DeliveryLatch : IEquatable<DeliveryLatch> {
    private readonly long m_epoch;

    private DeliveryLatch(long epoch) => m_epoch = epoch;

    /// <summary>Gets whether this latch was captured (<see cref="Capture"/>); <see langword="false"/> for
    /// <see langword="default"/>.</summary>
    public bool IsLatched => (m_epoch > 0L);

    /// <summary>Captures a new latch ordered after every span ended so far and ahead of every span ended later.</summary>
    /// <returns>The captured latch.</returns>
    public static DeliveryLatch Capture() => new(epoch: DeliverySpan.CaptureEpoch());

    /// <summary>Returns whether <paramref name="span"/> was open when this latch was captured.</summary>
    /// <param name="span">The span to check.</param>
    /// <returns><see langword="true"/> when this latch was captured while <paramref name="span"/> was open.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> is <see langword="null"/>.</exception>
    public bool IsCurrent(DeliverySpan span) {
        ArgumentNullException.ThrowIfNull(argument: span);

        return span.IsCurrentAt(epoch: m_epoch);
    }
    /// <inheritdoc/>
    public bool Equals(DeliveryLatch other) => (m_epoch == other.m_epoch);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DeliveryLatch other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => m_epoch.GetHashCode();
    /// <summary>Returns whether two latches name the same capture point.</summary>
    public static bool operator ==(DeliveryLatch left, DeliveryLatch right) => left.Equals(other: right);
    /// <summary>Returns whether two latches name different capture points.</summary>
    public static bool operator !=(DeliveryLatch left, DeliveryLatch right) => !left.Equals(other: right);
}

/// <summary>The engine sources permitted to mint a public <see cref="DeliveryLabel"/>, listed in one place for review:
/// stand-ins, CPU uploads, device or capture feeds and fills, and an unfiltered host view or internal storage.</summary>
public enum DeliveryPublicSource : byte {
    /// <summary>A runtime or node 1x1 transparent-black stand-in image.</summary>
    StandIn = 1,
    /// <summary>An uploaded CPU source or host buffer region.</summary>
    Upload = 2,
    /// <summary>A device feed, camera, desktop capture, converted device copy or capture fill.</summary>
    DeviceOrCapture = 3,
    /// <summary>An unfiltered host scene or a node's own storage.</summary>
    Host = 4,
}

/// <summary>The provenance and capture-taint classification of an image or buffer. <see langword="default"/> is
/// unclassified and is refused by every bind and latched read.</summary>
public readonly struct DeliveryLabel : IEquatable<DeliveryLabel> {
    private const byte KindUnclassified = 0;
    private const byte KindPublic = 1;
    private const byte KindSpans = 2;

    private readonly byte m_kind;
    private readonly bool m_external;
    private readonly DeliverySpan? m_span0;
    private readonly DeliverySpan? m_span1;
    private readonly DeliverySpan[]? m_more;

    private DeliveryLabel(byte kind, bool external, DeliverySpan? span0, DeliverySpan? span1, DeliverySpan[]? more) {
        m_kind = kind;
        m_external = external;
        m_span0 = span0;
        m_span1 = span1;
        m_more = more;
    }

    /// <summary>Gets the unclassified label (<see langword="default"/>), which every bind and latched read refuses.</summary>
    public static DeliveryLabel Unclassified => default;

    /// <summary>Gets whether the label was explicitly classified as public or spanned.</summary>
    public bool IsClassified => (m_kind != KindUnclassified);
    /// <summary>Gets whether the labelled content holds external pixels the capture gate did not fill.</summary>
    public bool External => (IsClassified && m_external);
    /// <summary>Gets the number of spans this label requires to remain open; zero for public or unclassified labels.</summary>
    public int SpanCount => ((m_kind != KindSpans)
        ? 0
        : ((m_span1 is null)
            ? 1
            : (2 + (m_more?.Length ?? 0))));

    /// <summary>Mints a public label for one of the permitted <see cref="DeliveryPublicSource"/> kinds.</summary>
    /// <param name="source">Which public source produced the content.</param>
    /// <param name="external">Whether the content holds external pixels the capture gate did not fill.</param>
    /// <returns>The classified public label.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a defined
    /// <see cref="DeliveryPublicSource"/>.</exception>
    public static DeliveryLabel ForSource(DeliveryPublicSource source, bool external = false) {
        if (source is < DeliveryPublicSource.StandIn or > DeliveryPublicSource.Host) {
            throw new ArgumentOutOfRangeException(paramName: nameof(source), actualValue: source, message: "Unknown public delivery source.");
        }

        return new DeliveryLabel(
            external: external,
            kind: KindPublic,
            more: null,
            span0: null,
            span1: null
        );
    }
    /// <summary>Mints a label gated on one <see cref="DeliverySpan"/>.</summary>
    /// <param name="span">The span the content was produced under.</param>
    /// <param name="external">Whether the content holds external pixels the capture gate did not fill.</param>
    /// <returns>The classified span label.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="span"/> is <see langword="null"/>.</exception>
    public static DeliveryLabel Of(DeliverySpan span, bool external = false) {
        ArgumentNullException.ThrowIfNull(argument: span);

        return new DeliveryLabel(
            external: external,
            kind: KindSpans,
            more: null,
            span0: span,
            span1: null
        );
    }

    /// <summary>Returns this label with <see cref="External"/> set to <paramref name="external"/>; an unclassified
    /// label stays unclassified.</summary>
    /// <param name="external">Whether the content holds unfilled external pixels.</param>
    /// <returns>The updated label, or <see cref="Unclassified"/> when unclassified.</returns>
    public DeliveryLabel WithExternal(bool external) => (!IsClassified
        ? Unclassified
        : ((m_external == external)
            ? this
            : new DeliveryLabel(
                external: external,
                kind: m_kind,
                more: m_more,
                span0: m_span0,
                span1: m_span1
            )));
    /// <summary>Combines two labels: requires every span of both, carries <see cref="External"/> when either does, and
    /// stays <see cref="Unclassified"/> when either is unclassified.</summary>
    /// <param name="other">The other label.</param>
    /// <returns>The combined label.</returns>
    public DeliveryLabel Union(in DeliveryLabel other) {
        if (!IsClassified || !other.IsClassified) {
            return Unclassified;
        }

        var external = (m_external || other.m_external);

        if ((m_kind == KindPublic) && (other.m_kind == KindPublic)) {
            return new DeliveryLabel(
                external: external,
                kind: KindPublic,
                more: null,
                span0: null,
                span1: null
            );
        }
        if (m_kind == KindPublic) {
            return other.WithExternal(external: external);
        }
        if (other.m_kind == KindPublic) {
            return WithExternal(external: external);
        }
        if (ContainsAll(other: in other)) {
            return WithExternal(external: external);
        }
        if (other.ContainsAll(other: in this)) {
            return other.WithExternal(external: external);
        }
        if ((m_span1 is null) && (other.m_span1 is null)) {
            return new DeliveryLabel(
                external: external,
                kind: KindSpans,
                more: null,
                span0: m_span0,
                span1: other.m_span0
            );
        }

        var combined = new List<DeliverySpan>(capacity: (SpanCount + other.SpanCount));

        AppendUnique(into: combined);
        other.AppendUnique(into: combined);

        return FromDistinct(
            external: external,
            spans: combined
        );
    }
    /// <summary>Returns whether this label is classified and every span it carries was open when
    /// <paramref name="latch"/> was captured.</summary>
    /// <param name="latch">The frame's latch.</param>
    /// <returns><see langword="true"/> when the content may be read at <paramref name="latch"/>.</returns>
    public bool Allows(in DeliveryLatch latch) {
        if (!latch.IsLatched || !IsClassified) {
            return false;
        }
        if (m_kind == KindPublic) {
            return true;
        }
        if (!latch.IsCurrent(span: m_span0!)) {
            return false;
        }
        if ((m_span1 is not null) && !latch.IsCurrent(span: m_span1)) {
            return false;
        }
        if (m_more is { } more) {
            for (var index = 0; (index < more.Length); index++) {
                if (!latch.IsCurrent(span: more[index])) {
                    return false;
                }
            }
        }

        return true;
    }

    private bool ContainsSpan(DeliverySpan candidate) {
        if (m_kind != KindSpans) {
            return false;
        }
        if (ReferenceEquals(objA: m_span0, objB: candidate) || ReferenceEquals(objA: m_span1, objB: candidate)) {
            return true;
        }
        if (m_more is { } more) {
            for (var index = 0; (index < more.Length); index++) {
                if (ReferenceEquals(objA: more[index], objB: candidate)) {
                    return true;
                }
            }
        }

        return false;
    }
    private bool ContainsAll(in DeliveryLabel other) {
        if (other.m_kind != KindSpans) {
            return true;
        }
        if (!ContainsSpan(candidate: other.m_span0!)) {
            return false;
        }
        if ((other.m_span1 is not null) && !ContainsSpan(candidate: other.m_span1)) {
            return false;
        }
        if (other.m_more is { } more) {
            for (var index = 0; (index < more.Length); index++) {
                if (!ContainsSpan(candidate: more[index])) {
                    return false;
                }
            }
        }

        return true;
    }
    private void AppendUnique(List<DeliverySpan> into) {
        if (m_kind != KindSpans) {
            return;
        }

        AddSpan(into: into, span: m_span0!);

        if (m_span1 is not null) {
            AddSpan(into: into, span: m_span1);
        }
        if (m_more is { } more) {
            for (var index = 0; (index < more.Length); index++) {
                AddSpan(into: into, span: more[index]);
            }
        }
    }
    private static void AddSpan(List<DeliverySpan> into, DeliverySpan span) {
        for (var index = 0; (index < into.Count); index++) {
            if (ReferenceEquals(objA: into[index], objB: span)) {
                return;
            }
        }

        into.Add(item: span);
    }
    private static DeliveryLabel FromDistinct(List<DeliverySpan> spans, bool external) => (spans.Count switch {
        0 => new DeliveryLabel(external: external, kind: KindPublic, more: null, span0: null, span1: null),
        1 => new DeliveryLabel(external: external, kind: KindSpans, more: null, span0: spans[0], span1: null),
        2 => new DeliveryLabel(external: external, kind: KindSpans, more: null, span0: spans[0], span1: spans[1]),
        _ => new DeliveryLabel(external: external, kind: KindSpans, more: [.. spans.Skip(count: 2)], span0: spans[0], span1: spans[1]),
    });

    /// <inheritdoc/>
    public bool Equals(DeliveryLabel other) => (
        (m_kind == other.m_kind) &&
        (m_external == other.m_external) &&
        (SpanCount == other.SpanCount) &&
        ContainsAll(other: in other)
    );
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DeliveryLabel other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() {
        var spansHash = 0;

        if (m_kind == KindSpans) {
            spansHash ^= RuntimeHelpers.GetHashCode(o: m_span0!);

            if (m_span1 is not null) {
                spansHash ^= RuntimeHelpers.GetHashCode(o: m_span1);
            }
            if (m_more is { } more) {
                for (var index = 0; (index < more.Length); index++) {
                    spansHash ^= RuntimeHelpers.GetHashCode(o: more[index]);
                }
            }
        }

        return HashCode.Combine(m_kind, m_external, SpanCount, spansHash);
    }
    /// <summary>Returns whether two labels have the same classification, external flag and span set.</summary>
    public static bool operator ==(DeliveryLabel left, DeliveryLabel right) => left.Equals(other: right);
    /// <summary>Returns whether two labels differ in classification, external flag or span set.</summary>
    public static bool operator !=(DeliveryLabel left, DeliveryLabel right) => !left.Equals(other: right);
}

/// <summary>Pairs a content value with its <see cref="DeliveryLabel"/> so the value can be obtained only through a
/// latched read (<see cref="TryRead"/>). <see langword="default"/> is unclassified and yields no value.</summary>
/// <typeparam name="T">The carried content type.</typeparam>
public readonly struct DeliveryContent<T> {
    private readonly T m_value;
    private readonly DeliveryLabel m_label;

    /// <summary>Initializes a new instance of the <see cref="DeliveryContent{T}"/> struct. An unclassified label
    /// retains no value.</summary>
    /// <param name="value">The content value.</param>
    /// <param name="label">The content's classification and spans.</param>
    public DeliveryContent(T value, DeliveryLabel label) {
        if (label.IsClassified) {
            m_value = value;
            m_label = label;
        } else {
            m_value = default!;
            m_label = DeliveryLabel.Unclassified;
        }
    }

    /// <summary>Gets the content's label.</summary>
    public DeliveryLabel Label => m_label;
    /// <summary>Gets whether the content carries a classified label.</summary>
    public bool IsClassified => m_label.IsClassified;

    /// <summary>Reads the carried value when <see cref="Label"/> allows <paramref name="latch"/>.</summary>
    /// <param name="latch">The frame's latch.</param>
    /// <param name="value">The carried value when this returns <see langword="true"/>; otherwise
    /// <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when <see cref="Label"/> is classified and every span in it is current at
    /// <paramref name="latch"/>.</returns>
    public bool TryRead(in DeliveryLatch latch, [MaybeNullWhen(returnValue: false)] out T value) {
        if (m_label.Allows(latch: in latch)) {
            value = m_value;

            return true;
        }

        value = default;

        return false;
    }
}
