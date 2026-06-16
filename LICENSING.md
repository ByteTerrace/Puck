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

> Defining exactly which institutions qualify as "public" (charter schools, public
> universities with large private endowments, non-U.S. public bodies, etc.) is a pricing
> policy, not a clause in the public license. The eligibility wording lives in the
> commercial agreement and price sheet, where it is enforceable as an ordinary contract.
> **Have an IP attorney finalize that definition before publishing prices.**

---

## Optional: a free tier for indie / small companies

If we want to let small commercial users in for free too (a common goodwill move that
drives adoption), we can add the
**[PolyForm Small Business License 1.0.0](https://polyformproject.org/licenses/small-business/1.0.0)**
as an *additional* grant on top of the noncommercial license. It makes commercial use
free for any company with **fewer than 100 people and under ~$1M (2019, CPI-adjusted)
revenue** in the prior tax year. Bigger companies still need a paid license.

This is **not enabled yet** — it is listed here as a ready-to-flip option. Enabling it is
a one-line addition to `LICENSE.md` plus a note here.

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
agree on a minor's behalf (and addresses COPPA / GDPR-K). See [CLA.md](CLA.md). It is a
draft pending attorney review and is not yet wired into the contribution flow.

---

## Third-party components

ByteTerrace.Puck builds on third-party software under permissive licenses (MIT, SIL OFL) and on
published algorithms credited to their authors. None of it is copyleft, which is what makes
this dual-license model legally possible. See
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) and [`ACKNOWLEDGMENTS.md`](ACKNOWLEDGMENTS.md).

---

## Placeholders to finalize

Resolved:

- **Copyright holder / licensor** — ByteTerrace. Confirm with
  your attorney which is the contracting party of record; whichever it is must own — or
  have a CLA covering — 100% of the code.
- **Licensing contact** — administrator@byteterrace.com.
- **Repository URL** — set in the `Required Notice:` line of `LICENSE.md`.

Still open (for your attorney):

- **Public-institution discount definition** — the eligibility wording for the public-vs-
  private school discount, to live in the commercial agreement / price sheet.
- **CLA finalization** — [CLA.md](CLA.md) is a draft; the minor-consent, COPPA/GDPR-K, and
  governing-law provisions need attorney sign-off before it is wired into the contribution
  flow.

> **Not legal advice.** This structure follows common industry practice, but you are about
> to monetize. Have your IP attorney review `LICENSE.md`, this document, the commercial
> agreement, and `CLA.md` before you publish prices or accept contributions.
