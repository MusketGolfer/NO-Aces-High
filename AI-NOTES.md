# NO Aces High: AI notes on flying any airframe

How an ace works out how to fly a jet it has never seen. Use these notes when changing `AirframeProfiles.cs`,
`PostStall.cs` (hunt and manoeuvres) or `FlightTest.cs` (the survey). Nothing in the AI is keyed to a specific
aircraft name: every decision comes from numbers read off the aircraft, or measured by flying it.

## 1. Where the numbers come from

| Source | When | What |
|---|---|---|
| Static stats | Always, from the live aircraft | mass (`definition.mass`), wing area (sum of `AeroPart.WingArea`), thrust (`Aircraft.GetMaxThrust`; propellers: shaft power x 0.8 / corner speed), corner and top speed, g limit (`AircraftParameters`), fly-by-wire AoA limit, g limit and roll rate (`ControlsFilter.FlyByWire.alphaLimiter / gLimitPositive / maxRollAngularVel`), thrust vectoring, relaxed-stability canards (`RelaxedStabilityController`), guns and the longest air-to-air missile |
| Survey | After running `-noahSurvey` | level acceleration from corner speed, sustained level turn rate at the fly-by-wire AoA limit, speed bleed in that turn, peak roll rate, low-speed pitch rate, highest AoA reached in the turn (departure check) |
| Estimate | Any airframe not in the survey store | sustained turn, bleed, acceleration and roll predicted from the static stats with the coefficients fitted to the surveyed fleet (section 3) |

Measured beats estimated. The store is `BepInEx/config/NOAcesHigh/airframe-profiles.json`; a full survey
replaces it and refits the coefficients, a filtered one (`-noahOnly "Name,Name"`) merges into it and keeps the
fleet-wide numbers. After installing aircraft mods, a filtered survey of just the new jets is enough.

```powershell
& "E:\SteamLibrary\steamapps\common\Nuclear Option\NuclearOption.exe" -autoHost -mission "Free Flight" -socket Offline -state SinglePlayer -noahRecord -noahSurvey -noahOnly "Gandiva" -noahOut "<folder>"
```

## 2. What the survey taught (2026-11-24, 19 airframes)

1. **The fly-by-wire AoA limit is the single best predictor of a jet's sustained turn.** Every jet's steady turn
   settles at its FBW alpha; jets limited to 10 deg (Cricket, Compass, Darkreach, Medusa) turn 11-14 deg/s, the 27 deg
   fighters 13-17. Wing loading is the next factor (heavier per square metre turns worse), thrust-to-weight barely
   matters for the *sustained* turn because these turns do not bleed much at the limit. Thrust vectoring adds nothing
   to the sustained turn; it is what lets a jet *exceed* the limit (instantaneous turn, post-stall, nose pointing).
2. **Turns at the limit barely bleed speed** (bleed -2.5 to +3 m/s per second for nearly everything). The heavy
   exception is the Centurion (12 m/s per second: a bomber at 3.5 g). So "energy fighter" is not about bleed in a
   sustained turn; it is about a jet that accelerates well but turns below the fleet. Bleed matters when the ace
   pulls *past* the limit, which is what the hunt's AoA governor manages.
3. **Acceleration tracks thrust-to-weight almost linearly**: accel = 4.78 T/W - 0.87 m/s per second at corner speed.
4. **Roll rate falls with wing loading** (230 - 0.56 x wing loading) but the spread is large: flying-wing and
   heavy designs (Eclipse 56, Trident 31, Centurion 56 deg/s) roll slowly whatever the formula says. Measured roll
   rate is worth trusting over the estimate.
5. **Relaxed-stability jets depart under a raw pull.** The FS-41 Eclipse's AoA ran from 21 to 97 deg with full
   nose-down elevator when pulled steadily without the game's stability augmentation. The ace's control override
   writes the control surfaces after the game's fly-by-wire, so it bypasses that augmentation. Such jets must fight
   inside their AoA limit. The survey flags them (`Departs`: turn-test AoA more than 15 deg past the limit); for
   unsurveyed jets the presence of a `RelaxedStabilityController` component raises the same caution.

