# Making your own ace squadron

A squadron is one folder with a `squadron.json` in it, plus any pictures and music you want. Every field is optional:
anything you leave out is rolled at random when the squadron launches. You can start with just a name and add the
rest as you go.

## Quick start (five minutes)

1. Open the folder `BepInEx\config\NOAcesHigh\Squadrons` in your Nuclear Option folder. (In game: press **F1**, open
   **NO Aces High → 6. Squadrons**, click **Open folder**.)
2. Copy the `_Template` folder and rename the copy, for example to `Crimson-Talons`. Folders whose names start with `_`
   are ignored, which is how the template stays out of the game.
3. Open `Crimson-Talons\squadron.json` in a text editor (Notepad works) and change the values. Delete any line you do
   not want; leaving a field out means "pick one for me".
4. In game, click **Reload files** in the squadron menu. Your squadron appears in the list.
   - Problems are listed under it in **yellow**, with a suggestion when it looks like a typo.
   - A file the mod could not read at all is listed in **red** at the top, with the line number.
5. Click **Spawn** next to it to test it straight away (you need to be flying).

Squadrons with **In rotation** ticked can also turn up in random encounters (`HandmadeSquadronChance` decides how
often a handmade one is picked over a randomly generated one).

## A full example

```json
{
  "squadron": "Crimson Talons",
  "faction": "PALA",
  "title": "Wings of the Liberation",
  "unit": "7th Unit",
  "designation": "PALA 2nd Air Regiment",
  "naming": "regiment",
  "color": "#C8102E",
  "emblem": "emblem.png",
  "introStyle": "pw-boss",
  "introShots": "headon",
  "formation": "fingerfour",
  "tier": 3,
  "aircraft": ["Revoker", "Ifrit"],
  "members": [
    { "callsign": "Talon 1", "name": "Mira Vance", "lead": true },
    { "callsign": "Talon 2" },
    { "callsign": "Talon 3" },
    { "callsign": "Talon 4" }
  ],
  "lines": {
    "intro": ["{country} sends its regards, {target}."],
    "wingmanDown": ["{callsign}! Talons, close up!"]
  },
  "music": {
    "intro": "music/intro.ogg",
    "loop": "music/theme.ogg",
    "outro": "music/defeat.ogg"
  }
}
```

JSON is strict about punctuation: every value is in double quotes (except numbers, `true` and `false`), items are
separated by commas, and there is no comma after the last item in a list or block. If the menu shows the file in red,
the message gives the line where the mistake is (often a missing or extra comma just before it).

## Every field

