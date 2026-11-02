# Manual pointer gesture checks

Two behaviors of Puck.World's pointer path need the real window and the
operating system's input queue, so no headless run reaches them. They are run
by hand, on a granted GPU leg, when the pointer, cursor or camera-orbit
gesture grammar changes enough that their claim might no longer hold. No build
or CI job runs them.

## Desktop requirements

Real mouse input goes to whichever window has operating-system focus. Another
application, another World process or another run can therefore receive input
meant for the test window. A check needs exclusive desktop access: close or
park other windows, and touch neither mouse nor keyboard while it runs.

Start the World with the `puck` server's `puck_world_start` (`windowed: true`
under `puck lease gpu`, `checkout` set to the worktree), `world: worlds/debug`
and the owner's device policy for real input, and end it with `puck_world_stop`.
Drive it with `puck_exec` console verbs. The operator (or a desktop-control
tool the owner granted) supplies the physical input. Synchronize on read-back
lines rather than elapsed time, because tick pacing under shared load is not a
clock: read a verb, act, then read it again. The world's tick rate is the
`WorldDefinition` default unless the document authors one, so convert any
wait to ticks from the document's rate rather than from a remembered figure.

## Pointer button across a keyboard-seat reassignment

Claim: a held pointer button cannot survive a keyboard-seat reassignment as a
phantom drag (`WorldPointer.ReleaseAllButtons`), and `SystemReleaseCount`
advances only on a forced release, never on a genuine one. The mouse rides
whichever seat owns the keyboard, so reassigning the keyboard away and back
must clear the old seat's held-button bit.

Read `world.view.pointer` after each step and compare its `reason` and
release-count fields:

1. Control: hold the right button on seat 1. The reason is the orbit drag and
   the release count is 0.
2. Genuine release: release the right button. The reason flips to visible and
   the release count is unchanged. The button must have been freshly pressed
   and untouched since step 1, or the flip proves nothing: a release landing on
   an already-cleared button looks the same whether it was delivered or never
   sent.
3. The fix: press the right button again and keep it down. Send
   `player.assign keyboard1 2` then `player.assign keyboard1 1`. The reason is
   no longer the orbit drag and the release count advanced by 2, one
   `ReleaseAllButtons` per assignment.
4. Liveness: press the right button again on seat 1. The reason is the orbit
   drag again and the count is unchanged. Without this step, step 3's null
   could mean a dead pointer path rather than a cleared stale bit.

Also send a burst of wheel input with no wheel consumer registered. The World
must keep running; `WorldPointerSink` drains the accumulator in the branch the
wheel arrives on.

## Camera orbit across focus loss

Claim: the per-seat pointer store's focus-lost handling releases an armed
camera-orbit drag without killing the orbit path.

Read the camera with `world.view.camera <player> full` (or name the fields the
check needs, and compare them by key) and compare readings at five points:

1. Boot: the baseline.
2. Control: right-drag inside the window. The orbit moved.
3. Focus loss: while the drag is held, switch to another window with Alt+Tab,
   then return and move the mouse with no button held. The orbit has not
   moved since the focus loss.
4. Liveness: press the right button and drag again. The orbit moves. Without
   this step, step 3's null could mean the pointer path died at the refocus.
5. Wheel: wheel input over the window is skipped by the window pump and the
   process stays alive.

Stop the run on a failed focus check before injecting: input sent to the wrong
window proves nothing about this one.
