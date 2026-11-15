---
name: stashbay-dev
description: >-
  Develop, build, test, and release StashBay (FileStash) Windows desktop app. Use when working on StashBay code, running tests, checking 18-language i18n parity, verifying DropPoint-style stash shelf interactions, or compiling release artifacts and installers.
---

# StashBay Development Runbook

This skill provides step-by-step procedures for building, testing, verifying, and releasing StashBay.

## 1. Environment & Build

- Target Framework: .NET 10 (`net10.0-windows` for App/Tests/Infrastructure, `net10.0` for Core)
- Development SDK minimum in `global.json`: `10.0.402`, with `rollForward: latestFeature`. CI installs and verifies `10.0.403`.

```powershell
# Build solution (0 warnings, 0 errors)
dotnet build --nologo

# Run all unit and integration tests (xUnit.v3, 366 test cases)
dotnet test --nologo -v q
```

## 2. Invariants Check

Before committing or releasing, verify the following invariants:

1. **i18n Parity (18 Languages)**:
   - All 18 `src/FileStash.App/Resources/Strings.*.resx` files and `Strings.cs` must have 100% identical key sets.
   - The repo already enforces this: `tests/FileStash.Core.Tests/ResourceTests.cs` (`EveryLanguageTable_MustHaveTheSameKeys`, `...MustHaveTheSamePlaceholders`, `NonHanLanguageTables_MustNotContainChinese`) plus `LocalizationTests.cs`. Prefer `dotnet test` over ad-hoc scripts.
   - If you do need a quick manual check, the glob must include the dot — `Strings.*resx` also matches `Strings.cs` and the XML parser fails on it:
     ```python
     import xml.etree.ElementTree as ET, glob
     files = glob.glob(r'src/FileStash.App/Resources/Strings.*.resx')   # dot before *.resx
     maps = {f: set(e.get('name') for e in ET.parse(f).getroot().findall('data')) for f in files}
     base = maps[r'src/FileStash.App/Resources/Strings.resx']
     assert all(v == base for v in maps.values()), "i18n keys mismatch!"
     ```
   - Note there is a **second, independent** resource set: `src/FileStash.Core/Resources/Strings.*.resx` (18 files, 55 keys each) for core error/status strings. Parity is required within each project, not across them.

2. **DPI & Multi-Monitor**:
   - `WindowEffects.PlaceNearCursor` / `.GetWindowWorkArea` / `.FitToWorkArea` / `.PlaceAtBottomRight` / `.ClampToWorkArea` must use Win32 `GetMonitorInfo(MonitorFromWindow)` and divide by monitor DPI scale. **Never use `SystemParameters.WorkArea` directly for positioning** — it only reports the primary screen.
   - The single sanctioned exception is inside `WindowEffects.GetWindowWorkArea` itself: when `hwnd` is zero or `GetMonitorInfo` fails it degrades to `SystemParameters.WorkArea` rather than crashing. That is a fallback on an already-failed path (and headless non-interactive runners), not a layout strategy. Any new `SystemParameters.WorkArea` read elsewhere is a regression.

3. **Anti-Flash**:
   - `WindowEffects.EnableAntiFlash` ensures DWM cloaks window until `ContentRendered`.

4. **DropPoint Shelf Interactions**:
   - `ShelfExportSurface` initiates bulk export in compact mode; the workbench keeps its drag-all handle.
   - `AutoDismissWhenEmpty`: window automatically fades out when stash empties.
   - `SummonAtCursor`: window pops up adjacent to mouse pointer.
   - `EnableShakeToSummon`: `ShakeDetector` captures rapid mouse reversals when dragging files.
   - Floating Ball: expands shelf on click or short drag, snaps to edges on release.
   - Action layout: empty shelves show only the drop prompt and close button; populated shelves show a single draggable stack and three restore icon buttons with localized tooltips. Compact mode defaults to 260×220 DIP with theme-specific pearl-grey / dark-graphite surfaces, a contrasting fine outline, rounded corners and native compositor shadow; drag-in shows one subtle dashed outline around the icon and prompt, without an inner solid frame; the default empty shelf has no dashed outline. Fully successful compact restores consume only the captured and fully delivered snapshots, then dismiss the empty shelf. Later additions, updated snapshots, skipped conflicts and cancelled/failed restores remain pending. Workbench restores retain reusable snapshots; source files and undo history are preserved. Double-click the file area or background to expand. Double-click the system tray icon creates an independent shelf window and group. Empty Temporary groups are collected after their last item is removed/moved, or their last empty window closes/switches away. Newly created drop targets are reserved until window registration; Default/Saved and populated groups never expire. The legacy ShelfRetentionDays JSON field no longer controls cleanup. Keep actions usable at 260 DIP, with localized labels and accessibility names. Check `ShelfLifetimeTests` and the independent-window ViewModel test before changing lifecycle behavior.

