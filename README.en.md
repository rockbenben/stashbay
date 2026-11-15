# StashBay

> A lightweight shelf for shuttling files between folders — drop in, drag out, done.

[简体中文](README.md) · **English** · [繁體中文](docs/i18n/README.zh-Hant.md) · [日本語](docs/i18n/README.ja.md) · [한국어](docs/i18n/README.ko.md) · [Español](docs/i18n/README.es.md) · [Français](docs/i18n/README.fr.md) · [Deutsch](docs/i18n/README.de.md) · [Português](docs/i18n/README.pt.md) · [Русский](docs/i18n/README.ru.md) · [Italiano](docs/i18n/README.it.md) · [العربية](docs/i18n/README.ar.md) · [हिन्दी](docs/i18n/README.hi.md) · [বাংলা](docs/i18n/README.bn.md) · [ไทย](docs/i18n/README.th.md) · [Türkçe](docs/i18n/README.tr.md) · [Tiếng Việt](docs/i18n/README.vi.md) · [Bahasa Indonesia](docs/i18n/README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Download Latest Release](https://github.com/rockbenben/048-stashbay/releases/latest)

![StashBay compact shelf: a small always-on-top card rack — drop files in to keep them, drag them out to use them](assets/shelf.png)

A lightweight local file transit station for Windows. Runs completely offline with zero telemetry, network requests, or account sign-ups. Works both as a transient shelf you use and dismiss, and as a full branch-aware workbench with snapshot backups and instant rollback.

---

## Support Range

| Dimension | Specifications |
| :--- | :--- |
| **Operating System** | Windows 10 (1809+) / Windows 11 (native x64 and ARM64 support) |
| **Runtime** | Official installer and portable builds include the runtime; compact builds require .NET 10 Desktop Runtime |
| **Stash Content** | Any file, entire folders (full recursive subdirectory structure preserved) |
| **Input Sources** | Explorer file selection, clipboard file list, mouse shake gesture |
| **Write-Back Targets** | Local disks, network shares (UNC / mapped drives), Git repositories |
| **Large Files** | Supports single files > 2 GB, supports Windows long paths (> 260 characters) |
| **Permissions** | Runs under standard user rights by default (installed in `%LOCALAPPDATA%\Programs`); optional admin startup |
| **Data Storage** | Saved under `%LOCALAPPDATA%\StashBay\`; backward compatible with legacy `FileStash` directory |

---

## Getting Started

1. **Install**: Download `StashBay-vx.y.z-setup.exe` (no admin needed) from [Releases](https://github.com/rockbenben/048-stashbay/releases/latest), or grab the portable `StashBay-vx.y.z-win-x64.zip` and unzip to run.
2. **Summon**: Press <kbd>Win+Alt+S</kbd>. The mini shelf opens near your cursor on the active display.
3. **Stash**: Select files in Explorer, press <kbd>Win+Alt+S</kbd> again (or drag them in, or shake the mouse) — the selected files land on the shelf.
4. **Drag out**: Drag the `[⤹ X items]` handle at the bottom into an Explorer folder or app window. When empty, the shelf fades out back to the tray.

---

## Features

### Minimal Transit (Yoink / DropPoint Experience)
* **One-shot drag all**: Grab the count badge at the bottom to bulk-export all stashed items to Explorer or chat apps in a single gesture.
* **Auto-dismiss when empty**: Items leave the shelf once dropped; once empty, the window smoothly fades away.
* **Summon at cursor**: Global hotkey summons the shelf directly adjacent to the mouse pointer on the active screen.
* **Shake to stash**: Quickly shake the cursor left and right while dragging files in Explorer to summon the shelf and stash them.
* **Edge-docked floating ball**: An edge widget that expands on click or short drag, snapping magnetically to the screen border on release.
* **Multiple independent shelves**: Double-click the tray icon or press <kbd>Ctrl+Shift+N</kbd> to open separate drop targets; empty temporary shelves are cleaned up automatically.

### Git Preservation & Safe Write-Back
* **Repository awareness**: Stashed files from Git repos remember the repo root; switching branches triggers a tray notice with one-click restore.
* **Lock inspection**: Automatically checks whether target files are held open by Word, Excel, or IDEs before writing.
* **Pre-overwrite snapshots**: Overwritten targets are backed up into a local content-addressed blob store with one-click undo rollback.
* **Content deduplication**: Skips writing when target and stashed files are byte-identical, preserving original timestamps and preventing rebuild loops.

### Full Workbench
* **Four view modes**: Tree (nested hierarchy), List (compact flat rows), By Source Folder, and Icons (dense tiles), with view persistence.
* **Three write-back modes**: Original location, alternate root, or specified directory; subpath structures support full relative path, item name only, or flatten.
* **Conflict preview**: Review detailed write actions before committing, with Ask, Overwrite, Skip, and Rename conflict policies.
* **Operation history**: Detailed history logs for every restore batch; partial failures can be retried individually.

### 18 Native Languages & Themes
* Native support for 18 languages with 100% key parity enforced by automated tests; full RTL layout mirroring for Arabic (ar-SA).
* Dual neutral-graphite themes (Dark and Light) that seamlessly follow system settings.

---

## Keyboard Shortcuts

| Shortcut | Scope | What it does |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Summon the shelf; if Explorer has selected files in front, stashes them directly |
| <kbd>Win+Alt+W</kbd> | Global | Summon the full workbench |
| <kbd>Win+Alt+V</kbd> | Global | Stash files currently in the clipboard |
| <kbd>Ctrl+V</kbd> | In window | Paste clipboard files into the active shelf |
| <kbd>Ctrl+Shift+N</kbd> | In window | Create a new independent empty shelf |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | In window | Toggle between mini shelf and full workbench |
| <kbd>Esc</kbd> | In window | In workbench, collapse to shelf; in shelf, hide to tray |
| <kbd>Delete</kbd> | List | Remove selected stashed items |

> [!TIP]
> Global shortcuts can be reconfigured or disabled in Settings; if a hotkey conflicts with another application, StashBay warns you explicitly rather than failing silently.

---

## Command-Line Interface (CLI)

`StashBay.exe` provides command-line flags for scripting, custom shortcuts, or automation workflows:

| Option | Description |
| :--- | :--- |
| `--add <paths...>` | Add one or more files or folders to the shelf |
| `--group <name>` | Specify target group name with `--add` (created automatically if missing) |
| `--apply-here <dir>` | Write stashed files into the specified directory |
| `--apply-original` | Restore files back to original repo-relative locations |
| `--show` | Summon and display the main window |
| `--minimized` | Start minimized to tray (used for Windows auto-start) |
| `--quit` | Terminate running StashBay instance |
| `--unregister-shell` | Unregister Explorer context menu, Send To shortcut, and auto-start entry |

---

## Known Limitations

* **Windows exclusive**: Deeply integrated with Windows Shell, DWM composition, and access control; no cross-platform version planned.
* **Snapshots, not links**: Content is snapshotted immediately when stashed; modifying original files does not alter stashed copies.
* **Single instance**: Re-launching passes arguments to the running tray instance rather than creating redundant processes.
* **Administrator startup**: Runs with standard privileges by default; if needed for dragging into elevated windows, opt in to administrator startup in Settings and approve via UAC. See the [startup guide](docs/admin-startup.md).

---

## About the 365 Open Source Plan

Project **#048** of the [365 Open Source Plan](https://github.com/rockbenben/365opensource) — one person + AI, 300+ open-source projects in a year.

[Submit your idea →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
