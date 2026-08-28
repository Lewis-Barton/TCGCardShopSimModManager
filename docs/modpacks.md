# Hosted modpacks

The long-term goal for the desktop app: instead of hand-editing a manifest and
pointing at a folder of archives, you pick a modpack from a list, see what's in
it, and install everything in one click. The packs and their manifests live on
GitHub; the app fetches them, downloads the archives, and runs the same install
pipeline it already uses.

This document is the v1 design. It is the spec to build against.

## Repository layout

Modpacks live in this repo, under `modpacks/`, submitted through pull requests
so they're reviewable like anything else:

```
modpacks/
  index.json              # the gallery index (small, fetched on open + Refresh)
  <packId>/
    manifest.json         # the full mod list (existing ModListManifest shape)
    logo.png              # pack logo, square, ~512x512
```

A `essential-qol` demo pack ships in this repo so the gallery has something to
show before real packs exist. Its manifest points `downloadUrl` at a sample
archive already committed under `samples/mod-archives/`, so the whole flow —
fetch index, fetch manifest, download, install — works once `modpacks/` is
pushed. Replace it (or add your own) with real packs when you have archives
hosted on Nexus or an author's URL.

The index points at each pack's logo and manifest by **repo-relative path**.
The app resolves those to `raw.githubusercontent.com` URLs from one hardcoded
base (the index URL itself), so there's a single place to change if the repo
moves.

## index.json

Kept tiny so the gallery loads instantly — it carries only what the card needs,
not the mod list.

```json
{
  "version": 1,
  "packs": [
    {
      "id": "essential-qol",
      "name": "Essential QoL",
      "shortDescription": "The must-have quality-of-life mods for a first playthrough.",
      "logo": "essential-qol/logo.png",
      "manifest": "essential-qol/manifest.json",
      "version": "1.2.0",
      "updated": "2026-08-12",
      "featured": true,
      "nsfw": false,
      "downloadSize": 285000000,
      "tags": ["quality-of-life", "starter"],
      "modIds": ["bepinex", "example-mod"],
      "compatibleGameBuildIds": ["19024567"]
    }
  ]
}
```

Fields:

- `id` — stable key; also the folder name under `modpacks/`.
- `name`, `shortDescription` — shown on the card.
- `logo`, `manifest` — repo-relative paths, resolved to raw GitHub URLs.
- `version`, `updated` — shown on the card; `version` is compared against the
  installed pack journal (`cardshopmodmanager.modpacks.json` in the game folder)
  so the app can show "Update available" when a newer pack is published.
- `featured`, `nsfw`, `downloadSize`, `tags`, `modIds` — optional gallery
  metadata used by the desktop filters. `downloadSize` is the total compressed
  download size in bytes. Older index entries can omit these fields.
- `compatibleGameBuildIds` — numeric Steam build IDs tested by the pack author.
  It must match the same field in the manifest so the gallery can show
  compatibility before opening the pack.

NSFW packs are hidden by default. A user must explicitly select the NSFW filter
to include them in the gallery. Nexus account restrictions still determine
whether restricted files can be viewed or downloaded; selecting the filter does
not bypass those controls.

## manifest.json

The existing `ModListManifest` (name, game, mods[]). The one addition for
hosted packs is **per-mod archive sourcing**, because the archives are not in a
single shared folder — they live on Nexus or the mod author's own host.

Each mod resolves its archive in this order:

1. `DownloadUrl` (new optional field) — a direct HTTPS link to the archive.
2. `NexusModId` (+ optional `NexusFileId`) — resolved through the Nexus API
   (already supported by `ModEntry`).
3. Neither present → falls back to a pack-level `source` (an http base URL or a
   local folder), for local-style packs that keep archives together.

`Archive` and `Sha256` stay as they are: after download, the file is hash-checked
against `Sha256` before anything is installed.

**Disk-space pre-flight:** a pack may declare a top-level `totalSize` (bytes, the
sum of its mod archives). When present, the installer checks free space on both
the download temp location and the game folder *before* fetching anything, and
fails fast with a clear message if either is short — so a large pack won't
partially download and then stall on a full disk. The per-file gate in
`ModDownloader` remains as a backstop for any mod whose real size exceeds the
declared total.

