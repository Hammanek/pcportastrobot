# Standalone ZArchive Selection and Quick Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let AstroHybrid accept a standalone `.zar`, validate its required contents with visible progress before enabling Install, and install without unpacking all game data.

**Architecture:** Add a managed ZArchive reader using ZstdSharp for bounded reads and decompression. Validation reads the complete index but only required entries; setup materializes those entries into an app-managed folder, reuses folder-based relinking, and links the original archive as the runtime's sibling `app0.zar`. Archive-vs-folder behavior is centralized so saved-path checks and Repair/Rebuild retain the original source path.

**Tech Stack:** C#/.NET Framework 4.8 WPF, ZstdSharp.Port 0.8.8, xUnit 2.9.3, .NET SDK 8/MSBuild, PowerShell.

**Spec:** `Docs/superpowers/specs/2026-11-24-standalone-zar-design.md`

## Global Constraints

- Never modify the selected archive or unpack optional `data/...` game content during validation/setup.
- Install remains hidden/disabled until validation succeeds; quick validation does not certify optional game-data blocks.
- Existing folder dumps and existing call sites remain source-compatible through optional parameters/defaults.
- Archive paths are root-relative and case-insensitive; a single wrapped `app0/` root is rejected.
- All index arithmetic and file sizes are bounds-checked before allocation, seeking, or extraction.
- No git commits or pushes unless the user explicitly asks for them.

## Review Focus

- Malformed/truncated footer, table, names, offsets, or huge declared sizes must produce a validation error, not OOM/overflow or a crash; pin with `ZArchiveReaderTests.MalformedTablesAreRejectedSafely`.
- A corrupt required executable/module must fail while corrupt optional `data/...` is outside quick-check scope; pin with `GameValidatorArchiveTests.CorruptRequiredEntryFailsButOptionalDataIsNotRead`.
- Changing/cancelling selection during an active check must never enable Install for stale results; pin with `SetupWizardViewModelTests.StaleOrCancelledValidationCannotProceed`.
- Removal/rebuild of generated staging must leave the original archive byte-for-byte intact; pin with `ArchiveSetupTests.GeneratedCleanupNeverTouchesSourceArchive`.
- Existing folder sources must keep their current validation and setup behavior; pin with `GameValidatorFolderTests.FolderDumpStillValidates` and existing Python regression suites.

---

### Task 1: Managed ZArchive reader and real-format fixtures

**Files:**
- Create: `AstroHybrid.App/Services/ZArchiveReader.cs`
- Modify: `AstroHybrid.App/AstroHybrid.App.csproj`
- Create: `Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj`
- Create: `Tests/AstroHybrid.App.Tests/ZArchiveFixture.cs`
- Create: `Tests/AstroHybrid.App.Tests/ZArchiveReaderTests.cs`

**Interfaces:**
- Produces: `ArchiveEntry(string Path, bool IsDirectory, ulong Size)`, `ArchiveProgress(int Percent, string Message, long ProcessedBytes, long TotalBytes)`.
- Produces: `sealed class ZArchiveReader : IDisposable` with `static ZArchiveReader Open(string path)`, `IReadOnlyDictionary<string, ArchiveEntry> Entries { get; }`, `void ValidateEntries(IEnumerable<ArchiveEntry> entries, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)`, `void ExtractEntry(ArchiveEntry entry, string outputRoot, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)`, and `byte[] ReadEntry(ArchiveEntry entry, CancellationToken cancellationToken)`.
- Dictionary keys use normalized `/` paths and `StringComparer.OrdinalIgnoreCase`; directories have trailing `/`.

- [ ] **Step 1: Write the failing fixture and reader tests**
  - Build real binary ZArchive fixtures from the upstream footer/16-byte-node/40-byte-offset-record format, with both raw and Zstd-compressed 64 KiB blocks.
  - Cover valid case-insensitive lookup, extraction/read byte equality, monotonic progress ending at 100, cancellation, malformed tables/names/ranges, short reads, corrupt compressed blocks, and path-safe extraction.
  - Assert `MalformedTablesAreRejectedSafely` throws `InvalidDataException` within bounded memory rather than allocating declared attacker-controlled sizes.