5. **Named elements are witnesses, not cosmetics**:
   - `ItemTree` must stay a `TreeView` that has a `ContextMenu`, and the header row's grid must keep exactly four `ColumnDefinition`s. `ProbeRunner.IsColumnGrid` counts columns and `WindowCatalog` opens the context-menu variant by name; if either misses, the witness writes "没有量到东西" and **the run still exits 0**.
   - All three structured workbench view modes (`Tree`, `List`, `BySourceFolder`) render through that same TreeView (mode changes the projection, never the control) for exactly that reason, while `Icons` renders dense tiles in `IconList`.
   - The alignment witness compares the header against **top-level** rows only: rows nested under a by-source section are indented by design, and comparing them would report a permanent false failure. It therefore abstains in `BySourceFolder` — the right-edge overflow check still runs over every row.
   - Any catalog variant that mutates state a setter **persists** must restore it before returning (`WindowCatalog.WithViewModeAsync`). The VM and the sandbox home are shared across the whole `--shots` round and reused by `--smoke`/`--probe` in the same home: a leaked mode made the dark-theme half of the matrix and the column witness measure the wrong view, and the acceptance suite turned red for a bug that was in the harness, not the app.

6. **Segmented controls must write back**:
   - `EnumMatchConverter.ConvertBack` returns the parsed enum member when a segment is checked, `Binding.DoNothing` when unchecked. Returning `DoNothing` unconditionally makes every segmented control visually clickable and model-dead — which is what happened to the write-back mode switcher, and no test caught it because nothing clicked.
   - Pinned by `SegmentBindingTests` (real `RadioButton.IsChecked` round-trip on an STA thread) and `RepositoryRulesTests.EnumMatchSegments_ReferenceRealEnumMembers` (every `ConverterParameter` is a real member, and all `WorkbenchViewMode` members appear).

7. **Release Packaging & Verification (`tools/package-release.ps1`)**:
   - Release workflow requires PowerShell 7.4+ and Inno Setup 6 (`ISCC.exe`).
   - `package-release.ps1` runs both locally (via `tools/release.ps1`) and in CI (`.github/workflows/ci.yml`).
   - Validates PE machine architecture (x64 = `0x8664`, ARM64 = `0xaa64`).
   - Framework-dependent packages (`*-needs-dotnet10`) must bundle `e_sqlite3.dll`.
   - Packages are created via `tar`, immediately extracted to temporary sandboxes, and checked for 100% file list equivalence and SHA-256 match.
   - Both x64 editions are unpack-smoke-tested with `--smoke` in isolated sandboxes (`STASHBAY_HOME` and `STASHBAY_NO_SHELL_INTEGRATION=1`).
   - When code signing is active (`RequireSigning`), all 4 executables and the installer must possess valid Authenticode signatures.
   - All checksum entries in `SHA256SUMS.txt` must use LF line endings.

## 3. Acceptance & Release Workflows

```powershell
# Run full automated acceptance suite (sandboxed)
pwsh -File tools/acceptance.ps1

# Render the screenshot matrix with real data (fixture → --seed → --shots + empty-shelf guard)
pwsh -File tools/shots.ps1

# Run manual OS-level automated harness (lock detection, UNC, >2GB files)
pwsh -File tools/manual-checks.ps1 -SkipBigFile

# Run crash recovery, interrupted apply/undo reconciliation & elevation verification
pwsh -File tools/recovery-checks.ps1

# Verify installer upgrade and clean uninstallation
pwsh -File tools/installer-checks.ps1 -Compiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" -CurrentPackages artifacts -BaselineRepository . -BaselinePackages artifacts

# Build release distributions and Inno Setup installer (requires PowerShell 7.4+ and Inno Setup 6)
pwsh -File tools/release.ps1
```

## 4. Headless Diagnostician Flags

All five set `App.InSmoke = true` (so no modal dialogs can hang the process) and run **before** the single-instance mutex, so they never disturb a running instance.

