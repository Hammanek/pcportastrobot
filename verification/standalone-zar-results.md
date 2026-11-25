# Standalone ZArchive verification — 2026-11-24

## Implementation and automated tests

- Full-framework build: `VS2022 MSBuild /t:Restore,Build /p:Configuration=Release` — PASS (0 errors).
- VSTest: `Tests/AstroHybrid.App.Tests/bin/Release/AstroHybrid.App.Tests.dll` — **29/29 PASS**, including the Install-button requery regression.
- `git diff --check` — PASS.
- `Tests/test_zar_layout.py` — 17 PASS, 1 pre-existing Windows error in `test_relative_source_archive_symlink_is_resolved_before_linking`; the same error was reproduced in the untouched source checkout before changes.
- `Tests/test_hybrid_launcher.py` — not runnable in the source-only checkout because generated `Build/RunHybrid/u59-preset.cmd` and the MinGCC fixture toolchain are absent. This is not counted as a product regression or a pass.

The legacy .NET Framework test project and VSTest are used instead of `dotnet test` because this host's SDK cannot run the WPF GenerateResource task correctly through the legacy project reference.

## Supplied real archive, read only

- Path: `d:\download\Astro Bot\PPSA21567-1.018.zar`
- Size before/after: **47,474,654,894 bytes** (unchanged).
- Last write UTC before/after: **2026-10-25 10:11:55** (unchanged).
- SHA-256 before/after deployment-independent checks: **42B6BAC4AA3EFF39136F5BF7760457793B0D9C5FADE88A5001A8564090FFC759** (identical).
- Result from the implemented `GameValidator`: **VALID**.
- Title ID: **PPSA21567**.
- Version: **01.018.000** (shown as not fully tested by the existing version policy).
- Reported eboot size: **259,312,583 bytes**.
- Required entries checked/decompressed: **327,274,507 bytes**.
- Genuine progress output advanced through eboot, original modules and sce_sys files to 100%.
- The same validation returned VALID again using a scratch probe whose application and dependency files were copied from the deployed directory. All those files' hashes matched the deployed copies.

No Install, game conversion, game launch or full-archive integrity scan was run. Optional compressed `data/` blocks were intentionally not read by the quick check.

## UI and deployment

- Deployed to `d:\download\Astro Bot\AstroBotPC-AstroHybrid-1.2.23-Windows-x64` after no AstroHybrid process was running.
- Replaced-file backup: `d:\download\Astro Bot\AstroHybrid-backup-20261124-165233`.
- UI Automation opened `AstroHybrid Setup` and observed buttons: `Browse folder...`, `Browse .zar...`, `Cancel`, and `Check source`.
- Setup was closed without selecting Install or changing game files. Installer debug logs show the expected startup only.
- ZstdSharp licence is included at `licenses/ZstdSharp.license.txt`.

## Independent review fixes and final checks

The fresh reviewer found one Critical and three Important issues. All were fixed test-first (RED observed before production fixes), then the whole suite passed **28/28**:

- `SameRuntimeArchiveSurvivesUninstall` and `ArchiveInsideApp0IsRejectedBeforeCleanup` pin source preservation for runtime and nested generated paths.
- `DeclaredHugeEntryIsRejectedBeforeAllocatingItsSize` plus expanded malformed table cases pin bounded metadata/index handling.
- Expanded `StaleOrCancelledValidationCannotProceed` also checks that `IsValidating` clears and Check source can execute again.
- `FolderDumpWhoseNameEndsInZarStillValidates` preserves folder compatibility for directories with a `.zar` suffix.

After the fixes, the real archive again returned VALID with the same required-entry byte count. The final UI smoke test again observed `Browse .zar...`; all seven deployed application/dependency files match the final Release build by SHA-256. The archive SHA-256 remains unchanged.

Pre-fix deployment backup: `d:\download\Astro Bot\AstroHybrid-backup-20261124-165233`.

Final deployment backup: `d:\download\Astro Bot\AstroHybrid-backup-20261124-171838`.

Install button deployment backup: `d:\download\Astro Bot\AstroHybrid-backup-20261124-185707`.

Install-button follow-up: reproduced `IsEnabled=False` for at least 2 seconds after successful real-archive validation despite valid `GameInfo` and `CanProceed=True`. `SuccessfulValidationRequestsInstallRequery` failed before the fix and passed after it; the final UI reported `IsEnabled=True` and `IsKeyboardFocusable=True` throughout the same observation window. The full suite then passed 29/29.

Deferred reviewer minor: generated `bin/` and `obj/` outputs exist as untracked test build artifacts in the temporary source worktree and are not covered by repository ignore rules. They were not copied as source files or included in the application deployment beyond the intended Release launcher files and dependencies.
