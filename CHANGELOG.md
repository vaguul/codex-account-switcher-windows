# Changelog

## 0.1.1 - 2026-09-14

- Disable switching and deletion for the account that is already active.
- Show a clear status telling the user to save another account before switching.
- Handle Refresh and switch exceptions inside the window instead of leaving an apparently inactive UI.

## 0.1.0 - 2026-09-14

- Add a native WPF account manager for Codex Desktop on Windows.
- Encrypt saved profiles and recovery backups with Windows DPAPI.
- Add atomic `auth.json` replacement, identity validation, transaction recovery, and automatic rollback.
- Preserve all other Codex files and block switching while standalone Codex processes are active.
- Add a 13-case synthetic regression suite and reproducible GitHub switcher audit.