| Flag | Purpose | Dependency |
| :--- | :--- | :--- |
| `--smoke` | Construct, lay out and close every window; write `OK` (or the failure) to `%TEMP%\filestash-smoke.txt` | none |
| `--seed <workspace>` | Seed the (redirected) stash from a fixture workspace — it must already contain `inbox/` and `repo/` with the exact file names `SeedRunner` asks for, or it fails loudly | a fixture workspace |
| `--shots <dir>` | Offscreen-render every window state × theme × work-area height as PNG | run `--seed` first, against the **same** `STASHBAY_HOME` |
| `--probe <dir>` | Measure the live visual tree; export geometry / colour / focus readouts | run `--seed` first |
| `--inspect` | Print the current stash state as tab-separated text to stdout | none |

CI reads the smoke **marker file**, not the process exit code, because a WPF GUI process's exit code is unreliable. `--shots` and `--seed` write to that same marker, and `--shots` counts its own broken variants into it (`FAIL shots=72 …`) while still exiting 0 — a run that silently lost 72 of 208 PNGs looks like a success unless the marker is read. `tools/shots.ps1` reads it. See `src/FileStash.App/Diagnostics/` and `WindowCatalog` (register every new window there, or it is invisible to both smoke and the screenshot matrix).

An unseeded `--shots` run is a **silent success**: every PNG lands, the process exits 0, and the "with data" and "empty" variants hash identically — so the matrix proves the windows still construct, not that the UI looks right. `tools/shots.ps1` builds the fixture, seeds, renders, then asserts those variants differ; use it instead of running the flags by hand.

Product CLI flags (see `Startup/CommandLine.cs`): `--add <paths...>`, `--apply-here <dir>`, `--apply-original`, `--group <name>`, `--minimized`, `--show`, `--quit`, `--unregister-shell`, `--elevated-retry <id>`, `--elevated-home <dir>`.

Release artifacts are generated in `artifacts/`:
- `StashBay-v<version>-setup.exe`: Inno Setup installer (per-user, no admin required)
- `StashBay-v<version>-win-x64.zip`: Self-contained 64-bit portable archive (runtime included)
- `StashBay-v<version>-win-arm64.zip`: Self-contained ARM64 portable archive
- `StashBay-v<version>-win-x64-needs-dotnet10.zip`: Framework-dependent 64-bit (small; needs the .NET 10 Desktop Runtime)
- `StashBay-v<version>-win-arm64-needs-dotnet10.zip`: Framework-dependent ARM64
- `SHA256SUMS.txt`: Cryptographic checksums — **must be LF line endings** (CRLF makes `sha256sum -c` treat `\r` as part of the filename and fail every line)

The four zip flavors come from the four publish profiles in `src/FileStash.App/Properties/PublishProfiles/` (`win-x64`, `win-x64-needs-dotnet10`, `win-arm64`, `win-arm64-needs-dotnet10`). `release.ps1` compiles the installer via `ISCC.exe` (set `ISCC_PATH` or `winget install JRSoftware.InnoSetup`); CI runs a tagged build, verifies Authenticode signatures, packages and smoke-tests via `package-release.ps1`, generates GitHub checksum attestations, and publishes the release assets.


## 5. Logon Startup

The default is ordinary privilege. Optional administrator startup supports portable directories through a per-user interactive task; only same-account UAC is supported. `LogonStartupService` owns both Run names and the administrator task in one transaction; `Program` dispatches the fixed helper before WPF initialization. Application startup never automatically elevates or repairs tasks. See `docs/admin-startup.md`.

```powershell
pwsh -NoProfile -File tools/Test-ElevatedStartup.ps1 -ValidateXml -PublishSingleFile
```

This runs the actual registration engine against substitutes and parses an unregistered TaskDefinition. It does not register tasks, invoke UAC or validate a real logon. Keep actual UAC, account switching, task permissions and logon checks in an isolated account/VM. Both no-shell-integration environment aliases also suppress administrator task operations. Installer cleanup failure must stop uninstall before deleting files.

After publishing both x64 forms, `pwsh -NoProfile -File tools/Test-StartupEntryPoints.ps1 -PublishRoot artifacts` validates the actual app entry point, both no-shell aliases and propagation of WPF failure exit codes. `Program` must use the return value from `Application.Run()`; the generated WPF `Main` discards it.
