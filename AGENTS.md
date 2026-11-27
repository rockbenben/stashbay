# StashBay contributor guide

StashBay is a Windows WPF app for moving files through a compact transit shelf and saving reusable snapshots in a full workbench.

## Working in this repository

- Use **StashBay** for the product, windows, logs, tray, installer, and shortcuts. Keep the solution, project files, and C# namespaces under `FileStash.*`. The application executable is `StashBay.exe`.
- `src/FileStash.Core` contains models, storage and restore logic, and service interfaces. Keep it UI-independent. `src/FileStash.Infrastructure` owns SQLite, Windows integration, Git, and IPC. `src/FileStash.App` owns WPF, view models, startup, and diagnostics. Unit and integration tests are in `tests/FileStash.Core.Tests`.
- Use the .NET 10 SDK specified in [global.json](global.json). Run `dotnet build --nologo` and `dotnet test --nologo -v q` for code changes. See the [development runbook](.agents/skills/stashbay-dev/SKILL.md) for acceptance, recovery, installer, and release commands.
- After a Git task with file changes, make one local commit with a one-line English Conventional Commit title. Include only this task's changes and preserve unrelated edits and staging. Skip empty commits. Push only when asked.

## Product behavior

### Transit shelf

- The compact shelf defaults to 220×220 DIP. Contents never resize or reposition it. Preserve manual resizing when returning from the workbench in the same session. The compact view always uses the file stack, including with old `CompactIconView=false` settings.
- The empty shelf shows the drop prompt with an outline only during drag-over. Use one subtle dashed outline around the icon and prompt. The populated shelf has one drag-out surface, `ShelfExportSurface`. It shows the drag prompt and either the file name and size or the item count and total size. The bottom buttons restore to the original location, a specified directory, or an alternate root. Keep tooltips and accessibility names localized and controls usable at the minimum width.
- Double-click the file area or background to expand the workbench. Keep button clicks independent. The global hotkey (default Win+Alt+S) and shake-to-summon share `HandleSummonTrigger` for Explorer selection. The floating ball expands on click or movement under 6 px and snaps within 32 px of its monitor edge.
- A fully successful compact restore consumes only the captured and fully delivered snapshots. Later additions, replaced snapshots, skipped conflicts, and failed or cancelled restores remain pending. Workbench restores keep reusable snapshots. Keep source files and undo history in both modes. When `AutoDismissWhenEmpty` is enabled, fade out only after the shelf is empty.
- Tray double-click creates a persisted group and its own window and `MainViewModel`. Existing windows keep their group, contents, and view mode. Only the main window persists the selected group for startup.
- Clearing Default, including via Clear All, preserves its identity. Deleting the selected non-default group returns the window to Default. Keep group identity in `StashGroup.Kind` (schema v6). Removing or moving the last item atomically deletes an empty Temporary group. An empty folder still counts as an item.
- `ShelfLifetimeService` reserves a new empty drop target until its window takes ownership. An empty target can be collected after its last window closes or switches away. Startup and periodic cleanup collect orphaned empty Temporary groups. Default, Saved, and populated groups never expire. Refresh all open group lists after collection and fall back to Default as needed. Use known localized generated names only for one-time adoption of Legacy groups. Keep `ShelfRetentionDays` for JSON compatibility.

### Workbench

- Restore targets are original location (Git-relative where applicable), alternate root, and specified directory. Preserve the full relative subpath, item-name-only, and flatten options. Keep Ask, Overwrite, Skip, and Rename conflict handling, pre-overwrite blob backups, and undo.
- Preserve the four persisted `WorkbenchViewMode` values. `Tree` stays the default at 0 and nests entries. `List` suppresses entries. `BySourceFolder` uses case-insensitive directory headers. `Icons` stays at 3 and renders in `IconList`.
- The three structured modes share the `ItemTree` TreeView and its context menu. Keep exactly four header grid columns for `ProbeRunner` and keep the named control available to `WindowCatalog`. The probe checks top-level column alignment outside `BySourceFolder` and checks right-edge overflow in all structured modes. Restore any persisted view mode after a diagnostic variant changes it.
- Segmented controls must round-trip the checked enum member through `EnumMatchConverter.ConvertBack` and return `Binding.DoNothing` when unchecked. See `SegmentBindingTests`.

## Windows UI and diagnostics

