# Doman Hint Pilot v0.8.22 — Tsumo after experimental Kan

The 2026-11-13 16:13 run attempted an experimental Kan. Seven seconds later the
controller paused because its Kan-resolution handler only looked for a discard
or a completed-round state. The automatic capture at the pause instead shows
**state 6, a visible `Tsumo!` hint, and a visible `Tsumo / Pass` choice list**.
The raw `AtkValue[6]` was numeric `4`; that numeric field is *not* the win
recommendation, but the verified hint-card and call reader correctly decoded
Tsumo.

## Changed

- While awaiting Kan resolution, a **verified Tsumo or Ron response plan** now
  exits the special Kan wait after the initial 500 ms settling period.
- The controller hands the win to the normal observation/stability/re-read
  process (900 ms and final revalidation). **It does not fire a win callback
  directly on the first sighting**, and never repeats the Kan callback.
- Unknown Kan meld chooser layouts still pause; a normal recommended discard,
  round result, or final results screen keeps its existing behavior.
- Kan is still experimental and opt-in. Other calls, round transitions and
  diagnostic capture are unchanged. Round Next, End Match, matchmaking and
  requeueing remain manual.

## Test

After an experimental Kan opportunity that yields a self-draw win, expect
`Kan follow-up presented verified Tsumo; handing off to ordinary stabilized win handling. No Kan retry.`
then, if the screen stays valid, `Response attempted: Tsumo (option 0)`.
If the offer vanishes, normal confirmation prevents a stale win callback.
Observe the next attempt in-game and export the activity log if anything
unexpected happens.

**Not compiled against Dalamud or tested in FFXIV.** Only static source and
archive checks can be performed in this environment. Use a supervised NPC or
unranked test; gameplay automation may violate FFXIV's rules.

---

# Doman Hint Pilot v0.8.21 — state-6 Kan hint recognition

The 2026-11-13 15:24 log paused at state 6 with raw action `4`.
The user observed that the actual live-game hint was **Kan**. The previous
reader recognized self Kan in state 30, but not state 6; the raw numeric
AtkValue was therefore treated as an unreadable action.

## Changed

- On state **6 or 30**, read the exact **visible Kan!** text from the active
  hint panel, independently of numeric AtkValues. Raw `4` by itself is **not**
  assumed to mean Kan.
- Do not promote a Kan label when the hint also shows a visible **Discard**
  label. Optional Kan availability must not override a discard recommendation.
- If experimental Kan is enabled, send the first-stage response only when
  the *currently active*, visible response list has exactly one **Kan** row.
  Use its observed option index, then enter the existing Kan-resolution
  observation state. Never retry blindly or guess an additional meld chooser.
- The Live Game Hint field also displays `Kan!` when verified, instead of
  its raw numeric code.
- If the text is Kan but the option row cannot be verified, pause with a
  **read-only automatic diagnostic snapshot** and a Kan-specific reason.
  Unreadable numeric hints now also trigger a diagnostic snapshot.
- No changes to Riichi, discard, round transition handling or other calls.
  Next, End Match, queueing and requeueing remain manual.

**Important:** This update has not been compiled or run in the game. The
capture does not prove where the state-6 Kan text is located; the new visual
reader must be tested with the next Kan offer. If a state-6 Kan hint still
appears as numeric, capture `/mahint riichi` and `/mahint hint` while it is
on screen, or provide the generated diagnostic if the controller pauses.

---

# Doman Hint Pilot v0.8.20 — Direct discard when Riichi is optional

The 2026-11-13 14:52 run reported two Pass callbacks on a Riichi/Pass
self-action offer even though the live hint recommended Discard. Neither
changed the visible state, and the controller paused. The user confirmed
that committing the recommended discard itself declines optional Riichi.

## Updated behavior

- On a *verified* self-action Riichi/Pass offer in state 6 or 30, a **Discard**
  recommendation goes straight to the already-established discard pair:
  `[15, recommended tile icon]`, then `[7, hand slot]` in one update.
  **No Pass callback precedes the discard.**
- The controller still requires a visible discard hint containing a valid
  tile icon, a fully decoded 14-tile hand, an exact icon match, a stable
  recommendation, and a final read-back of the same hand before sending
  either callback. If any validation fails, it does not guess an icon or slot.
- An explicit, verified **Riichi!** recommendation can still declare Riichi.
  Merely having Riichi available never causes a declaration.
- A verified post-declaration discard remains permitted; Riichi is never
  repeated automatically. State-15 opponent-call and stale-menu handling
  is unchanged (state 15 does **not** authorize a tile discard).
- Ron, Tsumo, Pon, Chi, Kan, and genuine Pass actions are unchanged.
- Results, Round Next, End Match, joining a table, matchmaking and
  requeueing remain completely manual / game-timed.

