# Community squadrons

Ace squadrons made by players. They are not part of the mod and do not come with a release. Take the ones you want.

## Installing one

1. Open the squadron's folder here and download it. (GitHub's **Code → Download ZIP** gets the whole repository; the
   folder is inside it.)
2. Put the folder in `BepInEx/config/NOAcesHigh/Squadrons/` in your game folder.
3. Check its README for anything else it needs, such as an add-on aircraft or a Steam Workshop livery.
4. In the game, **F1 → NO Aces High → 4. Squadrons** lists it. A file with a mistake shows in red, with the reason.

## Sharing yours

Open a pull request that adds one folder, or open an issue and attach the folder as a zip.

**Each squadron gets its own folder,** named after it (e.g. `Grey-Wolves`). In it:

| File | Needed | |
| --- | --- | --- |
| `squadron.json` | Yes | Your squadron. See the [squadron guide](../Squadrons/README.md). |
| `README.md` | Yes | What the squadron is, who made it, and what it needs (see below). |
| Emblem (`.png`) | No | Your own artwork only. |
| Voice lines | No | Your own recordings only. |
| Music | No | Only music you made or that is licensed for redistribution, with its licence named in your README. |

**Your README** should say:

- the squadron's name and a line or two about it;
- who made it (your name or handle);
- what it needs: add-on aircraft (by name and where to get them), Steam Workshop liveries (by number), other mods;
- the NO Aces High version you tested it with.

**Before you submit,** load it in the game and check it shows without errors in *4. Squadrons*, and fly against it once.

## What cannot be accepted

Assets you do not have the rights to share: music from games or films, artwork taken from other games (Ace Combat,
Project Wingman and the like), voice clips from either. Squadrons inspired by them are welcome; the copyrighted files
are not. Instead, say in your README where to get the file and what to name it, so each player can add their own copy:
for example, "put the track in `music/theme.ogg`". The squadron should still load without it (the mod then plays no
music for it, or the side's own).

Anything submitted here is shared under the repository's [MIT license](../LICENSE) unless the folder's README names a
different licence for its own files.
