# Bug Fix Status: TCG Card Shop Sim Mod Manager

Tracks the known bugs found in the 2026-08-13 red-team review and their fix status.
**Update this file as each bug is fixed:** set `Status` → `Fixed`, fill the **Fix** and
**Why / PR** columns, and record verification.

## 2026-08-13 follow-up review

The original table below remains the record of the first 40 bugs. Later reviews
and testing found the following additional issues, now fixed:

- **BUG-041 — unsafe modpack cache cleanup (Critical):** hosted installs now use
  a GUID-named internal workspace instead of deriving a directory from the
  manifest name. A caller-supplied cache is never deleted by the installer.
  Covered by `ModpackInstaller_ManifestNameCannotChooseCleanupDirectory` and
  `ModpackInstaller_DoesNotDeleteCallerOwnedCache`.
- **BUG-042 — mod-name destination traversal (High):** manifest validation now
  requires mod names that can safely form one directory segment. The installer
  also resolves every final destination and refuses paths outside the selected
  game folder, even when validation is bypassed. Covered by manifest-validation
  theories and `Install_RejectsDestinationOutsideGameFolder_WhenValidationIsBypassed`.
- **BUG-043 — stale modpack selection race (High):** the desktop app now tags
  each asynchronous selection and ignores results belonging to an older click,
  preventing one pack's metadata from being paired with another pack's
  manifest. The install action also rejects re-entry while it is running.
- **BUG-044 — modpack update records a version without replacing files (High):**
  install journals now retain the stable mod id, version and archive hash. An
  unchanged archive is skipped; a changed archive replaces only files still
  matching the previous journal, removes obsolete owned files, and writes the
  new identity after every copy verifies. Modified files block the update.
  Hosted installs also stamp their pack id onto each mod journal entry. Legacy
  journals remain readable and gain stable identity after their next update.
  Covered at both installer and deployment-service level.
- **BUG-045 — lifecycle operations report or retain partial state (Medium):**
  enable and disable now return a partial-failure result when some files move
  but collisions or non-managed files leave the mod in a mixed state. Uninstall
  also checks the manager's disabled folder, removes verified parked files, and
  clears stale journal entries when every tracked file is already gone. Covered
  by disabled-mod and missing-file uninstall tests.
- **BUG-046 — CLI failures exit successfully (Medium):** commands now use exit
  code 2 for missing or unsupported arguments and code 1 for operational
  failures. Downloads and demos aggregate per-mod failures, hosted-pack install
  failures propagate, invalid single-pack validation fails, and Nexus/update
  status errors no longer report process success.
- **BUG-047 — profile state is saved before files succeed (Medium):** profile
  enable and disable now run through a Core service that resolves the proposed
  state, applies file changes, and saves only after success. Failed enables roll
  back mods installed by that operation; a save failure after disable attempts
  to restore the removed mod. Uninstall now preflights every tracked file so a
  modified file stops removal before any sibling file is deleted.
- **BUG-048 — overlapping persistence loses journal and profile updates (Medium):**
  the three game-state stores now share one atomic JSON writer. A path-scoped
  cross-process lock covers each complete read-modify-write operation, temporary
  files are unique, and replacement keeps the previous version as a backup.
  Corrupt journals retain their existing recovery behavior; corrupt profiles
  still fail closed instead of being treated as "enable everything".
- **BUG-049 — temporary cleanup can replace a valid result (Medium):** planning,
  preview and installation now use one best-effort cleanup helper. A locked or
  unavailable temporary file can leave its workspace behind, but can no longer
  turn an otherwise controlled success or failure into an unexpected exception.
- **BUG-050 — desktop installs block the UI and logo requests leak clients (Medium):**
  the desktop window now runs the complete hosted-install pipeline away from the
  UI thread, reuses its existing HTTP client for logos, and disposes that client
  when the window closes. Update checks now dispose their parsed JSON document.
- **BUG-051 — desktop navigation and gallery do not scale (Medium):** the desktop
  now uses a persistent navigation rail with separate Browse, Manage and Settings
  pages. Browse has a fixed filter column, responsive large-card gallery and a
  focused pack-details window. Optional catalog metadata drives search, content,
  size, mod, tag and installed-pack filters without breaking older indexes.
  NSFW packs remain hidden by default and require an explicit filter selection;
  Nexus still enforces account restrictions when files are requested.
- **BUG-052 — gallery controls lose contrast and waste space (Medium):** the
  redundant game selector and local age-confirmation flow were removed, cards
  were reduced to a 3-by-3 default viewport, and checkboxes now use an explicit
  theme whose checked, unchecked and hover states stay visible. The NSFW filter
  is an unchecked opt-in rather than a disabled control.
- **BUG-053 — desktop account setup is CLI-only (Medium):** Settings now shows
  Nexus sign-in status and exposes OAuth sign-in and sign-out using the existing
  PKCE/token store. Branding now uses the full product name and the Refresh
  label is explicitly centred within its button.
- **BUG-054 — overlapping operations can corrupt an installation (High):** all
  file-changing operations now take a cross-process lock keyed to the selected
  game folder. A profile change or deployment keeps that lock for its complete
  file-and-journal transaction, while direct install, uninstall, enable and
  disable calls protect themselves. A second process receives a clear retry
  message instead of working against half-changed files.
- **BUG-055 — expired OAuth session assumes a refresh token (Medium):** token
  renewal now detects a missing refresh token before making a request and asks
  the user to sign in again. OAuth exchanges also dispose the HTTP client when
  the helper created it, while leaving caller-owned clients alone.
- **BUG-056 — a transient GitHub failure empties the modpack gallery (Medium):**
  catalog and manifest requests now retry rate limits and server errors with
  bounded backoff, honoring `Retry-After`. A successfully parsed index is saved
  atomically and used as a clearly labelled fallback when a later refresh fails.
  Covered by rate-limit recovery and last-good-cache tests.
- **BUG-057 — a later mod failure leaves earlier pack changes installed (High):**
  deployment now snapshots every destination and the complete install journal
  after preflight but before copying. If any mod fails, affected paths are
  restored in reverse mod order and the original journal is written back. This
  preserves both newly installed and previously updated mods as one transaction.
  Covered by new-install and update rollback tests.
- **BUG-058 — folder-first discovery loses or invents mods (Medium):** inventory
  now starts with journal ownership, keeping root, framework, plugin and patcher
  files together as one managed mod. Remaining physical content is labelled as
  unmanaged with its location, framework subdirectories form one entry, and
  same-named folders under different roots are retained separately.
- **BUG-059 — internally created HTTP clients are never disposed (Medium):**
  catalog, update and Nexus services now track whether they created their client
  and dispose only clients they own. Hosted-pack downloads reuse one client and
  Nexus source for the complete operation; the desktop and CLI inject or dispose
  clients at their natural lifetime boundaries. Covered by an ownership test
  that ensures disposing a service never closes a caller-supplied client.
- **BUG-060 — diagnostic and desktop logs grow without a memory bound (Medium):**
  support-bundle diagnostics now read only the final 1 MiB of the session log
  and retain the requested number of complete lines. The desktop log keeps its
  latest 500 lines and rebuilds the text box from that bounded queue instead of
  repeatedly appending to an ever-growing string. Covered by large-file tail,
  empty-file and zero-line tests.
- **BUG-061 — local HTTP server buffers files and abandons client tasks (Medium):**
  request headers are now read in chunks with the existing 8 KiB limit, folder
  responses stream full files and ranges from disk, and active client handlers
  are tracked and cancelled during shutdown. Folder serving also refuses paths
  outside its configured root. Covered by fragmented-header, large-range and
  incomplete-request shutdown tests.