## Testing

Build `DomanHintPilot.csproj` using the local Dalamud SDK and load the
plugin at an NPC or unranked Mahjong table. While optional Riichi/Pass is
shown and the hint says Discard, verify that the log records
`Discard pair attempted in one update` rather than `Response attempted: Pass`.
Verify the tile that was actually discarded in-game and confirm the turn
proceeds. If the discard cannot be validated, the controller pauses rather
than sending a blind click. Export the activity log if the game remains
unchanged.

**This release was not compiled with Dalamud or tested in the live game.**
Source and archive checks do not prove FFXIV accepted the input. Keep the
first test supervised; gameplay automation may violate FFXIV's rules.

---

# Doman Hint Pilot v0.8.19 — Follow the recommendation, not optional Riichi availability

The 2026-11-13 14:31:04 activity log showed a state-15 Riichi/Pass
residual popup immediately after a discard. The controller paused because it
had not previously selected Riichi. The user also observed that the game can
offer **Riichi / Pass** while its live hint still advises **Discard**.

## Behavior corrected

- **Riichi is not automatic when merely offered.** On a verified *self-action*
  Riichi/Pass offer (state 6 or 30), the controller chooses Riichi **only**
  for an explicit `Riichi!` recommendation.
- When that same real self-action popup offers Riichi/Pass but the hint
  recommends `Discard`, the controller sends **Pass once** (option 1) to
  decline optional Riichi, then performs the recommended discard only when
  the ordinary playable discard state and full 14-tile hand are validated.
  It does not try to commit a tile while a self-action popup is pending.
- In state **15**, lingering Riichi/Pass rows with a generic `Discard` or
  numeric payload are treated as residual UI: **wait without input**, even
  if the controller never previously declared Riichi. Genuine visible Ron
  and named winning hints remain higher priority.
- The state-30 hint reader no longer rewrites `Discard` into `Riichi!`
  merely because the hint card has both labels.
- After Riichi has been sent once, the controller still forbids duplicate
  declaration. A verified post-declaration discard at state 6 is allowed
  to proceed; ambiguous persistent self-action screens wait without input.
- The **manual** Riichi-first override remains available for deliberate
  manual testing, but is **off by default**. It is not used by automation.

All in-round actions and experimental Chi/Kan settings are retained. Round
Next, End Match, joining a table, matchmaking and requeueing remain manual.

## Verification and limits

The source has been statically checked and ZIP integrity tested, **not**
compiled against Dalamud or tested in FFXIV. The exact hint/choice pairing
may differ in-game; supervise the next test at an NPC or unranked table. If a
live Riichi/Pass offer remains after Pass, do not repeatedly resume; export
the activity log and save the call prompt and live hint for that moment.

---

# Doman Hint Pilot v0.8.18 — passive dealing/loading states

This release removes the plugin's **Round Next** and **End Match** click features,
including both automatic actions and their old manually confirmed buttons.
The game or player handles all round advancement and final match closure.
There is **no matchmaking, requeueing, or match restart** logic.

## What changed

- **Never sends Next or End Match.** The two result-button test classes and
  their callback implementations have been removed from this source project.
- **Waits passively on result screens.** Recognizes known Draw/Ron/Tsumo result
  labels at states 29/30 and state 32. All state-2 loading frames wait without
  input, regardless of their displayed counter (`× 0`, `× 1`, etc.).
  No result-screen timeout causes a plugin action or pause.
- **Waits through the observed dealing phase.** After manually pressing Next,
  state `27` with raw value `302` was captured while the game dealt tiles.
  Once a round boundary has been recognized, state `27` is an input-free wait
  regardless of its numeric value. It never applies the unsupported-hint
  two-second pause to that transition.
- **Resumes on a new playable hint.** After a recognized round transition,
  resets the previous round's Riichi/duplicate-action state and continues
  with normal in-round actions once a fresh, stable hint appears.
- **Final match results stop in-round automation** without clicking the
  game's End Match button. You can close the results yourself or let the
  game time out; starting another match is always manual.
- **Stale UI labels after Riichi or under a hidden choice list** cause an
  input-free wait without an arbitrary 10/20-second pause, including the
  newly observed numeric action `4` variant. A genuine new
  Ron/other verified active call still gets evaluated first by the existing
  hint-first response path.
- Keeps discards, Ron, Tsumo, Pon, Pass, Riichi, and opt-in experimental Chi
  and Kan. Unknown *active* call layouts still pause rather than guess.
- Removed the old Round Next / End Match controls from `/mahint`. Retained
  read-only `/mahint results` diagnostics and logging.

## How to test

1. Extract and build `DomanHintPilot.csproj` in your configured Dalamud / Visual
   Studio environment. Reload the plugin.