- Keep App and Core resource key sets and format placeholders complete across all 18 languages, within each project. Put UI strings in the App resx tables and regenerate `Strings.cs` with `tools/gen-strings.ps1`. `ResourceTests` checks parity. Missing keys otherwise fall back to neutral Chinese.
- Use the monitor-aware `WindowEffects` placement helpers and retain PerMonitorV2 and long-path awareness in the manifest. `SystemParameters.WorkArea` is permitted only as the fallback inside `GetWindowWorkArea`. Keep DWM cloaking until `ContentRendered` through `EnableAntiFlash`.
- Use theme `Brush.*` resources for runtime colors. The warm graphite palette uses teal for emphasis, including primary actions, focus, active navigation, and drag targets. The compact shelf uses `Brush.Shelf*`: pearl grey in light mode, graphite in dark mode, with a contrasting outline, rounded corners, and a compositor shadow.
- Auto-dismissed toasts share a close icon and incomplete countdown ring with a localized tooltip. Hover and focus pause it. Action-required failures stay open. Fade in over 160 ms before timing and fade out over 180 ms before closing. Stop and detach timers on close. Diagnostics show the full ring without timing. Diagnostics and shutdown bypass animation.
- Toasts stack upward on their monitor with 8-DIP gaps and 16-DIP work-area margins. Release a slot after fade-out, then move the remaining toasts down over 220 ms. Queued toasts wait for a free slot before counting down.
- For test runs set `STASHBAY_HOME` to an isolated directory for both database and settings. `FILESTASH_HOME` is its legacy alias. Also set `STASHBAY_NO_SHELL_INTEGRATION=1` (legacy `FILESTASH_NO_SHELL_INTEGRATION`) to suppress all registration and unregistration, including Explorer, Send To, Run, and scheduled tasks. Preserve the default `%LOCALAPPDATA%\StashBay` and `%APPDATA%\StashBay\settings.json` paths and legacy FileStash migration.
- Headless flags `--smoke`, `--seed`, `--shots`, `--probe`, and `--inspect` set `App.InSmoke`. Gate modal dialogs on that flag. For smoke, seed, and shots read `%TEMP%\filestash-smoke.txt` even if the process exits successfully. Register new windows in `WindowCatalog`.
- Use `pwsh -File tools/shots.ps1` for screenshots. It seeds fixtures under the same redirected home used for capture and checks that populated and empty variants differ. Seed the same home before running `--probe`. `tools/acceptance.ps1` publishes the app inside its sandbox before verification.

## Startup and release

- [Administrator startup](docs/admin-startup.md) is opt-in and only applies when Start with Windows is enabled. `LogonStartupService` owns both Run value names and the per-user interactive, highest-available task. Ordinary Run startup has no delay. Administrator tasks use Normal priority (4) and a configurable delay of 0–300 seconds, default 3 seconds.
- `Program` dispatches the fixed startup helper before WPF, settings, and IPC. Restrict it to the running executable and the requesting account. Keep portable paths supported. Task changes require explicit settings action. Verify readback before persisting success, and keep unrelated preferences saveable if startup status is unknown. Keep task name `StashBay.Startup.<SID>` and source `urn:desktop-startup:StashBay:<SID>`. Handle omitted scheduler defaults, account names, and the missing-task result during readback. Use memory stores for tests and an isolated account or VM for real UAC or logon checks.
- Release tooling uses PowerShell 7.4+ and Inno Setup 6. `tools/package-release.ps1` is shared by local release builds and CI. Verify x64 and ARM64 PE headers and embedded SQLite in all four portable flavors. Framework-dependent flavors need the .NET 10 Desktop Runtime. Verify ZIP inventory and SHA-256 after extraction. Smoke both unpacked x64 flavors with isolated data, temp and AppData paths, and shell integration disabled. When signing is required, verify Authenticode on all four binaries and the installer. Keep `SHA256SUMS.txt` LF-only. Release names follow `StashBay-v<version>-setup.exe` and `StashBay-v<version>-win-<x64|arm64>[-needs-dotnet10].zip`.

## Brand assets

Edit the HTML sources and regenerate the PNG and ICO files. Keep the teal, grey, and white stacked-card brand glyph on transparency. The social preview leads with English and includes Chinese copy and feature labels. Match the compact shelf and workbench to the current WPF UI. Use perspective and shadows for the floating windows, and keep the preview frame visible on GitHub's light and dark backgrounds.

| Source | Generated assets |
| --- | --- |
| `assets/icon.html` | `assets/logo.png`, `assets/app.ico`, `assets/favicon.ico`, `src/FileStash.App/Resources/app.ico` |
| `assets/shelf.html` | `assets/shelf.png` (Chinese), `assets/shelf-en.png` (English) |
| `assets/social-preview.html` | `assets/social-preview.png` (1200×630) |

```powershell
$skill = "$env:USERPROFILE\.agents\skills\html-shot"
node "$skill/render.mjs" assets/icon.html assets/logo.png --width 1024 --height 1024 --offline --transparent
node "$skill/icons.mjs" assets/icon.html artifacts/brand --preset electron --only ico --offline
Copy-Item artifacts/brand/icon.ico assets/app.ico -Force
Copy-Item artifacts/brand/icon.ico assets/favicon.ico -Force
Copy-Item artifacts/brand/icon.ico src/FileStash.App/Resources/app.ico -Force
node "$skill/render.mjs" assets/shelf.html assets/shelf.png --selector .stage --dpr 2 --transparent --offline
node "$skill/render.mjs" assets/shelf.html assets/shelf-en.png --selector .stage --dpr 2 --transparent --offline --style ".zh{display:none}.en{display:inline}"
node "$skill/render.mjs" assets/social-preview.html assets/social-preview.png --width 1200 --height 630 --offline
```
