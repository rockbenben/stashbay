# StashBay

> For shuttling loose files between folders: drop them on the shelf, drag them out, done — no two Explorer windows held side by side.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE) [![365 Open Source #048](https://img.shields.io/badge/365%20Open%20Source-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Download the latest](https://github.com/rockbenben/048-stashbay/releases/latest) · [简体中文](README.md)

![StashBay compact shelf: a small always-on-top card rack — drop files in to keep them, drag them out to use them](assets/shelf.png)

A file transfer station for Windows. **Everything runs locally: no uploads, no network, no account.** Two shapes: a mini shelf you use and dismiss, and a full workbench that switches branches, writes files back, and undoes.

---

## What it does for you

### Minimal transit (the DropPoint feel)
* **Drag everything out at once**: grab the `[⤹ X items]` badge on top of the shelf and one gesture dumps every stashed file into Explorer, or a WeChat / Slack / web form attachment box.
* **Dragged out means removed**: files leave the box once they land in the target window (on by default, switchable). When the box empties, the window fades out back to the tray — nothing to clean up.
* **Summoned at the cursor**: the global hotkey opens the shelf on the monitor your mouse is actually on. No sliding back to the primary screen.
* **Shake to stash**: select files in Explorer and shake the mouse left and right to stash those files.
* **Floating widget**: an unobtrusive edge ball that expands the shelf on click or short drag, snapping magnetically to screen borders on release.
* **Four buttons on top, no more**: pin, add, expand, close. View, write back, and clear all use fixed-size icons with tooltips in the status bar, staying on one row even in a narrow window.

### Holds your changes across a branch switch
* **Finds the repository**: when stashed files come from a Git repo, the write-back target is the repo root; switching branches raises a tray notification and one click writes everything back in place.
* **Never writes half-broken**: before writing, it checks whether the target file is held open by another program (Excel, Word, an IDE) and warns instead of plowing through.
* **Backs up before overwriting**: every overwritten target is snapshotted into a local content-addressed store; a wrong write is undone in one click with "Undo last".
* **Skips identical content**: when target and stash match, nothing is written and the original mtime stays untouched — no pointless rebuilds or hot reloads.

### A full workbench
* **Multiple boxes**: sort files by project, client, or purpose; name the boxes yourself.
* **Four view modes**: tree (expand to view nested entries), list (flat compact rows), by source (grouped under the directory they came from), and icons (dense file-icon tiles). The chosen view is persisted.
* **Three write-back modes**: original location (repo-relative), alternate root, specified directory; subpath structure can keep the full tree / item name only / flatten everything.
* **Conflict policies**: ask / overwrite / skip / rename, with a preview before anything is written.
* **History and rollback**: every write-back is recorded per file; when part of a batch fails, retry just those lines.

### The interface speaks your language
18 languages built in, every UI string translated (all 18 resource tables hold the identical key set, enforced by tests): 简体中文, 繁體中文, English, 日本語, 한국어, Deutsch, Français, Español, Italiano, Português (Brasil), Русский, العربية (full RTL mirroring), हिन्दी, Bahasa Indonesia, ไทย, Türkçe, Tiếng Việt, বাংলা. Dark and light themes; both language and appearance can follow the system.

---

## Support range

| Item | Support |
| :--- | :--- |
| **OS** | Windows 10 1809 or later / Windows 11 (x64, ARM64) |
| **Runtime** | .NET 10 Desktop Runtime (bundled in the portable builds — install nothing separately) |
| **Stashable content** | Any file, whole folders (subdirectory structure included) |
| **Sources** | Explorer selection, clipboard files, shake-selected Explorer files |
| **Write-back targets** | Local disks, network drives / UNC paths, Git repositories |
| **Large files** | Single files over 2 GB (paths longer than 260 characters too) |
| **Where data lives** | Everything under `%LOCALAPPDATA%\StashBay\`; uninstalling removes the program and settings but keeps this folder — delete it yourself for a clean sweep |
| **Requirements** | The installer and self-contained portable builds include the runtime; compact builds require the .NET 10 Desktop Runtime. The installer needs no admin rights and lands in `%LOCALAPPDATA%\Programs\StashBay` |

---

## Getting started

**Install**: grab `StashBay-x.y.z-setup.exe` from [Releases](https://github.com/rockbenben/048-stashbay/releases/latest) and double-click (no admin needed), or download `StashBay-x.y.z-win-x64.zip` and run it from anywhere.

**Use it**: press <kbd>Win+Alt+S</kbd>. The shelf appears next to your mouse.

It is empty right now. Select a few files in Explorer, press <kbd>Win+Alt+S</kbd> again — they are on the shelf. Grab the `[⤹ 3 items]` badge, release over the desktop, all three are out, and the shelf folds itself back into the tray.

That is the whole first run. Read about the rest once you need it.

---

## Keyboard shortcuts

| Shortcut | Scope | What it does |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Summon the shelf; when Explorer is in front with a selection, that selection goes straight into the box |
| <kbd>Win+Alt+W</kbd> | Global | Summon the full workbench |
| <kbd>Win+Alt+V</kbd> | Global | Stash files from the clipboard |
| <kbd>Ctrl+V</kbd> | In window | Paste files from the clipboard |
| <kbd>Esc</kbd> | Workbench | Collapse back to the mini shelf |
| <kbd>Esc</kbd> | Shelf | Hide to the tray |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | In window | Toggle shelf ⇄ workbench |
| <kbd>Delete</kbd> | List | Remove the selected stashed item |

All three global hotkeys are rebindable and can be cleared in Settings; if a key combo is already taken by another program you get a prompt, not a silent failure.

---

## Command-line options (CLI)

`StashBay.exe` supports the following command-line flags for scripting, custom shortcuts, or automation:

| Option | Description |
| :--- | :--- |
| `--add <paths...>` | Add one or more files or folders to the shelf |
| `--apply-here <dir>` | Write stashed files into the specified directory |
| `--apply-original` | Write stashed files back to their original locations (Git repo relative) |
| `--group <name>` | Specify the target group when adding files (created automatically if missing) |
| `--minimized` | Launch minimized to system tray (used for auto-start) |
| `--show` | Summon and show the main window |
| `--quit` | Terminate running StashBay instance |
| `--unregister-shell` | Clean up Explorer context menus, Send To shortcut, and auto-start entry |
| `--elevated-retry <id>` | Re-run failed operations with administrator elevation |

Headless diagnostician flags: `--smoke`, `--seed <workspace>`, `--shots <dir>`, `--probe <dir>`, and `--inspect`.

---

## How write-back works

The shelf handles transit, the workbench handles landing. The only step that matters is **Preview**: press it, read where each file will go, whether it creates or overwrites, how conflicts are handled — then write.

* **Original location**: writes back along each file's path inside the Git repo — this is what you want around a branch switch.
* **Alternate root**: relative paths preserved, everything moved under a different root.
* **Specified directory**: pick any of three subpath structures (full relative path / stashed item only / flattened) with any of the four conflict policies.

Afterwards "Undo last" rolls the whole batch back; if part of it failed, retry only the failed lines instead of redoing everything. Every write-back stays in History and is pruned by age (30 days by default, can be set to keep forever).

---

## Data and storage paths

| Data | Local path |
| :--- | :--- |
| **Database** | `%LOCALAPPDATA%\StashBay\stashbay.db` |
| **Pre-overwrite snapshots (blob store)** | `%LOCALAPPDATA%\StashBay\blobs\` |
| **User settings** | `%APPDATA%\StashBay\settings.json` |
| **Dropped content cache** | `%LOCALAPPDATA%\StashBay\clipboard\` |
| **Drag-out materialization cache** | `%LOCALAPPDATA%\StashBay\export\` |
| **Logs** | `%LOCALAPPDATA%\StashBay\logs\` |

Old `%LOCALAPPDATA%\FileStash\` is still honored: if that directory is found on first launch it keeps being used — no forced migration, no lost data.

---

## Known limitations

* **Auto-start uses standard privileges.** Saving the setting applies it immediately. Elevated auto-start is not supported; failed write-back entries can be retried with administrator privileges through UAC.
* **Windows only.** It is WPF plus Windows Shell integration (context menu, Send To, RestartManager lock detection). No cross-platform plans.
* **Stashes copies, not links.** Content is snapshotted when a file enters the box, so later edits to the original don't touch the stashed copy — and edits on the shelf never touch the source file.
* **One instance.** Starting it again forwards the arguments to the first instance instead of opening a second window.
* **Real drag-and-drop can't be fully automated.** The behavior of bulk drag-out and pasting into Explorer is covered by the acceptance suite; how it *feels* still needs a human eye.

---

## Development and testing

Repository layout, architecture conventions and engineering guardrails live in [AGENTS.md](AGENTS.md).

```powershell
# Build the solution (0 warnings, 0 errors)
dotnet build --nologo

# Unit and integration tests
dotnet test --nologo -v q

# End-to-end acceptance (runs entirely in a temp sandbox; touches no real data or registry)
pwsh -File tools/acceptance.ps1

# Screenshot matrix: build the fixture workspace, seed data, render, and prove the shelf wasn't empty
pwsh -File tools/shots.ps1
```

Requires the .NET 10 SDK; `global.json` pins the baseline to `10.0.402` (`rollForward: latestFeature`, CI verified on `10.0.403`). Release artifacts and the installer are produced by `tools/release.ps1` (four pubxml profiles + Inno Setup installer + SHA256SUMS).

---

## About the 365 Open Source Project

This is project **#048** of the [365 Open Source Project](https://github.com/rockbenben/365opensource) — one person plus AI, 300+ open source projects a year.

[Submit your request →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
