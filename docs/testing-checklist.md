# Release testing checklist

Real-world conditions to verify before distributing. Marked **[auto]** where a
unit test already covers it, **[manual]** where it needs a real environment.

## Environment

- [x] **[manual]** Clean Windows account (no dev tools): the published exe runs.
- [x] **[manual]** Machine **without the .NET runtime**: the self-contained
      build (`publish.ps1`) runs. Verify there is no shared runtime dependency.
- [x] **[auto]** Paths containing spaces install correctly
      (`Install_WorksWithSpacesInGamePath`).
- [x] **[manual]** Game folder on a **different drive** than the source folder.
- [x] **[manual]** A **non-default Steam library**: detect the game via Steam
      and install against a manually entered path on that library.

## Failures and recovery

- [x] **[auto]** Corrupted archive is refused before anything is written
      (`Install_RejectsArchiveHashMismatch`).
- [x] **[auto]** Common ZIP, RAR, 7Z, TAR, GZ, TGZ, BZ2 and XZ extensions are
      accepted, and compressed TAR extraction still rejects executables and
      duplicate destinations (`MultiFormatArchiveExtractorTests`,
      `Supports_AcceptsCommonArchiveExtensions`).
- [x] **[auto]** Interrupted download/cancel leaves no partial or fake-valid
      file (`Cancellation_RemovesPartial_AndLeavesNoFinalFile`).
- [x] **[manual]** Interrupt an install half-way (kill the process) and confirm
      the game folder is unchanged and a re-run completes cleanly.
- [x] **[auto]** Insufficient disk space fails fast without partial files
      (`InsufficientDiskSpace_FailsFast_WithoutDownloading`).
- [x] **[auto]** Corrupt remote payload is retried then fails cleanly
      (`CorruptSource_FailsCleanly_NoPartialNoFinal`).
- [x] **[manual]** A stale `.partial` file resumes (or the server re-downloads
      fresh) without producing a corrupt final file.
- [x] **[manual]** Lock a file in a temporary planning/install workspace and
      confirm cleanup failure does not replace the command's reported result.
- [x] **[auto]** A second operation for the same game folder is refused while
      the first holds the operation lock, then succeeds after release
      (`GameOperationLockTests`).
- [x] **[manual]** Start a long install in the desktop app, then try to change
      the same game through the CLI. The CLI should ask you to wait and neither
      operation should leave partial files or journals.

## Mod lifecycle

- [x] **[manual]** Install a mod, **update** the manifest to a newer archive,
      reinstall the newer version, confirm the newer file replaces the old.
- [x] **[auto]** An update replaces changed files, adds new files and removes
      obsolete files only while the previous copies still match the journal
      (`Install_UpdateReplacesAddsAndRemovesOwnedFiles`,
      `Install_NewerArchiveUpdatesExistingMod`).
- [x] **[auto]** An update refuses to overwrite a managed file changed by hand
      (`Install_UpdateRefusesToReplaceModifiedOwnedFile`).
- [x] **[manual]** **Downgrade** to an older archive and confirm it replaces the
      newer file.
- [x] **[auto]** Uninstall warns and keeps a file that was modified after
      install (`Uninstall_WarnsButKeepsFile_WhenFileWasModified`).
- [x] **[auto]** A dependency cycle is reported and blocks the list
      (`DetectsCircularDependencies`).
- [x] **[auto]** Two mods claiming the same file are refused at pre-flight
      (`SameDestinationAcrossMods_IsReportedOnce`).
- [x] **[auto]** If a later mod fails, earlier installs and updates are rolled
      back with their previous files and journal entries intact
      (`Install_ReportsFailureWhenAModInstallsNothing_Bug017`,
      `Install_LaterFailureRestoresEarlierUpdatedModAndJournal`).
- [x] **[auto]** An operation interrupted after files change but before commit is
      recovered from its durable record when the next game lock is acquired.
      Committed work remains, hosted recovery restores both journals, and a
      hostile recovery path cannot escape managed storage
      (`DurableRecoveryTests`).

