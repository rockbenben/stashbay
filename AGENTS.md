# StashBay Developer & Agent Guide (AGENTS.md)

> This document is the primary reference manual for AI coding agents (Antigravity, Claude, Cursor, Copilot, etc.) and human developers working in this repository.

---

## 1. Identity & Naming Conventions

* **Product Display Name**: `StashBay` (brand name for windows, installer, tray, taskbar, shortcuts, logs).
* **Code Namespaces**: `FileStash.*` (`FileStash.Core`, `FileStash.Infrastructure`, `FileStash.App`).
  * *Invariant*: Keep all C# namespaces, project files, and solution references named `FileStash.*` for stability and backward compatibility.
* **Executable Name**: `StashBay.exe`.
* **Installer Name**: `StashBay-<version>-setup.exe`.
* **Data Directories**:
  * Default: `%LOCALAPPDATA%\StashBay\` and `%APPDATA%\StashBay\settings.json`.
  * Backward compatibility: Automatically falls back and transitions from `%LOCALAPPDATA%\FileStash\` if present.
  * Sandbox overrides: `STASHBAY_HOME` and `FILESTASH_HOME` are the **same switch** (current name + legacy name) — `AppPaths.HomeVariable` / `AppPaths.LegacyHomeVariable`. It redirects all database and settings paths for testing.
  * The sibling switch is `STASHBAY_NO_SHELL_INTEGRATION` (legacy `FILESTASH_NO_SHELL_INTEGRATION`): it stops the app from touching the Explorer context menu / Send-To / startup registry **at all** — neither registering nor unregistering, so turning the switch off cannot silently change a user's existing setup.

---

## 2. Solution Architecture

```
FileStash.sln
├── src/
│   ├── FileStash.Core/             # net10.0, POCO models, repository & service interfaces, pure logic (no UI dependency)
│   │   ├── Abstractions/           # IStashService, IApplyEngine, IBlobStore, IDragExportService, IGitService, repositories
│   │   ├── Models/                 # AppSettings, StashItem, StashEntry, StashGroup, TargetSpec, ApplyModels, Enums, ProgressInfo
│   │   ├── Services/               # StashService, BlobStore, ApplyPlanner, ApplyExecutor, PathMapper, DragExportService, CleanupService, SettingsService
│   │   ├── Localization/           # AppLanguageResolver (language option → CultureInfo, incl. "follow system")
│   │   ├── Utils/                  # AppPaths, HashUtil, PathUtil, Glob
│   │   └── Resources/              # Core localized error and status strings (18 languages, 54 keys each)
│   ├── FileStash.Infrastructure/   # net10.0-windows, SQLite + Dapper, Windows Shell integration, Win32 P/Invoke
│   │   ├── Data/                   # SqliteConnectionFactory, SchemaReadyConnectionFactory, DatabaseMigrator, Migrations, Repositories, Iso8601DateTimeOffsetTypeHandler
│   │   ├── Shell/                  # ShellRegistrars (ContextMenuRegistrar, SendToRegistrar, StartupRegistrar)
│   │   ├── Native/                 # RestartManagerLockInspector (file lock detection), AtomicFileWriter, SelectionGrabber, ExplorerSelection, SystemTheme
│   │   ├── Services/               # GitService, GitBranchWatcher
│   │   └── Ipc/                    # SingleInstanceCoordinator (Mutex + NamedPipe)
│   └── FileStash.App/              # net10.0-windows WPF application (StashBay.exe)
│       ├── Views/                  # MainWindow (Compact Shelf & Workbench), FloatingWindow, SettingsWindow, HistoryWindow, RootPresetsWindow,
│       │                           # AboutWindow, PreviewDialog, ResultToast, GitStatusDialog, ApplyActions, Dialogs/{GroupPicker,Input,Message,SetRoot,StashPicker}
│       ├── ViewModels/             # MainViewModel, SettingsViewModel, HistoryViewModel, GitStatusViewModel, TreeRowViewModel, EnumOption
│       ├── Controls/               # WindowEffects (DPI/Cloak/Positioning), ThemeService, ShakeDetector, HotkeyManager, TrayIconService, HotkeyBox, WheelScroll, WindowWarmUp
│       ├── Services/               # IDialogService, WpfDialogService, ThemedMessageBox, AppRestart, ElevatedRetryLauncher, DataPayloadHelper, LanguageService, ShellIconService
│       ├── Converters/             # ByteSizeConverter, EnumLabelConverter, EnumMatchConverter, NullToVisibilityConverter, WorkbenchChildrenConverter
│       ├── Diagnostics/            # SmokeRunner, ShotsRunner, ProbeRunner, InspectRunner, SeedRunner, WindowCatalog, Fixture, UiPump
│       ├── Startup/                # CommandLine (parses --add / --apply-here / --apply-original / --group / --minimized / --show / --quit / --unregister-shell / --elevated-retry)
│       └── Resources/              # Controls.xaml, Themes/{Dark,Light}.xaml, Tray.xaml, Strings.cs, Strings.*.resx (18 languages, identical key sets)
├── tests/
│   └── FileStash.Core.Tests/       # net10.0-windows, xUnit.v3 unit and integration test suite (30 test files)
│       └── TempDirectory.cs, TempWorkspace.cs, GitHelper.cs, ApplyTestContext.cs   # test-only isolation helpers
├── tools/                          # Automated tooling
│   ├── acceptance.ps1              # End-to-end acceptance runner (fully sandboxed)
│   ├── shots.ps1                   # Fixture workspace → --seed → --shots, with an empty-shelf hash guard
│   ├── release.ps1                 # Release build script (4 pubxml profiles, setup installer, SHA256SUMS)
│   ├── manual-checks.ps1           # Automated harness for OS-level scenarios (lock detection, UNC, >2GB files, DPI)
│   ├── gen-strings.ps1             # Regenerates Resources/Strings.cs from the neutral resx
│   └── installer/                  # Inno Setup 6 script (FileStash.iss) + lang/ChineseSimplified.isl
└── README.md                       # User-facing manual and project documentation
```

---

## 3. Product Mental Models & Workflows

StashBay features two tightly integrated workflows:

### A. Minimalist Transit Shelf (DropPoint / Yoink Style)
* **Goal**: Frictionless file bridge across windows and folders. "Drop in, drag out, gone".
* **One-Shot Drag All**: Drag the `[⤹ 共 X 项]` handle in compact mode or toolbar to bulk-export all stashed files to Explorer or external apps in one single mouse gesture.
* **Auto-Dismiss on Empty**: When all files are dragged out or cleared, the window automatically fades out and closes (`AutoDismissWhenEmpty = true`).
* **Stable Compact Size**: The compact shelf defaults to 360×240 DIP. Adding/removing items and switching icon/list views must not resize or reposition it; overflow scrolls inside. Manual resizing is retained when returning from the workbench during the same session.
* **Summon near Cursor**: Pressing hotkey (<kbd>Win+Alt+S</kbd>) summons the shelf directly adjacent to the mouse pointer on the active monitor (`SummonAtCursor = true`).
* **Shake to Summon**: Shaking cursor rapidly while dragging files summons the shelf at cursor position and automatically stashes the selection (`EnableShakeToSummon = true`).
  * Shake and the global hotkey share `HandleSummonTrigger`, which reads Explorer file selections.
* **Floating Ball**: Topmost floating widget. Click **or a short drag** (movement under 6 px) expands the shelf; releasing within 32 px of a screen edge magnet-snaps it to that edge.
  * Placement and snapping both go through `WindowEffects.GetWindowWorkArea` (the window's *own* monitor), so the ball does not jump to the primary screen on multi-monitor setups.
* **Action layout**: the title bar keeps four buttons — `pin`, `add`, `expand`, `close`. The status bar keeps statistics and fixed-size `view / write back / clear all boxes` icon buttons on **one row**. Each icon has a localized tooltip and accessibility name; statistics may trim with a full-text tooltip, but actions must never wrap or be clipped, even at the 260-DIP minimum width.
  * The reason is a count, not taste: in a typical transit session none of the six original glyphs is pressed, and five of them already had another entry point (tray menu, workbench, per-item context menu, <kbd>F11</kbd>, <kbd>Esc</kbd>). Only `pin` was unique. A row of identical 26×26 icons mixing window chrome, intake and destructive actions is a lookup task, not a shortcut.
  * Keep it that way when adding buttons: if a command already has an entry point elsewhere, it does not need a glyph in the title bar.

### B. Full Workbench
* **Goal**: Power-user file management, branch switching, and structured writing.
* **Modes**: Original Location (Git repo relative), Alternate Root, Specified Directory.
* **Structure**: Full Relative Subpath, Item Name Only, Flatten.
* **Conflict Resolution**: Ask, Overwrite, Skip, Rename.
* **Safety**: Pre-overwrite backup to content-addressed blob store; one-click undo rollback.
* **Collapse / View Toggle**: Click "缩回" button, press <kbd>Esc</kbd> in workbench, or press <kbd>F11</kbd> / <kbd>Ctrl+E</kbd>.
* **Four view modes** (`AppSettings.WorkbenchViewMode`, persisted on click, default `Tree` — it must stay 0 so old settings files keep today's look):
  * `Tree` — one node per stashed item, its entries nested under it.
  * `List` — flat; the same rows with entries suppressed (`WorkbenchChildrenConverter` hands the TreeView an empty collection, so `HasItems` hides the expander).
  * `BySourceFolder` — section headers keyed by each item's source directory, case-insensitively (Windows spells the same folder two ways). Headers are `SourceFolderRowViewModel`, not a second kind of box.
  * `Icons` (`3`) — dense file-icon tiles in `IconList`. Compact mode uses the same tile template by default and persists its independent `CompactIconView` toggle.
  * `Tree`, `List`, and `BySourceFolder` render through the **same** `ItemTree` TreeView: `ProbeRunner`'s column-alignment witness looks it up by name and only accepts a header grid with exactly four `ColumnDefinition`s, and `WindowCatalog` opens its context-menu variant through it. Swapping the control for a per-mode list would silently blind both.

---

## 4. Critical Engineering Guardrails (DO NOT BREAK)

### 1. Localization (18 Languages & 100% Key Parity)
* All user-visible strings must reside in `src/FileStash.App/Resources/Strings.*.resx` and `Strings.cs`.
* **Parity Invariant**: Every `Strings.<culture>.resx` must have the exact same set of keys as `Strings.resx`.
* `ResourceManager` silently falls back to Chinese neutral strings if a key is missing in a culture file. Never leave a key missing.
* Supported languages: `zh-CN` (neutral), `en`, `zh-TW`, `ja`, `ko`, `de`, `fr`, `es`, `it`, `pt-BR`, `ru`, `ar`, `hi`, `th`, `vi`, `id`, `tr`, `bn`.

### 2. Multi-Monitor & DPI Awareness
* Application manifest declares `PerMonitorV2` (`permonitorv2,permonitor`, with `dpiAware=true/pm` as the Win10 1607–1702 fallback), plus `longPathAware`.
* **Never use `SystemParameters.WorkArea` directly for positioning** — it only reports the primary screen's work area, so window placement goes wrong on secondary monitors.
  * The **one** sanctioned exception is inside `WindowEffects.GetWindowWorkArea` itself: when `hwnd` is zero or `GetMonitorInfo` fails, it degrades to `SystemParameters.WorkArea` rather than crashing. That is a fallback for an already-failed path (or headless runners without an hwnd), not a layout strategy.
* Always query monitor info via `WindowEffects.PlaceNearCursor` / `WindowEffects.GetWindowWorkArea` / `WindowEffects.FitToWorkArea` / `WindowEffects.PlaceAtBottomRight` / `WindowEffects.ClampToWorkArea` — all of them obtain monitor info scaled by monitor DPI, with `GetWindowWorkArea` guarding headless and zero-hwnd edge cases.
* Guarded by review and the smoke suite; any new `SystemParameters.WorkArea` read outside `WindowEffects`' own fallback is a regression, not a convenience.

### 3. Anti-Flash & Window Cloaking
* WPF's first composited frame in DWM can flash white. `WindowEffects.EnableAntiFlash` sets `DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, 1)` and uncloaks on `ContentRendered`.

### 4. Headless Smoke & Non-Modal Dialogs
* The app supports several headless flags for automated testing: `--smoke`, `--shots`, `--probe`, `--seed`, `--inspect`.
* All of them set `App.InSmoke = true`, which is the single condition every dialog path must check.
* In smoke/headless mode, **NEVER show modal dialogs** (`MessageBox.Show`, `ShowDialog`). They block the UI thread and hang automated processes. Use `IDialogService.ShowToast` or check `App.InSmoke`.
* `--smoke` reports the run to `%TEMP%\filestash-smoke.txt` (content `OK`, or the failure text) — CI reads that marker rather than the exit code, because a WPF process's exit code is unreliable.
  * `--seed` and `--shots` write the **same** marker, and `--shots` counts its own broken variants into it (`FAIL shots=72 …`) while still exiting 0. A matrix that silently lost a third of its PNGs looks finished unless somebody reads the file.
* `--seed <workspace>` populates the redirected stash with realistic data; `--shots` and `--probe` both depend on it (without it the screenshot matrix and probe tables capture an empty shelf).
  * An unseeded matrix is not a visible failure: every PNG renders, the process exits 0, and the "with data" and "empty" variants come out byte-identical. `tools/shots.ps1` builds the fixture, seeds, renders, and asserts those two variants **differ** — run the chain through it rather than by hand.

### 5. Test Isolation
* Automated scripts set `$env:STASHBAY_NO_SHELL_INTEGRATION = '1'` to prevent writing to real Windows Explorer registry entries.
* Data isolation is achieved via `$env:STASHBAY_HOME = <temp_dir>`.
* `tools/acceptance.ps1` additionally **publishes** the app into the sandbox before running, so it also verifies the publish output (and avoids DLL locks against a running `bin` copy).

---

## 5. Standard Development Commands

```powershell
# Build solution (0 warnings, 0 errors required)
dotnet build --nologo

