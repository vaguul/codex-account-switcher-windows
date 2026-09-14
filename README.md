# Vaguul Codex Account Switcher for Windows

A small Windows desktop application for keeping multiple Codex logins on one PC and switching the active login safely. Saved credentials are encrypted with Windows DPAPI for the current user. The application changes only Codex's `auth.json`; sessions, skills, memories, configuration, and projects stay in place.

> Independent community project. Not affiliated with or endorsed by OpenAI.

## Why this implementation

Existing switchers demonstrate that account switching is useful, but their code and issue trackers also show recurring failure modes: plaintext credential copies, account logout after switching, interrupted conversations, incorrect quota labels, startup parse failures, and large background write activity. This project intentionally uses a narrower design:

- DPAPI-encrypted profiles and backups, plus a current-user-only ACL.
- Atomic replacement with an encrypted recovery snapshot and transaction journal.
- Automatic rollback if Codex does not reopen after a switch.
- Stable account fingerprints that survive access-token refreshes.
- Strict process checks to avoid racing a running CLI or app-server.
- Optional system-tray access, while keeping account switching and usage refresh foreground operations.
- No auto-switching, warm-up prompts, telemetry, LAN server, proxy, cloud sync, or background usage polling.
- Dynamic quota-window parsing by actual duration, never by `primary`/`secondary` position alone.

The evidence behind these choices is recorded in [docs/RESEARCH.md](docs/RESEARCH.md).

## Requirements

- Windows 10 version 2004 or newer, or Windows 11.
- Codex Desktop installed for the current Windows user.
- For framework-dependent builds, .NET 8 Desktop Runtime.

## Use

1. Sign in to an account in Codex Desktop.
2. Open the switcher and select **Save active account**.
3. To add another account, sign out and sign in to it in Codex, then save it too.
4. Select a saved profile and choose **Switch to selected**.
5. Choose **Refresh usage** to query the saved profiles through Codex's local `app-server` protocol. The card shows each returned quota window and keeps the previous snapshot if a profile is temporarily unavailable.

You can also add a new account without changing the active one. **Add account with browser** runs the normal Codex `login` OAuth flow in an isolated temporary home; **Add with device code** runs Codex's `login --device-auth` flow for environments where the local browser callback is unavailable. Both flows save the resulting account only after you give it a profile name. The original active-account save flow remains available.

The window can be minimized to the Windows system tray. Use the tray menu to reopen the switcher, refresh usage, or exit it completely.

Use **Export profiles** to create a portable, password-encrypted package. **Import profiles** decrypts that package into the current Windows user's DPAPI vault, skips duplicate account identities, and restores saved quota metadata when present. The package password is required; it is never stored by the application.

After confirmation, the application schedules a one-shot detached worker, closes this window, and lets that worker validate the active `auth.json`, close Codex Desktop and active Codex CLI/app-server processes, install the selected profile, and relaunch Codex Desktop. The detached handoff prevents Codex from terminating the switcher when the switcher was opened from a Codex terminal. A validated active account does not need to be pre-saved: it is kept encrypted in the recovery journal while the switch runs. If a valid target snapshot is temporarily ahead of its metadata, the worker recovers that profile metadata before switching; it never accepts an invalid or missing snapshot. Unsaved CLI work may be lost. Save an account as a profile if you want to switch back to it later. Profiles with the same label are shown with their email or a short profile identifier so the target is unambiguous.

## Build and test

```powershell
dotnet build Vaguul.CodexAccountSwitcher.sln -c Release -warnaserror
dotnet run --project tests/Vaguul.CodexAccountSwitcher.Tests -c Release
./scripts/publish.ps1
```

The 21-case test executable uses synthetic credentials and an isolated temporary directory. It never reads or changes the real Codex login.

## Security

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability. Never attach `auth.json`, DPAPI vault files, tokens, or account identifiers to an issue. The plaintext active `auth.json` remains in Codex's own directory because Codex must read it; encrypted copies belong only to this application. Portable exports are encrypted with a user-supplied password and should be treated like any other credential backup.

## Status

`0.2.9` is a Windows preview. Switching is handed off to a detached worker before Codex closes, so a switcher launched from a Codex terminal can finish safely; transient process-list races are ignored only after the process is confirmed to have exited, while still-unverifiable live processes remain a hard stop. Valid target snapshots are also recovered if metadata was briefly out of sync. Browser and device-code login are separate commands, matching Codex's current CLI contract. Usage refresh persists any validated token rotation and skips the active account while Codex is running. Recovery, usage parsing, login handoff, profile transfer, interrupted-save recovery, and process-race handling are tested with synthetic profiles, but the project is unsigned and cannot promise compatibility with every future Codex package change.

Licensed under the [MIT License](LICENSE).