## Mod inventory and enable/disable

- [x] **[auto]** A mod placed in `BepInEx/plugins` by hand (no journal) is listed
      as Unknown (`Discover_HandInstalledMod_IsUnknown`).
- [x] **[auto]** Journaled framework/root files stay grouped as one mod,
      unmanaged framework subdirectories do not become fake mods, and matching
      folder names in different roots remain distinct (`ModDiscoveryTests`).
- [x] **[auto]** Installed-mod discovery honours cancellation before scanning
      files (`Discover_CancelledScanStopsBeforeReadingFiles`).
- [ ] **[manual]** Refresh a large installed-mod list and close the window while
      it is hashing. The app should close promptly without finishing the scan;
      during a normal refresh the button should read **Scanning...** and prevent
      a duplicate click.
- [x] **[auto]** Disabling moves files out of the game into the manager's disabled folder and enabling moves
      them back (`Disable_MovesFilesToDisabledAndReportsDisabled`,
      `Enable_MovesFilesBackAndReportsInstalled`).
- [x] **[auto]** Default disabled storage is isolated per game installation so
      matching mod paths cannot overwrite one another
      (`DefaultDisabledStorage_IsolatedPerGameFolder`).
- [x] **[auto]** A modified file is left in place, not moved, when disabling
      (`Disable_LeavesModifiedFileInPlaceWithWarning`).
- [x] **[auto]** Uninstall removes a disabled mod from its parked location and
      clears a journal whose files are already gone
      (`Uninstall_DisabledModDeletesParkedFilesAndJournal`,
      `Uninstall_ClearsJournalWhenAllManagedFilesAreAlreadyMissing`).
- [x] **[auto]** Profile changes save only after file operations succeed and
      leave the previous profile intact on install, dependency, or modified-file
      failures (`ProfileServiceTests`).
- [x] **[auto]** Concurrent journal, modpack and profile updates retain every
      entry and leave valid JSON; replacement keeps a backup and no temporary
      files (`PersistenceStoreTests`).
- [x] **[auto]** Install journals store file locations relative to the selected
      game folder and migrate legacy absolute paths when a complete game folder
      moves (`JournalStore_StoresRelativePathsAndResolvesThemForUse`,
      `JournalStore_RebasesLegacyAbsolutePathsAfterGameFolderMoves`).
- [x] **[auto]** A newly copied file's verified destination hash is recorded in
      the install journal without a second destination read
      (`Install_LooseFile_GoesToPluginFolderAndJournals`).
- [x] **[manual]** Disable + enable a mod on the real install and confirm the
      game stops/starts loading it.
- [x] **[fixed]** A transient test failure turned out to be a real concurrency
      bug: installs shared a temp work-root and deleted it when momentarily
      empty, racing parallel installs. Fixed by never deleting the shared root
      (only per-run subfolders).

## Hosted modpacks (modpacks/)

- [x] **[auto]** The published catalog is embedded for immediate first-launch
      rendering, and a saved catalog can be read without a network request
      (`IndexReader_BundledCatalogContainsPublishedPacks`,
      `IndexReader_ReadsSavedCatalogWithoutNetworkRequest`).
- [ ] **[manual]** With a populated game folder and no network connection, open
      Browse during startup. Cards should appear from the local catalog before
      installed-mod discovery finishes; repeated filter changes should reuse
      logos without visible reloads.

- [x] **[manual]** Resize the desktop window at its minimum and normal sizes;
      the navigation remains visible, filters remain usable and cards wrap
      without overlapping or clipping.
- [x] **[manual]** At the normal desktop size, the installed-pack heading and
      persistent **Launch game** action are visible and correctly aligned.
- [x] **[manual]** Pack artwork fits inside its preview without cropping or
      distortion, and the compatibility warning remains inside the card.