# Run unit & integration test suite (xUnit.v3)
dotnet test --nologo -v q

# Run end-to-end automated acceptance verification
pwsh -File tools/acceptance.ps1

# Render the screenshot matrix with real data (fixture → --seed → --shots)
pwsh -File tools/shots.ps1

# Automated harness for OS-level scenarios (lock detection, UNC, >2GB, DPI)
pwsh -File tools/manual-checks.ps1 -SkipBigFile

# Build release distributions & Inno Setup installer
pwsh -File tools/release.ps1

# Run smoke test on compiled executable
artifacts/publish-x64/StashBay.exe --smoke
```

---

## 6. Brand Assets

The rendered PNG/ICO files are **generated output — never hand-edit them**. Edit the HTML
sources and re-render.

| Source of truth | Renders to |
| :--- | :--- |
| `assets/icon.html` | `assets/logo.png` (1024²), `assets/app.ico`, `assets/favicon.ico`, `src/FileStash.App/Resources/app.ico` |
| `assets/social-preview.html` | `assets/social-preview.png` (1200×630) |

```powershell
$SKILL_DIR = "$env:USERPROFILE\.agents\skills\html-shot"
node "$SKILL_DIR\render.mjs" assets\icon.html assets\logo.png --width 1024 --height 1024 --offline --transparent
node "$SKILL_DIR\icons.mjs" assets\icon.html artifacts\brand --preset electron --only ico --offline
Copy-Item artifacts\brand\icon.ico assets\app.ico -Force
Copy-Item artifacts\brand\icon.ico assets\favicon.ico -Force
Copy-Item artifacts\brand\icon.ico src\FileStash.App\Resources\app.ico -Force
node "$SKILL_DIR\render.mjs" assets\social-preview.html assets\social-preview.png --width 1200 --height 630 --offline
```

### Brand rules

> **2026-11-05 视觉方向更新：两层配色，不要混。**
>
> | 层 | 方向 | 覆盖范围 |
> | :--- | :--- | :--- |
> | **品牌层** | 彩色 teal/mint（`#3FC7AE` 系） | 图标字形、光晕、社交卡标题与特性行、拖出卡片 |
> | **产品层** | 中性石墨（无色相） | 运行时 UI 全部界面、社交卡里"画的是真实窗口"那段 |
>
> 运行时 UI 已改为中性石墨：近黑无彩底、大圆角卡片/药丸、发丝线分层、粗体大字配
> 灰色说明。`Brush.Accent` 不再是色相，而是"更亮/更深的中性色"；`Brush.Warning` /
> `Brush.Danger` 降饱和后只用来区分 Git 状态。
> **但品牌资产不跟着去色** —— 图标就是那个 teal 玻璃字形，别把它抹成灰的。