| Field | What it does | Values |
| --- | --- | --- |
| `squadron` | The squadron's name. | Any text. Left out: a name is generated. |
| `faction` | Which side flies it. | `"BDF"` (Boscali Defense Force) or `"PALA"` (Primeva Armed Liberation Front). Left out: either side. |
| `title` | A nickname shown on the title card. | Any text, e.g. `"The Shade at Noon"`. |
| `unit` | Unit number shown after the name. | e.g. `"41st Unit"`. |
| `designation` | Parent organisation on the title card. | e.g. `"BDF 3rd Tactical Fighter Wing"`. |
| `naming` | The naming tradition used to fill in anything you left out. | `classic`, `team`, `unit`, `regiment`, `navy`, or `random` |
| `color` | Squadron colour (generated emblem, title card accents). | A hex code, e.g. `"#C9B26B"`, or `random` (a new colour every time they fly). |
| `emblem` | Your emblem artwork. | A `.png` or `.jpg` in the squadron folder, e.g. `"emblem.png"`; or a folder of numbered PNG frames for an animated emblem, e.g. `"emblem"` holding `0001.png`, `0002.png`... (WebP is not supported: save as PNG.) Backups work like aircraft: `"mine.png, fallback.png"` uses the first file that is there. |
| `emblemFps` | Frame rate of an animated emblem. | A number, default 12. |
| `emblemShape` | Shape of the generated emblem when you have no artwork. | `roundel`, `shield`, `pennant`, `arrowhead`, `wings`, `crescent`, or `random` |
| `introStyle` | The title card. | `pw-boss`, `stamp`, `typewriter`, `caution`, `unit`, or `random` |
| `introShots` | The intro camera's shot list. | `classic`, `tails`, `headon`, `cockpit`, `lead`, `nose`, `distant`, or `random` |
| `formation` | Formation on the way in (and when leaving). | `echelonright`, `echelonleft`, `vic`, `fingerfour`, `lineabreast`, `trail`, `diamond`, `box`, or `random` |
| `tier` | How good they are. | `1`, `2` or `3` (3 flies post-stall manoeuvres). |
| `aircraft` | The jet they fly, as a priority list: the first one installed is used, so later names are backups (`["Su-30VXN Gandiva", "Revoker"]` flies the modded jet when it is installed, the Revoker when it is not). | A list of names: the short name is enough (`"Revoker"`, `"Ifrit"`), the full name (`"FS-12 Revoker"`) also works, and so do modded aircraft names. Left out: any allowed airframe. If none is installed, the squadron does not fly. |
| `loadout` | Their weapons. | A loadout name (one of the jet's standard loadouts as the game's loadout menu shows it, or a preset you saved in the F1 loadout editor), or a loadout built station by station. See [Loadouts](#loadouts). Left out: the best air-to-air loadout. |
| `livery` | Their paint. | `{ "builtin": 2 }` (by number) or `{ "builtin": "name" }`, `{ "appdata": "folder" }` for a livery in your liveries folder, `{ "workshop": 123456 }` for a Steam Workshop livery. Left out: a random one for their side. |
| `members` | The pilots, 1 to 16 (more than 4 is fine), lead first. | See below. Left out: 2 to 4 generated pilots. |
| `lines` | What they say on the radio. | See below. |
| `music` | Their own music. | See below. |
| `voice` | Recorded radio lines. | See below. |

**Left out or `random`?** A field you leave out follows the mod's settings (F1, NO Aces High), so you can set a
house style there for every squadron that does not choose. `"random"` ignores those settings and picks afresh among
every choice each time the squadron flies (for `introShots`, picked to suit how many of them are still flying).

**Several choices:** `naming`, `color`, `emblemShape`, `introStyle`, `introShots` and `formation` also take a list
separated by commas, and one entry is picked at random each time the squadron flies:
`"introShots": "headon, lead, distant"`, `"color": "#C9B26B, #8A1C1C"`. An entry may be `random`. A misspelt
entry is reported in the squadron menu; if it is picked, that flight uses your settings for that field instead.

### Members

```json
"members": [
  { "callsign": "Vesper 22", "name": "Arkady Ilyin", "lead": true },
  { "callsign": "Vesper 99", "skill": 2.5, "aircraft": ["Ifrit"] }
]
```

- Up to 16 members fly; any after the sixteenth are ignored. Big flights are a spectacle but a much harder fight. For a lone ace, keep just one entry:
  `"members": [ { "callsign": "Phantom", "name": "The One", "lead": true } ]` (a lone ace does not fly a formation).

- `callsign` is what shows on the radio.
- `name` is the pilot's name (the title card can feature the lead).
- `lead` marks the leader. Exactly one member should have it.
- `skill` overrides that pilot's skill. Leave it out to use the tier's value (1.5, 2 or 2.5).
- `aircraft` gives that pilot its own jet, as a priority list like the squadron's (backups after the first). Leave it
  out to fly the squadron's. A mixed flight is fine: the formation lead flies at cruise so slower jets keep up.
- `loadout` gives that pilot its own weapons, written like the squadron's [`loadout`](#loadouts). Leave it out to
  use the squadron's.

### Radio lines

Give any of these keys a list of lines; one is picked at random each time. A key you give replaces the built-in lines
for that moment; keys you leave out use the built-in ones.

| Key | When it is said |
| --- | --- |
| `awacs` | Your side's AWACS warns that the squadron is coming. |
| `allied` | The squadron's own command announces the launch (heard by their side). |
| `intro` | The lead's opening line after the intro. |
| `break` | The flight breaks formation to engage. |
| `snap` | An ace takes a quick shot. |
| `parthian` | An ace fires mid-manoeuvre or over its shoulder. |
| `evade` | An ace dodges a missile. |
| `overshoot` | Someone overshoots an ace. |
| `cover` | An ace comes to help a wingman with someone on its tail. |
| `pincer` | Two aces split to squeeze a target. |
| `intercept` | An ace shoots down an incoming missile. |
| `dark` | The flight switches its radars off. |
| `winchester` | An ace is out of weapons and leaves. |
| `bingo` | An ace is low on fuel and leaves. |
| `wingmanDown` | A wingman is shot down. |
| `leadDown` | The lead is shot down. |
| `defeated` | The whole squadron is shot down. |
| `withdraw` | The squadron gives up and leaves. |

These words in a line are filled in when it is said:

| Placeholder | Becomes |
| --- | --- |
| `{squadron}` | The squadron's name. |
| `{title}` | The squadron's title. |
| `{unit}` | The squadron's unit. |
| `{designation}` | The squadron's designation (its name if there is none). |
| `{callsign}` | The speaker's callsign, or in `wingmanDown` the pilot who went down. |
| `{pilot}` | The speaker's name. |
| `{target}` | The pilot being hunted. |
| `{faction}` | The squadron's side, short (BDF, PALA). |
| `{factionLong}` | The squadron's side, in full. |
| `{country}` | The squadron's country (Boscali, Primeva). |
| `{foe}` | The hunted side, short. |
| `{foeLong}` | The hunted side, in full. |

## Loadouts

`loadout` takes one of two things.

**A name.** One of the jet's standard loadouts, exactly as the game's loadout menu shows it, or a preset you saved
in the loadout editor (F1 → NO Aces High → *5. Airframes → Loadouts*):

```json
"loadout": "Air Superiority"
```

**A loadout built by station.** Name the weapon for each station you care about:

```json
"loadout": {
  "base": "Air Superiority",
  "fuel": 0.8,
  "stations": {
    "Outer Pylons": "AAM-36 Scimitar",
    "Inner Pylons": "AAM-29 Scythe",
    "Centerline": "empty"
  }
}
```

- `stations`: each key is a station, by the name the loadout editor shows or by its number (`"1"` is the first).
  Each value is a weapon, by its name in the game (full or short), or `"empty"`.
- `base` (optional): a loadout to start from. Stations you don't list keep its weapons. Without a base, every
  station you don't list gets the best air-to-air weapon it can carry.
- `fuel` (optional): 0 to 1. Left out: the base loadout's, otherwise the jet's default.
- If a station blocks another (a centreline tank over two inner pylons, say), the blocked one is left empty.

Station names and weapons differ from jet to jet. The easy way to get them right is the loadout editor: pick the
jet, copy a standard loadout into your presets, change it, then press **Copy as squadron JSON** and paste the result
into your file. The squadron menu checks a loadout against the squadron's first installed jet and says which
station or weapon it doesn't recognise. A loadout that doesn't fit the jet actually flown (a backup jet, say) is
reported in the log, and the aces pick their own.

A member can have its own `loadout`, written the same way.

## Music

Music plays only for the side being hunted, at the game's music volume. There are five slots:

| Slot | When it plays |
| --- | --- |
| `intro` | As the squadron is called in. The intro cutscene and title card wait for it to finish (settings: `IntroWaitsForMusic`, capped by `MaxIntroMusicWait`), so keep it short: about 5-20 seconds. |
| `cutscene` | During the intro cutscene itself. Optional: without it, the intro track carries on. |
| `loop` | The fight. Loops until the squadron is beaten or leaves, so a track that loops cleanly sounds best. |
| `outro` | The squadron is beaten. |
| `escape` | The squadron withdrew. Falls back to `outro`. |

Files can be `.ogg`, `.wav` or `.mp3`. There are three places to put them, used in this order for each slot:

1. **This squadron only.** Put the files in the squadron's folder (a `music` subfolder keeps things tidy) and list them
   in `squadron.json`; the paths are relative to the squadron folder:
   ```json
   "music": { "intro": "music/intro.ogg", "loop": "music/theme.ogg", "outro": "music/defeat.ogg" }
   ```
   **One file for everything.** If your music is a single track (a soundtrack with an intro, a loop section and an
   ending), name it once as `"file"` and give each slot the part of it to play, as `start-end` in minutes:seconds:
   ```json
   "music": {
     "file": "music/soundtrack.ogg",
     "intro": "0:00-0:18",
     "loop": "0:18-2:45",
     "outro": "2:45-3:10"
   }
   ```
   Times can have decimals (`1:02.5`) or be plain seconds (`62.5-100`). Leave the end off (`"2:45-"`) to play to the
   end of the file. A slot can still be its own file, or a part of another file: `"escape": "music/other.ogg@1:02-1:40"`.
   For a seamless loop, cut the `loop` part exactly on the beat where the music repeats.
2. **Every squadron of one side.** Drop files named after the slot (`intro.ogg`, `loop.ogg`...) into
   `BepInEx\config\NOAcesHigh\Music\BDF` or `...\Music\PALA`.
3. **Every squadron.** Drop files named after the slot into `BepInEx\config\NOAcesHigh\Music`.

A slot with no file anywhere is silent and the game's own music carries on. The squadron menu's **Music folder**
button opens the music folder.

Music is streamed from the file as it plays, so a long soundtrack costs nothing to load: no hitch when the squadron
launches, however big the file.

## Voice lines

Recorded radio lines work like music. Put the recordings in a `lines` folder in the squadron's folder and point each
[radio line](#radio-lines) at its recording in a `voice` block. Each key takes a list in the **same order as that
key's text lines**, so the first recording goes with the first line, and so on. The text still shows as a subtitle
(unless subtitles are off in *7. HUD*), and the recording plays with it.

```json
"lines": {
  "intro": [ "I want to understand the enemy.", "Show me how you fly." ],
  "evade": [ "Too slow." ]
},
"voice": {
  "intro": [ "intro1.ogg", "intro2.ogg" ],
  "evade": [ "evade.ogg" ]
}
```

**One file for everything.** As with music, name one recording as `"file"` and give each line the part of it to
play:

```json
"voice": {
  "file": "lines/all-lines.ogg",
  "intro": [ "0:00-0:03.2", "0:04-0:06" ],
  "evade": [ "0:07-0:08" ]
}
```

- Paths are looked up in the squadron folder, then in its `lines` folder, so `"intro1.ogg"` and
  `"lines/intro1.ogg"` both work.
- A line without a recording is fine. Put `null` in its place to skip it: `[ null, "intro2.ogg" ]`.
- Lines play one at a time: one that comes up while another is playing waits for it (up to 6 s).
- Settings in *8. Sound*: `VoiceLines` (on/off), `VoiceVolume`, and `VoiceRadioFilter` (on by default: thin and
  slightly crunchy, like a radio; turn it off if your recordings already sound like one).
- In multiplayer, players hear the recordings if they have the same squadron installed.
- A recording that cannot be found is reported in the squadron menu.

## Generated liveries (experimental)

With **5. Variants â†’ GeneratedLiveries** on, a squadron without its own `livery` gets one made in game: the jet's
livery recoloured to the squadron's `color` as a two-tone scheme (its camouflage pattern and panel lines carried
through), and the squadron emblem on both sides of the tail fins. It is made once per airframe and colour, over a
few frames, and every player builds the same one. At long range the game draws simplified models without livery
textures, so distant aces still show their stock paint.

## Sharing a squadron

Zip the squadron's folder. Whoever receives it unzips it into their own `BepInEx\config\NOAcesHigh\Squadrons`. A mod can
also ship squadrons: any folder called `AceSquadrons` inside `BepInEx\plugins` is scanned the same way.

In multiplayer the host's squadrons are the ones that fly, and everyone should have the same squadron folders so the
emblems and music match.

## Checklist when something does not show up

- **Not in the list at all.** The folder must contain a file named exactly `squadron.json`, and the folder name must not
  start with `_`. Click **Reload files** after changes.
- **Listed in red.** The JSON is broken; the message gives the line. Check the commas and quotes just before it.
- **Yellow notes.** A field or value the mod does not know: usually a typo, and the note suggests the right spelling.
- **"not installed" next to the aircraft.** None of the names in `aircraft` matched an installed jet; check the spelling
  against the name the game's hangar shows.
- **No music.** Check the slot names and file paths against the yellow notes, and that the files play in a media player.
