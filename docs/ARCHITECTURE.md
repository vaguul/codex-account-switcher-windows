# Architecture

## Owned data

The application owns `%LOCALAPPDATA%\Vaguul\CodexAccountSwitcher`:

- `profiles.json`: labels, colors, timestamps, and irreversible account fingerprints. No credentials.
- `profiles.json` also stores the optional account email, plan label, quota windows, and last usage check.
- `vault/*.auth.dpapi`: account snapshots encrypted for the current Windows user.
- `backups/*.auth.dpapi`: encrypted rollback snapshots, retained to a maximum of five.
- `switch-transaction.json`: non-secret recovery journal for an incomplete switch.
- Usage percentages, window durations, reset timestamps, and the last check status are stored in `profiles.json`; they are not credentials.

Codex owns `%CODEX_HOME%` (normally `%USERPROFILE%\.codex`). The only Codex-owned file this application writes is `auth.json`.

## Switch protocol

1. The UI validates the selected profile identifier and creates a one-shot Windows Scheduled Task whose command contains only the target profile ID and task ID.
2. The UI exits before Codex is closed; the scheduled worker waits for the original instance to release the mutex and then performs the switch outside Codex's process/job ancestry.
3. The worker decrypts and validates the target profile.
4. If the target is already active, exit without touching processes or files.
5. Validate the active `auth.json` before shutdown. If its stable fingerprint belongs to a saved profile, refresh that profile; otherwise retain the validated active bytes only in the encrypted recovery journal for rollback.
6. Close only a verified `ChatGPT.exe` located in the installed `OpenAI.Codex_*` package, then attempt a graceful close followed by a bounded individual close for each standalone `codex.exe`.
7. Refuse to continue while any standalone `codex.exe` remains active.
8. Save the departing account's latest credential snapshot.
9. Write an encrypted rollback backup, then a non-secret transaction journal.
10. Atomically replace only active `auth.json` and verify the installed fingerprint.
11. Relaunch the verified Codex package and wait for its process.
12. Complete the journal and remove the one-shot task. If launch or verification fails, restore the previous account and relaunch it.

On the next startup, an incomplete journal is never applied silently. The user is asked before Codex is closed and the encrypted backup is restored.

## Usage query

Usage is refreshed only when the user chooses **Refresh usage**. For each saved profile, the application creates a private temporary `CODEX_HOME`, writes the decrypted profile snapshot there, starts the installed Codex binary with `app-server --stdio`, and requests `account/rateLimits/read` over the local JSON protocol. The temporary directory is deleted after the request. No private web usage endpoint, telemetry, background polling, or refreshed token is retained by the usage feature.

When startup finds a valid DPAPI vault snapshot for the current active account but no matching metadata entry, it treats the snapshot as an interrupted save. The user is asked for a profile label and the existing encrypted file is adopted without copying credentials into plaintext or deleting other vault snapshots.

## Login handoff

The optional browser flow invokes the installed Codex CLI as `codex login --device-auth` with a private temporary `CODEX_HOME`. The switcher does not implement or scrape the browser OAuth pages. After the CLI exits successfully, it validates the generated `auth.json`, encrypts it into the DPAPI vault, and deletes the temporary home. The existing **Save active account** path remains the primary manual alternative.

## Portable transfer

Export packages contain profile labels, optional account metadata, cached usage, and encrypted-in-transit authentication snapshots. The package uses AES-GCM with a key derived from the user password using PBKDF2-SHA256. Import validates every snapshot before adding it to the current-user DPAPI vault and never restores the original profile IDs, preventing accidental overwrites.

## System tray

The tray icon is a convenience surface, not a switching daemon. Minimizing hides the window and keeps the local process available; usage refresh is still explicit. A confirmed switch is handed to a one-shot detached worker before Codex closes, so a switcher launched from Codex's terminal is not a descendant that Codex can terminate. The worker closes the verified Codex Desktop window and then attempts a graceful close followed by a bounded individual close for every `codex.exe` CLI/app-server process. Closing the window exits normally, while the tray menu provides an explicit exit action.

## File-system defenses

Application storage receives a protected ACL for the current Windows SID. Every sensitive write is created with that ACL, flushed to disk, and moved or replaced atomically. Paths are canonicalized; vault identifiers are strict GUIDs; paths outside their expected root and reparse points are rejected.

## Non-goals

The application does not bypass account limits, merge subscriptions, automate usage, send warm-up prompts, expose a remote API, rotate accounts silently, or synchronize credentials. It does not modify Codex sessions, projects, skills, memories, configuration, or logs.
