# Writing a mod list

A mod list is a JSON file that says *what* to install and *where it comes from*.
The tool never executes code and never overwrites files silently. Everything
is hash-checked, previewed and journaled before it lands.

## The manifest

```json
{
  "manifestVersion": 1,
  "name": "My Server Pack",
  "game": "tcgcardshopsimulator",
  "mods": [ ... ]
}
```

Fields:

- `manifestVersion` — always `1` for now.
- `name` — any name for the list.
- `game` — the game key. `tcgcardshopsimulator` for TCG Card Shop Simulator.
- `mods` — the entries, each:

```json
{
  "id": "example-mod",
  "name": "Example Mod",
  "version": "1.2.0",
  "archive": "ExampleMod.zip",
  "sha256": "<lowercase hex sha256 of the archive>",
  "installType": "BepInExPlugin",
  "dependencies": ["shared-library"],
  "conflicts": ["old-mod"],
  "nexusModId": 4000,
  "nexusFileId": 7000
}
```

- `id` is required and must be unique. Profiles and dependencies reference it.
- `name` is what users see and the folder the plugin installs under.
- `archive` is the archive file name as it will exist in the source folder.
- `sha256` is **required** — the tool refuses to install anything that does not
  match. To produce it for a file:

  ```
  certutil -hashfile ExampleMod.zip SHA256
  ```

  (lowercase the output).
- `installType` — use `BepInExPlugin` for a plugin. The reserved `bepinex`
  framework entry uses `BepInEx`.
- `dependencies` / `conflicts` may be omitted. Dependencies are installed
  first; a circular dependency makes the list invalid. Conflicts mean the two
  mods cannot both be enabled.
- For a Nexus-hosted mod, add `nexusModId`. With only the mod id the file is
  looked up by its `archive` name; an explicit `nexusFileId` skips that lookup.

## Archive layout rules

An archive's structure decides where files go; the `plan` command shows the
result before anything is installed. In order:

1. **BepInEx layout** — archive contains a top-level `BepInEx/` folder. Its
   contents mirror into the game's `BepInEx/` folder (plugins, config,
   patchers all land in the right place). Best for mods that ship config or
   patchers.
2. **Wrapped BepInEx layout** — the archive's only top-level folder contains a
   `BepInEx/` directory. The outer folder is stripped and the BepInEx contents
   are mirrored into the game's `BepInEx/` folder.
3. **Loose plugin folder** — a bare `.dll` at the archive root. Everything goes
   to `BepInEx/plugins/<mod name>/`.
4. **Patcher** — a top-level `patchers/` folder. Its contents go to
   `BepInEx/patchers/`; anything else in the archive goes to
   `BepInEx/plugins/<mod name>/`.
5. **Game root files** — anything else mirrors into the game folder root (for
   e.g. texture replacements).

`README`, `LICENSE`, `CHANGELOG` and OS junk (`.DS_Store`, `__MACOSX`) are
skipped, and `plan` reports that they were skipped.

## Rules for archives

- Supported formats are ZIP, RAR, 7Z, TAR, GZ, TGZ, BZ2 and XZ. Encrypted and
  multi-volume archives are not supported.
- Paths must stay inside the archive: `/../`, absolute paths, and symbolic
  links are rejected and would make the archive refuse to extract.
- `.exe`, `.bat`, `.cmd`, `.ps1`, `.vbs` and similar are rejected outright —
  mods are declarative, not code.
- Two mods claiming the same destination file is a conflict the installer
  refuses at pre-flight.

## Checking your list

```
dotnet run --project src/TCGCardShopSimModManager.Cli -- validate list.json
dotnet run --project src/TCGCardShopSimModManager.Cli -- plan     list.json sourceFolder gameFolder
```

`validate` checks structure and the dependency/conflict graph and prints the
install order. `plan` shows the exact file-by-file plan and what would be
skipped or rejected. Neither touches the game.

## Distributing a list

Ship the manifest plus the archives (a folder works). Users run:

```
download list.json <source> ...
install  list.json <source> <gameFolder>
```

If the list targets Nexus, the user needs `nexus set-key` and a premium account
for automatic downloads (free accounts place the file manually).
