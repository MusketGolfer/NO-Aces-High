<p align="center"><img src="docs/logo.svg" width="220" alt="NO Aces High logo"></p>

# NO Aces High

Enemy ace squadrons for **Nuclear Option**, in the spirit of Ace Combat and Project Wingman. Make enough noise and the
enemy sends its best after you: a named squadron with an emblem, a title card and a camera fly-by, radio chatter,
its own music, and pilots who fly like they mean it.

## What it does

- **Aces come for you.** Every kill your side makes raises a threat meter. Past the threshold, the enemy may launch
  an ace squadron at the pilot who has done the most damage. A gauge on your HUD shows the threat building.
- **An introduction.** The flight comes in in formation. The camera cuts to them in slow motion, and a title card
  names the squadron, its unit and its lead pilot. Then the radio crackles.
- **They fly differently.** Aces are graded in three tiers. The best of them pull post-stall manoeuvres (Cobra,
  Kulbit, J-turn) and take shots mid-manoeuvre. They also turn missiles on the beam, jam you, go dark on the way
  in, shoot down incoming missiles, and fire over the shoulder as they leave. Every stock fixed-wing jet has been
  surveyed and given tactics that suit it. Modded jets are read from their stats.
- **Teamwork.** Wingmen hold formation on the way in, break when you engage, bracket and cover each other, and
  call it a day when they run out of missiles or fuel.
- **Health bars.** A bar per ace along the bottom of the screen, in your HUD's danger colour.
- **Your own squadrons.** A folder, a `squadron.json` and optionally an emblem, music and recorded voice lines.
  Up to 16 pilots, mixed aircraft with backups, loadouts built station by station, `random` styles. See the
  [squadron guide](Squadrons/README.md). Squadrons other players have made, and how to share yours, are in
  [Community-Squadrons](Community-Squadrons/README.md).
- **Loadouts.** A loadout editor in the settings: view every jet's loadouts, build your own presets, and choose what
  random aces carry.
- **A record.** The settings page keeps your score: squadrons faced and beaten, aces you shot down, your fastest win,
  a kill marking (its emblem) for every squadron you have beaten, and ten medals to earn.
- **Squadron Down.** Wipe out a squadron and its emblem is stamped DESTROYED, with your time, how many of its aces
  were yours, and any medal you just earned.

## Requirements

