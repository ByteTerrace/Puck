# Input backend surface audit

**Measured 2026-07-31, statically, against `features/maths-excursion`; corrected
2026-08-01** — the F-key row of "The Windows half of the same defect" below was
wrong; see that section for the correction. Surfaced
while scoping keyboard-chord support for
[capability channels](../campaign.md); the hole turned out to be
far larger than the work that found it. This is a measurement and a handoff, not a
plan — no fix is scoped here.

**Plain verdict: a person cannot meaningfully drive `Puck.World` on Linux today,
and never could.** No backend emits the full `KeyCode` surface, and the two window
backends disagree about which parts they *do* emit, in both directions.

## Reachable input, per backend

| Backend | Keyboard | Pointer | Modifier keys | Text |
|---|---|---|---|---|
| **Win32** | Letters, Space, Tab (chorded only), Escape, Backspace, Enter, Backtick, arrows, **F1–F12**\* | Yes | **Yes**\* — left/right Control, Shift, Alt, Super as ordinary sources | Yes (`WM_CHAR`) |
| **Xcb (X11)** | Escape, Backspace, Enter, Backtick, arrows, **F1–F12**\* — no letters, no Space, no Tab | Motion + buttons | **Yes**\* — the same eight | **None** — nothing ever feeds `Keyboard.Text` |
| **Wayland** | **Nothing.** | **Nothing.** | — | — |

\* Cells marked with an asterisk are CURRENT, not as-measured, and the difference
matters — see the corrections below. As measured, both backends stopped at F8 and
neither emitted modifier keys at all; a separate "modifier state" column described a
`ComputeModifiers` bitfield that has since been **deleted** outright. Modifiers are
no longer ambient state riding on other signals; they are keys, like any other key.

Wayland's own file doc states the position: binding a `wl_seat` plus xkb is out of
scope, so the backend handles lifecycle and resize only.

**Gamepads are Windows-only.** `GamepadManager` is platform-neutral, but the
composition root unconditionally constructs the Win32 HID source, and the Linux
hidraw transport is deferred (`src/Puck.Input/README.md`). The parsers are
OS-agnostic; only the transport is missing. **This includes the Steam Deck's own
built-in pad.**

## What a human actually gets on Linux/X11

Against the default bindings: forward, back, and turn on the arrow keys. Confirm on
Enter. F-keys. That is all.

- **No jump or primary** — those are bound to Space, which Xcb never emits.
- **No strafe** — bound to Q and E; no letter reaches the binding layer.
- **The console panel opens** (Backtick emits) **and cannot be typed into.**

On Wayland — the native Steam Deck path — there is no human input at all. The Deck
is a named bring-up target with no functional input route in-tree: its native path
is Wayland plus the built-in pad, and both are dead; the X11 path needs an external
keyboard and still only gives arrows.

## Why this stayed invisible

**Every deterministic proof drives stdin.** The console wire is platform-neutral and
fully functional, so an agent can exercise the entire engine on Linux over a pipe
without ever touching a window backend. Nothing in the proof surface drives the
window, so nothing ever reported that the window gives you almost nothing.

That is worth generalising: a verification surface that only ever uses the
programmatic path cannot see a hole in the human path.

## The Windows half of the same defect

~~`Win32NativeWindow`'s virtual-key constants stop at F8, so **F9–F12 never emit**,
even though `KeyCode` and `WindowInputMapper` both handle F1–F12. Xcb emits the full
F1–F12 range but no letters; Win32 emits letters but not F9–F12. Neither backend
covers the surface the shared vocabulary declares, and they fail to cover it in
opposite directions.~~ **FACTUAL ERROR, corrected 2026-08-01.** The Win32 half was
measured correctly — Win32 did stop at F8. The Xcb half was not: Xcb's function-key
mapping was (and until this correcting change remained) a single range check bounded
by `KeycodeF8`, so **Xcb stopped at F8 too**. Both backends shared the identical
ceiling; "opposite directions" never applied to F-keys, because there was no second
direction — the framing implied a Win32-only gap where the gap was symmetric. This
is my error: I published the "Xcb has the full range" claim without verifying it,
and it propagated — an agent scoping the follow-up read this doc and concluded
F9–F12 was out of scope for Xcb, on the strength of a measurement I never checked.
Both gaps are closed as of this correction: Win32's F9–F12 constants were wired
earlier in the same OS-modifier-as-source change, and Xcb's F9/F10 (contiguous with
F1–F8 on the evdev keymap) plus F11/F12 (95/96, not contiguous with anything above)
are wired alongside this correction.

