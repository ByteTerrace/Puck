# Licensing ByteTerrace.Puck

ByteTerrace.Puck is **source-available and dual-licensed**. The source is public so you can
read it, learn from it, and use it for noncommercial purposes for free — but it is
**not** open source, and commercial use requires a paid license. This page explains
who needs what.

> This document is a plain-language summary for humans. The binding legal terms are in
> [`LICENSE.md`](LICENSE.md) (the noncommercial license) and in the commercial license
> agreement you receive when you purchase one. Where this summary and those documents
> disagree, those documents control.

---

## The short version

| You are… | What you may do | Cost |
| --- | --- | --- |
| An individual (study, hobby, personal projects, research) | Everything noncommercial | **Free** |
| A school, university, public research org, charity, or government body | Everything noncommercial — teaching, coursework, academic research | **Free** |
| A company evaluating ByteTerrace.Puck, or using it noncommercially | Evaluate and experiment | **Free** |
| **A company shipping or operating ByteTerrace.Puck commercially** | Build and ship commercial products | **Paid commercial license** |

The dividing line is **commercial vs. noncommercial use**, *not* the size of the user.
A trillion-dollar company and a one-person studio are treated the same: noncommercial
use is free for both; commercial use requires a license from both. That is what stops a
major company from simply taking the engine and shipping it — and it is why a public
school can use it in the classroom at no charge.

---

## Why this license

The default license is the **[PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)**,
a modern, plain-language, lawyer-drafted, [SPDX-recognized](https://spdx.org/licenses/PolyForm-Noncommercial-1.0.0.html)
source-available license. It grants free use for **any noncommercial purpose**, and it
spells out that the following are always permitted (free) uses — *regardless of how they
are funded*:

> Use by any charitable organization, educational institution, public research
> organization, public safety or health organization, environmental protection
> organization, or government institution.

So every university and every public school gets the engine for free, with no paperwork
and no negotiation. Commercial users come to us for a commercial license.

---

## Schools and universities — your "good discount" is a zero

Under the noncommercial license above, **accredited educational institutions and public
schools already pay nothing** for teaching, coursework, and academic research. There is
nothing to apply for: the license grants it directly.

The only time a school would ever need to talk to us is if it wants to use ByteTerrace.Puck for a
genuinely **commercial** activity (for example, a university spin-out selling a product
built on the engine). In that case it needs a commercial license like anyone else — but:

- **Public, government-funded institutions** receive our deepest commercial discount.
- **Private institutions** are handled on standard commercial terms.

> Which institutions qualify as "public" (charter schools, public universities with large private
> endowments, non-U.S. public bodies, and the like) is a pricing question, not a clause in the
> public license. The precise eligibility criteria live in the commercial agreement and price
> sheet, where they are enforceable as an ordinary contract.

---

## Getting a commercial license

If your use is commercial, contact us:

- **Licensor:** ByteTerrace
- **Email:** administrator@byteterrace.com

Tell us roughly what you're building, your organization type (company / public
institution / private institution), and headcount, and we'll send terms.

---

## For contributors

We intend to accept outside contributions — including from students and from players who
submit content, some of whom may be minors. To keep the dual-license model intact, **every
contributor must agree to the [Contributor License Agreement](CLA.md) before their first
contribution is merged or accepted.** The CLA grants ByteTerrace a broad license to the
contribution, *including the right to relicense it under both the noncommercial and the
commercial terms* — without it, we could not legally offer contributed code or content
under the paid commercial license.

Because contributors may be children, the CLA requires a **parent or legal guardian** to
agree on a minor's behalf (and addresses COPPA / GDPR-K). See [CLA.md](CLA.md).

---

## Third-party components

ByteTerrace.Puck builds on third-party software under **non-copyleft** licenses and on published
algorithms credited to their authors. The core engine's redistributed dependencies are under
MIT (mimalloc, the .NET runtime) and the SIL Open Font License 1.1 (the Cascadia / Caskaydia
glyphs). The experimental bare-metal target (`experimental/Puck.BareMetal/`) additionally vendors
components under Apache-2.0 (mbedTLS), BSD (lwIP), and MIT (AMD register headers, RADV, musl),
plus AMD's **proprietary-but-redistributable** GPU firmware — a signed binary blob that is *not*
open source, but is licensed for unmodified binary redistribution (with pass-along, no-reverse-
engineering, and U.S. export-control conditions).

None of it is **copyleft** — which is what makes this dual-license model legally possible: no
dependency forces the engine's own source open or blocks offering it under paid commercial terms.
The redistributable firmware carries its own conditions rather than copyleft ones, satisfied by
shipping its license alongside the binary. Full inventory:
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), the bare-metal subtree's
[`NOTICE.md`](experimental/Puck.BareMetal/NOTICE.md), and [`ACKNOWLEDGMENTS.md`](ACKNOWLEDGMENTS.md).

> **Note:** the AMD firmware is subject to U.S. export regulations (EAR / ITAR), and mbedTLS
> contains cryptography; anyone redistributing the bare-metal binaries internationally is
> responsible for export compliance.

## Trademarks

The license covers the **code**; it does not grant any rights in the **names** "Puck" or
"ByteTerrace," which are **registered trademarks** of ByteTerrace (Puck®, ByteTerrace®). PolyForm Noncommercial's *No Other Rights*
clause already means the license conveys no trademark rights — so using the software under it does
**not** grant permission to use the marks. The trademark usage terms live in
[`TRADEMARKS.md`](TRADEMARKS.md), which draws the line between permitted truthful references
("built on Puck") and uses that need permission (naming a product/fork "Puck," using the logos,
implying endorsement).

---

## Related documents

- [`LICENSE.md`](LICENSE.md) — the PolyForm Noncommercial 1.0.0 license text.
- [`TRADEMARKS.md`](TRADEMARKS.md) — trademark usage policy for the Puck® and ByteTerrace® marks.
- [`CLA.md`](CLA.md) — the Contributor License Agreement (required before a first contribution).
- [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) — third-party components and their licenses.

_Last updated: July 5, 2026._