- **BUG-062 — desktop release omits native runtime libraries (High):** the
  publish script now embeds native dependencies in the self-extracting
  single-file executables, matching the two executable assets uploaded by the
  release workflow. Local and GitHub packages also include the license, privacy
  policy and third-party notices.
- **FEATURE-001 — hosted packs support required and optional mods:** manifest
  entries now default to required for backward compatibility and may opt into
  user selection with `"required": false`. The desktop locks required choices,
  starts optional choices clear, and keeps dependency selections consistent.
  Core resolves and validates the final subset before downloading anything,
  and the pack journal preserves the selection for later updates.
- **FEATURE-002 — modpacks declare compatible game builds:** the manager reads
  the selected installation's Steam build id and compares it with build ids in
  the catalog and manifest. Matching packs are confirmed as compatible;
  mismatches, unknown builds and undeclared packs are marked as potentially
  unsupported and require desktop acknowledgement before installation.

## 2026-08-17 red-team review

Findings are handled in severity order. Critical download and persisted-state
boundaries were fixed before work continued on application-state defects.

- **BUG-063 — hosted manifests can write outside the download workspace
  (Critical, fixed):** hosted installs now validate the complete manifest before
  making a request or creating a download. The downloader independently contains
  every destination path, rejects malformed expected hashes, and keys cached
  files by a validated content hash rather than a manifest-controlled mod id.
  Covered by pre-network manifest rejection, traversal, invalid-hash and isolated
  cache regression tests.
- **BUG-064 — a tampered install journal can delete files outside the game
  (High, fixed):** every journal read and write now verifies that tracked files
  remain under the selected game folder. Lifecycle commands fail with a useful
  error and leave the external file untouched. Covered by a hostile-journal
  uninstall regression test.
- **BUG-065 — deselecting an optional pack mod leaves it installed
  (High, fixed):** hosted updates now remove deselected optional entries and
  entries retired from the new manifest. The complete pack change runs under one
  game-operation lock with a pack-level snapshot of active or disabled files and
  both journals. A modified file blocks removal and restores the previous pack
  version, selection, files and ownership data. Covered by successful deselection
  and unsafe-removal rollback tests.
- **BUG-066 — ZIP entries can use Windows path aliases (Medium, fixed):** ZIP
  extraction now rejects alternate data-stream syntax, reserved device names,
  dot segments, and segments ending in a dot or space before writing any entry.
  Covered by Windows-path-alias extraction theories.
- **BUG-067 — desktop dependency has a known High advisory (High, fixed):** the
  application no longer suppresses GHSA-xrw6-gwf8-vvr9 for the old Linux DBus
  library. It pins the patched `Tmds.DBus.Protocol` 0.94.2 release instead, and
  the complete solution builds cleanly with that override.
- **BUG-068 — malformed manifests can bypass friendly validation (Medium,
  fixed):** local and hosted readers now normalize missing mod and relationship
  arrays, BepInEx ordering tolerates a missing mod list, and validation rejects
  malformed hashes, archive names, download URLs, Nexus ids, and negative sizes
  without dereferencing null input. Covered by malformed-manifest theories.
- **BUG-069 — a manifest can target a different game (High, fixed):** manifest
  validation now requires the TCG Card Shop Simulator game domain before any
  hosted request or local deployment can proceed.
- **BUG-070 — directory links can redirect managed writes and deletes outside
  the game (High, fixed):** install destinations and journal paths now reject
  any existing symbolic link or junction below the selected game root in
  addition to lexical containment. Covered by a linked-plugin-folder regression
  where no payload reaches the external target.
- **BUG-071 — disabled files from different game installs share one directory
  (High, fixed):** default disabled storage is now scoped by a stable hash of the
  normalized game path. Two installations can disable the same mod without one
  deleting the other's parked copy. Existing unscoped files remain discoverable
  as a legacy fallback. Covered by a two-game isolation regression test.
- **BUG-072 — rebuilt releases are invisible to the update checker (High,
  fixed):** release builds now append the monotonically increasing GitHub run
  number to the checked-in base version and use that exact version for build,
  publish, tag and release metadata. An older executable can therefore detect a
  newer push even when the base product version remains `0.3.0`. Covered by
  fourth-component update-check theories.
- **BUG-073 — a newer push can cancel a half-published release (Medium, fixed):**
  main-branch release jobs now queue instead of cancelling one another. A push
  can no longer interrupt the workflow after tagging but before all checked and
  hashed assets have been uploaded.
- **BUG-074 — process termination can leave an unjournaled partial deployment
  (High, fixed):** deployments and hosted-pack changes now write their original
  files and journals to a durable transaction directory before changing managed
  state. Backups and transaction markers are flushed to disk, successful work
  writes a committed marker before cleanup, and the next game-locked operation
  automatically rolls back any uncommitted records. Hosted recovery covers both
  journals and active or disabled pack files. Recovery paths are containment and
  reparse-point checked so a tampered record cannot reach outside managed
  storage. Covered by interrupted new-install, update, hosted-pack, committed
  transaction, and hostile-record recovery tests.
- **BUG-075 — Nexus file discovery crashes on null numeric metadata (Medium,
  fixed):** Nexus file-list records may explicitly set fields such as
  `size_in_bytes` or `file_id` to null. Numeric parsing now accepts numbers or
  numeric strings, treats null or malformed optional values as absent, and
  skips records without a usable file id. Covered by null/string metadata tests
  and a successful live lookup of mod 577.
- **BUG-076 — import selectors reject trailing descriptions (Low, fixed):**
  modpack import lines now allow a trailing comment introduced by whitespace
  and `#`, so authors can label large lists without making a valid
  `nexus:<modId>:<fileId>` selector fail parsing. URL fragments without a
  preceding space remain intact. Covered by a CLI parser smoke test.
- **BUG-077 — HTTP shutdown test rejects a valid connection reset (Low,
  fixed):** server disposal now closes clients that connected but were still
  queued for acceptance, and the incomplete-client shutdown test accepts either
  orderly EOF or the Windows reset/abort socket results that mean the server
  closed the connection. A connection that remains open until the timeout still
  fails.
- **BUG-078 — hosted installs give no useful progress or option review (Medium,
  fixed):** pack details now separate required and optional mods, show the
  optional selection count, and require a final review of those choices before
  downloading. The install view reports the current file, byte progress and
  download speed, then clearly marks the file-installation phase. Optional
  choices are locked while work is running. Core progress reporting is covered
  by `ModpackInstaller_ReportsDownloadAndInstallProgress`.
- **BUG-079 — Nexus packs can start without credentials and hide the failure
  (Medium, fixed):** pack details now detect whether the selected required and
  optional mods use Nexus. Without OAuth or a saved personal API key, the
  install button stays disabled and the dialog offers both setup routes. Other
  install failures now display the report returned by Core instead of only
  directing the user to logs.
- **BUG-080 — large archives are rejected and failed retries redownload the pack
  (High, fixed):** production extraction limits now accommodate large game-mod
  assets, including archives up to 64 GiB extracted, while retaining path, type,
  entry-count, per-file and total-size protection. The limit applies per archive,
  not to the combined pack. Hosted installs keep hash-verified archives in a
  persistent local cache separate from their disposable workspace, so a
  planning or installation failure can be retried without contacting the source
  again. Covered by the large-archive policy and failed-planning cache-reuse
  regressions.
