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

# Run all unit and integration tests (xUnit.v3)
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
   - Note there is a **second, independent** resource set: `src/FileStash.Core/Resources/Strings.*.resx` (18 files, 50 keys each) for core error/status strings. Parity is required within each project, not across them.

2. **DPI & Multi-Monitor**:
   - `WindowEffects.PlaceNearCursor` / `.GetWindowWorkArea` / `.FitToWorkArea` / `.PlaceAtBottomRight` / `.ClampToWorkArea` must use Win32 `GetMonitorInfo(MonitorFromWindow)` and divide by monitor DPI scale. **Never use `SystemParameters.WorkArea` directly for positioning** — it only reports the primary screen.
   - The single sanctioned exception is inside `WindowEffects.GetWindowWorkArea` itself: when `hwnd` is zero or `GetMonitorInfo` fails it degrades to `SystemParameters.WorkArea` rather than crashing. That is a fallback on an already-failed path (and headless non-interactive runners), not a layout strategy. Any new `SystemParameters.WorkArea` read elsewhere is a regression.

3. **Anti-Flash**:
   - `WindowEffects.EnableAntiFlash` ensures DWM cloaks window until `ContentRendered`.

4. **DropPoint Shelf Interactions**:
   - `DragAllHandle` initiates bulk export of all stashed files.
   - `AutoDismissWhenEmpty`: window automatically fades out when stash empties.
   - `SummonAtCursor`: window pops up adjacent to mouse pointer.
   - `EnableShakeToSummon`: `ShakeDetector` captures rapid mouse reversals when dragging files.
   - Floating Ball: expands shelf on click or short drag, snaps to edges on release.
   - Action layout: title bar carries only `pin / add / expand / close`; actions with consequences (`write back`, `clear all boxes`) live in the status bar with text. Before adding a title-bar glyph, check whether the command already has an entry point (tray menu, workbench, per-item context menu, hotkey).

5. **Named elements are witnesses, not cosmetics**:
   - `ItemTree` must stay a `TreeView` that has a `ContextMenu`, and the header row's grid must keep exactly four `ColumnDefinition`s. `ProbeRunner.IsColumnGrid` counts columns and `WindowCatalog` opens the context-menu variant by name; if either misses, the witness writes "没有量到东西" and **the run still exits 0**.
   - All three structured workbench view modes (`Tree`, `List`, `BySourceFolder`) render through that same TreeView (mode changes the projection, never the control) for exactly that reason, while `Icons` renders dense tiles in `IconList`.
   - The alignment witness compares the header against **top-level** rows only: rows nested under a by-source section are indented by design, and comparing them would report a permanent false failure. It therefore abstains in `BySourceFolder` — the right-edge overflow check still runs over every row.
   - Any catalog variant that mutates state a setter **persists** must restore it before returning (`WindowCatalog.WithViewModeAsync`). The VM and the sandbox home are shared across the whole `--shots` round and reused by `--smoke`/`--probe` in the same home: a leaked mode made the dark-theme half of the matrix and the column witness measure the wrong view, and the acceptance suite turned red for a bug that was in the harness, not the app.

6. **Segmented controls must write back**:
   - `EnumMatchConverter.ConvertBack` returns the parsed enum member when a segment is checked, `Binding.DoNothing` when unchecked. Returning `DoNothing` unconditionally makes every segmented control visually clickable and model-dead — which is what happened to the write-back mode switcher, and no test caught it because nothing clicked.
   - Pinned by `SegmentBindingTests` (real `RadioButton.IsChecked` round-trip on an STA thread) and `RepositoryRulesTests.EnumMatchSegments_ReferenceRealEnumMembers` (every `ConverterParameter` is a real member, and all `WorkbenchViewMode` members appear).

## 3. Acceptance & Release Workflows

```powershell
# Run full automated acceptance suite (sandboxed)
pwsh -File tools/acceptance.ps1

# Render the screenshot matrix with real data (fixture → --seed → --shots + empty-shelf guard)
pwsh -File tools/shots.ps1

# Run manual OS-level automated harness (lock detection, UNC, >2GB files)
pwsh -File tools/manual-checks.ps1 -SkipBigFile

# Build release distributions and Inno Setup installer
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

Product CLI flags (see `Startup/CommandLine.cs`): `--add <paths...>`, `--apply-here <dir>`, `--apply-original`, `--group <name>`, `--minimized`, `--show`, `--quit`, `--unregister-shell`, `--elevated-retry <id>`.

Release artifacts are generated in `artifacts/`:
- `StashBay-<version>-setup.exe`: Inno Setup installer (per-user, no admin required)
- `StashBay-<version>-win-x64.zip`: Self-contained 64-bit portable archive (runtime included)
- `StashBay-<version>-win-arm64.zip`: Self-contained ARM64 portable archive
- `StashBay-<version>-win-x64-needs-dotnet10.zip`: Framework-dependent 64-bit (small; needs the .NET 10 Desktop Runtime)
- `StashBay-<version>-win-arm64-needs-dotnet10.zip`: Framework-dependent ARM64
- `SHA256SUMS.txt`: Cryptographic checksums — **must be LF line endings** (CRLF makes `sha256sum -c` treat `\r` as part of the filename and fail every line)

The four zip flavors come from the four publish profiles in `src/FileStash.App/Properties/PublishProfiles/` (`win-x64`, `win-x64-needs-dotnet10`, `win-arm64`, `win-arm64-needs-dotnet10`). `release.ps1` skips the installer when `ISCC.exe` is not found (set `ISCC_PATH` or `winget install JRSoftware.InnoSetup`); CI runs a tagged build and then publishes a **draft** release, verifying checksums and `releases/latest` before flipping it to public.