`DownloadUrl`, `required`, `excludedArchivePaths`, `compatibleGameBuildIds`, and
the optional top-level `totalSize` are the hosted pack schema additions;
`NexusModId`/`NexusFileId` already exist on `ModEntry`.

### Excluding bundled files

Some upstream archives bundle another mod's assets or generated configuration.
When two selected entries would install the same destination, the manager stops
before copying anything because it cannot safely guess which mod owns the file.
A mod entry may use `excludedArchivePaths` to leave those bundled copies out:

```json
"excludedArchivePaths": [
  "BepInEx/config/generated.cfg",
  "BepInEx/plugins/Shared Defaults/"
]
```

A value ending in `/` excludes that complete archive-relative directory tree.
Any other value excludes one exact archive-relative file. Paths use `/`, cannot
be rooted or contain `.` or `..` segments, and are matched without regard to
case. Give every shared destination one deliberate owner; do not use exclusions
to hide different files without first deciding which version the pack needs.
Excluded paths appear as notes in the install report and are never journaled.

Archives that already contain a top-level `plugins/` directory are treated as
BepInEx content trees. Their `plugins/`, `patchers/`, and `config/` directories
are mirrored under `BepInEx/`. This is distinct from game-root content: a
dependency such as `plugins/Example.API/Example.API.dll` must be available to
BepInEx rather than copied beside the game files.

An archive may wrap a complete `BepInEx/` directory in one named outer folder.
The manager strips that single wrapper and mirrors the enclosed BepInEx tree.
Files elsewhere in the wrapper are skipped.

A ZIP may instead wrap a plugin DLL and its supporting files in one named
directory, such as `TextureReplacer/TextureReplacer.dll`. When that DLL is
directly inside the archive's only top-level directory, the manager preserves
the complete directory under `BepInEx/plugins/`.

### Game build compatibility

Pack authors list tested Steam builds in `compatibleGameBuildIds` at the top of
the manifest and repeat the same list in `index.json`. Steam records the current
installation's numeric `buildid` in `appmanifest_3070070.acf`; the manager reads
that value for the selected game folder.

A matching build is shown as compatible. A different build, an installation
whose build cannot be read, or a pack with no declared builds is marked **May
not be supported** on the gallery card and in pack details. The user can still
install after explicitly acknowledging the warning. The CLI prints the same
warning before continuing. This is intentionally advisory because a mod may
still work after a game update even before its author has retested it.

### Required and optional mods

Each mod may declare `required`. It defaults to `true`, so older manifests keep
their install-all behaviour. Required entries are always selected and cannot be
cleared in the desktop app. An entry with `"required": false` starts unchecked
and is installed only when the user selects it.

Selecting an optional mod also selects its dependency chain. Clearing an
optional dependency clears optional mods that depend on it. A required mod may
not depend on an optional one: pack validation asks the author to mark that
dependency as required instead. The BepInEx framework is always required.

The CLI follows the same default with
`modpack install <id> [gameFolder] [optionalId1,optionalId2|all]`.
The selected optional ids are stored with the installed pack version, so the
desktop and CLI preserve that selection on later updates. Legacy pack journals
are treated as having selected every entry, matching their original behaviour.
If a later update clears an optional entry, its unchanged managed files are
removed. A modified file blocks the change and restores the previous pack files,
selection, version and journals instead of leaving a partial update.

Valid `installType` values are `BepInExPlugin` (a plugin that loads inside
BepInEx) and `BepInEx` (the BepInEx framework itself — see below). The
on-disk layout of every entry is decided by `ArchiveClassifier` from the
archive's contents, not by `installType`.

### BepInEx must come first

Every modpack must include the **BepInEx framework** as a mod entry, with the
reserved `id` `bepinex` and `installType` `BepInEx`:

```json
{
  "id": "bepinex",
  "name": "BepInEx",
  "version": "5.4.23",
  "archive": "bepinex.zip",
  "sha256": "<sha256 of the BepInEx archive>",
  "installType": "BepInEx",
  "dependencies": [],
  "conflicts": []
}
```

