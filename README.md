# Stranded Deep Table Storage

A BepInEx mod for Stranded Deep that adds native-style storage to the game's craftable tables.

Current version: **0.8.2**

## 0.8.2 Mod Settings localization

- Added Russian/English localization for all three Mod Settings controls.
- Synced the vendored reflection bridge with the authoritative bilingual SDK.
- Seven-slot storage, stack behavior, display contents and persistence are unchanged.
- Russian/English Mod Settings and local split-screen storage behavior were field-tested before publication.

## Features

- Adds storage to `WOOD_TABLE`, `PLANK_TABLE` and `CORRUGATED_TABLE`.
- 7 native-style storage slots per table.
- Uses the game's normal storage interface.
- Designed to work correctly in local split-screen.
- Configurable stack multiplier.
- Optional decorative display of stored contents on the tabletop.
- Persistent storage across save/load and full game restarts.

## Persistence

Table Storage maintains persistent sidecar data under:

`BepInEx\config\StrandedDeepTableStorage\Storage\`

Storage records are associated with their table using the table's stable `Construction.ReferenceId`.

Persistence is synchronized with the normal Stranded Deep save cycle. The mod also integrates with native table saving so table state remains consistent across save/load.

Because this mod participates in save persistence, back up both your Stranded Deep save data and the Table Storage config directory before updating or removing the mod.

## Legacy Kitchen Table migration

Table Storage is the successor to the earlier Stranded Deep Kitchen Table mod.

Existing legacy storage data under:

`BepInEx\config\StrandedDeepKitchenTable\Storage\`

is preserved and can be migrated into the new Table Storage location.

The historical BepInEx plugin GUID is intentionally retained for upgrade continuity.

## Raft Furniture compatibility

Table Storage contains soft compatibility with Stranded Deep Raft Furniture.

Raft Furniture is not required. When it is installed, Table Storage respects its ownership of tables attached to rafts so the two mods do not fight over table physics.

## Optional Mod Settings integration

Stranded Deep Mod Settings provides an in-game settings page for Table Storage.

Mod Settings is optional. Table Storage continues to work without it.

## Installation

1. Install BepInEx for Stranded Deep.
2. Download `StrandedDeepTableStorage-0.8.2.zip` from the GitHub Release.
3. Extract the archive into the Stranded Deep game directory.
4. Allow the included `BepInEx` folder to merge with the existing `BepInEx` folder.
5. Fully restart Stranded Deep.

Requires BepInEx.