## 3. Estimator (for unsurveyed jets)

Fitted to 17 airframes (the ultralight and placeholder definitions excluded):

```
sustained turn (deg/s) = 13.60 + 0.13 T/W - 0.0350 wing loading (kg/m2) + 0.213 FBW alpha (deg) + 0.20 if vectoring   (within ~3 deg/s)
turn bleed (m/s/s)     = 3.24 - 1.52 T/W
acceleration (m/s/s)   = 4.78 T/W - 0.87
roll rate (deg/s)      = 230 - 0.56 wing loading
low-speed pitch        = 100 deg/s with vectoring, else 15 + 30 T/W (20-60)
```

The coefficients are only meant to *rank* jets sensibly; tactics thresholds are relative to the fleet (below), so
an estimate that is a little off rarely changes the style.

## 4. From numbers to tactics (`AirframeProfiles.Derive`)

Fleet references, set by the last full survey: turn-fighter threshold = median sustained turn x 1.1 (14.5 deg/s),
fleet acceleration = median acceleration (6.5 m/s per second).

| Style | Rule (first match) | Fights by | Hunt pull scale | Energy floor (x corner) | Extends after a pass |
|---|---|---|---|---|---|
| striker | anti-air role < 0.25, no guns, no air-to-air missile | only when forced, keeps speed | 0.6 | 0.75 | yes |
| turn | turn >= threshold, or vectoring and turn >= 0.85 threshold | commits to the turning fight and guns | 1.0 | 0.5 | no |
| interceptor | top speed > 2.6 x corner, turn < 0.9 threshold, accel >= 0.9 fleet | missile slashes, extend, come back | 0.7 | 0.75 | yes |
| heavy | turn < 0.9 threshold, accel < 0.6 fleet | missile shots and slashing passes, never a turning fight | 0.75 | 0.72 | yes |
| energy | turn < 0.9 threshold (or bleed/accel > 1.4) | boom and zoom, never slow | 0.85 | 0.68 | yes |
| balanced | everything else | turns when it pays, holds energy otherwise | 0.95 | 0.55 | no |

Then, independent of style:

- **AoA governor (hunt).** The hunt pulls to the FBW limit + 4 deg; + 15 deg with vectoring (the jet can point
  past its lift); + 6 more for a snap shot under 1.2 km and 30 deg off. Departure-prone jets: limit - 2 (snap: + 2).
  Past the cap the pull fades out over 6 deg.
- **Missile breaks** are flown by every tier. Slow rollers start them earlier: under 100 deg/s by 0.6 s, under
  150 by 0.3 s. Greener pilots see the missile later (tier 1: 0.5 s, tier 2: 0.2 s). The Cobra break (missile from
  behind) needs a post-stall tier and an airframe that suits it; everything else breaks conventionally. A
  non-vectoring jet rolling under 90 deg/s leaves the missile to the game's own evasion.
- **Post-stall showpieces** suit an airframe with vectoring, or with low-speed pitch >= 45 deg/s and T/W >= 0.8.
  Never for departure-prone jets.
- **Departure-prone jets** also get 0.85 x hunt pull and an energy floor of at least 0.65.

## 5. Reading a new jet by hand

If a modded aircraft behaves badly and you want to know why before surveying it:

1. FBW alpha under 12 deg: a slow turner no matter how much thrust; expect "energy" or "heavy".
2. T/W under 0.6 and alpha under 15: "heavy"; it should never be asked to turn with a fighter.
3. Vectoring: the hunt will pull 15 deg past the limit; check the jet does not stall-spin there (survey it).
4. Canards with a `RelaxedStabilityController`: survey it; if it departs, it is flown inside the envelope.
5. Top speed well over 2.6 x corner speed with a poor turn: an interceptor; it should slash and extend.

## 6. Survey method (`FlightTest.cs`), and the traps found building it

