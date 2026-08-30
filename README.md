# TCG Card Shop Sim Mod Manager

This is a mod installer/manager for **TCG Card Shop Simulator** (Windows). You
list the mods you want in a JSON manifest; we verify, extract, plan, install,
journal and uninstall them, and we never silently overwrite anything.

Two front-ends share the same engine: the CLI and the desktop app both work
through `DeploymentService` in the Core project, so the app always does exactly
what the CLI does.

Status: engine, CLI, desktop UI, downloads, Nexus integration, Steam
auto-detection and in-app mod enable/disable are working. Release tooling and
docs are in place; publishing/testing to real hardware is next.

## Desktop app

<img src="src/TCGCardShopSimModManager.App/app-icon.png" width="180" alt="TCG Card Shop Sim Mod Manager app icon" />

Needs a display (Windows):

```
dotnet run --project src/TCGCardShopSimModManager.App
```

Browse the hosted modpack gallery, use its filters and sorting to find a pack,
and open a card to review required and optional mods before installing it. The
gallery renders its bundled or last saved catalog immediately while checking GitHub for
updates, and reuses decoded logos when filters rebuild the cards. Nexus-backed
entries in pack details link to their original mod pages, while the pack summary
shows its version, update date, mod count and download size. The app
confirms your optional choices and shows per-download progress and speed during
the install. A running install can be cancelled; cancellation stops at a safe
operation boundary, retains an incomplete download for a later ranged resume
and rolls back mods already changed by that operation. Packs
that download from Nexus prompt for sign-in or a personal API key before
installation can begin. The gallery identifies the installed pack and
shows a green banner when its published pack version changes. Only one hosted
pack can be installed at a time; choosing another offers a transactional switch
that keeps matching shared mods, removes old-only mods and restores the original
pack if the change cannot finish. Before confirmation, the app shows how many
mods will be kept, updated, removed and added, with the mod names grouped under
each action. The same confirmation can keep separate save progress for each
pack. The game must be closed, and Steam Cloud should be disabled before using
that option. An installed pack can also be uninstalled as one journal-backed
operation from its details window. The
persistent **Launch game** button starts the game through Steam, reports the
launch state and stays disabled while the game is running. The **Manage mods**
page lets you choose the game folder, inspect installed and manually placed
mods, search or filter them by state, and enable, disable or uninstall managed
mods. Nexus account controls and update checks live under **Settings**.
Appearance settings can follow Windows or use a
light, dark or high-contrast theme. Text and modpack card sizes can be increased
independently, and the choices are kept for the next launch. The CLI retains
the local manifest validation, planning and installation commands. Settings
reports how much space downloaded mod archives and separate modpack saves use.
Either store can be cleared independently without changing installed mods or
the saves currently active in the game. Stored saves can also be reviewed by
modpack and deleted individually. Update checks report their result on
Settings and link directly to a newer release when one is available. A clearly
labelled modpack-author tool
checks hosted manifests against Nexus. People who only install modpacks do not
need it. The check is read-only and every suggested file still needs review and
testing before a pack update is published.

On open, the window tries to find TCG Card Shop Simulator through your Steam
library folders automatically and fills the game folder; if not found, use
Browse on the Manage mods page. **Refresh list** starts with journal ownership
across the game root and BepInEx tree, then adds clearly labelled unmanaged
content found under `plugins`, `patchers`, `core` and disabled storage. It marks
entries as installed, modified, disabled or unknown. Select a managed mod and
use **Enable** / **Disable** to move it
out of the game into the manager's own disabled folder (beside the executable)
with separate storage for each game installation — nothing is deleted, and a
modified file is left alone with a warning.
**Uninstall** removes only verified managed files. The title shows the version.

## Commands