- **BUG-081 — identical leftovers block migration from a manual install
  (Medium, fixed):** a real install found an empty manager journal and no
  `BepInEx` directory, but four unmanaged Doorstop bootstrap files remained in
  the game root. Every file was byte-for-byte identical to the verified BepInEx
  archive, yet preflight reported ordinary destination conflicts and blocked the
  complete pack. Installation now adopts an identical existing file into the
  journal with a preservation marker instead of copying it. Updates cannot
  replace adopted files, disable leaves them active, and uninstall leaves them
  on disk while clearing the journal. Different content still blocks the install.
  Covered by adoption, update and uninstall regressions.
- **BUG-082 — bundled shared files make the published overhaul uninstallable
  (High, fixed):** the 22 required Real TCG Overhaul archives claimed 1,303
  destinations, with 89 paths claimed by more than one mod. Eighty-eight are
  byte-identical shared assets or generated configuration files, while
  `BepInEx/config/munch.PhoneOverhaul.cfg` has genuinely different versions in
  the Phone Overhaul and expansion archives. Mod entries can now exclude exact
  archive-relative files or directory trees through validated
  `excludedArchivePaths`. The corrected manifest gives every shared destination
  one owner and retains the newer expansion-provided Phone Overhaul settings.
  All 24 cached archives now plan 1,400 files with no conflicts or unmatched
  exclusions. Long conflict reports are capped at 20 paths with a remaining
  count. Covered by exclusion, ownership and report regressions.
- **BUG-083 — the published framework cannot install its bootstrap DLL (High,
  fixed):** the classifier rejected root `winhttp.dll` from the reserved BepInEx
  entry, which would leave the framework unable to load even after pack
  conflicts were resolved. The hash-verified framework entry may now place its
  bootstrap DLL beside the game executable. The same DLL remains refused for
  ordinary mods. Covered by framework and non-framework classifier regressions.
- **BUG-084 — top-level plugin trees install outside BepInEx (High, fixed):**
  archives rooted at `plugins/` were treated as game-root content, so the Real
  TCG Overhaul API dependency landed in an unused game `plugins` folder and
  BepInEx could not load Enhanced Prefab Loader. The classifier now mirrors this
  common archive layout into `BepInEx/plugins/`. Covered by a classifier
  regression and confirmed against the cached API archive from the real install.
- **BUG-085 — wrapped plugin folders install at the game root (High, fixed):**
  Texture Replacer's archive contains one `TextureReplacer/` folder with its DLL
  and supporting data inside it. The classifier treated that as game-root data,
  so BepInEx never discovered the installed DLL. A single archive folder with a
  plugin DLL directly inside it is now preserved under `BepInEx/plugins/`.
  Covered by a wrapped-plugin regression and confirmed against the real archive.
- **BUG-086 — desktop modpacks must be uninstalled one mod at a time (Medium,
  fixed):** installed pack details now offer a confirmed **Uninstall modpack**
  action backed by Core. It removes journaled pack entries in reverse install
  order under the game lock and uses the durable pack snapshot to restore an
  earlier removal if a later mod is modified or another failure occurs. Covered
  by successful full-pack and rollback regressions.
- **BUG-087 — installed pack state and updates are easy to miss (Medium,
  fixed):** the Browse page now names the installed pack and version, with a
  green card banner for a newer published pack version. Core refuses an
  accidental second hosted-pack install. The explicit desktop switch retains
  shared mod ids, removes old-only entries, installs the destination selection
  and restores the original files and journals if any stage fails. A persistent
  navigation button launches the game through Steam. Covered by second-pack,
  successful-switch and failed-switch regressions.
- **BUG-088 — gallery artwork and compatibility text overflow their cards
  (Low, fixed):** pack artwork now fits within the preview area without being
  cropped or stretched, and taller cards keep the compatibility warning inside
  their border. The installed-pack heading and persistent launch action were
  confirmed at the normal desktop size during the same visual review.
- **BUG-089 — modpack switching does not show the planned changes (Low,
  fixed):** the switch confirmation now reports how many mods will be kept,
  updated, removed and added. A shared Core planner derives those categories
  from stable mod ids and archive hashes, with regression coverage for all four
  outcomes and legacy journal entries.
- **BUG-090 — desktop appearance cannot be adapted for accessibility (Medium,
  fixed):** Settings now offers system, light, dark and high-contrast themes,
  plus independent text and modpack-card sizes. Choices are stored per user and
  restored at launch. Shared button styles keep their labels readable in every
  palette, system-theme changes refresh the palette, and large text widens the
  navigation and Browse filters instead of clipping their contents. Preference
  persistence is covered by Core tests; every option and the corrected layouts
  were reviewed in the running desktop app.
- **BUG-091 — Nexus pack imports reject common non-ZIP archives (Medium,
  fixed):** the importer and shared Core extraction registry now accept RAR,
  7Z, TAR, GZ, TGZ, BZ2 and XZ alongside ZIP. Non-ZIP content is read through
  SharpCompress but still passes the manager's path, link, file-type, duplicate
  and size checks before it can enter an installation plan. Encrypted and
  multi-volume archives remain unsupported. Extension coverage and protected
  compressed-TAR extraction have regression tests.
- **BUG-092 — Collect Em All cannot safely replace Real TCG Overhaul (High,
  fixed; pack temporarily removed):** the published manifests now use the same
  stable ids for their eight shared Pokemon archives, so a switch keeps those
  entries instead of trying to uninstall them. Collect Em All also declares one
  owner for every bundled configuration and asset path found during a complete
  destination audit. The corrected pack plans 82 archives and 6,753 files
  without a conflict.
- **BUG-093 — solid 7Z archives are reopened for every entry (Medium, fixed):**
  7Z extraction now uses one sequential reader for the complete archive. This
  avoids restarting decompression for each file in a solid archive. The archive
  classifier also strips one outer folder when it contains a complete BepInEx
  tree, preserving the intended layout without weakening root-DLL protection.
  Covered by multi-entry 7Z and wrapped-layout regressions.
- **BUG-094 — large installs appear frozen after downloading (Medium, fixed):**
  hosted install progress now distinguishes preparation, archive planning and
  file installation. Pack details name the current archive or mod and show its
  position in the remaining work while the animated progress bar continues.
  Core reports each stage through the existing progress contract, covered by
  the hosted-install progress regression.
- **BUG-095 — large-pack preflight fills the temporary drive (High, fixed):**
  preflight now discards each archive's extracted scratch files as soon as its
  destination plan has been recorded, rather than retaining the expanded
  contents of every archive until all conflict checks finish. ZIP and other
  extractors now identify duplicates before writing, so disk and directory I/O
  failures are no longer misreported as thousands of duplicate entries. Empty
  archive errors also cap rejection details at ten paths plus a remaining count.
  Covered by ZIP duplicate and write-failure regressions.
- **BUG-096 — Collect Em All includes an entirely excluded mod (High, fixed;
  pack temporarily removed):**
  the MTG Shop archive contains only the same six phone-app images already
  supplied by the base Magic pack. Excluding that overlap left the entry with
  nothing to install and caused a late rollback. The redundant Nexus entry has
  been removed from the manifest, index and retained selector list. A complete
  cached-archive audit now finds 81 non-empty plans, 6,753 destinations and no
  conflicts.
- **BUG-097 — Normal gameplay prevents complete modpack uninstall (High,
  fixed):** BepInEx rewrites cache data and mods update their configuration
  after the game runs. Complete uninstall treated those expected changes like a
  modified plugin binary, rolled back every removal and left the pack
  installed. Modified configuration is now kept as user state, regenerated
  BepInEx cache files can be removed, and later installs reuse existing
  configuration without overwriting it. Ordinary modified files still stop
  uninstall. Pack failures now put the actual blocker before rollback details,
  while successful desktop uninstalls show warnings for retained files. Covered
  by focused configuration, cache and rollback-order regressions.