- [x] **[manual]** Light, dark, system and high-contrast themes remain readable,
      including accent buttons and navigation hover states. Large text expands
      the navigation and filter columns without clipping, and appearance choices
      survive while moving between pages.
- [x] **[manual]** With several packs in the catalog, test standard and large
      cards with normal and large text at the minimum and normal window sizes;
      cards should wrap without overlapping or clipping.
- [x] **[manual]** Search and each Browse filter update the card grid, Reset
      restores the full catalog, and clicking a card opens its details.
- [x] **[manual]** NSFW packs are hidden on startup and after Reset, appear only
      after the user selects the NSFW filter, and remain subject to Nexus account
      restrictions when their files are requested.
- [x] **[manual]** With a registered Nexus client ID, Settings can sign in,
      display the account name, survive a restart, and sign out cleanly.
- [x] **[manual]** Settings opens Nexus API Access, validates and saves a
      personal API key, uses it for Nexus downloads after restart, and removes
      it without exposing the key in logs or support bundles.
- [x] **[manual]** Required and optional mods appear in separate sections in
      pack details. Required mods are checked and locked; optional mods start
      unchecked, selecting one selects its dependencies, and clearing a
      dependency clears optional dependants.
- [x] **[manual]** Starting a hosted install shows a confirmation listing the
      selected optional mods, including the no-selection case, before any
      download begins.
- [x] **[manual]** With no Nexus OAuth session or personal API key, a pack that
      contains selected Nexus mods disables installation and offers both sign-in
      and API-key setup in pack details. Saving either credential enables the
      install without reopening the pack.
- [x] **[manual]** A pack matching the installed Steam build is marked
      compatible. A mismatch, unknown build or undeclared compatibility is
      marked “May not be supported” and requires acknowledgement before install.
- [x] **[auto]** Steam build IDs are read from the selected installation's app
      manifest and compatibility distinguishes match, mismatch, unknown and
      undeclared states (`SteamLocatorTests`, `GameCompatibilityTests`).
- [x] **[manual]** Real TCG Overhaul is declared compatible with Steam build
      `22936874` after a successful local launch with its plugins loaded and a
      beta tester completing installation and gameplay without an issue.
- [x] **[auto]** A hosted install downloads and installs required mods plus only
      the optional selection, while legacy manifests still default every entry
      to required (`ModpackSelectionTests`,
      `ModpackInstaller_InstallsRequiredButSkipsUnselectedOptionalMod`).
- [x] **[auto]** An expired Nexus session without a refresh token asks the user
      to sign in again without making a token request
      (`RefreshAsync_MissingRefreshToken_AsksForSignInWithoutCallingNexus`).
- [x] **[auto]** A GitHub 429 is retried, and a refresh that remains unavailable
      uses the last successfully saved catalog (`IndexReader_RetriesRateLimitResponse`,
      `IndexReader_UsesLastGoodCacheAfterRetriesFail`).
- [x] **[auto]** BepInEx is ordered first when a pack includes it
      (`EnforceBepInExFirst_MakesBepInExAResolverDependency`,
      `ModpackInstaller_InstallsBepInExFirstAndRecordsPack`).
- [x] **[manual]** Install a hosted pack and confirm BepInEx lands first: the
      `BepInEx/` folder exists and the game launches with plugins loaded.
- [x] **[auto]** The installed pack version is recorded and re-read back
      (`ModpackJournalStore_RecordsAndReadsBack_ReplacingOnRerecord`).
- [x] **[auto]** Clearing a previously selected optional mod removes it, while a
      modified file blocks removal and restores the previous pack state
      (`ModpackInstaller_DeselectingOptionalModRemovesPreviousInstall`,
      `ModpackInstaller_UnsafeDeselectionRollsBackPackState`).
- [x] **[auto]** A newer published version is flagged, an equal/older one is not
      (`ModpackVersion_IsNewer_Cases`, `UpdateDetection_FlagsNewerPublishedVersion`).