2. Manually join an NPC/unranked table, acknowledge that test mode, and select
   **START in-match automation**. Observe the actions directly.
3. At any Draw/Ron/Tsumo result, allow the game to time out or click **Next**
   yourself. Doman Hint Pilot should wait silently, then resume at the next
   supported hint. At the final match results, it stops without clicking.
4. Export the automation activity log if gameplay pauses unexpectedly.

Expected log when a round completes:

    Round transition detected; Next is manual or game-timed. No round-control inputs sent.
    Observed new-round dealing state 27; waiting for a playable hint without input or timeout.
    New playable round detected; in-round automation resumed.

## Limits

The November 13 13:53 test showed a previously unseen post-Riichi state-15
variant: AtkValue[6] was integer `4`, after the controller attempted Riichi
and discarded 2 Bamboo. The exact visible choice rows still contained Riichi
(index 0) and Pass (index 1), but no explicit winning hint was captured.

v0.8.16 continues to treat the observed post-Riichi state-15 action variants `0`, `4`,
and `Discard` as passive waits **only when the currently visible choice rows
are exactly Riichi / Pass and the controller already attempted Riichi in this
round**. It sends neither Riichi nor Pass and does not pause merely because
those two stale controls remain. Ron visible as an active response is checked
first, and explicit other action hints are not converted to waits.

This does not establish that the game's callback succeeded or guarantee that
an unobserved Ron opportunity will be recognized. If the screen offers an
actual winning action that the plugin does not take, handle it manually and
capture a fresh snapshot of that exact moment.

The source was inspected and packaged here, but not compiled against the
Dalamud SDK or tested in FFXIV. Always supervise testing, avoid ranked
matches, and be aware gameplay automation may violate FFXIV's rules.

## November 13, 14:00 test and scope

The activity log shows a result transition at `14:00:44.220` after two Ron
callback attempts. At `14:00:53.559` the controller paused on unsupported
hint `302` in state `27`; the user reports that tiles were still being dealt.
This was the controller's two-second unknown-hint grace, not evidence of a
game-enforced round timer. This patch is intentionally limited to a
**previously recognized** between-round transition plus this exact state and
hint. Unexpected state-27 hints outside that context still pause for safety.

No Round Next or End Match callbacks exist in this version, and nothing
initiates or requeues a match. The two Ron attempts recorded in this log are
not changed by this patch; they should be investigated separately if repeated
for a single winning offer. The source has not been compiled against Dalamud
or run inside FFXIV in this environment.

## v0.8.17 — Numeric state-15 false pause

The 2026-11-13 `14:09:26` pause snapshot captured `state=15`,
`AtkValues[6]=Int(22)`, with old `Chi` and `Pass` row labels still in the
UI tree, **but their enclosing choice list was not visible**. v0.8.16 only
ignored hidden lists with action `0` or `Discard`, so it reached the generic
unknown-call two-second pause. This was not evidence of a missed Chi action.

v0.8.17 removes the action-value restriction from the *read-only* hidden-list
check. If the component tree matches and the enclosing choice list is hidden,
the controller waits, regardless of numeric or string payload, sending NO
response. When a new supported live hint arrives, automation resumes. When
there is a real **visible** but unknown choice list, it still pauses instead
of inventing a callback. In addition, an exact visible Riichi/Pass pair after
Riichi can be treated as a non-actionable residual for any **numeric**
payload (or Discard). A named win such as Ron retains higher priority.

The UI now distinguishes raw numeric values from named actions in its
"Live game hint" panel, because those fields aren't always text hints
outside playable turns. This fix does not guess what `22` means, and does
not enable any round control, End Match, match entry, or requeue action.

Build / live testing with Dalamud is still required; compilation is not
available in this environment.

## v0.8.18 — State-2 `× 1` pause while tiles are dealt

The 2026-11-13 14:20:35 export showed the controller recognized a round
transition at 14:20:19, then **paused at 14:20:25** on `× 1` in state `2`.
This was the controller's unsupported-hint grace, not a confirmed timeout
from FFXIV. v0.8.17 only treated `× 0` as a known state-2 placeholder.

State `2` is now *always* passive: none of its text values are considered
playable recommendations. After a recognized round boundary, state `27` is
also passive throughout dealing, not just when its raw value is exactly 302.
A fresh, supported, stable in-round hint resumes the controller and resets
per-round Riichi action tracking. Result screens continue to require a
manual Next click or the game's own timer. No automatic Next, End Match,
queue/requeue or match entry has been added.

If an unknown **active** response menu appears during a playable round, the
existing pause-and-snapshot protection remains. This patch targets loading,
not the separate issue of two Ron callbacks previously observed in a log.

Static source and ZIP checks were performed. The patch has NOT been compiled
with the Dalamud SDK or tested in FFXIV here.
