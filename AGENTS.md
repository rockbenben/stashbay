# StashBay Developer & Agent Guide (AGENTS.md)

> This document is the primary reference manual for AI coding agents (Antigravity, Claude, Cursor, Copilot, etc.) and human developers working in this repository.

---

## 1. Identity & Naming Conventions

* **Product Display Name**: `StashBay` (brand name for windows, installer, tray, taskbar, shortcuts, logs).
* **Code Namespaces**: `FileStash.*` (`FileStash.Core`, `FileStash.Infrastructure`, `FileStash.App`).
  * *Invariant*: Keep all C# namespaces, project files, and solution references named `FileStash.*` for stability and backward compatibility.
* **Executable Name**: `StashBay.exe`.
* **Installer Name**: `StashBay-v<version>-setup.exe`.
* **Archive Names**: `StashBay-v<version>-win-x64.zip`, `StashBay-v<version>-win-x64-needs-dotnet10.zip`, `StashBay-v<version>-win-arm64.zip`, `StashBay-v<version>-win-arm64-needs-dotnet10.zip`.
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
│   │   ├── Abstractions/           # IStashService, IApplyEngine, IBlobStore, IDragExportService, IGitService, ISettingsService, repositories
│   │   ├── Models/                 # AppSettings, StashItem, StashEntry, StashGroup, TargetSpec, ApplyModels, Enums, ProgressInfo, AppLanguage, ThemeChoice, GitStatusEntry, FolderEnumerator
│   │   ├── Services/               # StashService, ShelfLifetimeService, BlobStore, ApplyPlanner, ApplyExecutor (incl. ApplyRecovery, ApplyRetry), PathMapper, DragExportService, CleanupService, SettingsService, StorageWriteLease
│   │   ├── Localization/           # AppLanguageResolver (language option → CultureInfo, incl. "follow system")
│   │   ├── Utils/                  # AppPaths, HashUtil, PathUtil, Glob, ZoneIdentifier
│   │   └── Resources/              # Core localized error and status strings (18 languages, 55 keys each)
│   ├── FileStash.Infrastructure/   # net10.0-windows, SQLite + Dapper, Windows Shell integration, Win32 P/Invoke
│   │   ├── Data/                   # SqliteConnectionFactory, SchemaReadyConnectionFactory, DatabaseMigrator, Migrations, Repositories, Iso8601DateTimeOffsetTypeHandler, ISqliteConnectionFactory
│   │   ├── Shell/                  # ShellRegistrars (ContextMenuRegistrar, SendToRegistrar), LogonStartupService, ElevatedStartup
│   │   ├── Native/                 # RestartManagerLockInspector (file lock detection), AtomicFileWriter, SelectionGrabber, ExplorerSelection, SystemTheme
│   │   ├── Services/               # GitService, GitBranchWatcher
│   │   └── Ipc/                    # SingleInstanceCoordinator (Mutex + NamedPipe)
│   └── FileStash.App/              # net10.0-windows WPF application (StashBay.exe)
│       ├── Views/                  # MainWindow (Compact Shelf & Workbench), FloatingWindow, SettingsWindow, HistoryWindow, RootPresetsWindow,
│       │                           # AboutWindow, PreviewDialog, ResultToast, GitStatusDialog, ApplyActions, Dialogs/{GroupPicker,Input,Message,SetRoot,StashPicker}
│       ├── ViewModels/             # MainViewModel, SettingsViewModel, HistoryViewModel, GitStatusViewModel, TreeRowViewModel, EnumOption
│       ├── Controls/               # WindowEffects (DPI/Cloak/Positioning), ToastStack, ToastCountdown, ToastStackLayout, ThemeService, ShakeDetector, HotkeyManager, TrayIconService, HotkeyBox, WheelScroll, WindowWarmUp
│       ├── Services/               # IDialogService, WpfDialogService, ThemedMessageBox, AppRestart, ElevatedRetryLauncher, ShelfGroupMigration, DataPayloadHelper, LanguageService, ShellIconService
│       ├── Converters/             # ByteSizeConverter, EnumLabelConverter, EnumMatchConverter, NullToVisibilityConverter, WorkbenchChildrenConverter
│       ├── Diagnostics/            # SmokeRunner, ShotsRunner, ProbeRunner, InspectRunner, SeedRunner, WindowCatalog, Fixture, UiPump
│       ├── Startup/                # CommandLine (parses --add / --apply-here / --apply-original / --group / --minimized / --show / --quit / --unregister-shell / --elevated-retry)
│       └── Resources/              # Controls.xaml, Themes/{Dark,Light}.xaml, Tray.xaml, Strings.cs, Strings.*.resx (18 languages, identical key sets)
├── tests/
│   └── FileStash.Core.Tests/       # net10.0-windows, xUnit.v3 unit and integration test suite (42 test/helper files: 38 suites + 4 helpers, 510 test cases)
│       └── TempDirectory.cs, TempWorkspace.cs, GitHelper.cs, ApplyTestContext.cs   # test-only isolation helpers
├── tools/                          # Automated tooling
│   ├── acceptance.ps1              # End-to-end acceptance runner (fully sandboxed)
│   ├── shots.ps1                   # Fixture workspace → --seed → --shots, with an empty-shelf hash guard
│   ├── release.ps1                 # Release build script (4 pubxml profiles, setup installer, SHA256SUMS)
│   ├── package-release.ps1         # Shared local/CI packaging & verification runner (PE check, SQLite dll, ZIP verify, isolated smoke, Authenticode)
│   ├── performance.ps1             # Isolated performance comparison benchmark harness
│   ├── manual-checks.ps1           # Automated harness for OS-level scenarios (lock detection, UNC, >2GB files, DPI)
│   ├── recovery-checks.ps1         # Crash recovery, interrupted apply/undo reconciliation, and elevation verification
│   ├── installer-checks.ps1        # Upgrade, clean uninstall, and user content preservation verification
│   ├── Test-ElevatedStartup.ps1    # Automated engine test for Task Scheduler registration
│   ├── Test-StartupEntryPoints.ps1 # Entry-point and CLI flag validation
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
* **Stable Compact Size**: The compact shelf defaults to 220×180 DIP. Adding/removing items must not resize or reposition it. Compact mode always shows the draggable file stack, including when old settings contain CompactIconView=false. The empty shelf has no dashed outline; drag-in adds one subtle dashed outline around the icon and prompt, with no inner solid frame. Manual resizing is retained when returning from the workbench during the same session.
* **Summon near Cursor**: Pressing hotkey (<kbd>Win+Alt+S</kbd>) summons the shelf directly adjacent to the mouse pointer on the active monitor (`SummonAtCursor = true`).
* **Shake to Summon**: Shaking cursor rapidly while dragging files summons the shelf at cursor position and automatically stashes the selection (`EnableShakeToSummon = true`).
  * Shake and the global hotkey share `HandleSummonTrigger`, which reads Explorer file selections.