- Each jet flies alone at 5 km, its AI pilot step and autopilot switched off (no g damage from the speed resets,
  no AI inputs), flown by the same body-rate controller the aces use, applied every physics step.
- **Keep the jet in jointed (complex) physics.** Locally simulated aircraft spawn jointed; merging them into one
  body mid-flight left a wrong inertia tensor (800 deg/s rolls). Speed resets set the velocity of *every part's*
  rigidbody, so no joint is loaded by the reset.
- **The turn is AoA-governed, not rate-commanded.** A raw full pull departs most jets. The test pulls with
  load-factor feed-forward (g x 9.81 / speed) plus AoA error to the FBW limit, damped by AoA rate, and banks to hold
  altitude (78 deg, steeper while climbing, shallower while sinking).
- Peak g ignores the first 0.5 s after a reset (the reset itself reads as hundreds of g).
- Acceleration is measured from corner speed (propeller aircraft decelerate from 200 m/s).
- Placeholder definitions ("???") and ultralights (corner under 60 m/s) are kept out of the fit.

## 7. Systems and judgement beyond the stick (`AceTactics.cs`, `PostStall.cs`, `FormationFlying.cs`)

All of these work from what the aircraft carries, so modded jets get them without tuning.

- **Ripostes (opportunistic shots).** Checked every physics step for every fighting ace of tier 2 and up, whatever
  it is flying (post-stall manoeuvre, missile break, notch, recovery, hard turn or the stock AI). The moment a
  hostile is within 12 deg of the nose (tier 2: 8 deg), between the missile's minimum range (at least 350 m) and
  5 km (or 70% of its range), with nothing friendly in line: fire, one every 2.5 s (tier 2: 4 s). Guns inside 1 km
  and 3 deg. The hunt's attack phase does its own shooting. This is what turns a bell or a Cobra that brings the
  bandit across the nose into a kill, without the shot belonging to any one manoeuvre.
- **Over-the-shoulder shots.** Extending (hunt phase 2) or retreating, a radar missile (minimum radar return > 0;
  an infrared seeker cannot see behind) goes back at a pursuer in the rear hemisphere 1.5 km to half the missile's
  range that is not falling away. One every 8 s.
- **Counter-missile shots.** Only weapons the game rates anti-missile (`effectiveness.antiMissile` >= 0.2), the
  inbound within 35 deg of the nose and 3-12 s out. Threat (what the shot saves): 0.35, + 0.3 radar-guided, + 0.15
  slow, + 0.2 more than one inbound, + 0.2 out of flares, + 0.15 its launcher ahead too, - 0.25 infrared with flares in
  hand. Cost 0.6 / missiles left. Fire when threat - cost >= 0.35; last missile only at threat >= 0.85; tier 2 from
  0.7 threat; never tier 1.
- **Radar missiles.** Self-protection jammers (`RadarJammer`) fire every frame with the capacitor kept full: ECM is
  subtracted from every radar return, and an active seeker that has not acquired yet cannot acquire inside 5 km
  against more than 2 ECM. A radar missile 2.2-10 s out is beamed first (the seeker's Doppler gain is zero square to
  its line), descending toward ground clutter with the jammer running; under 2 s the break takes over.
- **Going dark.** The flight's radars and jammers go off between 60 and 12 km while their side tracks the target and
  nothing has been fired at them (at least 10 s dark, 15 s lit between changes); they light up for a lost track,
  inside 12 km, or a missile in the air.
- **Retreat.** Winchester (no air-to-air missile, and no gun or under 10% of its rounds) or fuel under
  `RetreatFuelShare`: the ace says so and runs for the map edge away from its target, keeping power, ECM and the
  over-the-shoulder shot. When every fighting ace has gone, the encounter ends as a withdrawal.