- **BUG-098 — Gallery cards clip pack descriptions (Low, fixed):** standard
  cards allowed only two description lines, while large text allowed three
  lines inside a card sized for less text. Both cut off longer pack
  descriptions. Standard and large-text cards now reserve enough vertical space
  for four lines, including the combined large-card mode, and the navigation
  branding has additional left inset so it doesn't touch the window edge.
- **BUG-099 — updated expansion archives conflict over their bundled bridge
  plugin (High, fixed):** the August 25 Pokemon archive updates added
  `HolographicBundleBridge.dll` to Generation 1, Generation 2 and Pocket A1.
  The hosted manifests let all three entries claim the same destination, so a
  clean install of either overhaul stopped during preflight. Generation 1 now
  owns the shared plugin and the other two entries exclude their bundled copy.
  Both pack versions were advanced so installed copies receive the correction.
- **BUG-100 — a missing workspace archive ignores the verified cache (Medium,
  fixed):** a Cardverse install downloaded every selected mod but reached
  preflight without the temporary copy of Pokemon Shop Textures. The verified
  archive was still present in the persistent cache, but planning checked only
  the disposable workspace and stopped the complete installation. Hosted
  installs now restore any missing workspace copy from the verified cache
  immediately before planning. Covered by a regression that removes the
  workspace archive at that boundary and confirms no second network request.
- **BUG-101 — moving the game folder strands managed mods (Medium, fixed):**
  install journals recorded absolute file locations, so moving a complete Steam
  installation made every managed path appear to escape the newly selected game
  folder. Journals now store paths relative to their game folder, resolve and
  validate them only when used, and atomically migrate legacy absolute paths
  when the original and current game folders have the same name. Hostile and
  traversal paths remain outside that migration and are still rejected. Covered
  by storage, moved-folder and hostile-journal regressions.
- **BUG-102 — recovery failures are reported as another running operation
  (Medium, fixed):** the game-operation lock now limits its contention handling
  to opening the lock file. If durable recovery fails after the lock is
  acquired, the actual recovery error reaches the user instead of being retried
  and replaced with a misleading message that survives a restart. Covered by a
  recovery-failure regression test.
- **BUG-103 — desktop support bundles omit install failures and game state
  (Medium, fixed):** pack-install failures now reach the diagnostic log, and the
  desktop passes its selected game folder to support-bundle creation. Bundles
  include the install, modpack and profile journals plus pending recovery state,
  so a failed recovery can be diagnosed from the exported files. Covered by a
  support-bundle content regression test.
- **BUG-104 — pack recovery rewrites unchanged locked files (High, fixed):**
  pack and pack-switch rollback now remove only files added after their recovery
  snapshot and skip a backup copy when the destination already has identical
  content. An unchanged loader DLL no longer prevents recovery merely because
  the running game has it open. Covered by locked-file recovery tests.
- **BUG-105 — update preflight rejects mutable runtime files (High, fixed):**
  deployment preflight now applies the installer's existing mutable-file policy
  when checking for modified managed files. Changes under `BepInEx/config` and
  `BepInEx/cache` no longer block a pack update; the installer keeps modified
  configuration through its existing preservation path. Covered by a runtime
  configuration and cache regression test.
- **BUG-106 — hosted operations duplicate large pack data (High, fixed):**
  verified cache files now use same-volume Windows hard links in disposable
  install workspaces, with copies retained as the fallback. Pack updates also
  use the durable recovery transaction as their only rollback snapshot instead
  of copying every installed file into a second temporary backup. Covered by
  cache-storage, download, update and rollback tests.
- **BUG-107 — support export gives feedback on another page (Low, fixed):** the
  Settings maintenance card now reports export progress, the saved bundle path
  or the failure beside the export action and prevents duplicate clicks while
  work is running. The existing Manage-page log remains available as history.
- **BUG-108 — persistent downloads grow without user controls (Medium, fixed):**
  Settings now reports the file count and total size of the verified archive
  cache. Users can clear it after confirmation without changing installed mods;
  unexpected directories and linked files are left alone. Covered by missing,
  populated and cleanup boundary tests.
- **BUG-109 — desktop installs cannot be cancelled (Medium, fixed):** pack
  details now shows a cancel action while installation is running. Downloads
  remove their partial file through the existing cancellation path; planning
  stops before the next archive, and file installation stops between mods and
  rolls back completed changes. Covered by a cancellation rollback regression.

## Summary
| Severity | Open | Fixed |
|----------|------|-------|
| Critical | 0 | 3 |
| High     | 0 | 40 |
| Medium   | 0 | 51 |
| Low      | 0 | 15 |
| **Total**| **0** | **109** |