- [x] **[manual]** Install a pack, then bump `version` in `index.json`; the card
      shows the green update banner and the button reads **Install update**.
      Running it should not corrupt the existing install.
- [x] **[auto]** Installing a second hosted pack is refused unless the caller
      explicitly requests a switch. A switch retains an unchanged shared mod,
      removes old-only mods, installs new-only mods and transfers journal
      ownership (`ModpackInstaller_RefusesDifferentPackWithoutExplicitSwitch`,
      `ModpackInstaller_SwitchRetainsMatchingModsAndRemovesUnusedMods`).
- [x] **[auto]** A failed pack switch restores the original files and both
      journals (`ModpackInstaller_FailedSwitchRestoresOriginalPack`).
- [x] **[auto]** The switch preview classifies unchanged shared mods, changed
      shared mods, old-only mods and new-only mods as keep, update, remove and
      add (`ModpackSwitchPlannerTests`).
- [x] **[auto]** An opted-in pack switch stores the current world saves,
      restores the destination pack's saves, preserves unrelated game settings,
      rolls saves back when the mod switch fails and recovers an interrupted
      save transaction before the next swap (`ModpackSaveProfileTests`,
      `ModpackInstaller_SwitchCanKeepSeparateSaveProfiles`,
      `ModpackInstaller_FailedSwitchRestoresActiveSaveProfile`).
- [x] **[auto]** Save storage inspection counts only recognized per-pack save
      files, and clearing it preserves the game's active saves and unexpected
      storage content. A clear cannot race an active swap
      (`StorageInspectionAndClearCoverOnlyOwnedSaveProfiles`,
      `StorageClearRefusesToRaceAnActiveSaveSwap`).
- [x] **[auto]** Stored profiles retain their original pack IDs, list with file
      counts and sizes, and can be deleted individually without removing another
      pack's progress. Metadata for a different pack is rejected
      (`StoredProfilesCanBeListedAndDeletedIndividually`,
      `StoredProfileListRejectsMetadataForAnotherPack`).
- [ ] **[manual]** With Steam Cloud disabled and the game closed, opt into
      separate saves while switching between two packs. Create different
      progress in each pack, switch both ways, and confirm each pack restores
      its own save slots while keybinds remain unchanged. Repeat with the game
      running and confirm the manager refuses to move saves.
- [ ] **[manual]** Settings reports stored modpack save usage. Cancel **Clear
      stored saves** once, then confirm clearing removes inactive pack progress
      without changing the save currently loaded by the game.
- [ ] **[manual]** **Manage stored saves** lists each saved pack with its file
      count and size. Delete one profile and confirm the other stored profiles
      and the game's active saves remain unchanged.
- [x] **[manual]** With one pack installed, confirm its card and the Browse
      header show its name and version. Open another pack, cancel the switch
      summary once, confirm its keep/update/remove/add counts, then complete the
      switch and confirm the gallery identifies only the new pack.
- [x] **[manual]** Use the persistent **Launch game** button from each page and
      confirm Steam starts TCG Card Shop Simulator.
- [x] **[manual]** During a large hosted install, move and resize the desktop
      window and confirm it remains responsive. Confirm the current mod, file
      number, transferred bytes, progress bar and download speed update until
      downloading finishes. During preflight and installation, confirm the
      current archive or mod and its position continue to change while the
      progress bar remains animated.
- [ ] **[manual]** Cancel a hosted install once during a download and once after
      file installation begins. Settings should report the resumable partial,
      retrying should continue it instead of starting over, and changed mods
      should roll back.
- [x] **[auto]** Cancelling after one mod installs rolls back that mod and its
      journal entry before reporting the operation as cancelled
      (`ModpackInstaller_CancellationDuringInstallRollsBackCompletedMods`).
- [x] **[auto]** Cancelling a download retains a content-addressed partial that
      resumes into a later disposable workspace, while cache inspection reports
      partials separately from verified archives
      (`CancelledDownloadResumesIntoANewWorkspace`,
      `InspectReportsPartialDownloadsAndIgnoresLockFiles`).