- **Close formation.** Within 2.5 km of its slot a wingman flies it (and the rejoin) itself: the slot's velocity (the lead's, plus
  the slot's swing round the lead as it turns), a correction of 0.3 x the error (at most 25 m/s near the slot, 70 m/s far out), the nose offset by
  its own AoA, the lead's bank, and 30 m minimum separation. The lead holds at most 35 deg of bank and half pull
  and 90% of cruise throttle while the flight is in formation.

## 8. Missile jousts (diagnosed 2026-11-24)

Head-on missile fights were the aces' main cause of death. What the diagnostics showed, and what changed:

- **Every loss was a radar missile hit** (ARH within 11-43 m, SARH within 40-55 m, proximity fuze); fire, lost wings
  and the ejection follow a few seconds later, which is why the recorded time of loss lags the hit.
- **Loadouts.** The picker accepted any loadout with `antiAir > 0`, but the game rates bombs above zero against air
  (PAB-250 0.20, GPO-500 0.40), so aces flew with bombs and 2 air-to-air missiles against the bandit's 4. Loadouts
  are now scored on real air-to-air missiles (missile, not bomb, antiAir >= 0.5 and > antiSurface), weighted toward
  range; the pick is among those within 85% of the best.
- **The notch bled the jet dry.** It pulled up to 2.2 rad/s with no AoA limit: 619 to 345 km/h without reaching the
  beam, leaving nothing for the break. It now holds the pull under the fly-by-wire AoA limit, eases off below 90% of
  corner speed, dives to buy speed back, and beams the launcher (not the missile) for SARH.
- **Re-attacking missiles went unanswered.** A missile that overshot and came back round was blocked by a 5 s
  per-missile cooldown; the same missile is now answered again after 2.5 s.
- **Low defences.** The notch dives toward 250 m above ground (the seeker's ground-clutter term grows as the target
  gets low and far below it); breaks and notches may be flown down to 150-300 m, and a low break never pulls
  toward the ground.
- **Countermeasures.** The vanilla game has only IR flares and the radar jammer (chaff comes only from a mod, so aces
  never use it). Jamming pods are turned on an incoming radar missile, whose seeker counts jamming aimed at it.
- **Joust-averse airframes crank.** Energy, heavy and strike styles, and non-vectoring jets limited to 15 deg AoA or less
  (`AirframeProfile.JoustAverse`), meeting a bandit nose-on at 4.5-16 km take a radar-missile shot if they have one in
  reach, then turn 55 deg off its bearing and drop a little (`PostStall.TryCrank`). This cuts closure and gives an offset
  merge. Inside 60% of the bandit's longest missile range (10 km at most) with it still nose-on, the ace drags (tail on,
  full power, a little lower) until the range opens 2 km, the bandit turns off or 12 s pass, then pumps back round,
  takes a fresh long shot and cranks again. What made it work was shooting first: radar missiles are recognised by the
  seeker on the weapon's prefab (`AceSpawner.IsRadarMissile`), because the game's ARH missiles carry no minimum radar
  return in their target requirements, so the old check never let an ace fire one (it also blocked the over-the-shoulder
  shot). Vagrant jousts: 1 of 9 survived before; crank alone 3 of 5; drag-and-pump without the shot 2 of 6; with the
  shot 6 of 6, 3 bandits down.

Result (54 jousts against a stock-AI bandit in the same jet, tier 3): Shrike 8/9, Revoker 7/9, Ifrit 7/9, Icarus II
6/9, Ternion 5/9, Vagrant 1/9; about 63% survival overall, against about 50% before. What is left is tactical, not
defensive: weaker airframes should not take the joust at all (crank or drag at long range, shoot first), and a
missile that has acquired at 5 km is rarely defeated in the last 2 km by flares or the jammer alone.

## 9. Squadron health bars (`SquadronHealth.cs`)

The game keeps no single health value for an aircraft: each part has hit points out of 100 (what its own damage display
reads) and detaches when they run out. An ace's health is the mass-weighted condition of its parts (detached = 0;
destroyed, pilot dead or ejected = 0). The bars sit along the bottom of the screen in the HUD Danger colour (Accessibility
colour theme): the lead's under the squadron name, one more per wingman (a four-ship shows four); aces that retreated show
dimmed. Settings: `SquadronHealthBars`, `SquadronHealthBarsY`.