```
dotnet run --project src/TCGCardShopSimModManager.Cli -- detect  [gameFolder]
dotnet run --project src/TCGCardShopSimModManager.Cli -- validate <manifest.json> [gameFolder]
dotnet run --project src/TCGCardShopSimModManager.Cli -- plan     <manifest.json> <sourceDir> <gameFolder>
dotnet run --project src/TCGCardShopSimModManager.Cli -- download <manifest.json> <httpUrlBase|localFolder|nexus> <cacheDir> <outDir>
dotnet run --project src/TCGCardShopSimModManager.Cli -- serve     <folder> [port]
dotnet run --project src/TCGCardShopSimModManager.Cli -- demo
dotnet run --project src/TCGCardShopSimModManager.Cli -- nexus     <set-key <apikey>|set-client <id> [redirectUri]|login|logout|status|clear>
dotnet run --project src/TCGCardShopSimModManager.Cli -- nexus-demo
dotnet run --project src/TCGCardShopSimModManager.Cli -- update-check
dotnet run --project src/TCGCardShopSimModManager.Cli -- support-bundle [outDir]
dotnet run --project src/TCGCardShopSimModManager.Cli -- --version
dotnet run --project src/TCGCardShopSimModManager.Cli -- install  <manifest.json> <sourceDir> <gameFolder>
dotnet run --project src/TCGCardShopSimModManager.Cli -- uninstall <modName> <gameFolder>
dotnet run --project src/TCGCardShopSimModManager.Cli -- profile  <list|use|enable|disable> ...
dotnet run --project src/TCGCardShopSimModManager.Cli -- mods     <list <gameFolder> | disable <name> <gameFolder> | enable <name> <gameFolder>>
dotnet run --project src/TCGCardShopSimModManager.Cli -- modpack install <id> [gameFolder] [optionalId1,optionalId2|all]
dotnet run --project src/TCGCardShopSimModManager.Cli -- modpack files <NexusFilesUrl|modId>
dotnet run --project src/TCGCardShopSimModManager.Cli -- modpack check-updates <packId|manifest.json>
dotnet run --project src/TCGCardShopSimModManager.Cli -- modpack import <links.txt> <packFolder> [packName]
```

- `detect`    — with a path, check it's a game install. With no path, auto-detect the game through Steam (reads the Steam library folders — no API key needed).
- `validate`  — checks the manifest and the enabled list; prints the valid install order, or every reason it can't be installed.
- `plan`      — dry run: exactly which files each archive would install, where, and what's skipped/rejected. Never touches the game.
- `download`  — fetch every archive into `outDir` through the download pipeline. Source is an http(s) base URL, a local folder, or `nexus`.
- `serve`     — host a folder over HTTP with Range support (in-process server, mainly for demos). `demo` is the one-command version: serve + download + install for you.
- `nexus`     — manage Nexus auth: `set-client` (OAuth app id), `login`/`logout`/`status` (OAuth), and `set-key`/`clear` (classic API key, development only). `nexus-demo` runs the whole Nexus path against a mock API.
- `update-check` — compares the running version with the latest GitHub release (runs only when you ask).
- `support-bundle` — zips environment info and recent diagnostics for sharing. Never includes the API key.
- `install`   — resolve the enabled list, verify order, pre-flight file conflicts, then hash-verify, extract, plan, stage, copy, journal.
- `uninstall` — removes only files whose hashes still match the journal; a modified file is warned about and left alone.
- `mods`      — list what's actually on disk (`BepInEx/plugins`, `patchers`) with journal-backed state; disable moves a mod's files out of the game into the manager's disabled folder, enable moves them back.
- `profile`   — named sets of enabled mods:

```
profile list                <gameFolder>
profile use <name>          <gameFolder>
profile enable  <id>        <manifest.json> <sourceDir> <gameFolder>
profile disable <id>        <manifest.json> <sourceDir> <gameFolder>
```

`enable` installs the mod (and its enabled dependencies, in order); `disable`
removes its files from the game directory. A profile change is only committed
once the new state is proven valid.

## Manifest

```json
{
  "manifestVersion": 1,
  "name": "Development Test List",
  "game": "tcgcardshopsimulator",
  "compatibleGameBuildIds": ["19024567"],
  "mods": [
    {
      "id": "example-mod",
      "name": "Example Mod",
      "version": "1.0.0",
      "archive": "ExampleMod.zip",
      "sha256": "expected-hash-here",
      "installType": "BepInExPlugin",
      "required": false,
      "dependencies": ["shared-library"],
      "conflicts": ["old-mod"]
    }
  ]
}
```

- `id` is the key that `dependencies`, `conflicts` and profiles reference.
- `version` is optional. `dependencies`/`conflicts` are optional (empty when absent).
- `required` defaults to `true`. Hosted packs show required mods as locked
  selections and let the user opt into entries marked `false`. Selecting an
  optional mod also selects its optional dependencies. The desktop app confirms
  the selected optional mods before starting the download.
- `excludedArchivePaths` may name an exact archive-relative file or a directory
  tree ending in `/`. Pack authors use it to give bundled shared files one
  deliberate owner; excluded content is reported and never installed or
  journaled.
- `compatibleGameBuildIds` lists the numeric Steam build IDs the list author
  has tested. The app reads the installed build from Steam's app manifest and
  marks the pack as potentially unsupported when it cannot confirm a match.
- For the Nexus backend add `nexusModId` (and optionally `nexusFileId`; with only
  the mod id the file is found by `archive` name via the files API).

Sample manifests live in `samples/manifests/` (`dev-test`, `archive-demo`,
`dependency-demo`, `invalid-demo` — a deliberately broken list that shows the
error output). Sample archives are in `samples/mod-archives/`.

## Dependencies, conflicts, profiles