## Status table
| BUG | Sev | Area | Title | Status | Files to change | Fix | Why / PR | Verified |
|-----|-----|------|-------|--------|-----------------|-----|----------|----------|
| BUG-001 | Critical | archive/classifier (security) | Game-root loader-hijack DLL placed via BepInEx-layout mirror | Fixed | ArchiveClassifier.cs | Denylist of known DLL-hijack target names (winhttp/version/winmm/dbghelp/d3d*/dxgi/…) refused only at the sensitive roots (game root + BepInEx/ root); framework tree (incl. BepInEx/core/doorstop.dll) mirrors freely | BUG-001: allowlist wrongly rejected the framework's own BepInEx/core/doorstop.dll and still let hijack DLLs reach the game root; denylist blocks the exact attack vector without breaking the framework | Verified (unit + 104-test suite + build) |
| BUG-002 | High | modpack validate | Validator crashes (ArgumentNull) on malformed index/manifest | Fixed | ModpackSubmissionValidator.cs | `ValidatePack`/`ValidateAll` now guard a null `Packs` array (return a structural failure instead of letting LINQ throw) | BUG-002: a malformed/missing `packs` array must be a clean failure, not an unhandled crash | Verified (unit + suite) |
| BUG-003 | High | update detection | GUI never records pack journal -> feature dead in app | Fixed | MainWindow.cs, ModpackInstaller.cs | GUI now forwards the selected pack into `InstallAsync(manifest, fallback, pack: pack)` so the `ModpackJournalStore.Record` call on success writes `cardshopmodmanager.modpacks.json` | BUG-003: the update badge/button depend on the journal; without it the feature was dead in the app | Verified (build + source trace) |
| BUG-004 | High | pack journal | Corrupt modpacks.json throws unhandled, blocks all install/upgrade | Fixed | ModpackJournalStore.cs | `Load()` now catches `JsonException`, backs the bad file up to `.corrupt`, and returns an empty list | BUG-004: a corrupt pack journal must never abort an otherwise-successful install/upgrade | Verified (unit + suite) |
| BUG-005 | High | pack journal | Uninstall never clears pack journal -> stale "Update available" | Fixed | ModpackJournalStore.cs, ModInstaller.cs | Added `ModpackJournalStore.Remove(packId)`; `Install` now records `PackId` on the per-mod journal entry, and `Uninstall` drops the pack entry when no journaled mod still belongs to it | BUG-005: a pack's last mod being uninstalled must clear the stale "Update available" badge | Verified (unit + suite) |
| BUG-006 | High | update detection | v-prefixed / pre-release versions never detected as updates | Fixed | ModpackVersion.cs | `IsNewer` now normalizes versions: it strips a leading `v` and a trailing `-prerelease`/`+build` label and parses the numeric components (missing default to 0), so a genuinely newer `v1.3.0` / `1.3.0-beta` is detected | BUG-006: v-prefixed / pre-release versions must be recognized as newer when the numeric base is higher | Verified (unit + suite) |
| BUG-007 | Medium | update detection | Spurious "Update available" on component-count change (1.0 vs 1.0.0) | Fixed | ModpackVersion.cs | Normalization pads the four numeric components to 0, so `1.0` and `1.0.0` compare identical and no longer spuriously flag an update | BUG-007: `1.0` and `1.0.0` are the same version and must not show "Update available" | Verified (unit + suite) |
| BUG-008 | Medium | UI | Corrupt journal wipes entire Modpacks gallery | Fixed | MainWindow.cs | `ReadInstalledPacks()` is now wrapped in its own try/catch inside `LoadPacksAsync`, so a corrupt/unreadable pack journal only suppresses update badges (with a warning) and the index gallery still renders | BUG-008: a bad pack journal must never abort gallery rendering | Verified (build + source analysis) |
| BUG-009 | Medium | pack journal | Pack id rename orphans journal entry / breaks tracking | Fixed | ModpackIndex.cs, ModpackJournalStore.cs, MainWindow.cs | `ModpackSummary` gained `FormerIds` + `IsId()`; `IsUpdateAvailable` matches the canonical id or any legacy `FormerId`, and `ReadInstalledPacks` rewrites a legacy stored `PackId` to its canonical id (persisted) so the entry is not orphaned and the next `Record` can cleanly replace it | BUG-009: a pack-id rename must not break update detection or leave the old journal entry lingering | Verified (unit: IsId + build + source) |
| BUG-010 | Medium | pack journal | Journal write non-atomic, no backup -> self-perpetuating corruption | Fixed | ModpackJournalStore.cs, JournalStore.cs | Both stores now write via temp-file + rename with a `.bak` of the previous good content | BUG-010: a crash mid-write must not leave an unreadable journal | Verified (unit + suite) |
| BUG-011 | High | lifecycle | disable/enable silent no-op for framework/game-root mods, reports success | Fixed | ModInstaller.cs | `Disable`/`Enable` now count managed/non-managed/skipped files and return non-success when a framework/game-root mod is not something we toggle | BUG-011: toggling a non-managed mod must report failure, not silent success | Verified (unit + suite) |
| BUG-012 | Medium | lifecycle | `mods list` blind to framework/game-root mods | Fixed | ModDiscovery.cs | Added `BepInEx/core` to `ModDiscovery.ActiveRoots` so framework mods are enumerated | BUG-012: `mods list` must report every installed mod, including framework/core | Verified (unit + suite) |
| BUG-013 | High | lifecycle | Partial disable leaves modified file active, reports success | Fixed | ModInstaller.cs | `Disable` tracks kept-vs-moved; if any managed file was kept (modified), it returns non-success with a "partially disabled" message | BUG-013: a partial disable must be reported as failure, not success | Verified (unit + suite) |
| BUG-014 | High | lifecycle | uninstall removes journal entry even when a file was kept -> mod stranded | Fixed | ModInstaller.cs | `Uninstall` only calls `_journal.Remove` when every file was actually deleted; a kept (modified) file retains the entry | BUG-014: an incomplete uninstall must keep the journal entry so the mod stays tracked | Verified (unit + suite) |
| BUG-015 | High | lifecycle | corrupt journal breaks every operation, no recovery | Fixed | JournalStore.cs | `Load()` now catches `JsonException`, backs the bad file up to `.corrupt`, and returns an empty list | BUG-015: a corrupt per-mod journal must not abort every lifecycle op; recover to empty | Verified (unit + suite) |
| BUG-016 | High | lifecycle | re-install disabled mod then disable again -> "The file exists" crash | Fixed | ModInstaller | `Disable` now deletes a stale disabled copy before `File.Move` instead of throwing | BUG-016: re-disabling a reinstalled mod must not crash with "file already exists" | Verified (unit + suite) |
| BUG-017 | High | install | `install` reports success (exit 0) even when a mod fails | Fixed | DeploymentService.cs | The install loop now tracks `anyFailure`; when any mod's `InstallResult.Success` is false the whole report returns `DeploymentReport.Failure`, so `InstallCommand` sets a non-zero exit code | BUG-017: a failed mod must make the command fail, not report success because another mod installed | Verified (unit + suite) |
| BUG-018 | Low | lifecycle/UI | enable/disable of never/already-disabled mod reports success doing nothing | Fixed | ModInstaller.cs, InstallPlan.cs, ModsCommand.cs | `DisableResult`/`EnableResult` gain a `Message` (e.g. "Already disabled/enabled"); CLI prints it | BUG-018: toggling an already-target-state mod must report a distinct "already" status | Verified (unit + suite) |
| BUG-019 | Medium | conflicts | install pre-flight conflict ignores already-installed mods | Fixed | DeploymentService.cs, DestinationConflictFinder.cs | `DestinationConflictFinder.Find` gained an `installedPlans` overload that seeds already-owned destinations from the journal; `DeploymentService.Install` feeds `BuildInstalledPlans(gameFolderPath)` so a pending mod colliding with an installed file is refused at pre-flight | BUG-019: a pending mod must not be allowed to overwrite a file owned by an installed mod, caught before any byte is copied | Verified (unit + suite) |
| BUG-020 | Medium | resolver | BepInEx-first ordering NOT enforced for local install/validate | Fixed | DeploymentService.cs, ModpackInstaller.cs | `DeploymentService.Validate` and `Install` now call `ModpackInstaller.EnforceBepInExFirst` before resolving, so the local path guarantees BepInEx sorts first (previously only the hosted-modpack path did) | BUG-020: plugins must load into an existing framework; BepInEx-first must hold for local validate/install, not just hosted packs | Verified (unit + CLI smoke + suite) |
| BUG-021 | Low | resolver | wrong-case dependency/id refs silently accepted | Fixed | ModListResolver.cs | `ModListResolver` now keeps an exact-case id index (`allByIdExact`); a dependency/conflict reference that matches a real id only by case is reported as a hard error instead of being silently accepted | BUG-021: ids are stable keys; a case-only mismatch must be flagged so pack authors fix the reference | Verified (unit + suite) |
| BUG-022 | Medium | archive security | archives with executables not rejected outright (banned .exe dropped, rest installs) | Fixed | ModInstaller.cs, ZipArchiveExtractor.cs, InstallPlan.cs, DeploymentService.cs | ExtractionResult.RejectedEntries now flow into InstallResult.RejectedEntries and are surfaced as warnings by DeploymentService (was a silent drop behind a success message) | BUG-022: a bundled .exe must be flagged loudly, not hidden behind success | Verified (unit + suite) |
| BUG-023 | Medium | archive | oversized archives install partially and report success | Fixed | ZipArchiveExtractor.cs, ModInstaller.cs | ExtractionResult.Truncated (set on entry/size cap) now makes CreatePlan throw InvalidDataException, so a partial copy is never installed | BUG-023: a truncated extraction must fail loudly, not install partial + report success | Verified (unit + suite) |
| BUG-024 | Medium | validation | safe archive filenames with ".." (MyMod..v1.zip) falsely rejected | Fixed | ManifestValidator.cs | Replaced the `Contains("..")` substring test with a segment-based traversal check (rejects a `..` *segment* or rooted path, allows `..` inside a filename) | BUG-024: `MyMod..v1.zip` is a safe filename and must validate | Verified (unit + suite) |
| BUG-025 | Medium | validation | installType "BepInEx" accepted for non-bepinex id on local path | Fixed | ManifestValidator.cs | Reserved the `BepInEx` install type for the framework entry (id `bepinex`); a non-framework mod claiming it is now rejected | BUG-025: `BepInEx` is the framework's reserved type and must not be used by ordinary mods | Verified (unit + suite) |
| BUG-026 | Low | UX | malformed manifests surface raw serializer exceptions | Fixed | DeploymentService.cs | `Validate`/`Install` now catch `JsonException`/`InvalidOperationException` from `ManifestReader.Read` and return a friendly `DeploymentReport.Failure` ("Manifest is not valid JSON: ...") instead of the raw serializer message | BUG-026: a malformed manifest must read as a clear user-facing error, not the internal JSON parser exception | Verified (unit + CLI smoke + suite) |
| BUG-027 | Low | CLI | install with <3 args prints usage but exits 0 | Fixed | InstallCommand.cs, ValidateCommand.cs, PlanCommand.cs | All three commands now set `Environment.ExitCode = 2` on the usage branch, matching the unknown-command handling in `Program.cs` | BUG-027: a usage/misuse error must be detectable by callers via a non-zero exit code | Verified (CLI smoke + suite) |
| BUG-028 | Low | validation | empty mods list validated as valid (no warning) | Fixed | ManifestValidator.cs | `Validate` now reports an error when `Mods` is null/empty | BUG-028: an empty pack must be surfaced, not silently "valid" | Verified (unit + suite) |
| BUG-029 | Medium | classifier | loose .dll at root alongside BepInEx/ lands in game root, not BepInEx/plugins | Fixed | ArchiveClassifier.cs | In the BepInExLayout branch, a root-level .dll now routes to BepInEx/plugins/<mod>/ instead of mirroring to the game root | BUG-029: loose plugin DLL must live under plugins, never the game root where the loader could pick it up | Verified (unit + suite) |
| BUG-030 | Low | archive | nested .zip installed as-is, unvalidated | Fixed | ZipArchiveExtractor.cs, ArchiveProtectionSettings.cs | ArchiveProtectionSettings.Default now rejects archive extensions (.zip/.7z/.rar/.tar/.gz/.tgz/.bz2/.xz) so a nested archive is refused, not written unvalidated | BUG-030: a nested archive bypasses all protection checks if written verbatim | Verified (unit + suite) |
| BUG-031 | High | modpack validate | `modpack validate` (all) reports "All packs valid." when index.json missing | Fixed | ModpackSubmissionValidator.cs, ModpackCommand.cs | `ValidateAll` returns a single `(index.json)` failure entry when index is missing; `ModpackCommand` sets a non-zero exit code | BUG-031: a missing index must be a clear failure, not "All packs valid." | Verified (unit + suite) |
| BUG-032 | High | modpack validate | BepInEx framework accepted with wrong installType "BepInExPlugin" -> VALID | Fixed | ManifestValidator.cs, ModpackSubmissionValidator.cs | Framework entry (id `bepinex`) must use `BepInEx` install type; `ModpackSubmissionValidator` enforces the exact type on the framework entry | BUG-032: a mislabeled framework entry must be INVALID | Verified (unit + suite) |
| BUG-033 | Medium | modpack validate | wrong manifest (different pack name) accepted as VALID | Fixed | ModpackSubmissionValidator.cs | Manifest/index name mismatch is now an error (was only a warning), so a mismatched manifest cannot validate as VALID | BUG-033: a manifest for a different pack must not validate as VALID | Verified (unit + suite) |
| BUG-034 | Low | modpack validate | no path sanitization for logo/manifest refs (traversal/absolute) | Fixed | ModpackSubmissionValidator.cs, ManifestValidator.cs | `ModpackSubmissionValidator` now rejects `..`/rooted `Logo`/`Manifest` references before resolving (consistent with archive handling) | BUG-034: traversal/absolute logo/manifest refs must be rejected | Verified (unit + suite) |
| BUG-035 | Medium | CLI UX | `modpack install` no-id throws "Unexpected error" after network fetch | Fixed | ModpackCommand.cs | `ModpackCommand` now validates the install id up front (before `FetchIndexAsync`) and prints a usage hint with exit code 2, so no wasted network round-trip | BUG-035: a missing pack id should be a usage hint, not a scary "Unexpected error" after fetching the index | Verified (CLI smoke) |
| BUG-036 | Medium | CLI UX | missing/bad args collapse into generic "Unexpected error" | Fixed | PlanCommand.cs, DeploymentService.cs | `PlanCommand` now validates the manifest's existence and JSON at the CLI boundary with clear messages and non-zero exit codes; `DeploymentService` already returns friendly not-found/invalid-JSON errors, so bad arguments no longer reach the generic top-level handler | BUG-036: argument *values* must be validated where they enter, reserving "Unexpected error" for genuine crashes | Verified (CLI smoke + suite) |
| BUG-037 | Medium | UI | RunHandler swallows exceptions -> stale UI state on thrown failure | Fixed | MainWindow.cs | `RunHandler` now logs the full exception type + message to the screen and writes the stack to the diagnostic log, instead of swallowing only `ex.Message` | BUG-037: a thrown failure must be diagnosable, not silently swallowed | Verified (build + source analysis) |
| BUG-038 | Medium | UI | WelcomeDetectAsync not wrapped -> unobserved exception at startup risk | Fixed | MainWindow.cs | `Opened` now routes `WelcomeDetectAsync` through `RunHandler` (try/catch + log) instead of an unguarded `async void` lambda | BUG-038: a startup-detection failure must be caught and logged, never an unobserved async-void exception | Verified (build + source analysis) |
| BUG-039 | Low | CLI | serve ignores SIGINT headless; no clean shutdown | Fixed | ServeCommand.cs | `ServeCommand` now also watches for stdin EOF and `AppDomain.ProcessExit` (alongside Ctrl+C) and disposes the server on each, so a headless/terminated run releases the listener cleanly | BUG-039: a server run with no console must still shut down cleanly on termination signal or stdin EOF | Verified (build + source analysis) |
| BUG-040 | Low | CLI | uninstall on non-existent game folder -> misleading "No journal entry" | Fixed | ModInstaller.cs, UninstallCommand.cs | `Uninstall` returns a distinct "Game folder not found" error; `UninstallCommand` validates the folder up front | BUG-040: a missing game folder must be distinguished from a missing journal entry | Verified (unit + suite) |