| | |
| --- | --- |
| [BepInEx 5](https://github.com/BepInEx/BepInEx) (5.4.x) | Required. |
| **BepInEx Configuration Manager** | Strongly recommended: it is the settings page (F1). |
| **UI Plus** | Recommended. The threat gauge, health bars and subtitles sit inside its HUD, and can be placed with its *Customize HUD Layout*. Without it they are drawn plainly over the screen. |
| **ArcadeCam** | Optional. When installed, intros wait while its weapon camera is showing. |

## Installing

**With NOMM** (the Nuclear Option Mod Manager): search for *NO Aces High* and install. NOMM sets up BepInEx and the
Configuration Manager for you.

**By hand:**

1. Install BepInEx 5 (and UI Plus, ArcadeCam and the Configuration Manager if you want them).
2. Download `NO-Aces-High-<version>.zip` from the [Releases page](https://github.com/MusketGolfer/NO-Aces-High/releases) and extract it into your `Nuclear Option` game folder (the one holding `NuclearOption.exe`). It puts the mod in `BepInEx/plugins/NOAcesHigh/`. When updating, your own squadrons and settings in `BepInEx/config/NOAcesHigh/` are not touched.
3. Start the game and fly. Settings are under **F1 → NO Aces High**.

(`NOAcesHigh-nomm.zip` on each release is the same mod packed for NOMM, with the files at the top of the archive.)

Your own squadrons, music and settings profiles live in `BepInEx/config/NOAcesHigh/`. That folder is created on
first run, with a copy of the squadron guide and a template.

## Settings at a glance

The settings page (F1) opens with **NO Aces High**, which holds:

- **Your record** against the aces.
- **Difficulty presets**: Rookie, Veteran, Ace (the intended experience) and Nightmare. One click sets the
  difficulty and tactics settings. Change anything afterwards and it shows as Custom.
- **Saved settings**: save your whole setup under a name and load it back later.
- **No aces for the rest of this mission**: a one-mission off switch.

The sections below it:

| Section | Holds |
| --- | --- |
| 1. Encounters | How often aces come, how far out they appear, the size cap, multiplayer and PvP. |
| 2. Difficulty | Tiers, skill, post-stall manoeuvres, the hunt, flares. |
| 3. Tactics | Holding fire through the intro, missile defence, jamming, going dark, retreating. |
| 4. Squadrons | Your installed squadrons (spawn, take out of rotation) and the styles for random ones. |
| 5. Airframes | Which jets random aces may fly and which weapons they may carry, plus the **loadout editor**: every jet's standard loadouts, and your own presets built station by station. Tick "Random aces use this" on a preset and random aces flying that jet carry it. Squadron files can name a preset. |
| 6. Intro | The cinematic and title card. |
| 7. HUD | Threat gauge, health bars, subtitles. To move, resize or turn the gauge and bars, use UI Plus's **Customize HUD Layout**. |
| 8. Sound | Music and recorded voice lines. |
| 9. Experimental and testing | Folded away: unfinished features and testing buttons. |

## Multiplayer

The host runs the encounters. Every player should have the mod. Each player sees the intro on their own screen and
hears the radio as subtitles. Players without the mod still see the radio lines in the message feed. Aces are off by
default when humans fly on more than one side (PvP); see *1. Encounters → AcesInPvp*.

Multiplayer has had much less testing than single player. Please report anything odd.

## Compatibility

Run together with these mods during development. "Loads fine" means no errors or conflicts were seen, not that every combination was tested in depth.

| Mod | Notes |
| --- | --- |
| UI Plus | Recommended. Its **MatchMissileIndicators** setting (on by default) changes NO Missile Indicators style and hides its indicators while UI Pluss missile arcs are showing. Turn it off if you want Missile Indicators own style. |
| ArcadeCam | Optional. |
| NO Missile Indicators | Works. See the UI Plus note above. |
| KR-67 Ifrit FCS, Su-30VXN FCS, Solar Su-30, Aryx F-99 Shrike, Aryx FS-41 Eclipse, F-204 Icarus II, PB-22 Trident, MRB-100 Centurion, AH-31 Kukri, UL-2 Airtrike | Modded aircraft: aces may fly the fixed-wing jets (tick them in *5. Airframes*). Tactics are worked out from each jet's stats. |
| NOX, Nuclear Option Playable Ground Vehicles | Loads fine. |
| WT Mouse Aim, Mouse Aim Tweaks, Steady HUD Markers, SimpleWSO, WSOYappinator | Loads fine. |
| Global Sound Replacer, GAS sound mods, Equalizer Mod | Works. Ace music plays through the game's music player. |
| Nuclear Replay Recorder / Player | Loads fine. |
| Custom Maps, NOMapLoader, TerrainLab, Weather Controller, Nuclear Rain | Loads fine. |
| Blueprinter, LoadoutPresets, NO Mod Framework, NOSMR, Fuel Burn Endurance Timer, AlarmSystemMod, NO Grid Letters, NOFrameLimiter | Loads fine. |

## Troubleshooting

- **Nothing happens.** Aces only come once your side has built up threat (watch the gauge), at most once per
  mission by default. To test, use *4. Squadrons → Spawn*: the settings window closes and they launch 2 s later.
- **My squadron doesn't show up.** The squadron list in *4. Squadrons* shows each file's problems, with line numbers
  and suggestions.
- **"Some features are off".** The game has updated in a way this version does not know. The features named at the
  top of the settings are off until the mod is updated; everything else keeps working.
- **Logs.** `BepInEx/LogOutput.log`, lines tagged `NO Aces High`.

## Building from source

The project targets netstandard2.1 and references the game's and BepInEx's DLLs from the game folder.

```
dotnet build -c Release -p:Install=true
```

This builds the mod and installs it into the game's `BepInEx/plugins/NOAcesHigh`, adding any shipped squadrons that
are missing (it never overwrites one already installed). `AI-NOTES.md` explains how the aces fly and why.

## Credits

Made by MusketGolfer, with the code written alongside Claude (Anthropic). Released under the [MIT license](LICENSE).

Ace Combat and Project Wingman, which inspired this mod, belong to their owners; this is an unofficial fan project.