* **Runtime UI: neutral graphite.** The app UI has **no chroma**: near-black
  surfaces (`#17181A` / `#1E2022` / `#131415`), hairline borders (`#2C2E31`),
  large radii (control 8 / panel 12 / pill), and hierarchy carried by font weight and
  grey levels rather than colour. Accent is a neutral light/dark step, not a hue.
  All colours live in `Themes/Dark.xaml` and `Themes/Light.xaml` as `Brush.*` tokens;
  never hard-code a colour in a view.
* **Brand layer stays colourful.** The mark is *glass cards rising out of a lit bay*:
  saturated-teal fills (readable on white) *and* lit-mint edges (readable on
  near-black), with the `#3FC7AE` accent glow. `assets/icon.html` is the source of
  truth for it — re-render from there, never hand-edit the PNG/ICO.
* **The icon has no background plate.** It is a bare glyph on transparency, so it must
  read on both light and dark surfaces. Do not add a coloured tile.
* **The social preview is bilingual (EN + 中文).** English headline leads
  (`Stash files. / Drag them out.`), Chinese headline sits beneath it in accent mint
  (`暂存文件，一键拖出。`); feature rows carry both languages, with teal icons.
* **The real app UI is the hero** of the preview — the compact shelf and the write-back
  bar are drawn from the actual `Dark.xaml` tokens (`#17181A` surface, `#2C2E31` border,
  `#ECEDEE` text, `#E4E6E8` accent, `#C9A227` / `#C46A62` status). Never invent a
  mockup that differs from the shipped window. The preview's CSS keeps the two layers
  apart: `--surface` / `--border` / `--text` / `--accent` are the **product** tokens,
  `--brand` / `--brand-lit` / `--brand-deep` are the **brand** ones.
* **Show the app as a real floating window.** The right side is a tilted, shadowed window
  (perspective + contact shadow + sheen), not a flat diagram — flat panels read as
  wireframes, not as the running app.
* **The preview carries a lit inner frame** (`.frame`: a teal hairline plus an inset
  glow). A dark og:image on a dark repo page otherwise bleeds into the background:
  measured against GitHub dark `#0d1117`, the bare top-left corner sat at **1.05:1** —
  i.e. invisible. With the frame it is ~1.96:1 and the whole perimeter stays above
  1.17:1. Re-check both `#ffffff` and `#0d1117` after changing the background gradient.



