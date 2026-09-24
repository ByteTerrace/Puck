namespace Puck.State;

/// <summary>The one spelling of a name Puck generates rather than an author writes: its parts joined by a character no
/// author-written name may carry, so a generated name reads as machinery wherever it is printed and can never equal a
/// name an author wrote.
/// <para>A name that lives inside a document — a state row, a cell key, a rule, a rule group, a placement, a
/// prototype — joins its parts with <see cref="Joiner"/>: a stabilize group's member is <c>turn$east</c>, a test's
/// first verdict is <c>expect$1</c>, a pool's live-slot row is <c>$pool$pieces$live</c>. The character is the one
/// <see cref="IdentifierSpelling"/> already keeps to the front of a name, so a generated document name is never an
/// identifier and never reads bare; at the front it still opens a reserved word or channel (<c>$value</c>,
/// <c>$tick</c>, <c>$cell:</c>), which is why only a part the engine itself keeps under that prefix may open with it.
/// A name that becomes a file or a directory — a generated world, a started instance — joins its parts with
/// <see cref="FileJoiner"/> instead: <c>rulepush~push-block</c>. A shell expands <c>$</c> inside a path, and <c>~</c> is
/// inert in every shell, in a URL, and in a file name.</para>
/// <para>Minting goes through <see cref="Join"/> or <see cref="JoinFile"/>, which refuse a part carrying the joiner, so
/// the parts a name was joined from are always recoverable from it. Every door an author names something through
/// refuses both reserved characters by name (<see cref="TryValidateAuthored"/>, <see cref="TryValidateAuthoredFile"/>):
/// a document name an author writes carries neither <c>$</c> past its front nor <c>~</c> anywhere. A world document is
/// what the compiler writes, so it may hold a <c>$</c>-joined name, but never a <c>~</c> in a document name
/// (<see cref="TryValidateDocument"/>). That is what makes <see cref="ToFile"/> — the spelling of a document name
/// inside a file or directory name — injective. The document's own storage types (<see cref="CellName"/>,
/// <see cref="SafeName"/>) hold instance names too, so they admit both characters and the doors refuse.</para></summary>
public static class GeneratedName {
    /// <summary>The character that joins the parts of a generated name inside a document.</summary>
    public const char Joiner = '$';
    /// <summary>The character that joins the parts of a generated name that becomes a file or a directory.</summary>
    public const char FileJoiner = '~';

    private const string FileJoinerText = "~";
    private const string JoinerText = "$";

    /// <summary>Returns whether <paramref name="name"/> is in the generated document form: it carries
    /// <see cref="Joiner"/> anywhere past its first character.</summary>
    /// <param name="name">The name. A key spelling (<c>$cell:row:$value</c>) is not a name and is never asked.</param>
    /// <returns><see langword="true"/> when the name is spelled the way Puck spells the names it generates.</returns>
    public static bool IsGenerated(ReadOnlySpan<char> name) => (
        (name.Length > 1) &&
        name[1..].Contains(value: Joiner)
    );
    /// <summary>Returns whether <paramref name="name"/> is in the generated file-backed form: it carries
    /// <see cref="FileJoiner"/>.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name is spelled the way Puck spells the world and instance names it
    /// generates.</returns>
    public static bool IsGeneratedFile(ReadOnlySpan<char> name) => name.Contains(value: FileJoiner);
    /// <summary>Returns whether <paramref name="name"/> carries either reserved spelling: <see cref="Joiner"/> past its
    /// first character, or <see cref="FileJoiner"/> anywhere.</summary>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when no author door admits the name.</returns>
    public static bool IsReserved(ReadOnlySpan<char> name) => (
        IsGenerated(name: name) ||
        IsGeneratedFile(name: name)
    );
    /// <summary>Returns the spelling a document name takes inside a file or directory name: every
    /// <see cref="Joiner"/> becomes <see cref="FileJoiner"/> (<c>link$west</c> spells <c>link~west</c>), so the
    /// directory a document name reaches never carries a character a shell expands.
    /// <para>Total and injective over every name an admitted document holds: such a name carries no
    /// <see cref="FileJoiner"/> (<see cref="TryValidateDocument"/>), so the map relabels one character by another the
    /// name never held. A name carrying <see cref="FileJoiner"/> is outside that set — two such names could spell
    /// alike — and is refused rather than spelled.</para></summary>
    /// <param name="name">A document name; non-empty and free of <see cref="FileJoiner"/>.</param>
    /// <returns>The file-backed spelling.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or carries <see cref="FileJoiner"/>.</exception>
    public static string ToFile(string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        if (!TryValidateDocument(
            name: name,
            reason: out var reason
        )) {
            throw new ArgumentException(
                message: reason,
                paramName: nameof(name)
            );
        }

        return name.Replace(
            newChar: FileJoiner,
            oldChar: Joiner
        );
    }
    /// <summary>Joins <paramref name="parts"/> into a generated document name.</summary>
    /// <param name="parts">Two or more parts, none empty and none carrying <see cref="Joiner"/> — except the first,
    /// which may open with it when the engine keeps the name under the reserved prefix (<c>$pool</c>).</param>
    /// <returns>The generated name.</returns>
    /// <exception cref="ArgumentException">Fewer than two parts, or a part that is empty or carries the joiner.</exception>
    public static string Join(params ReadOnlySpan<string> parts) => JoinCore(
        joiner: Joiner,
        parts: parts
    );
    /// <summary>Appends one more part to <paramref name="name"/>: a scope's name, generated or authored, followed by
    /// the joiner and the part — the way a nested scope names what it holds (<c>outer$inner$step</c>).</summary>
    /// <param name="name">The enclosing name; non-empty.</param>
    /// <param name="part">The part; non-empty and free of <see cref="Joiner"/>.</param>
    /// <returns>The generated name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or <paramref name="part"/> is empty or
    /// carries the joiner.</exception>
    public static string Append(string name, string part) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentException.ThrowIfNullOrEmpty(argument: part);