- [x] **[auto]** Hosted installs report the current mod and byte counts while
      downloading, followed by preparation, archive planning and per-mod
      installation stages
      (`ModpackInstaller_ReportsDownloadAndInstallProgress`).
- [x] **[auto]** Production archive limits accept large game-mod payloads while
      custom low limits still reject truncated extraction
      (`DefaultProtection_AllowsLargeGameModArchives`,
      `CreatePlan_ThrowsOnTruncatedArchive`).
- [x] **[auto]** If planning fails after a verified hosted download, retrying
      uses the persistent content cache without requesting the archive again
      (`ModpackInstaller_RetryUsesVerifiedCacheAfterPlanningFailure`).
- [x] **[auto]** Preflight releases each archive's extracted scratch files
      before planning the next archive. ZIP write failures remain I/O errors
      instead of being reported as duplicate entries
      (`Extract_DoesNotReportDirectoryWriteFailureAsDuplicate`).
- [x] **[auto]** An identical unmanaged file is adopted without being copied,
      remains marked as pre-existing, and is preserved by disable, update and
      uninstall (`Install_AdoptsIdenticalExistingFileWithoutTakingDeletionOwnership`,
      `Install_UpdateRefusesToReplaceAdoptedFile`).
- [x] **[auto]** Exact-file and directory-tree archive exclusions leave bundled
      copies uninstalled and give a shared destination one journal owner
      (`CreatePlan_ExcludesExactFileAndDirectoryTree`,
      `Install_ManifestExclusionAssignsOneOwnerForBundledFile`).
- [x] **[auto]** Pack uninstall removes every journaled pack mod and its pack
      record as one operation, and restores earlier removals when a modified
      managed file blocks completion (`ModpackUninstall_RemovesEveryJournaledPackMod`,
      `ModpackUninstall_RestoresEarlierRemovalWhenLaterModIsModified`).
- [x] **[manual]** Open an installed pack, confirm **Uninstall modpack** lists a
      clear warning, cancel once, then confirm it removes the complete pack and
      refreshes the installed state after closing pack details.
- [x] **[manual]** Interrupt or fail a multi-gigabyte hosted install after its
      downloads complete, retry it, and confirm each verified archive reports
      that it is ready from cache rather than downloading again.
- [x] **[manual]** Force a hosted download failure and confirm pack details show
      the returned reason instead of only directing the user to logs.
- [x] **[auto]** `modpack validate` passes a well-formed pack and fails one
      missing the `bepinex` entry, a mod with no source, or a missing logo
      (`ModpackSubmissionTests`).
- [x] **[manual]** From the repo root, `dotnet run --project
      src/TCGCardShopSimModManager.Cli -- modpack validate` reports
      `[VALID] real-tcg-overhaul`.
- [x] **[manual]** Disable + enable a *plugin* mod from an installed pack and
      confirm the game stops/starts loading it. (BepInEx is the framework and is
      intentionally not toggled.)

## Shipping

- [x] `dotnet build` — 0 warnings.
- [x] `dotnet test` — all tests pass.
- [x] `modpack validate` (no args) reports every pack valid from the repo root.
- [x] **[auto]** `update-check` compares the CI build component so consecutive
      pushed builds of the same base version are distinguishable
      (`UpdateCheckerTests`).
- [x] **[manual]** `update-check` reports correctly with no release and offline.
- [ ] **[manual]** On Settings, **Check for updates** reports checking, failure,
      up-to-date and update-available states beside the button. An available
      update exposes a working **Open release page** action, and repeated clicks
      cannot start overlapping checks.
- [x] `support-bundle` produces a zip that contains environment info + logs and
      **no** API key.
- [x] Read `PRIVACY.md`, `THIRD-PARTY-NOTICES.md`; license ships with the exe.
