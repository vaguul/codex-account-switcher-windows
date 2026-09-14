# GitHub switcher audit

Research performed on 2026-09-13 and rechecked on 2026-09-14. Repositories were cloned and inspected at the commits below. No source code was copied into this project.

| Repository | Commit inspected | Relevant observation |
| --- | --- | --- |
| [Sls0n/codex-account-switcher](https://github.com/Sls0n/codex-account-switcher) | `fad1a419` | Simple CLI profile copies; saved authentication is not protected with Windows credential encryption. |
| [JJLiebig/codex-account-switcher](https://github.com/JJLiebig/codex-account-switcher) | `045cb4f6` | Cross-platform Rust implementation; Windows snapshots are written without DPAPI. Includes optional periodic warm-up activity. |
| [liuzhao1225/codex-account-switcher](https://github.com/liuzhao1225/codex-account-switcher) | `5f2a0352` | Strong path/reparse checks and atomic writes; profile `auth.json` copies remain plaintext under a private ACL. |
| [ZOONGG/codex-swap-account](https://github.com/ZOONGG/codex-swap-account) | `f3e2073a` | Windows WPF transaction backup and tray UI; profile credentials are plaintext under `.codex-profiles`. |
| [Lampese/codex-switcher](https://github.com/Lampese/codex-switcher) | `d245d218` | Broad Tauri feature set including warm-up, LAN mode, and updater, with a larger operational surface. |
| [wannanbigpig/codex-accounts-manager](https://github.com/wannanbigpig/codex-accounts-manager) | `e88a2cd7` | VS Code extension with automatic switching and background account management. |
| [korsun009/codex-account-switcher](https://github.com/korsun009/codex-account-switcher) | `709ca1c9` | Uses DPAPI but also calls a private usage endpoint and offers remote API/Telegram surfaces. |
| [Chisiki1/codex-account-switcher-windows](https://github.com/Chisiki1/codex-account-switcher-windows) | `cd5ac60a` | Windows implementation; issue #1 documents access failure when trying a protected WindowsApps binary path. |
| [cguru/Codex-Account-Switcher-Windows](https://github.com/cguru/Codex-Account-Switcher-Windows) | `17e316cb` | Delegates login handling to a bundled third-party authentication component. |
| [mahirozdin/Codex-Multi-Account-Manager](https://github.com/mahirozdin/Codex-Multi-Account-Manager) | `42290d1e` | Additional multi-account implementation included for behavior comparison. |

## Issue evidence

The design responds to public failure reports, not hypothetical feature lists:

- Lampese issues [#140](https://github.com/Lampese/codex-switcher/issues/140), [#136](https://github.com/Lampese/codex-switcher/issues/136), [#129](https://github.com/Lampese/codex-switcher/issues/129), and [#113](https://github.com/Lampese/codex-switcher/issues/113) report unsafe login flow, invalidated refresh tokens, sign-in after switching, and account-file parse failures.
- Lampese issue [#154](https://github.com/Lampese/codex-switcher/issues/154) reported more than 40 GB/day of background IPC writes from UI animation.
- wannanbigpig issues [#33](https://github.com/wannanbigpig/codex-accounts-manager/issues/33), [#28](https://github.com/wannanbigpig/codex-accounts-manager/issues/28), and [#26](https://github.com/wannanbigpig/codex-accounts-manager/issues/26) report conversations stopping after auto-switch, deleted accounts returning, and logout after switching.
- Chisiki1 issue [#1](https://github.com/Chisiki1/codex-account-switcher-windows/issues/1) records the protected WindowsApps executable failure reproduced during local evaluation.

## Decisions

These findings led to a deliberately local, foreground-only application: DPAPI instead of plaintext snapshots; rollback before feature breadth; exact installed-process verification; no private web endpoint; no periodic prompts; no remote control; and no automatic account rotation.