## 10. Breaking formation without collisions (`FormationFlying.Split`, `FormationFlying.Deconflict`)

- For 3 s after a flight breaks formation, each wingman flies a split heading: 30 deg off the lead's heading to its
  slot's side (alternating for slots straight behind), stacked slots descending, others climbing or descending by
  turn. A missile within 2.5 km overrides the split.
- Always on for every ace: a squadmate within 150 m that will reach a 25 m miss inside 2.5 s (or inside 50 m and
  closing faster than 3 m/s) is turned away from, toward vertical separation, overriding every other manoeuvre.
- Test (8 four-ship breaks into a fight, Revoker and Shrike): no collisions; the closest two aces came was 32 m.

## 11. Airframes tearing themselves apart (`Breakups.cs`, `AceFlight.SafeG`)

- **In fights.** Some airframes break well below the 14 g pull ceiling: measured Shrike break points ranged from about
  12 g to 22 g between aircraft, and damage weakens joints further (the game scales joint strength with part hit
  points). The stress governor reacted only after a joint passed 75% of its load, and a rate command cannot be
  trusted to stay gentle at speed (3 rad/s at 250 m/s asks for 75 g). Now `ApplyRates` caps the commanded pitch rate
  at `SafeG x 9.81 / speed` while AoA is under 25 deg (post-stall manoeuvres are unaffected). `SafeG` is the lower of
  PostStallMaxG and 70% of the measured break g, easing further while a joint is loaded past 60%. The stock-AI path's
  lifted fly-by-wire limit uses the same 70% margin. Test: 16 Shrike and Su-30 dogfights, no breakups, 9 kills.
- **Around the intro.** The game simulates aircraft as jointed parts within 10 km of the camera and as one body
  beyond, switching as the camera moves; the intro's cut moves the camera tens of km at once, so the player's jet
  and the aces switched mode in bulk. No aircraft changes physics mode while the camera is cut away or for 3 s after.
- `Breakup:` lines in the log give g, speed, joint stress, physics mode, the part's hit points (very negative = shot
  off, not overstressed) and the manoeuvre, for every part an ace or the player loses.

## 12. Holding fire through the intro (`IntroGate.cs`)

From the moment an encounter's intro is queued (`IntroDirector.Begin`: the intro music, the wait for a safe moment to cut
away, the camera shots and the title card) until `IntroDirector.Active` goes false, `WeaponManager.Fire` and
`FireGuns` do nothing for aces (and retreating aces). That covers the stock AI's shots and all of ours (snap shots,
ripostes, Parthian shots, counter-missile shots), since they all go through the weapon manager. Jamming of the target
and counter-missile shots are also skipped while the gate holds. Flying, flares and evasion are untouched. The first
held shot of each intro is logged ("Intro gate: ... held fire"). Config: `AcesHoldFireDuringIntro` (on).

## 13. The UIPlus layout editor (`HudLayoutBridge.cs`)

UIPlus's "Customize HUD Layout" works on fixed internal tables of its own 30 elements, with no API, so the threat
gauge and health bars join it from outside with four Harmony hooks on `UIPlus.Helpers.HudLayoutEditor`:
- `Update` prefix: while the editor is open, a click on one of our boxes or handles (or our settings window) is ours,
  and the original is skipped for that frame; a click anywhere else goes to UIPlus and drops our selection. A click on
  ours clears UIPlus's `_focus` so only one item shows handles. UIPlus's Reset button (`_pressedButton` 2 released
  over `ButtonAt` 2) resets ours too.
- `Draw` postfix: sample gauge and sample four-ship drawn with the IMGUI fallbacks at the working layout, outlines
  (green, gold when selected), a gold size square and a turn handle, and a settings window (size, rotation, width for
  the bars, reset buttons). The real UGUI items are hidden while the editor is open, so there is no double image.