- [ ] **Step 2: Run tests to verify RED**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release`
  - Expected: build/test failure because `ZArchiveReader` does not exist.

- [ ] **Step 3: Implement the bounded reader**
  - Deserialize the upstream big-endian structures explicitly; validate footer magic/version/total size, section arithmetic, record counts, root node, child ranges, name lengths, and file offset/size ranges before use.
  - Decode raw or Zstd blocks in 64 KiB chunks with `ZstdSharp.Port`; no full-archive read and no unchecked `ulong` conversion.
  - Extract only through resolved destination paths guaranteed to remain beneath the requested output root; honour cancellation and report processed/total uncompressed bytes.

- [ ] **Step 4: Wire production/test projects and verify GREEN**
  - Add `ZstdSharp.Port` 0.8.8 and the reader to the WPF project; tests use `net48`, Microsoft.NET.Test.Sdk 17.14.1, xUnit 2.9.3 and xunit.runner.visualstudio 2.8.2 with a `ProjectReference` to the app.
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release`
  - Expected: all Task 1 tests PASS.

- [ ] **Step 5: Inspect the focused diff**
  - Run: `git diff --check` and inspect the listed files.
  - Expected: no whitespace errors, dependency lock changes are intentional, and no generated binaries are included.

### Task 2: Standalone archive validation with quick-check progress