        if (part.Contains(value: Joiner)) {
            throw new ArgumentException(
                message: $"the part '{part}' carries the joiner '{Joiner}'",
                paramName: nameof(part)
            );
        }

        return string.Concat(
            str0: name,
            str1: JoinerText,
            str2: part
        );
    }
    /// <summary>Qualifies <paramref name="name"/> under <paramref name="head"/>: the head, the joiner, and the name — the
    /// way a module instance's alias namespaces every name the instance declares (<c>use counter as left</c> declares
    /// <c>left$score</c>, and a nested instance <c>box$child$score</c>).</summary>
    /// <param name="head">The qualifying part; non-empty and free of both joiners, except that it may open with
    /// <see cref="Joiner"/> when the engine keeps the name under the reserved prefix (<c>$pool</c>).</param>
    /// <param name="name">The name it qualifies; non-empty, and itself possibly generated.</param>
    /// <returns>The generated name.</returns>
    /// <exception cref="ArgumentException"><paramref name="head"/> is empty or carries a joiner past its front, or
    /// <paramref name="name"/> is empty.</exception>
    public static string Qualify(string head, string name) {
        ArgumentException.ThrowIfNullOrEmpty(argument: head);
        ArgumentException.ThrowIfNullOrEmpty(argument: name);

        if (
            head.AsSpan(start: ((head[0] == IdentifierSpelling.Sigil) ? 1 : 0)).Contains(value: Joiner) ||
            head.Contains(value: FileJoiner)
        ) {
            throw new ArgumentException(
                message: $"the head '{head}' carries a joiner",
                paramName: nameof(head)
            );
        }

        return string.Concat(
            str0: head,
            str1: JoinerText,
            str2: name
        );
    }
    /// <summary>Appends one more part to a file-backed <paramref name="name"/>: a generated world's own name, followed
    /// by <see cref="FileJoiner"/> and the part — the way a generated composition names each world beside the one it
    /// boots (<c>rulepush~visit~hedges</c>).</summary>
    /// <param name="name">The enclosing name; non-empty.</param>
    /// <param name="part">The part; non-empty and free of <see cref="FileJoiner"/>.</param>
    /// <returns>The generated name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or <paramref name="part"/> is empty or
    /// carries the joiner.</exception>
    public static string AppendFile(string name, string part) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentException.ThrowIfNullOrEmpty(argument: part);

        if (part.Contains(value: FileJoiner)) {
            throw new ArgumentException(
                message: $"the part '{part}' carries the joiner '{FileJoiner}'",
                paramName: nameof(part)
            );
        }

        return string.Concat(
            str0: name,
            str1: FileJoinerText,
            str2: part
        );
    }
    /// <summary>Joins <paramref name="parts"/> into a generated name that becomes a file or a directory.</summary>
    /// <param name="parts">Two or more parts, none empty and none carrying <see cref="FileJoiner"/>.</param>
    /// <returns>The generated name.</returns>
    /// <exception cref="ArgumentException">Fewer than two parts, or a part that is empty or carries the joiner.</exception>
    public static string JoinFile(params ReadOnlySpan<string> parts) => JoinCore(
        joiner: FileJoiner,
        parts: parts
    );
    /// <summary>Returns the rest of <paramref name="name"/> after <paramref name="head"/> and the joiner, when the
    /// name was joined under that head.</summary>
    /// <param name="name">The candidate generated name.</param>
    /// <param name="head">The first part it would have been joined under.</param>
    /// <param name="rest">What follows the head's joiner, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is <paramref name="head"/>, the joiner, and a
    /// non-empty rest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="head"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryStripHead(string name, string head, out string rest) {
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: head);
        rest = string.Empty;

        if (
            (name.Length <= (head.Length + 1)) ||
            (name[head.Length] != Joiner) ||
            !name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: head
        )
        ) {
            return false;
        }

        rest = name[(head.Length + 1)..];

        return true;
    }
    /// <summary>Validates a name an author wrote into a document namespace: it may carry neither reserved spelling —
    /// no <see cref="Joiner"/> past its front and no <see cref="FileJoiner"/> anywhere. A <see cref="Joiner"/> at the
    /// front is each namespace's own reserved prefix and is refused there, not here.</summary>
    /// <param name="name">The author's name.</param>
    /// <param name="reason">Why it was refused, naming the reservation, or empty on success.</param>
    /// <returns><see langword="true"/> when an author may write the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static bool TryValidateAuthored(string name, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (IsGenerated(name: name)) {
            reason = $"'{name}' carries '{Joiner}' inside it, the spelling Puck reserves for the names it generates (a stabilize member such as 'turn{Joiner}east', a test verdict such as 'expect{Joiner}1'); write a name without '{Joiner}'";

            return false;
        }

        return TryValidateDocument(
            name: name,
            reason: out reason
        );
    }
    /// <summary>Validates a name an admitted world document holds in a document namespace — a row, a key, a rule, a
    /// placement, a destination: it may not carry <see cref="FileJoiner"/>. The compiler writes <see cref="Joiner"/>
    /// into the names it generates, so a document may hold that spelling; it never writes <see cref="FileJoiner"/>
    /// into one, so a document name carrying it came from an author and is refused.</summary>
    /// <param name="name">The document name.</param>
    /// <param name="reason">Why it was refused, naming the reservation, or empty on success.</param>
    /// <returns><see langword="true"/> when a document may hold the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static bool TryValidateDocument(string name, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (!IsGeneratedFile(name: name)) {
            reason = string.Empty;

            return true;
        }

        reason = $"'{name}' carries '{FileJoiner}', the character Puck reserves for the file and directory names it generates (a test world such as 'rulepush{FileJoiner}push-block', the instance a destination such as 'link{Joiner}west' starts as 'link{FileJoiner}west'); write a name without '{FileJoiner}'";

        return false;
    }
    /// <summary>Validates a name an author wrote into a file-backed namespace — a world or an instance: it may not be
    /// in the generated form.</summary>
    /// <param name="name">The author's name.</param>
    /// <param name="reason">Why it was refused, naming the reservation, or empty on success.</param>
    /// <returns><see langword="true"/> when an author may write the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static bool TryValidateAuthoredFile(string name, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (!IsGeneratedFile(name: name)) {
            reason = string.Empty;

            return true;
        }

        reason = $"'{name}' carries '{FileJoiner}', the character Puck reserves for joining the world and instance names it generates (a test world such as 'rulepush{FileJoiner}push-block'); write a name without '{FileJoiner}'";

        return false;
    }

    private static string JoinCore(ReadOnlySpan<string> parts, char joiner) {
        if (parts.Length < 2) {
            throw new ArgumentException(
                message: $"a generated name joins at least two parts, and {parts.Length} were given",
                paramName: nameof(parts)
            );
        }

        var length = (parts.Length - 1);

        for (var index = 0; (index < parts.Length); index++) {
            var part = parts[index];

            if (string.IsNullOrEmpty(value: part)) {
                throw new ArgumentException(
                    message: $"part {index} of a generated name is empty",
                    paramName: nameof(parts)
                );
            }

            var inner = (((index == 0) && (joiner == Joiner) && (part[0] == IdentifierSpelling.Sigil))
                ? part.AsSpan(start: 1)
                : part.AsSpan()
            );

            if (inner.Contains(value: joiner)) {
                throw new ArgumentException(
                    message: $"part {index} of a generated name, '{part}', carries the joiner '{joiner}'",
                    paramName: nameof(parts)
                );
            }

            length += part.Length;
        }

        return string.Create(
            length: length,
            state: (Parts: parts.ToArray(), Joiner: joiner),
            action: static (span, state) => {
                var offset = 0;

                for (var index = 0; (index < state.Parts.Length); index++) {
                    if (index > 0) {
                        span[offset++] = state.Joiner;
                    }

                    state.Parts[index].AsSpan().CopyTo(destination: span[offset..]);
                    offset += state.Parts[index].Length;
                }
            }
        );
    }
}