## Fix log
Detailed entries are appended here as bugs are resolved (files changed, what/why, verification).

### BUG-001 (Critical) + BUG-029 (Medium) — ArchiveClassifier.cs
- **Files:** `src/TCGCardShopSimModManager.Core/ArchiveClassifier.cs`, `tests/.../ArchiveClassifierTests.cs`
- **What:** Replaced the prior allowlist (only `plugins`/`patchers`/`config` under `BepInEx`) with a **denylist of known DLL search-order hijack targets** (`winhttp.dll`, `version.dll`, `winmm.dll`, `dbghelp.dll`, `d3d9.dll`, `d3d11.dll`, `dxgi.dll`, `dsound.dll`, `mscoree.dll`, `propsys.dll`, `userenv.dll`, `dinput8.dll`, `dwrite.dll`, `apphelp.dll`, `comctl32.dll`, `secur32.dll`, `cryptbase.dll`, `msimg32.dll`, `uxtheme.dll`, `ws2_32.dll`). A file bearing one of these names is refused **only when it would land at the game root or the `BepInEx/` root**; everything else (including the genuine framework's `BepInEx/core/doorstop.dll`) mirrors normally. Root-level `.dll`s in a `BepInExLayout` now route to `BepInEx/plugins/<mod>/` (BUG-029).
- **Why:** The allowlist wrongly rejected `BepInEx/core/doorstop.dll` (breaks the framework) and the original mirror logic still let `winhttp.dll`/`version.dll` reach the game root / `BepInEx/` root — the classic pre-launch RCE vector. The denylist blocks exactly that vector while permitting the framework to install.
- **Verification:** 11 ArchiveClassifier tests pass (incl. new `FrameworkDllUnderBepInExCore_IsAllowed`, `RootHijackDllInBepInExLayout_IsRejected`, `GameRootHijackDll_IsRejected`); full Core suite 104/104; `dotnet build` clean. Installer only writes `plan.Files`, so refused hijack DLLs are never written to disk.

### BUG-022 (Medium) + BUG-023 (Medium) + BUG-030 (Low) — archive security/extraction
- **Files:** `ZipArchiveExtractor.cs`, `ArchiveProtectionSettings.cs`, `ModInstaller.cs`, `InstallPlan.cs`, `ArchiveModels.cs`, `DeploymentService.cs` (+ tests `ZipArchiveExtractorTests.cs`, `ModInstallerTests.cs`)
- **What:**
  - BUG-030: `ArchiveProtectionSettings.Default` now rejects archive extensions (`.zip/.7z/.rar/.tar/.gz/.tgz/.bz2/.xz`); a nested archive is refused rather than written out unvalidated.
  - BUG-023: `ZipArchiveExtractor` already tracks `Truncated` on the entry/size cap; `ModInstaller.CreatePlan` now throws `InvalidDataException` when `result.Truncated`, so a partial copy is never installed and reported as success.
  - BUG-022: `ExtractionResult.RejectedEntries` flow into `InstallPlan`/`InstallResult` (new `RejectedEntries`/`SkippedEntries` fields), and `DeploymentService` surfaces them as warnings/notes — a banned `.exe` is no longer a silent drop behind a success message.
- **Why:** Each was a silent-failure / partial-install / bypass hole in the archive pipeline. The fixes make the pipeline fail loudly (truncation), refuse nested archives (bypass), and report rejections (executables).
- **Verification:** New tests `Extract_RejectsNestedZip`, `Extract_FlagsTruncationWhenSizeCapHit`, `CreatePlan_ThrowsOnTruncatedArchive`, `Install_SurfacesRejectedExecutable_WhileInstallingRest` all pass; full Core suite 104/104.

### BUG-004, BUG-005, BUG-010, BUG-011, BUG-012, BUG-013, BUG-014, BUG-015, BUG-016, BUG-018, BUG-040 — Journals & lifecycle (Workstream 2)
- **Files:** `JournalStore.cs`, `ModpackJournalStore.cs`, `ModDiscovery.cs`, `ModInstaller.cs`, `InstallPlan.cs`, `InstallJournal.cs`, `ModListManifest.cs`, `ModsCommand.cs`, `UninstallCommand.cs` (+ tests `ModInstallerTests.cs`, `ModDiscoveryTests.cs`)
- **What:**
  - BUG-015 / BUG-004 (High): `JournalStore.Load` and `ModpackJournalStore.Load` now catch `JsonException`, back the bad file up to `<journal>.corrupt`, and return an empty list — a corrupt journal no longer aborts every operation.
  - BUG-010 (atomic writes): both stores now write via temp-file + rename and keep a `<journal>.bak`, so a crash mid-write cannot leave an unreadable journal.
  - BUG-011 / BUG-013 / BUG-016 / BUG-018 (High/High/High/Low): `Disable`/`Enable` now count managed/non-managed/moved/kept files — non-managed framework/game-root mods report non-success (BUG-011), a partial disable reports failure (BUG-013), a stale disabled copy is cleared before `File.Move` (BUG-016), and an already-target-state toggle returns a distinct "Already disabled/enabled" `Message` (BUG-018).
  - BUG-014 (High): `Uninstall` only drops the journal entry when every file was actually deleted; a kept (modified) file retains the entry so the mod stays tracked.
  - BUG-005 (High): added `ModpackJournalStore.Remove(packId)`; `Install` records `PackId` on the per-mod entry (added `PackId` to `InstallJournalEntry`/`ModEntry`), and `Uninstall` clears the pack entry when no journaled mod still belongs to it.
  - BUG-012 (Medium): `ModDiscovery` now includes `BepInEx/core` in its active roots, so framework mods appear in `mods list`.
  - BUG-040 (Low): `Uninstall` returns a distinct "Game folder not found" error, and `UninstallCommand` validates the folder up front.
- **Why:** Each was a silent-failure / wrong-status / no-recovery hole in the lifecycle and journaling paths.
- **Verification:** New tests `Disable_FrameworkMod_ReportsNonSuccess`, `Disable_AlreadyDisabledMod_ReportsAlreadyDisabled`, `Disable_ReinstallThenDisable_DoesNotThrow`, `Uninstall_MissingGameFolder_ReportsGameFolderNotFound`, `Uninstall_KeepsJournalEntryWhenFileModified`, `Uninstall_LastModOfPack_ClearsPackJournal`, `JournalStore_ToleratesCorruptFile`, `ModpackJournalStore_ToleratesCorruptFile`, `Discover_FrameworkModUnderBepInExCore_IsListed`, and the updated `Disable_LeavesModifiedFileInPlaceAndReportsFailure` all pass; full Core suite 113/113.

### BUG-002, BUG-010, BUG-024, BUG-025, BUG-028, BUG-031, BUG-032, BUG-033, BUG-034 — Manifest & modpack validation (Workstream 3)
- **Files:** `ManifestValidator.cs`, `ModpackSubmissionValidator.cs`, `ModpackCommand.cs` (+ tests `ManifestValidatorTests.cs` (new), `ModpackSubmissionTests.cs`)
- **What:**
  - BUG-002 (High): `ValidatePack`/`ValidateAll` now guard a null `Packs` array and return a clean structural failure instead of letting LINQ throw `ArgumentNullException`.
  - BUG-031 (High): `ValidateAll` returns a single `(index.json)` failure entry when `index.json` is missing (so the CLI no longer prints "All packs valid."); `ModpackCommand` sets a non-zero exit code on validation failure.
  - BUG-032 (High): the `BepInEx` install type is reserved for the framework entry (id `bepinex`); `ManifestValidator` rejects it for other ids, and `ModpackSubmissionValidator` requires the framework entry to use exactly `BepInEx`.
  - BUG-033 (Medium): a manifest/index name mismatch is now an error, not a warning, so a mismatched manifest cannot validate as VALID.
  - BUG-034 (Low): `ModpackSubmissionValidator` rejects `..`/rooted `Logo`/`Manifest` references before resolving.
  - BUG-024 (Medium): `ManifestValidator` uses a segment-based traversal check (rejects a `..` path segment or rooted path, allows `..` inside a filename like `MyMod..v1.zip`).
  - BUG-025 (Medium): a non-framework mod claiming install type `BepInEx` is rejected.
  - BUG-028 (Low): an empty `Mods` list is reported as an error.
  - BUG-010 (Medium, atomic writes): implemented in Workstream 2 — both stores write via temp-file + rename with a `.bak`.
- **Why:** These were crashes, silent "valid" outcomes, and traversal/type-enforcement holes in pack validation.
- **Verification:** New tests `ManifestValidatorTests.*` (5) and `ModpackSubmissionTests.*` (ValidatePack_Fails_WhenIndexMissingPacksArray, ValidateAll_Fails_WhenIndexMissingPacksArray, ValidateAll_Fails_WhenIndexMissing, ValidatePack_Fails_WhenFrameworkUsesWrongInstallType, ValidatePack_Fails_WhenManifestNameMismatchesIndex, ValidatePack_Fails_WhenLogoReferenceUnsafe) all pass; full Core suite 124/124.

### BUG-003, BUG-017, BUG-019, BUG-020, BUG-021 — Resolver, ordering & install reporting (Workstream 4)
- **Files:** `DeploymentService.cs`, `ModListResolver.cs`, `DestinationConflictFinder.cs`, `ModpackInstaller.cs`, `MainWindow.cs` (+ tests `DeploymentServiceTests.cs`, `ModListResolverTests.cs`, `DestinationConflictFinderTests.cs`)
- **What:**
  - BUG-020 (Medium): `DeploymentService.Validate` and `DeploymentService.Install` now call `ModpackInstaller.EnforceBepInExFirst(manifest)` before resolving, so the local `validate`/`install` path guarantees BepInEx sorts first — previously only the hosted-modpack `InstallAsync` did this. (Confirmed end-to-end: a manifest listing `bepinex` last with no plugin dependency still orders it first.)
  - BUG-021 (Low): `ModListResolver` keeps an exact-case id index (`allByIdExact`); a dependency/conflict reference that matches a real id only by case is reported as a hard error ("matches 'X' only by case") instead of being silently matched.
  - BUG-017 (High): the `Install` loop tracks `anyFailure`; when any mod's `InstallResult.Success` is false the report returns `DeploymentReport.Failure`, so `InstallCommand` sets a non-zero exit code.
  - BUG-019 (Medium): `DestinationConflictFinder.Find` gained an `installedPlans` overload that seeds already-owned destinations loaded from the journal; `DeploymentService.Install` feeds `BuildInstalledPlans(gameFolderPath)`, so a pending mod colliding with an installed mod's file is refused at pre-flight (not mid-install).
  - BUG-003 (High): `MainWindow.OnPackInstallAsync` now forwards the selected pack via `InstallAsync(manifest, fallback, pack: pack)`, so the `ModpackJournalStore.Record` call runs on success and the update badge/button work in the GUI.
- **Why:** These were silent-success / wrong-ordering / late-conflict holes in the local install and resolver paths (and the GUI journal-write gap).
- **Verification:** New tests `DeploymentServiceTests.Install_ReportsFailureWhenAModInstallsNothing_Bug017`, `Install_RefusesConflictWithInstalledMod_Bug019`, `Validate_EnforcesBepInExFirst_Bug020`; `ModListResolverTests.RejectsDependencyThatMatchesOnlyByCase_Bug021`, `RejectsConflictThatMatchesOnlyByCase_Bug021`; `DestinationConflictFinderTests.PendingPlanCollidingWithInstalledMod_IsReported_Bug019` all pass; full Core suite 130/130; solution builds clean (incl. GUI).

### BUG-026, BUG-027, BUG-035, BUG-036 — CLI error handling & argument UX (Workstream 5)
- **Files:** `DeploymentService.cs`, `InstallCommand.cs`, `ValidateCommand.cs`, `PlanCommand.cs`, `ModpackCommand.cs` (+ tests `DeploymentServiceTests.cs`)
- **What:**
  - BUG-026 (Low): `DeploymentService.Validate`/`Install` now wrap `ManifestReader.Read` in a try/catch for `JsonException`/`InvalidOperationException` and return a friendly `DeploymentReport.Failure` ("Manifest is not valid JSON: ...") — the raw serializer message no longer escapes to the user.
  - BUG-027 (Low): `InstallCommand`, `ValidateCommand`, and `PlanCommand` now set `Environment.ExitCode = 2` on the usage branch, matching the unknown-command handling in `Program.cs`, so misuse is detectable by scripts.
  - BUG-035 (Medium): `ModpackCommand` validates the `install` pack id up front (before `FetchIndexAsync`) and prints a usage hint with exit code 2 — no wasted network round-trip and no "Unexpected error".
  - BUG-036 (Medium): `PlanCommand` validates the manifest's existence and JSON at the CLI boundary with clear messages; combined with BUG-026, bad arguments no longer collapse into the generic top-level "Unexpected error".
- **Why:** These were raw-exception leaks and silent zero-exit misuse paths at the CLI boundary.
- **Verification:** New tests `DeploymentServiceTests.Validate_MalformedManifest_ReturnsFriendlyJsonError_Bug026`, `Install_MalformedManifest_ReturnsFriendlyJsonError_Bug026`; CLI smoke confirms `validate`/`install a b`/`plan` exit 2, `validate bad.json` exits 1 with friendly text, `plan missing.json` exits 2 with "not found", and `modpack install` (no id) exits 2 with a usage hint and no fetch; full Core suite 132/132.

### BUG-008, BUG-009, BUG-037, BUG-038 — GUI robustness (Workstream 6)
- **Files:** `MainWindow.cs`, `ModpackIndex.cs`, `ModpackJournalStore.cs` (+ tests `ModpackTests.cs`)
- **What:**
  - BUG-003 (High): completed in Workstream 4 (GUI forwards the pack into `InstallAsync`).
  - BUG-008 (Medium): `ReadInstalledPacks()` is now wrapped in its own try/catch inside `LoadPacksAsync`, so a corrupt/unreadable pack journal only suppresses update badges (with a warning) — the index gallery still renders.
  - BUG-009 (Medium): `ModpackSummary` gained `FormerIds` + `IsId()`; `IsUpdateAvailable` matches the canonical id or any legacy `FormerId`, and `ReadInstalledPacks` rewrites a legacy stored `PackId` to its canonical id (persisted) so a pack-id rename neither orphans the entry nor breaks update detection.
  - BUG-037 (Medium): `RunHandler` now logs the full exception type + message on screen and writes the stack to the diagnostic log, instead of swallowing only `ex.Message`.
  - BUG-038 (Medium): `Opened` now routes `WelcomeDetectAsync` through `RunHandler` instead of an unguarded `async void` lambda.
- **Why:** These were GUI robustness holes — a corrupt journal wiping the gallery, a rename orphaning tracking, and unobserved/swallowed exceptions.
- **Verification:** New tests `ModpackSummaryTests.IsId_MatchesCanonicalAndFormerIds_Bug009`, `IsId_WithoutFormerIds_MatchesOnlyCanonical`; full Core suite 134/134; solution builds clean (incl. GUI). BUG-008/037/038 verified by build + source analysis (GUI-only, no automated harness).

### BUG-006, BUG-007, BUG-039 — remaining update-detection & CLI lifecycle
- **Files:** `ModpackVersion.cs`, `ServeCommand.cs` (+ tests `ModpackTests.cs`)
- **What:**
  - BUG-006 (High): `ModpackVersion.IsNewer` now normalizes versions — strips a leading `v` and any `-prerelease`/`+build` label, parses the numeric components (missing default to 0) — so a genuinely newer `v1.3.0` / `1.3.0-beta` is detected as an update.
  - BUG-007 (Medium): the same normalization pads the four components to 0, so `1.0` and `1.0.0` compare identical and no longer spuriously flag "Update available".
  - BUG-039 (Low): `ServeCommand` now also watches for stdin EOF and `AppDomain.ProcessExit` (in addition to Ctrl+C) and disposes the server on each, so a headless or terminated run releases the listener cleanly.
- **Why:** These were the last open holes — update detection ignoring real version formats / false-positiving on component counts, and a demo server that wouldn't shut down cleanly headless.
- **Verification:** New test `ModpackTests.ModpackVersion_IsNewer_ToleratesPrefixesAndComponentCounts_Bug006_Bug007`; full Core suite 135/135; solution builds clean. BUG-039 verified by build + source analysis. **All 40 known bugs are now fixed.**