**Files:**
- Modify: `AstroHybrid.App/Services/GameValidator.cs`
- Modify: `AstroHybrid.App/Services/GameSetup.cs` (metadata parsing helpers only)
- Modify: `AstroHybrid.App/ViewModels/SetupWizardViewModel.cs`
- Modify: `AstroHybrid.App/Views/SetupWizard.xaml`
- Create: `AstroHybrid.App/Services/GameSource.cs` (path helpers only)
- Create: `Tests/AstroHybrid.App.Tests/GameValidatorArchiveTests.cs`
- Create: `Tests/AstroHybrid.App.Tests/GameValidatorFolderTests.cs`
- Create: `Tests/AstroHybrid.App.Tests/SetupWizardViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1 `ZArchiveReader`, `ArchiveEntry`, and `ArchiveProgress`.
- Produces: `GameValidator(long minEbootSize = 100_000_000)` and `ValidateGameDumpAsync(string gamePath, IProgress<ArchiveProgress>? progress = null, CancellationToken cancellationToken = default)`; existing one-argument calls retain their signature behavior.
- Produces: `GameSource.IsArchive(string? path)` and `GameSource.Exists(string? path)` in `AstroHybrid.App/Services/GameSource.cs` for callers that need file-aware paths.

- [ ] **Step 1: Write failing archive/folder validator and view-model tests**
  - `GameValidatorArchiveTests`: valid standalone source succeeds with title/version/size; missing `eboot.bin`, `sce_module/*.prx`, `sce_sys`, or `data/` fails; tiny eboot fails; wrapped root fails; required metadata decompresses; progress is non-empty and reaches 100.
  - `CorruptRequiredEntryFailsButOptionalDataIsNotRead`: identical fixtures prove corrupt eboot/module fails while a corrupt optional data entry is not opened by the quick check.
  - `SetupWizardViewModelTests.StaleOrCancelledValidationCannotProceed`: simulate changed/cancelled selection and assert `CanProceed == false` and no old `GameInfo` can enable Install.
  - `FolderDumpStillValidates`: a valid folder and the current missing-data error remain unchanged.

- [ ] **Step 2: Run tests to verify RED**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release --filter "FullyQualifiedName~GameValidator|FullyQualifiedName~SetupWizardViewModel"`
  - Expected: archive/progress/cancellation tests FAIL; existing folder baseline is documented before modification.

- [ ] **Step 3: Implement quick archive validation**
  - Add `GameSource.IsArchive/Exists` and branch on an existing `.zar` file; otherwise retain the folder path.
  - Read the complete index but decompress only `eboot.bin`, original `sce_module/*.prx`, and files under `sce_sys/`; require an archive `data/` directory and enforce the injected eboot minimum.
  - Parse `titleId`, `contentVersion`, concept/title metadata from archived `sce_sys/param.json`; share JSON classification helpers with `GameSetup` and retain the existing version warnings.
  - Convert parse/decompression/IO failures into a clear invalid `GameInfo`; never mark the archive valid before the required reads finish.

- [ ] **Step 4: Add wizard progress and safe cancellation/stale-result handling**
  - Add a second **Browse .zar...** button using `Microsoft.Win32.OpenFileDialog` filtered to `.zar`, while retaining the folder button and editable path.
  - Report `ArchiveProgress` in the existing status/progress properties; show a visible determinate progress bar and processed/total status while `IsValidating`.
  - Run the check on a background task, use a selection generation plus `CancellationTokenSource`, ignore stale completions, and prevent Install unless the completed result belongs to the current path.
  - Keep the existing folder path flow intact; no artificial delay or fabricated progress.

- [ ] **Step 5: Verify validator/UI state tests and build**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release`
  - Run: `dotnet build AstroHybrid.App/AstroHybrid.App.csproj -c Release`
  - Expected: all tests PASS and the WPF Release build has no errors.

### Task 3: Setup, Repair/Rebuild and cleanup from a standalone archive

**Files:**
- Modify: `AstroHybrid.App/Services/GameSource.cs` (add setup materialization)
- Modify: `AstroHybrid.App/Services/GameSetup.cs`
- Modify: `AstroHybrid.App/Services/InstallationManager.cs`
- Modify: `AstroHybrid.App/ViewModels/MainWindowViewModel.cs`
- Modify: `AstroHybrid.App/App.xaml.cs` (comment/passthrough only if needed)
- Create: `Tests/AstroHybrid.App.Tests/ArchiveSetupTests.cs`
- Create: `Tests/AstroHybrid.App.Tests/GameSourceTests.cs`

**Interfaces:**
- Produces: `GameSource.MaterializeForSetup(string archivePath, string outputDirectory, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken = default)` returning the generated dump-folder path.
- Consumes: existing `GameSetup.BuildApp0`; extend it with optional `string? archiveSource = null` so the original selected archive is linked/copied rather than duplicated into staging.
- `GameSource.Exists` returns `File.Exists` for `.zar`, otherwise the current `eboot.bin` existence check used by launch/repair.

- [ ] **Step 1: Write failing setup/source tests**
  - Require staging to contain `eboot.bin`, original non-`.guest.prx` modules, `sce_sys`, and optional startup files (`args.txt`, `param.sfx`) while containing no `data/...` files.
  - Supply `archiveSource` and assert `BuildApp0` creates a sibling `app0.zar` referring to the original selected file and leaves normal dump links intact.
  - Assert generated rebuild/uninstall cleanup removes only `Build/ZarSource` and generated runtime paths, with `GeneratedCleanupNeverTouchesSourceArchive` comparing source length/hash/bytes before and after.
  - Assert `GameSource.IsArchive` handles mixed-case `.ZAR`, pasted quoted paths, and that `Exists` distinguishes files from folders.

- [ ] **Step 2: Run tests to verify RED**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release --filter "FullyQualifiedName~ArchiveSetup|FullyQualifiedName~GameSource"`
  - Expected: FAIL because materialization and file-aware saved-source handling are absent.

- [ ] **Step 3: Implement source materialization and setup integration**
  - At the beginning of `GameSetup.Run`, retain `selectedSource`; for archive input materialize required entries to fixed app-owned `Build/ZarSource` and use that generated path only for existing unpack/relink operations.
  - Extend `BuildApp0` with `archiveSource` and avoid deleting/linking a path onto itself. Link/copy the original selected archive to runtime `app0.zar`; do not create a staging copy of the archive.
  - Pass `selectedSource`, not the staging path, to installation manifest/settings-facing metadata. Preserve folder behavior with the default argument.
  - Delete only generated `Build/ZarSource` during uninstall/rebuild; do not follow links beyond the existing guarded deletion helpers.

- [ ] **Step 4: Update repair/launch saved-path handling**
  - Replace directory-plus-`eboot.bin` existence assumptions in `MainWindowViewModel` with `GameSource.Exists` and file-aware messages.
  - Keep `App --setup <path>` and `InstallationManager.RepairAsync/RebuildAsync` passing the original saved path; setup performs the required-entry reads again when materializing.
  - Update obsolete “folder”/“dump directory” messages where an archive is accepted; no RecompLauncher manifest redesign.

- [ ] **Step 5: Verify setup tests and regressions**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release`
  - Run existing portable checks: `python Tests/test_zar_layout.py` and `python Tests/test_hybrid_launcher.py`.
  - Expected: all PASS, with no changes to normal extracted-dump behavior.

### Task 4: Documentation, local build, and verification against the supplied archive

**Files:**
- Modify: `README.md`
- Modify: `Docs/ZAR.md`
- Modify: `Tools/release/make_release.ps1` (generated README text and dependency/license packaging)
- Modify: `NOTICE` / `licenses` only as required by the added dependency license
- Create: `verification/standalone-zar-results.md` outside source-controlled release payloads if a results record is useful

- [ ] **Step 1: Update user-facing instructions**
  - Document standalone `.zar` selection, quick-check scope (required data only), visible progress, and the small staging footprint.
  - State explicitly that optional compressed game-data blocks are not fully checked before Install.
  - Ensure the release script includes every new runtime DLL/config and the dependency licence.

- [ ] **Step 2: Run complete build and test verification**
  - Run: `dotnet test Tests/AstroHybrid.App.Tests/AstroHybrid.App.Tests.csproj -c Release`
  - Run: `dotnet build AstroHybrid.App/AstroHybrid.App.csproj -c Release`
  - Run: `python Tests/test_zar_layout.py` and `python Tests/test_hybrid_launcher.py`
  - Run: `git diff --check` and `git status --short`.
  - Expected: all tests/builds PASS; inspect the intentional diff before deployment.

- [ ] **Step 3: Smoke-test the actual supplied archive without installing**
  - Load the built `AstroHybrid.exe` assembly in Windows PowerShell and invoke `GameValidator.ValidateGameDumpAsync` on `d:\download\Astro Bot\PPSA21567-1.018.zar`, recording progress synchronously to a writer.
  - Expected: explicit PASS, or record the exact real-format incompatibility and return to Task 1/2; do not claim support based only on synthetic fixtures.
  - Verify source file metadata/hash before and after this read-only check.

- [ ] **Step 4: Deploy verified launcher files with a backup**
  - Stop only a running `AstroHybrid.exe` process after confirming it; do not stop `AstroBot.exe` or the emulator.
  - Back up replaced files in the local AstroHybrid folder to a timestamped sibling backup, then copy the Release `AstroHybrid.exe`, generated config, and new managed dependencies into `d:\download\Astro Bot\AstroBotPC-AstroHybrid-1.2.23-Windows-x64`.
  - Leave the source archive, game runtime, saves, patches, and other project files untouched.

- [ ] **Step 5: Smoke-test deployment and record evidence**
  - Start the deployed launcher, verify the Setup UI renders when first-run state permits, then close it without starting Install.
  - Re-run validation using the deployed assembly and confirm the original archive is still unchanged.
  - Record commands, test totals, archive result, backup path, and any limitations in `verification/standalone-zar-results.md`; do not claim a full in-game test unless one is actually run.
