# Changelog

## 0.2.2 - 2026-09-14

- Automatically close Codex Desktop and active `codex.exe` CLI/app-server processes before a confirmed account switch.
- Attempt a graceful process exit first, then use a bounded force-close when a process does not exit in time.
- Clarify the confirmation warning and failure message when a process cannot be closed safely.

## 0.2.1 - 2026-09-14

- Detect and recover an active DPAPI snapshot whose metadata write was interrupted, instead of silently leaving the account unavailable.
- Add a regression test for orphaned active-profile recovery.

## 0.2.0 - 2026-09-14

- Keep the manual active-account save flow and add browser-assisted `codex login --device-auth` account capture in an isolated temporary home.
- Add system-tray reopen, usage refresh, and explicit exit actions.
- Add password-encrypted profile export/import with duplicate detection and DPAPI re-encryption on import.
- Show account email and plan metadata when Codex provides it through `account/read`.
- Expand transfer and metadata regression coverage to 15 tests.

## 0.1.5 - 2026-09-14

- Show per-profile progress while usage is being refreshed.
- Prevent duplicate delete clicks and show the delete operation state.
- Explain empty account names directly inside the save dialog.
- Preserve startup errors instead of replacing them with a misleading ready state.

## 0.1.4 - 2026-09-14

- Preserve the selected profile after a usage refresh or reload.
- Keep selected cards visibly blue while hovering them.
- Show a clear state for profiles whose usage has not been checked or is unavailable.
- Display quota reset times and the last successful usage check time.
- Prevent long profile names from distorting the account list layout.

## 0.1.3 - 2026-09-14

- Add on-demand usage refresh through the local Codex app-server protocol.
- Show each saved account's current quota windows, remaining percentage, and cached check state.
- Keep the previous usage snapshot when a profile cannot be queried.
- Run each usage query in a temporary isolated `CODEX_HOME` and remove the copied credential after completion.
- Add regression coverage for usage metadata persistence without credential leakage.

## 0.1.2 - 2026-09-14

- Replace default WPF button rendering with compact rounded controls and clear disabled states.
- Replace the default white ListBox focus border with a restrained blue selection state.
- Mark each profile as `ACTIVE` or `SAVED` and keep the action state synchronized with the selected account.

## 0.1.1 - 2026-09-14

- Disable switching and deletion for the account that is already active.
- Show a clear status telling the user to save another account before switching.
- Handle Refresh and switch exceptions inside the window instead of leaving an apparently inactive UI.
- Make failed publish commands stop instead of reporting a stale artifact hash.

## 0.1.0 - 2026-09-14

- Add a native WPF account manager for Codex Desktop on Windows.
- Encrypt saved profiles and recovery backups with Windows DPAPI.
- Add atomic `auth.json` replacement, identity validation, transaction recovery, and automatic rollback.
- Preserve all other Codex files and block switching while standalone Codex processes are active.
- Add a 13-case synthetic regression suite and reproducible GitHub switcher audit.