`validate` (and `install`, before anything is touched) checks the enabled mods
and reports all problems at once:

- a dependency whose id isn't in the list, or isn't enabled;
- two enabled mods that declare each other as conflicting;
- a dependency cycle, naming the mods stuck in it.

When the list is good it returns the install order, dependencies first. `install`
won't touch the game if the enabled list is invalid.

Profiles live in `cardshopmodmanager.profiles.json` in the game folder. No
profile file means every mod in the manifest is enabled (the default). The first
`profile disable` creates a `default` profile containing everything except the
disabled mod.

Profile changes are committed only after their file operations succeed. If an
enable or disable cannot be completed, the manager rolls back its work and
leaves the saved profile unchanged.

Hosted `modpack install` installs required entries by default. Pass a
comma-separated list of optional ids, or `all`, after the game folder to add
optional entries from the CLI.

Before copying anything, `install` builds the plan for every archive and refuses
to proceed if two mods claim the same destination file.

Installing a newer archive for the same mod id performs an update. The manager
replaces or removes only files that still match its previous journal and refuses
the update if a managed file has been changed by hand. Older journal files do
not contain stable ids or archive hashes; they remain readable and are upgraded
when the matching mod is next installed.

Hosted-pack update notices compare the installed pack version with the catalog
whenever the app starts or the gallery is refreshed. Individual Nexus files are
not followed automatically: the hosted manifest pins a reviewed Nexus file id
and SHA-256 hash, so an upstream mod update becomes available only after the pack
author publishes a new pack version.

## Installation layout rules

An archive's structure decides where its files go, and `plan` shows the choice
before anything is installed. Rules, in order:

1. **BepInEx layout** — archive has a top-level `BepInEx/` folder → its contents mirror into the game's `BepInEx/`.
2. **Loose plugin folder** — loose `.dll` at the archive root → everything goes to `BepInEx/plugins/<mod name>/`.
3. **BepInEx content tree** — top-level `plugins/` → `plugins/`, `patchers/`
   and `config/` mirror under `BepInEx/`.
4. **Wrapped plugin folder** — one top-level folder with a `.dll` directly
   inside it → the complete folder goes under `BepInEx/plugins/`.
5. **Patcher** — top-level `patchers/` → its contents go to
   `BepInEx/patchers/`; anything else to `BepInEx/plugins/<mod name>/`.
6. **Game root files** — anything else → mirrors into the game folder root.

`README`/`LICENSE`/`CHANGELOG` files and OS junk (`.DS_Store`, `__MACOSX`, ...)
are skipped, and the plan tells you what it skipped.

## Downloads

The downloader works with any source: an `IModSource` only opens the file's
bytes from a given offset (`HttpModSource`, `LocalFileSource`,
`NexusModSource`). The `ModDownloader` applies the safety rules:

- bytes are written to `<name>.partial` and only renamed to the final name after
  the whole file passes its SHA-256 check, so a cancelled or corrupt download
  leaves no valid-looking file behind;
- an existing `.partial` is resumed (HTTP Range / 206) instead of restarted;
- transient failures (5xx, network errors, corrupt payloads) are retried with
  backoff, deleting the partial between attempts;
- verified downloads are cached, so a repeat never touches the source again;
- free disk space is checked against the announced size before writing starts.

Hosted-pack downloads use a persistent, SHA-256-keyed cache under
`%LOCALAPPDATA%\TCGCardShopSimModManager\download-cache`. A failed planning or
installation attempt keeps verified archives there, so retrying does not
download them again. Cancelling retains incomplete data there with a `.partial`
suffix; it is resumed on retry and cannot become a usable archive until its
complete SHA-256 hash passes. On Windows, the disposable install workspace uses
hard links to cached archives when both locations support them, avoiding
another full copy of a large pack; cross-volume and unsupported filesystems
fall back to normal copies. Archive extraction remains bounded, but its production
limits allow large game assets: up to 32 GiB for one file, 64 GiB extracted per
archive and 100,000 entries. The archive limit is not a limit on the combined
size of a modpack; packs containing multiple archives may be larger.

Try it in one terminal:

```
dotnet run --project src/TCGCardShopSimModManager.Cli -- demo
```

`demo` serves the archives, downloads every mod, installs them into a temp game
folder and stops the server. Run it again and the second pass shows
`(from cache)`.

## Nexus backend

Nexus is just another `IModSource`. The manifest's `nexusModId`/`nexusFileId`
resolve through the Nexus v1 API to an authenticated download URI, and the plain
HTTP source fetches the bytes. Notes:

- `nexus set-key <apikey>` stores the key encrypted with DPAPI (current user
  only) in `%LOCALAPPDATA%\TCGCardShopSimModManager\nexus-key.bin`.
