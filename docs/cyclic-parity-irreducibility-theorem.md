# Cyclotomic irreducibility for odd cyclic parity configurations

## The theorem

Let a finite ray--basis incidence configuration admit a cyclic automorphism
(g) of odd order (n). Assume every ray and basis under consideration lies
in a free (C_n=\langle g\rangle)-orbit. Choose (k) complete basis orbits,
where (k) is odd, and let (r) be the number of ray orbits.

After choosing one representative in every orbit, the incidence map is an
(r\times k) matrix

\[
P(t)\in\operatorname{Mat}_{r\times k}
\bigl(\mathbf F_2[t]/(t^n-1)\bigr).
\]

Let \(\widetilde P\) be the ordinary \(nr\times nk\) binary incidence matrix
obtained by expanding all cyclic shifts, and suppose the selected \(nk\) bases
form a parity proof. Then the following are equivalent:

1. the expanded parity proof is irreducible;
2. \(\ker_{\mathbf F_2}\widetilde P\) is spanned by the all-bases vector;
3. \(P(1)\) has column rank \(k-1\), and for every irreducible factor
   \(f\ne t+1\) of \(t^n-1\), the reduction \(P_f\) has column rank \(k\)
   over \(K_f=\mathbf F_2[t]/(f)\);
4. provided \(r\ge k\),

   \[
   \gcd\!\left(t^n-1,
     \{\text{all }k\times k\text{ minors of }P(t)\}\right)=t+1,
   \]

   together with \(\operatorname{rank}P(1)=k-1\).

The binary nullity is exactly

\[
\operatorname{nullity}_{\mathbf F_2}\widetilde P
=\sum_{f\mid t^n-1}\deg(f)
  \operatorname{nullity}_{K_f}P_f.
\]

Thus every factor beyond \(t+1\) that divides all maximal minors is an
additional cyclotomic obstruction to irreducibility.

## Proof

Because \(n\) is odd, the derivative of \(t^n-1\) over \(\mathbf F_2\) is
\(t^{n-1}\), which is coprime to \(t^n-1\). Hence the polynomial is square
free. Writing its distinct monic irreducible factors as \(f\), the Chinese
remainder theorem gives

\[
R=\mathbf F_2[t]/(t^n-1)\cong\prod_f K_f.
\]

The expanded binary matrix is the underlying \(\mathbf F_2\)-linear map of
the \(R\)-linear map represented by \(P(t)\). Kernels commute with the product
decomposition. Since \(\dim_{\mathbf F_2}K_f=\deg f\), taking dimensions gives
the displayed nullity formula.

Let \(S_n=1+t+\cdots+t^{n-1}\). Selecting every shifted basis in each chosen
orbit is the vector \(S_n\mathbf1_k\in R^k\). The parity-proof hypothesis says
that this vector is in the kernel. At \(t=1\), \(S_n(1)=n=1\) in
characteristic two; at every other \(n\)-th root of unity, \(S_n\) vanishes.
The compulsory all-bases relation therefore contributes exactly the vector
\(\mathbf1_k\) in the \(t+1\) component and zero in every other component.

The total number \(nk\) of bases is odd. For any odd-cardinality parity proof,
the complement of an even kernel selection is odd. Consequently a second
independent kernel vector always produces a proper odd parity subproof, while
a one-dimensional kernel contains only zero and the complete proof. The
parity-complement lemma is exposed as a geometry-independent Lean theorem in
[`CyclicParity/Irreducibility.lean`](../formal/PuckMathsFormal/PuckMathsFormal/CyclicParity/Irreducibility.lean).
It proves the equivalence of items 1 and 2.

The nullity formula now proves item 3: total nullity is one exactly when the
(t+1\) component has nullity one and every other component has nullity zero.
Finally, over the field \(K_f\), an \(r\times k\) matrix has rank below \(k\)
exactly when all its maximal minors vanish. This is equivalent to \(f\)
dividing every lifted minor. Square-freeness then proves item 4.

## Syndrome-matroid layer

For every available basis orbit \(j\), let

\[
s_j=P_j(1)\in\mathbf F_2^r
\]

be its syndrome. A word is parity valid exactly when its selected syndromes
sum to zero. Its \(t=1\) nullity is one exactly when those syndrome columns
form a circuit of the represented binary matroid: their all-ones sum is the
unique relation.

If \(n_v\) letters share syndrome \(v\), the number of candidate words over a
syndrome circuit \(C\) is \(\prod_{v\in C}n_v\). Hence the complete candidate
count at word length \(k\) is the multivariate circuit enumerator

\[
\sum_{\substack{C\text{ a syndrome circuit}\\|C|=k}}
  \prod_{v\in C} n_v.
\]

The nontrivial cyclotomic rank tests then select the irreducible candidates.
This separates the universal binary-matroid layer from the configuration's
genuinely geometric finite-field data.

## Scope and extension boundary

The theorem is intentionally stated for odd \(n\), free cyclic actions, and
complete orbit selections. If \(n\) is even, \(t^n-1\) has repeated factors in
characteristic two; nilpotent primary components replace the product of
fields, so ranks at roots alone no longer determine binary nullity. Nonfree
actions require permutation modules induced from stabilizers rather than free
copies of \(R\). Both are natural extensions, but neither is silently covered
by the theorem above.

## Application ledger

| Configuration | Cyclic order | Status |
|---|---:|---|
| E8/Gosset \(4_{21}\) rays | 15 | Flagship recovered through the generic checker: 3,569,146 irreducible five-letter proofs. |
| 600-cell rays | 15 | Independently reconstructed and exhaustively verified: 2 irreducible words among 8 parity words. |
| 120-cell rays | 15 | Application in progress. |
| Additional odd-cyclic configuration | — | New-result search in progress. |

## Reproduction interface

[`odd-cyclic-incidence-verifier.cs`](../tools/odd-cyclic-incidence-verifier.cs)
consumes the geometry-neutral `puck.odd-cyclic-incidence.v1` certificate
format. It verifies the proposed irreducible factorization, derives syndromes,
computes every finite-field component rank, and compares the CRT nullity with
the direct expanded binary rank for explicit words. For manageable alphabets
it exhausts all odd words; for larger alphabets it traverses syndrome-matroid
circuits and their letter transversals.

The same program recovers both completed applications:

```text
dotnet run -c Release -p:NuGetAudit=false tools/odd-cyclic-incidence-verifier.cs -- --certificate docs/certificates/e8-odd-cyclic-incidence.json
dotnet run -c Release -p:NuGetAudit=false tools/odd-cyclic-incidence-verifier.cs -- --certificate docs/certificates/600-cell-c15-parity.json --enumerate
```

The E8 certificate deliberately references the independently reconstructed
orbit table rather than copying it. The geometry-specific reconstruction and
the generic algebraic classification therefore remain separate audit layers.
