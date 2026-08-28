# Privacy

TCG Card Shop Sim Mod Manager does not collect telemetry or upload personal
data. It does make the network requests needed to show and download online
content.

- **No telemetry.** The desktop app fetches the hosted modpack catalog and its
  images from GitHub when it opens or you refresh it. Other network calls follow
  actions you start: checking for an app update, signing in to Nexus, and
  downloading mods. None of these requests are analytics or usage tracking.
- **Crash data stays local.** An unexpected error is written to a diagnostic log
  on this machine only. Nothing is uploaded, and there is no opt-in anywhere
  that would send it.
- **Your Nexus key stays on this machine.** `nexus set-key` stores the key
  encrypted with DPAPI, readable only by the current Windows user, in
  `%LOCALAPPDATA%\TCGCardShopSimModManager`. It is never written into the project, the
  logs, or the support bundle.
- **Nexus OAuth tokens stay on this machine.** `nexus login` stores the access
  and refresh tokens encrypted with DPAPI, readable only by the current Windows
  user, in `%LOCALAPPDATA%\TCGCardShopSimModManager\nexus-oauth-tokens.bin`. The
  OAuth client id is stored unencrypted (it is public, not a secret) in
  `%LOCALAPPDATA%\TCGCardShopSimModManager\oauth-settings.json`. Neither is
  written into the project, the logs, or the support bundle.
- **Diagnostic logs** are plain text in `%LOCALAPPDATA%\TCGCardShopSimModManager\logs`
  (override with the `CSMM_LOG_DIR` environment variable). You can delete them
  at any time. The `support-bundle` command collects them into a zip you share
  only if you choose to.
- **The support bundle** contains environment info and recent log lines. It
  deliberately excludes anything that could be a key or credential.
- **Installed-mod records** (`cardshopmodmanager.journal.json`,
  `cardshopmodmanager.profiles.json`) live inside the game folder you manage.
  They record file paths and hashes only.
- **Separate modpack saves** are optional and stay on this machine under
  `%LOCALAPPDATA%\TCGCardShopSimModManager\save-profiles`. The manager moves
  only the game's world save and backup files between local pack profiles. Save
  contents are never added to diagnostic logs or support bundles and are never
  uploaded by the manager.

This project is open source: the source of truth for the privacy behaviour is
the code, which anyone can inspect.
