# Architecture

## Owned data

The application owns `%LOCALAPPDATA%\Vaguul\CodexAccountSwitcher`:

- `profiles.json`: labels, colors, timestamps, and irreversible account fingerprints. No credentials.
- `vault/*.auth.dpapi`: account snapshots encrypted for the current Windows user.
- `backups/*.auth.dpapi`: encrypted rollback snapshots, retained to a maximum of five.
- `switch-transaction.json`: non-secret recovery journal for an incomplete switch.

Codex owns `%CODEX_HOME%` (normally `%USERPROFILE%\.codex`). The only Codex-owned file this application writes is `auth.json`.

## Switch protocol

1. Decrypt and validate the target profile before stopping Codex.
2. If the target is already active, exit without touching processes or files.
3. Close only a verified `ChatGPT.exe` located in the installed `OpenAI.Codex_*` package.
4. Refuse to continue while any standalone `codex.exe` remains active.
5. Re-read active `auth.json` after shutdown and match its stable fingerprint to a saved profile.
6. Save the departing account's latest credential snapshot.
7. Write an encrypted rollback backup, then a non-secret transaction journal.
8. Atomically replace only active `auth.json` and verify the installed fingerprint.
9. Relaunch the verified Codex package and wait for its process.
10. Complete the journal. If launch or verification fails, restore the previous account and relaunch it.

On the next startup, an incomplete journal is never applied silently. The user is asked before Codex is closed and the encrypted backup is restored.

## File-system defenses

Application storage receives a protected ACL for the current Windows SID. Every sensitive write is created with that ACL, flushed to disk, and moved or replaced atomically. Paths are canonicalized; vault identifiers are strict GUIDs; paths outside their expected root and reparse points are rejected.

## Non-goals

The application does not bypass account limits, merge subscriptions, automate usage, send warm-up prompts, expose a remote API, keep a tray process alive, or synchronize credentials. It does not modify Codex sessions, projects, skills, memories, configuration, or logs.