- `Apply` postfix saves our working values to this mod's config; `Close` postfix ends editing (Cancel just discards).
Placement: `HudPlace` (X, Y screen fraction; Scale; Rotation in degrees anticlockwise; Width for the bars). The gauge
pivots on its centre; the bars on their bottom centre, with anchors spanning Width / Scale so the scaled strip is
Width of the screen wide. The bars' size square is the top-right corner: right for wider, up for thicker.
Record-mode check: `-noahHud -noahLayoutTest` opens the editor at 40 s, moves both items, applies at 48 s, and puts
the config back at the end.

## 14. Formation through the intro, safe spawn points, debug buttons

- While `IntroGate.Holding`, range alone no longer breaks the formation (only a missile warning or losing the lead),
  the stock lock-only evade is skipped for every ace, and ripostes are off. Before this, a squadron spawned inside
  `FormationBreakKm` (record mode spawns at 20 km) broke formation at once and the stock AI jinked away from the
  player's radar lock on camera.
- `AceSpawner.SafeSpawn` (normal spawns only, not set test distances): the lead's spawn point must be 30 km from the
  hunted side's ships, 15 km from its anti-air vehicles and emplacements (a weapon station with antiAir >= 0.3), 6 km
  from its aircraft. Tries the planned bearing, then +-20 deg steps to 180, at 1x, 1.25x and 0.8x the distance; else
  the least covered point. Logged as "Spawn point moved ...".
- Debug/menu Spawn, Replay intro and forced manoeuvres go through `DebugQueue`: the F1 window closes and the action
  fires 2 s later.
- squadron.json `naming`, `color`, `emblemShape`, `introStyle`, `introShots`, `formation`: `"random"` = any choice,
  ignoring the config; a comma list = one entry picked per flight (`Variants.Pick` / `Entries` / `IsRandom` / `Any`).
- Record mode: write `-noahOut` to E: (C: filled up with frames once); `-noahHud` honours `-noahNoVideo`; a failed frame
  write switches the run to no-video instead of killing the coroutine.

## 15. Pre-release pass (voice, settings, stats, streaming music)

- `VoiceLines.cs`: squadron.json `"voice"` block, same syntax as `"music"` (shared `"file"` + ranges, or files), one list
  per line key in the order of that key's text lines. Clips stream from disk and play on our own AudioSource (radio
  filter optional) when the subtitle shows; matched by text (placeholders as wildcards), so clients with the same
  squadron hear them too. Lines wait for a playing recording (up to 6 s). Settings in 8. Sound; `RadioSubtitles` in 7. HUD.
- `Music.cs` now streams files (`streamAudio = true`) and plays sections by seeking the game's music source
  (`fadeSource.time = Start` right after `CrossFadeMusic`) and holding it in the section from `Tick` (loops jump back,
  cut one-shots fade). Decoding a 35 MB mp3 for sections had frozen the game for ~6 s at launch.
- Spawning 8 Revokers costs ~0.2 s in one frame (SpawnAircraft ~80 ms, loadouts ~20 ms); left as is.
- `SettingsMenu.cs`: sections 0-9, order and advanced flags applied after binding (`Arrange`), values carried over
  from old section names (orphaned entries), presets Rookie/Veteran/Ace/Nightmare (Ace = defaults), profiles in
  `config/NOAcesHigh/Profiles/*.txt`, "no aces this mission", AcesInPvp (off), MaxAcesPerSquadron, a folded
  "9. Experimental and testing" panel (its entries are hidden and drawn by our own drawer, with the debug tools).
- `Stats.cs`: record in `config/NOAcesHigh/stats.json` (host / single player only); kill credit = most damage
  (`Unit.damageCredit`).
- `Patches.Failed` feeds the "some features are off" notice. `Encounters.ResetForMission` is the single clean-up point.
- The install step no longer overwrites installed squadron files (it wiped a user's edits once); only README.md is refreshed.
