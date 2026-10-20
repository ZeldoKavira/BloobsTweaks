# Bloobs Tweaks

Quality-of-life mod for [Bloobs Adventure Idle](https://store.steampowered.com/app/2942780/). Every feature
has its own switch in an in-game settings window (**F8**) styled like the game's own skill windows, and
in `BepInEx/config/zeldo.bloobs.tweaks.cfg`.

## Features

**Bank**
- Items that don't fit in a full inventory go to the bank instead of being lost.
- Crafting stations, artisan skills and firemaking use materials from the bank; stations start with a full inventory.
- Homesteading costs, seeds, ranch eggs and traps use the bank (optionally before homestead tier 7).
- Fishing bait, chest keys, thieving lockpicks and auto-eat food come from the bank when the inventory runs out.
- No limit on the auto-sell / auto-bank list (selling speed unchanged).
- "Picked up" notes show bag and bank totals.

**Items**
- Tooltips show what an item is used in, made with, gathered with, obtained from and spent on.
- Ctrl+click an item to open its skill window; click recipe ingredients to jump to where they're made.
- Recipe ingredients turn green or red by what you have (skill windows and the Homesteading guide).
- Click a chest key to walk to its chest and open it.
- Golden resources are collected automatically.
- Trees, rocks, bushes and fishing spots never run out (no rewards lost; thieving chests excluded).

**Gear**
- Better tools are taken from the bank when gathering.
- Best armour and weapon for your combat style when a fight starts.
- Fastest boots outside combat (run speed affects all walking).

**Combat and challenges**
- Head back and refight after dying, with a guard against death loops.
- Progress counts toward untracked challenges once Auto Navigate + Start and Auto Progress are owned.

**Speed**
- Game speed multiplier up to 200x, stacking with the game's own 2x/3x/turbo, with the hidden per-frame caps lifted.
- Smooth high speed: camera glide, normal-speed animations and readable popups.

## Install

Needs [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64) in the game folder.

- **PC:** download `BloobsTweaks-<version>.zip` from [Releases](../../releases) and extract it into the game's
  `BepInEx` folder (it contains `plugins/BloobsTweaks.dll`).
- **Steam Deck:** install from DeckyModManager.

## Building

```
dotnet build BloobsTweaks/BloobsTweaks.csproj -c Release
```

Locally this builds against the game install (`-p:GameDir=...` to point elsewhere). CI builds against the
stripped reference assemblies in `refs/Managed`, which hold metadata only; regenerate them after a game
update with `refs/update-refs.ps1`.

Releases: bump `Version` in `BloobsTweaks/Plugin.cs`, commit, and push a matching tag (`v1.18.1`). The
workflow checks the tag against `Plugin.cs` and publishes `BloobsTweaks-<version>.zip`.
