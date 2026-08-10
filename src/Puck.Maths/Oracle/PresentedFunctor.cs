namespace Puck.Maths;

/// <summary>The source relation a morphism's images did not satisfy, carried as the relation itself rather than as a
/// fault.</summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <param name="RuleIndex">The index of the source rewrite rule the images broke, or <c>-1</c> where no rule did.</param>
/// <param name="Rule">That rule, so a caller reads the relation that blocked instead of looking it up.</param>
/// <param name="LeftKey">The left key of the ordered source basis pair whose product the images did not preserve, or
/// <c>-1</c> where no pair did.</param>
/// <param name="RightKey">The right key of that pair, or <c>-1</c>.</param>
/// <remarks>A successful call returns every field at <c>-1</c> with a default rule, which reads unambiguously as
/// "nothing blocked": a rule index is never negative and a basis key is never negative.</remarks>
public readonly record struct FunctorObstruction<TValue>(int RuleIndex, RewriteRule<TValue> Rule, long LeftKey, long RightKey);

/// <summary>
/// A morphism of presented algebras: one target element per source generator, admitted only after the source's own
/// relations were evaluated on those images. It adds no arithmetic — every statement it makes is a product and a
/// comparison in the target — so a substitution system, a change of coefficients and a knot state sum are one type.
/// </summary>
/// <typeparam name="TValue">The material's carrier.</typeparam>
/// <typeparam name="TOps">The material.</typeparam>
/// <remarks>
/// <para>
/// <b>An image is assigned to a generator symbol, and the map is the linear extension over the basis.</b> The image of
/// a normal form is the ordered product of its letters' images, and the image of an element is that product summed
/// against the element's own coefficients. A generator carrying a presentation charge — a quiver arrow's weight —
/// therefore rides through by linearity, since <see cref="PresentedAlgebra{TValue, TOps}.Generator"/> is that charge
/// times the basis element and <see cref="Map"/> is linear.
/// </para>
/// <para>
/// <b>What TryCreate proves.</b> Every word-matched source rule is evaluated on the images: the pattern's product must
/// equal the charged combination of its replacement terms' products, and an annihilation must reach the target's zero.
/// An empty-pattern rule is that same statement at the empty word, so a quiver's diagonal unit is checked as the
/// unitality condition without a case of its own. A <see cref="RuleKind.Reassociate"/> rule states no relation among
/// words — it charges bracket splices, which <see cref="Map"/> never sees, because an element is already a combination
/// of normal forms — so it constrains no image and is passed over.
/// </para>
/// <para>
/// <b>The scalars are one material, compared by value.</b> This overload has no scalar morphism to carry coefficients
/// between different materials, so source and target must carry equal material values. The distinction matters for a
/// runtime material such as a prime field: GF(3) and GF(5) have the same carrier and material type, but linear extension
/// in one cannot be interpreted in the other. Requiring equality makes <see cref="Map"/> preserve zero, one, addition,
/// scalar multiplication and product in the one shared material.
/// </para>
/// <para>
/// <b>Where the source has a finite basis the check is complete, and the rules alone are not.</b> A positive
/// <see cref="ChargedPresentation{TValue, TOps}.WindowDegree"/> annihilates every over-heavy product without any rule
/// saying so, so a second pass compares the images' product against the compiled cell at every ordered basis pair;
/// with unitality that is exactly the statement that the map is a homomorphism, both products being bilinear. The rule
/// pass still runs first, and first, because a failing relation is the readable diagnostic and it is the only check
/// available where no finite basis exists. There the rules are complete, provided no window is set — and a windowed
/// presentation with no finite basis is refused at construction rather than half-checked.
/// </para>
/// <para>
/// <b>A substitution's word is never an element.</b> The fixed point of a substitution grows exponentially, so a word
/// morphism — one whose every image is a single basis element at the material's one — carries its images as words and
/// <see cref="MapWord"/> substitutes letter by letter into a caller-sized buffer, allocating nothing and never forming
/// a key. Composing two substitutions is <see cref="MapWord"/> of one letter's image through the other, so no
/// composition operator is needed.
/// </para>
/// <para>
/// Not thread-safe, because <see cref="PresentedAlgebra{TValue, TOps}"/> is not: every product this type forms runs in
/// the target's own scratch.
/// </para>
/// </remarks>
public sealed class PresentedFunctor<TValue, TOps>
    where TOps : struct, IMaterialOps<TValue, TOps> {
    // A packed key is the mixed-radix sum over a radix of at least two — one more than the generator count — so its
    // scale doubles at worst per letter and no word past 63 letters has a key at all. That is the whole word buffer a
    // presentation with no finite basis can ever need.
    private const int PackedWordCapacity = 63;

    private readonly PresentedAlgebra<TValue, TOps>.Element[] m_basisImage;
    private readonly PresentedAlgebra<TValue, TOps>.Element[] m_images;
    private readonly int[] m_imageStart;
    private readonly int[] m_imageSymbols;
    private readonly TOps m_material;
    private TValue[] m_scale = [];

    private PresentedFunctor(
        PresentedAlgebra<TValue, TOps> source,
        PresentedAlgebra<TValue, TOps> target,
        PresentedAlgebra<TValue, TOps>.Element[] images
    ) {
        var presentation = source.Presentation;

        ImageCount = images.Length;
        Source = source;
        Target = target;
        m_images = images;
        m_material = target.Presentation.Material;

        BuildWordImages(target: target, images: images, start: out m_imageStart, symbols: out m_imageSymbols);

        IsWordMorphism = (0 != m_imageStart.Length);

        if (!presentation.HasCompiledNormalFormBasis) {
            m_basisImage = [];

            return;
        }

        // The basis images, built once: over a finite basis the map IS a table, so Map becomes a linear combination
        // and the compiled pass below costs one product per ordered pair rather than one word fold per pair.
        m_basisImage = new PresentedAlgebra<TValue, TOps>.Element[presentation.NormalFormCount];

        for (var key = 0; (key < m_basisImage.Length); ++key) {
            m_basisImage[key] = Fold(word: presentation.NormalFormWord(key: key));
        }
    }

    /// <summary>Gets the number of images, which is the source presentation's generator count.</summary>
    public int ImageCount { get; }
    /// <summary>Indicates whether every image is a single basis element at the material's one, so the morphism sends
    /// words to words and <see cref="MapWord"/> is available.</summary>
    /// <remarks>A summed or weighted image names no word, which is a shrunk guarantee rather than a refusal: such a
    /// morphism still maps elements through <see cref="Map"/>.</remarks>
    public bool IsWordMorphism { get; }
    /// <summary>Gets the algebra the morphism maps out of.</summary>
    public PresentedAlgebra<TValue, TOps> Source { get; }
    /// <summary>Gets the algebra the morphism maps into.</summary>
    public PresentedAlgebra<TValue, TOps> Target { get; }

    /// <summary>Creates a morphism, evaluating the source's own relations on the images before admitting one.</summary>
    /// <param name="source">The algebra to map out of.</param>
    /// <param name="target">The algebra to map into.</param>
    /// <param name="images">One image per source generator, in symbol order; each belongs to
    /// <paramref name="target"/>.</param>
    /// <param name="functor">On success, the morphism; otherwise <see langword="null"/>.</param>
    /// <param name="obstruction">On failure, the source relation the images did not satisfy.</param>
    /// <returns><see langword="true"/> when every source relation holds on the images; otherwise
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">An algebra is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source and target carry different material values, the image count is
    /// not the source generator count, an image belongs to another algebra, or the source carries a degree window with
    /// no finite basis.</exception>
    /// <exception cref="InvalidOperationException">The target has no finite basis and a product exceeded its
    /// normalization budget or its key scheme.</exception>
    /// <remarks>
    /// <para>
    /// A free source accepts every assignment, which is the universal property stated as an outcome rather than as a
    /// special case: it carries no word-matched rule, so the rule pass has nothing to refuse.
    /// </para>
    /// <para>
    /// The whole cost is paid here, once. The rule pass is one product per pattern letter and per replacement letter;
    /// the compiled pass is keys-squared products in the target, bounded by the source's own normal-form count and so
    /// by the 512 a finite basis holds. Nothing after this call multiplies anything it could have multiplied here:
    /// <see cref="Map"/> over a finite basis is a linear combination of images this call already folded.
    /// </para>
    /// </remarks>
    public static bool TryCreate(
        PresentedAlgebra<TValue, TOps> source,
        PresentedAlgebra<TValue, TOps> target,
        ReadOnlySpan<PresentedAlgebra<TValue, TOps>.Element> images,
        out PresentedFunctor<TValue, TOps>? functor,
        out FunctorObstruction<TValue> obstruction
    ) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: target);

        if (!EqualityComparer<TOps>.Default.Equals(x: source.Presentation.Material, y: target.Presentation.Material)) {
            throw new ArgumentException(
                message: "A functor with no scalar morphism maps coefficients only between equal material values.",
                paramName: nameof(target)
            );
        }

        var presentation = source.Presentation;

        if (images.Length != presentation.GeneratorCount) {
            throw new ArgumentException(message: "A morphism carries one image per source generator.", paramName: nameof(images));
        }

        // A degree window annihilates products no rewrite rule names. Where the basis is finite those annihilations are
        // cells and the compiled pass sees them; where it is not, they are infinitely many relations with no rule and
        // no key to enumerate them by, so no morphism out of such a presentation can be verified at all.
        if (!presentation.HasCompiledNormalFormBasis && (0 != presentation.WindowDegree)) {
            throw new ArgumentException(
                message: "A degree window states annihilations no rewrite rule carries, and without a finite basis they cannot be enumerated, so no morphism out of this presentation can be verified.",
                paramName: nameof(source)
            );
        }

        foreach (var image in images) { target.RequireOwned(value: image, paramName: nameof(images)); }

        var candidate = new PresentedFunctor<TValue, TOps>(source: source, target: target, images: images.ToArray());
        var rules = presentation.Rules;

        functor = null;
        obstruction = new(RuleIndex: -1, Rule: default, LeftKey: -1L, RightKey: -1L);

        for (var index = 0; (index < rules.Length); ++index) {
            if (candidate.Satisfies(rule: rules[index])) { continue; }

            obstruction = new(RuleIndex: index, Rule: rules[index], LeftKey: -1L, RightKey: -1L);

            return false;
        }

        if (!candidate.PreservesCompiledProduct(leftKey: out var leftKey, rightKey: out var rightKey)) {
            obstruction = new(RuleIndex: -1, Rule: default, LeftKey: leftKey, RightKey: rightKey);

            return false;
        }

        functor = candidate;

        return true;
    }

    /// <summary>Returns the image of one source generator.</summary>
    /// <param name="symbol">The source generator's symbol.</param>
    /// <returns>The image, an element of <see cref="Target"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The symbol names no source generator.</exception>
    public PresentedAlgebra<TValue, TOps>.Element Image(int symbol) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: symbol);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: symbol, other: ImageCount);

        return m_images[symbol];
    }

    /// <summary>Maps an element of <see cref="Source"/> to an element of <see cref="Target"/>.</summary>
    /// <param name="value">The element to map; it belongs to <see cref="Source"/>.</param>
    /// <returns>The image, an element of <see cref="Target"/>.</returns>
    /// <exception cref="ArgumentException">The element belongs to another algebra.</exception>
    /// <exception cref="InvalidOperationException">The target has no finite basis and a product exceeded its
    /// normalization budget or its key scheme.</exception>
    /// <remarks>The fold is over the basis: each support key's normal form becomes the ordered product of its letters'
    /// images, scaled by that key's coefficient and summed. Over a rounding carrier it therefore rounds exactly as
    /// writing that sum of products by hand does, once per component per operation, and no more.</remarks>
    public PresentedAlgebra<TValue, TOps>.Element Map(in PresentedAlgebra<TValue, TOps>.Element value) {
        Source.RequireOwned(value: value, paramName: nameof(value));

        var coefficients = value.Coefficients;
        var keys = value.Keys;
        var result = Target.Zero;

        if (0 != m_basisImage.Length) {
            for (var index = 0; (index < keys.Length); ++index) {
                result = Target.Add(left: result, right: Scale(value: m_basisImage[((int)keys[index])], coefficient: coefficients[index]));
            }

            return result;
        }

        Span<int> word = stackalloc int[PackedWordCapacity];

        for (var index = 0; (index < keys.Length); ++index) {
            result = Target.Add(
                left: result,
                right: Scale(value: Fold(word: WordOf(presentation: Source.Presentation, key: keys[index], scratch: word)), coefficient: coefficients[index])
            );
        }

        return result;
    }

    /// <summary>Substitutes a source word letter by letter, writing the concatenated image word.</summary>
    /// <param name="word">The source word as generator symbols.</param>
    /// <param name="image">Receives the image word as target generator symbols, truncated to its own length.</param>
    /// <returns>The full length of the image word, which may exceed the destination.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A letter names no source generator.</exception>
    /// <exception cref="InvalidOperationException">Some image is not a single basis element at the material's one, so
    /// it names no word.</exception>
    /// <exception cref="OverflowException">The image word is longer than an <see cref="int"/> can count.</exception>
    /// <remarks>
    /// <para>
    /// The destination is the bound, and that is the point: a substitution's fixed point grows exponentially, so a
    /// streamer sizes the prefix it can hold and reads the returned length to learn what it did not receive. Passing an
    /// empty destination measures an image without writing it.
    /// </para>
    /// <para>
    /// The image is the concatenation the substitution names, not a normal form: over a target with reductions it may
    /// well be reducible. Over a free target — where every substitution system lives — the two coincide.
    /// </para>
    /// </remarks>
    public int MapWord(ReadOnlySpan<int> word, Span<int> image) {
        if (!IsWordMorphism) {
            throw new InvalidOperationException(message: "A word image needs every image to be one basis element at the material's one; a summed or weighted image names no word.");
        }

        var total = 0L;
        var written = 0;

        for (var index = 0; (index < word.Length); ++index) {
            var symbol = word[index];

            ArgumentOutOfRangeException.ThrowIfNegative(value: symbol, paramName: nameof(word));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: symbol, other: ImageCount, paramName: nameof(word));

            var start = m_imageStart[symbol];
            var length = (m_imageStart[(symbol + 1)] - start);
            var room = (image.Length - written);

            total += length;

            if (room <= 0) { continue; }

            var taken = Math.Min(val1: room, val2: length);

            m_imageSymbols.AsSpan(start: start, length: taken).CopyTo(destination: image.Slice(start: written, length: taken));
            written += taken;
        }

        return checked((int)total);
    }

    // The word images of every generator, flat, or two empty arrays when some image names no word. An image names one
    // exactly when it is a single basis element at the material's one: a two-term image is a sum of words rather than a
    // word, and a weighted one is a word times a scalar the concatenation cannot carry.
    private static void BuildWordImages(
        PresentedAlgebra<TValue, TOps> target,
        PresentedAlgebra<TValue, TOps>.Element[] images,
        out int[] start,
        out int[] symbols
    ) {
        var comparer = EqualityComparer<TValue>.Default;
        var material = target.Presentation.Material;
        var presentation = target.Presentation;
        var words = new List<int>();
        var offsets = new int[(images.Length + 1)];

        Span<int> scratch = stackalloc int[PackedWordCapacity];

        for (var symbol = 0; (symbol < images.Length); ++symbol) {
            var image = images[symbol];

            offsets[symbol] = words.Count;

            if ((1 != image.SupportCount) || !comparer.Equals(x: image.Coefficients[0], y: material.One)) {
                start = [];
                symbols = [];

                return;
            }

            foreach (var letter in WordOf(presentation: presentation, key: image.Keys[0], scratch: scratch)) { words.Add(item: letter); }
        }

        offsets[images.Length] = words.Count;
        start = offsets;
        symbols = [.. words];
    }

    // The generator word one key names — without a copy where the presentation has a finite basis, and out of the
    // packed key where it does not.
    private static ReadOnlySpan<int> WordOf(ChargedPresentation<TValue, TOps> presentation, long key, Span<int> scratch) {
        if (presentation.HasCompiledNormalFormBasis) { return presentation.NormalFormWord(key: key); }

        if (!presentation.TryUnpackWord(key: key, word: scratch, length: out var length)) {
            throw new InvalidOperationException(message: "A support key of this element names no word of its presentation.");
        }

        return scratch.Slice(start: 0, length: length);
    }

    // The ordered product of a word's images, seeded at the target's unit, so an empty word maps to that unit.
    private PresentedAlgebra<TValue, TOps>.Element Fold(ReadOnlySpan<int> word) {
        var result = Target.Identity;

        for (var index = 0; (index < word.Length); ++index) { result = Target.Multiply(left: result, right: m_images[word[index]]); }

        return result;
    }

    // The compiled pass: over a finite basis the images preserve the product exactly when they reproduce every cell,
    // which with unitality is the whole homomorphism statement, both products being bilinear. It is what sees the
    // annihilations a degree window states without any rule.
    private bool PreservesCompiledProduct(out long leftKey, out long rightKey) {
        leftKey = -1L;
        rightKey = -1L;

        if (0 == m_basisImage.Length) { return true; }

        var compiled = Source.Compile();
        var target = Target;

        for (var left = 0; (left < m_basisImage.Length); ++left) {
            for (var right = 0; (right < m_basisImage.Length); ++right) {
                var expected = target.Zero;
                var terms = compiled.TargetCount(leftKey: left, rightKey: right);

                for (var term = 0; (term < terms); ++term) {
                    expected = target.Add(
                        left: expected,
                        right: Scale(
                            value: m_basisImage[((int)compiled.Target(leftKey: left, rightKey: right, index: term))],
                            coefficient: compiled.Charge(leftKey: left, rightKey: right, index: term)
                        )
                    );
                }

                if (target.AreEqual(left: target.Multiply(left: m_basisImage[left], right: m_basisImage[right]), right: expected)) { continue; }

                leftKey = left;
                rightKey = right;

                return false;
            }
        }

        return true;
    }

    // One element scaled by one coefficient, through the target's own canonicalization. The material's one is passed
    // through untouched, which keeps the common case free of both a multiply and an allocation.
    private PresentedAlgebra<TValue, TOps>.Element Scale(in PresentedAlgebra<TValue, TOps>.Element value, TValue coefficient) {
        if (EqualityComparer<TValue>.Default.Equals(x: coefficient, y: m_material.One)) { return value; }

        var coefficients = value.Coefficients;

        if (m_scale.Length < coefficients.Length) { m_scale = new TValue[coefficients.Length]; }

        for (var index = 0; (index < coefficients.Length); ++index) { m_scale[index] = m_material.Multiply(left: coefficient, right: coefficients[index]); }

        return Target.FromSupport(keys: value.Keys, coefficients: m_scale.AsSpan(start: 0, length: coefficients.Length));
    }

    // One source relation evaluated on the images: the pattern's product against the charged combination of the
    // replacement terms' products. An annihilation carries no term, so its combination is the target's zero.
    private bool Satisfies(in RewriteRule<TValue> rule) {
        // A re-association charge is a datum about bracket splices rather than a relation among words, and Map never
        // sees a bracket — an element is already a combination of normal forms — so it constrains no image.
        if (RuleKind.Reassociate == rule.Kind) { return true; }

        var pattern = Fold(word: rule.Pattern);
        var replacement = rule.Replacement;
        var offset = 0;
        var total = Target.Zero;

        for (var term = 0; (term < rule.TermCount); ++term) {
            var length = replacement[offset++];

            total = Target.Add(left: total, right: Scale(value: Fold(word: replacement.Slice(start: offset, length: length)), coefficient: rule.Charges[term]));
            offset += length;
        }

        return Target.AreEqual(left: pattern, right: total);
    }
}