## Chords the Windows layer swallows before bindings see them

Recorded because they become silent-failure modes the moment modifier keys are
bindable sources:

1. **Alt+Enter** → fullscreen toggle, consumed.
2. **Ctrl+V** → paste, consumed *unconditionally*, even with an empty clipboard
   (deliberate — the comment explains that a clipboard-state-dependent binding would
   be worse).
3. **Bare Tab** → never emitted; its `WM_CHAR` is a control character and drops too.
   Reverse-swallow: Tab is reachable *only* when chorded.
4. **Alt + any handled key** — `WM_SYSKEYDOWN` enters the same switch and returns 0,
   so **Alt+F4 emits `F4(Alt)` and suppresses the system close**; Alt+Space
   suppresses the system menu; Alt+letter suppresses menu mnemonics. Whether
   swallowing Alt+F4 is intended deserves a decision rather than remaining an
   accident.
5. **Modifier keys themselves, and Win-key chords** — fell to `DefWindowProc`, never
   reaching bindings. **Accurate as measured; since CLOSED, not corrected.** An
   earlier revision struck this as a "factual error"; that retraction was itself
   wrong and is withdrawn. At the commit this audit measured, `Win32NativeWindow`
   contained no `case VkControl` at all — the claim was true, and it is precisely
   *why* the OS-modifier-as-source work existed. It reads stale today only because
   that work has since landed in the working tree, adding first-class
   `VkControl`/`VkMenu`/`VkShift`/`VkLWin`/`VkRWin` cases that resolve physical
   left/right `KeyCode`s.

   Worth keeping as a lesson about corrections: the mistaken retraction cited the
   new code's own comment — *"Was previously unhandled here and fell to
   `DefWindowProc`, so bare Ctrl never reached bindings"* — as evidence the original
   claim was an error, when that sentence is evidence the claim was **right**. A
   measurement that a later change makes obsolete is not a measurement that was
   wrong, and recording it as wrong misdates when the behavior changed.

   What still never reaches the app either way: OS-reserved Win-key shell chords
   (Win+D, Win+L, Win+Tab, …), intercepted below the window-message layer regardless
   of what the wndproc does with a bare Super key.

Linux swallows nothing, because the window manager owns Alt+Enter and Xcb consumes
no chords. So reserve-versus-stop-swallowing is a Windows-only decision today.

## Size of the hole

**Ruled a stretch goal by the owner, 2026-08-01: all three workstreams below are
deferred to the future, and this document is where that decision lives.** Two
reasons, and the second is the one that would otherwise get forgotten. First, they
are a stretch goal rather than a dependency — nothing in the capability-channels
work needs them. Second, **none of it is verifiable from the Windows machine this
was measured on.** Every path here is Linux-only, so a green Windows build says
exactly nothing about whether the code works, and landing it from here would mean
shipping under the one rule this area has repeatedly been burned by: a green build
proves nothing about behavior. Closing any of these honestly needs the Steam Deck
or another Linux target in the loop, which is precisely why it is a stretch goal
and not a to-do.

Three independent workstreams, none scoped here:

1. **Xcb keyboard completion** — letters, Space, Tab, and typed text; the
   letter/`WM_CHAR` dual that Win32 already has. **Modifier keys are no longer part
   of this gap** — Xcb emits all eight as of the OS-modifier-as-source change. Do
   not re-implement them.
2. **Wayland input from scratch** — `wl_seat` plus xkb.
3. **Linux hidraw transport** — parsers already OS-agnostic.

The Windows F9–F12 gap this doc originally flagged is closed (see "The Windows half
of the same defect" above for the correction); Xcb's matching F9–F12 gap closed in
the same change that corrects this doc.

## Caveats

All static. Nothing was booted on Linux. The Xcb file's claim that its path runs on
the Steam Deck is in-tree prose that was not verified against hardware.