- **No secrets in the repo.** The API key never lives in the project directory,
  and `.gitignore` excludes anything that would hold or reference a key
  (`nexus-key*`, `*.key`, `*apikey*`, ...). No key material exists anywhere in
  the repository, and the ignore rules are
  visible in `.gitignore`.
- Premium accounts download automatically. Free accounts get the mod page and a
  note to place the file manually — Nexus only hands premium users direct URIs.
- Rate limits are honoured: a `429` carries a `Retry-After` delay that is waited
  out before retrying.
- Archived or missing mods are reported as such.
- Requests send an identifying `User-Agent`.

`nexus-demo` runs the whole path against a mock API. For real use, set your key
(`nexus set-key <apikey>`) and optionally point `NEXUS_API_BASE` at a different
host. Personal keys are fine for development; before distributing a build it
must be registered with Nexus per its Acceptable Use Policy, and personal keys
must not be embedded in it.

## Nexus sign-in (OAuth)

Mod downloads authenticate with Nexus through OAuth 2.0 (PKCE), so the app
never sees your Nexus password and each user signs in with their own account.
This is the path used in distributed builds. The classic API-key command
(`nexus set-key`) remains for development only.

Sign-in needs a Nexus OAuth client id. Register one with Nexus by emailing
`api@nexusmods.com` with your application name, description, logo (dark
background) and a callback URI of `http://127.0.0.1:8089/callback`, then set
it once:

```
dotnet run --project src/TCGCardShopSimModManager.Cli -- nexus set-client <clientId>
```

`nexus login` opens your browser to Nexus, the app receives the redirect on a
local loopback listener, and the token is stored per user (DPAPI on Windows).
Tokens refresh automatically and are cleared with `nexus logout`. The desktop
Settings page exposes the same sign-in, status and sign-out flow.
Until the OAuth client is approved, **Enter API key** opens the Nexus API Access
page and accepts a personal key. The app validates it before storing it with
the same per-user encryption used by the CLI, and Nexus downloads use it
automatically when no OAuth session is active.

- `nexus set-key <apikey>` stores the key encrypted with DPAPI (current user
  only) in `%LOCALAPPDATA%\TCGCardShopSimModManager\nexus-key.bin`.
- `nexus set-client <clientId>` stores the OAuth client id in
  `%LOCALAPPDATA%\TCGCardShopSimModManager\oauth-settings.json`. The client id
  is public, not a secret.
- No secrets live in the repo; `.gitignore` excludes anything that would hold or
  reference a key (`nexus-key*`, `oauth-settings.json`, `*.key`, ...).

## Safety

- Source hash must match the manifest's `sha256` before anything happens.
- Extraction happens into a temp folder and is protected: `../` paths, rooted
  paths, Windows path aliases, symbolic links, oversized archives and unexpected executables
  (`.exe`, `.bat`, `.cmd`, ...) are rejected. Nothing is extracted into the game
  directly.
- Install refuses to overwrite existing files and rejects two sources mapping
  to one destination. An unmanaged file whose hash already matches the planned
  source can be adopted for tracking, but remains marked as pre-existing so an
  uninstall or update cannot delete or replace it. Symbolic links and junctions
  below the selected game root cannot redirect file operations elsewhere. If
  any mod in a deployment fails, earlier mods from that deployment are rolled
  back and previous versions are restored. A durable recovery record also
  restores interrupted deployment or hosted-pack changes on the next operation
  after a process or machine stop.
- Every installed file is hashed in `cardshopmodmanager.journal.json` in the
  game folder, so uninstall can prove a file is still what we installed before
  deleting it. The hash calculated while verifying a new copy is reused for the
  journal, avoiding a second full read of large installed assets. File locations
  are stored relative to that game folder, allowing a complete Steam installation
  to move without leaving stale absolute paths.

## Supported archive formats

ZIP, RAR, 7Z, TAR, GZ, TGZ, BZ2 and XZ. ZIP uses the built-in .NET reader;
the other formats use SharpCompress behind the same protected extraction
boundary. Encrypted and multi-volume archives are not supported.

## Diagnostics and privacy

Every command writes a structured JSON-lines log to
`%LOCALAPPDATA%\TCGCardShopSimModManager\logs` (override with `CSMM_LOG_DIR`). An
unexpected error is captured there locally; nothing is uploaded. Export a
bundle with `support-bundle` when sharing a problem. See `PRIVACY.md`, the
`LICENSE`, and `THIRD-PARTY-NOTICES.md`. Docs for list authors and the release
testing checklist live in `docs/`, and `publish.ps1` produces a self-contained
win-x64 build into `dist/`. GitHub release builds append the workflow run number
to the base product version so update checks can distinguish every pushed build.

## Running the tests

```
dotnet test
```