* **Floating Ball**: Topmost floating widget. Click **or a short drag** (movement under 6 px) expands the shelf; releasing within 32 px of a screen edge magnet-snaps it to that edge.
  * Placement and snapping both go through `WindowEffects.GetWindowWorkArea` (the window's *own* monitor), so the ball does not jump to the primary screen on multi-monitor setups.
* **Action layout**: the empty compact shelf shows a drop prompt and a close button. A populated shelf shows one draggable file stack and a bottom row of three icon buttons with localized tooltips (original location, specified directory, alternate root). The stack is the only drag-out control. A fully successful restore consumes only the captured, fully delivered snapshots from the compact queue and fades out only when it is empty. Newer snapshots, later additions, skipped conflicts and failed/cancelled restores remain pending. Workbench restores keep their reusable snapshots; undo history and source files are preserved. Keep theme tokens, localized tooltips and accessibility names; actions must remain usable at the 220-DIP minimum width.
* **Double-click to expand**: double-click the compact file area or background to open the full workbench. Buttons retain their own actions.
* **Independent windows**: double-click the system tray icon creates a new persisted group with its own MainViewModel and MainWindow. Opening another shelf must never change an existing window's group, files or compact/workbench mode.
* **Empty temporary group cleanup**: `StashGroup.Kind` persists Legacy/Saved/Default/Temporary identity (schema v6). Removing or moving the last `StashItem` atomically deletes only empty Temporary groups; an empty folder is still an item. Default and populated groups never expire. `ShelfLifetimeService` reserves newly created empty drop targets until their window takes ownership, and collects a fresh empty target only after its last window closes or switches away. Startup/periodic cleanup removes orphaned empty temporary groups. Never infer new group type from names; old untyped rows are adopted once from known localized generated names. All open workbench lists refresh after automatic deletion and fall back to Default. `ShelfRetentionDays` remains JSON-compatible but is no longer a cleanup policy.

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
  * `Icons` (`3`) — dense file-icon tiles in `IconList`. Compact mode shows a single draggable stack; `CompactIconView` is retained only for settings compatibility and does not select its presentation.
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

### Toast dismissal feedback
* Automatically dismissed notices use one close button: the remaining-time ring surrounds the close icon, with localized remaining seconds in its tooltip. The ring itself becomes incomplete as time elapses (no full-circle track underneath). Clicking anywhere on the ring/icon dismisses the notice with a fade. Hovering or keyboard focus pauses the countdown; failure prompts requiring action remain persistent and show no countdown.
* Headless diagnostics render the countdown at full duration without starting its timer. Real toasts fade in over 160 ms, start counting after the fade-in, and fade out over 180 ms before closing. Timers stop/unsubscribe on `Closed`; headless diagnostics and shutdown bypass animation.

* Notices stack upward on their own monitor with an 8-DIP gap and 16-DIP work-area margins. Closed slots are released after fade-out, then remaining notices move down over 220 ms. Overflow waits for space without consuming its countdown; incoming notices wait if a moving notice still occupies their slot.

### 5. Test Isolation
* Automated scripts set `$env:STASHBAY_NO_SHELL_INTEGRATION = '1'` to prevent writing to real Windows Explorer registry entries.
* Data isolation is achieved via `$env:STASHBAY_HOME = <temp_dir>`.
* `tools/acceptance.ps1` additionally **publishes** the app into the sandbox before running, so it also verifies the publish output (and avoids DLL locks against a running `bin` copy).

### 6. Release Packaging, Verification & Integrity Invariants
* Release tooling requires **PowerShell 7.4+** (`#Requires -Version 7.4`) and **Inno Setup 6** (`ISCC.exe`).
* `tools/package-release.ps1` is the shared single source of truth for packaging verification between local builds (`tools/release.ps1`) and CI (`.github/workflows/ci.yml`).
* **PE Architecture Check**: Validates the PE machine header (x64 = `0x8664`, ARM64 = `0xaa64`) to reject cross-compilation misconfigurations.
* **SQLite Runtime Dependency**: All four publish flavors embed SQLite in `StashBay.exe` using `IncludeNativeLibrariesForSelfExtract`. Framework-dependent bundles (`*-needs-dotnet10`) still require .NET 10 Desktop Runtime. Release packaging rejects external DLLs and verifies extracted SQLite during isolated x64 smokes.
* **Roundtrip ZIP Verification**: Every ZIP archive is created using `tar`, immediately unpacked to an isolated directory, and checked for 100% file inventory and byte-for-byte SHA256 match.
* **Isolated x64 Package Smokes**: Both unpacked x64 distributions (`win-x64` and `win-x64-needs-dotnet10`) execute `--smoke` in separate sandboxed directories with isolated `TEMP`, `TMP`, `APPDATA`, `LOCALAPPDATA`, `STASHBAY_HOME`, and `STASHBAY_NO_SHELL_INTEGRATION=1`.
* **Authenticode Validation**: When code signing is enabled (`RequireSigning`), all 4 application binaries and the installer must have valid digital signatures.
* **LF Line Endings**: `SHA256SUMS.txt` must always use LF line endings (CRLF breaks `sha256sum -c`).

---

### 7. Logon Startup
* Default remains ordinary privileges; `StartWithWindowsAsAdministrator` is opt-in and only acts when `StartWithWindows` is enabled.
* `LogonStartupService` is the single owner of both Run names (`StashBay` and legacy `FileStash`) and the per-user administrator task. Do not add a separate registration path.
* Administrator logon tasks use Priority=4 (Normal), with AdministratorStartupDelaySeconds default 3 and range 0–300. Generation and readback compare the configured delay; normal Run startup is not delayed.
* Task identity: `StashBay.Startup.<SID>`, source `urn:desktop-startup:StashBay:<SID>`. Interactive logon, highest available rights, no battery restriction or execution time limit. Do not use SYSTEM or a noninteractive task.
* `Program` dispatches the fixed `--startup-task <operation> <requester-SID> [delay-seconds]` helper before WPF/settings/IPC. It may only act on the running StashBay executable. A different UAC account is rejected.
* Portable writable directories are supported; protected deployment is recommended, not required. Never add an ACL or Program Files gate.
* Startup only reads system state; task changes require an explicit settings action. Read back before persisting success. Cancellation and uncertainty must not become success or an automatic ordinary-privilege fallback.
* Task Scheduler XML readback accommodates omitted schema defaults (`Enabled`, `RunOnlyIfNetworkAvailable`, `RunOnlyIfIdle`), resolves `DOMAIN\User` names to SIDs via `NTAccount`, and maps missing tasks (`0x80070002` COM / `FileNotFoundException`) to an ordinary unregistered state.
* Ordinary settings persistence is decoupled from Task Scheduler queries: readback failure or an unknown task status alone does not lock saving general preferences; startup reconciliation only triggers upon startup configuration change or an explicit Repair action.
* The no-shell-integration switches suppress task operations as well as Run/context-menu/Send-To operations. Tests must use the memory stores, not real registration.
* `tools/Test-ElevatedStartup.ps1 -ValidateXml -PublishSingleFile` tests the actual engine in isolation. XML parsing and substitute tests do not prove real UAC, task DACLs or logon triggering; those require an isolated account/VM.

---

## 5. Standard Development Commands

```powershell
# Build solution (0 warnings, 0 errors required)
dotnet build --nologo

# Run unit & integration test suite (xUnit.v3, 510 test cases)
dotnet test --nologo -v q

# Run end-to-end automated acceptance verification
pwsh -File tools/acceptance.ps1

# Render the screenshot matrix with real data (fixture → --seed → --shots)
pwsh -File tools/shots.ps1

# Automated harness for OS-level scenarios (lock detection, UNC, >2GB, DPI)
pwsh -File tools/manual-checks.ps1 -SkipBigFile

# Crash recovery, interrupted apply/undo reconciliation & elevation verification
pwsh -File tools/recovery-checks.ps1

# Verify installer upgrade and clean uninstallation
pwsh -File tools/installer-checks.ps1 -Compiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" -CurrentPackages artifacts -BaselineRepository . -BaselinePackages artifacts

# Build release distributions & Inno Setup installer (requires PowerShell 7.4+ and Inno Setup 6)
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
| `assets/shelf.html` | `assets/shelf.png` (紧凑搁架状态展示，`README.zh.md` 与 `docs/i18n/*` 用)、`assets/shelf-en.png` (同一构图、英文字样，`README.md` 用) |

```powershell
$SKILL_DIR = "$env:USERPROFILE\.agents\skills\html-shot"
node "$SKILL_DIR\render.mjs" assets\icon.html assets\logo.png --width 1024 --height 1024 --offline --transparent
node "$SKILL_DIR\icons.mjs" assets\icon.html artifacts\brand --preset electron --only ico --offline
Copy-Item artifacts\brand\icon.ico assets\app.ico -Force
Copy-Item artifacts\brand\icon.ico assets\favicon.ico -Force
Copy-Item artifacts\brand\icon.ico src\FileStash.App\Resources\app.ico -Force
node "$SKILL_DIR\render.mjs" assets\shelf.html assets\shelf.png --selector .stage --dpr 2 --transparent --offline
node "$SKILL_DIR\render.mjs" assets\shelf.html assets\shelf-en.png --selector .stage --dpr 2 --transparent --offline --style ".zh{display:none}.en{display:inline}"
node "$SKILL_DIR\render.mjs" assets\social-preview.html assets\social-preview.png --width 1200 --height 630 --offline
```

### Brand rules

> **2026-11-27 视觉方向更新：温润石墨基底 + 克制品牌青绿点缀（与品牌图标统一）。**
>
> | 层 | 方向 | 覆盖范围 |
> | :--- | :--- | :--- |
> | **品牌层** | 彩色 teal/mint（`#3FC7AE` 系） | 图标字形、光晕、社交卡标题与特性行、拖出卡片 |
> | **产品层** | 温润石墨 + 克制青绿强调 | 运行时 UI：`#0C6E5F`（浅色）/ `#3FC7AE`（深色）仅用于主按钮、焦点环、活动导航细条与拖拽高亮；语义状态使用低饱和绿/琥珀/红胶囊 |
>
> * **Compact shelf visibility**: use the theme `Brush.Shelf*` tokens: a soft pearl-grey surface (`#ECEFEF`) with dark text in light mode and soft graphite (`#23272A`) with light text in dark mode. Each has a contrasting hairline outline, rounded corners and a native compositor shadow so the shelf stays visible on both white and black desktops.
> * **Runtime UI: warm graphite + restrained teal.** Near-black / paper surfaces (`#151719` / `#1C1F22` / `#101214` dark; `#F9FAFA` / `#F1F3F4` / `#E7EAEB` light), hairline borders (`#2A2E33` / `#D8DDDF`), large radii (control 8 / panel 12 / pill 999), and brand teal accent (`#3FC7AE` dark / `#0C6E5F` light) reserved for primary actions, active sidebar indicators, focus rings, and drag targets. All colours live in `Themes/Dark.xaml` and `Themes/Light.xaml` as `Brush.*` tokens; never hard-code a colour in a view.
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