BepInEx is the loader every plugin runs inside, so it has to be on disk before
any plugin is copied in. `ModpackInstaller.EnforceBepInExFirst` guarantees this:
at install time it makes **every other mod depend on `bepinex`** (if it doesn't
already), and the resolver orders dependencies first — so pack authors can't
forget it. The classifier installs a top-level `BepInEx/` folder and permits the
reserved, hash-verified framework entry to place its root bootstrap DLL, such as
`winhttp.dll`, beside the game executable. The same DLL remains forbidden for
ordinary mod entries.

The demo pack points `bepinex`'s `downloadUrl` at the committed
`samples/mod-archives/bepinex-layout.zip` placeholder so the flow is
self-contained and testable; a real pack should point at the official BepInEx
release archive instead.

## Download and install flow

`DeploymentService.Install` reads archives from a **local folder**, matched by
each mod's `Archive` name. The download step is what decides where each archive
comes from. So the app's job is:

1. Fetch `index.json` (on open, and on a Refresh button).
2. For each mod in the chosen pack's manifest, resolve its source and download
   the archive into a temporary install workspace. Every verified archive is
   also retained in a content-addressed cache under
   `%LOCALAPPDATA%\TCGCardShopSimModManager\download-cache`. On Windows, a
   same-volume workspace uses hard links to those cached files so it does not
   consume another pack-sized block of disk space. A normal copy remains the
   fallback when links are unavailable.
3. Call the existing `Install(manifest, workspace, gameFolder)`. The workspace
   is removed afterwards, while the verified cache survives failed and
   successful attempts for later retries.

Step 2 is the only non-trivial new engine piece: a **per-mod source
dispatcher** — a composite `IModSource` that, for each mod, picks `DownloadUrl`
→ `NexusModId` → pack-level fallback. `ModDownloader`, `HttpModSource` and
`NexusModSource` themselves are reused unchanged, including their caching,
HTTP Range resume, and retry behaviour.

Because `Install` already validates the manifest, plans every archive, and
refuses conflicts before copying a byte, the one-click path is the same safe
pipeline the manual flow uses — just fed from a downloaded cache instead of a
folder the user picked.

## App UI

- **Browse modpacks** shows a wrapping grid of cards beside fixed search, tag,
  featured and NSFW filters. Each card shows the logo, name, short description,
  tags and compressed download size when available.
- Clicking a card opens a modal with the logo, description and full mod list.
  The modal owns the **Install modpack**, **Update** and **Uninstall modpack**
  actions. Pack uninstall is transactional: a modified managed file stops the
  operation and restores pack files already removed. A running install also has
  a **Cancel install** action. Download cancellation keeps incomplete data in
  the content cache for a ranged resume on retry; the `.partial` file is never
  treated as an installable archive. Cancellation during installation stops
  between mods and rolls back changes already made by that deployment.
- A pack switch can optionally keep separate save progress for each modpack.
  The manager stores `savedGames_Release*` and `savedGames_BackupFile*` under
  `%LOCALAPPDATA%\TCGCardShopSimModManager\save-profiles`; keybinds, logs and
  other Unity files remain in place. Returning to a pack restores its saved
  slots. A failed or interrupted switch restores the previous active saves and
  stored profiles before another save swap begins.
- Save swapping requires the game to be closed. Steam Cloud can restore or
  overwrite local files independently of the manager, so the switch dialog
  recommends disabling it for this game before separate pack saves are used.
- Settings reports the number and size of stored save profiles. **Clear stored
  saves** removes only the manager's inactive per-pack copies after confirmation;
  it does not touch the save files currently used by the game.
- A newer published version adds an **Update available** badge to its card.
- **Manage mods** contains game-folder selection and installed-mod lifecycle
  controls. Local manifest workflows remain available through the CLI.
- **Settings** contains Nexus sign-in, app update checks and support-bundle
  export. Update results and support-export progress, failures and saved paths
  appear beside their actions. An available update includes a button that opens
  its release page. Settings also reports the number and total size of cached
  mod archives. It can clear those downloads after confirmation without
  changing installed mods. Stored modpack saves have a separate usage display
  and clear action.
  Its separate **Modpack author tools** section can check every hosted
  manifest for newer, archived or missing pinned Nexus files. It is clearly
  marked as unnecessary for people who only install packs, and it never edits
  manifests or installs suggested replacements.

## Validating a submission

Before merging a pack, run the local check from the repo root:

```
dotnet run --project src/TCGCardShopSimModManager.Cli -- modpack validate [packId]
```

With no `packId` it checks every pack listed in `modpacks/index.json`. It reads
`index.json`, the referenced `manifest.json` and `logo` from disk — it never
contacts GitHub — and fails the submission on:

- a missing or non-JSON `index.json` / `manifest.json`;
- a missing `logo`, or one that isn't a PNG;
- a manifest that fails `ManifestValidator`, including a name that does not
  match the index entry;
- a mod with no resolvable source (`DownloadUrl`, `NexusModId`, or a pack-level
  `source`);
- a pack that omits the required `bepinex` entry (see BepInEx above).

It warns (without failing) on a suspiciously small logo.
It also warns when no compatible Steam builds are declared, and fails when the
index and manifest build lists differ or contain malformed build IDs.

## Importing a large Nexus list

The temporary authoring commands below turn a Nexus Files-tab page into stable
file selectors, then create a manifest draft. The importer downloads each
selected archive once to calculate the SHA-256 used by
the normal installer; those archives stay in a local authoring cache and are not
added to the modpack folder or published.

```powershell
dotnet run --project src/TCGCardShopSimModManager.Cli -- `
  modpack files "https://www.nexusmods.com/tcgcardshopsimulator/mods/698?tab=files"

dotnet run --project src/TCGCardShopSimModManager.Cli -- `
  modpack import nexus-links.txt modpacks/my-pack "My Pack"

dotnet run --project src/TCGCardShopSimModManager.Cli -- `
  modpack check-updates cardverse-overhaul
```

`modpack files` lists every file Nexus currently exposes for the mod, grouped
with its category, display name, filename, version and size. Each result includes
a copy-ready selector such as `required nexus:698:12345`. This uses Nexus's
public API identifiers rather than the website's `/api/files/.../download` URL,
which does not carry the mod id and is not stored in a published manifest.

Put one link on each line. Bare links and `required` links become required mods;
use `optional` for entries users may select, and mark exactly one framework
archive as `bepinex`. Blank lines and lines beginning with `#` are ignored.
Comments may also follow a selector when separated from it by whitespace.

```text
bepinex nexus:10:100
required nexus:20:200
optional nexus:30:300
```

Exact website links containing `file_id` and NXM links containing both ids are
accepted too. A general mod page produces guidance to run `modpack files`
because the importer cannot safely guess which file or version the pack author
intended. Nexus only provides automatic download links to Premium accounts, so
the stored OAuth session or personal API key must belong to a Premium user.

The command reads names, versions, filenames and sizes from Nexus, downloads and
hashes every archive, and writes `manifest.json`. If that file already exists it
writes `manifest.imported.json` instead and leaves the existing manifest alone.
Reruns reuse the cache under `%LOCALAPPDATA%\TCGCardShopSimModManager`.

`modpack check-updates` checks every pinned Nexus file in a local pack. It
separates required and optional entries, reports current, missing and archived
files, and suggests a replacement only when Nexus exposes a newer file with the
same display name. Suggested replacements are printed as copy-ready selectors.
The command does not edit the manifest or download anything; review and test
each replacement, then increment the pack version before publishing it.

The desktop Settings page exposes the same read-only check for all hosted packs
under **Modpack author tools**. This is an authoring aid, not an update action
for people who install packs. It requires Nexus access and leaves every
manifest unchanged.

The importer accepts ZIP, RAR, 7Z, TAR, GZ, TGZ, BZ2 and XZ files. Encrypted
and multi-volume archives are not supported. Every format goes through the same
path, link, file-type and extracted-size checks before installation.

Before publishing, review required/optional choices and add dependencies,
conflicts and `compatibleGameBuildIds`. Add the pack metadata and logo to
`index.json`, then run `modpack validate` as usual.

## What is reused vs. new

Reused as-is: `ModDownloader`, `HttpModSource`, `NexusModSource`,
`DeploymentService.Install`, the install journal, and Steam auto-detection.

New: the index schema + fetcher, the gallery and detail-panel UI, logo
loading/caching, the per-mod source dispatcher, the selection → download →
install wiring, and the optional `DownloadUrl` field.

## Deferred (not v1)

- (Pack-submission validation is done — see "Validating a submission" above.)
