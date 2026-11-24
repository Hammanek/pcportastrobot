# Standalone ZArchive selection and quick validation

## Intent

Let the Windows AstroHybrid setup accept a standalone `.zar` file such as `PPSA21567-1.018.zar`, instead of requiring a pre-existing dump folder with an archive renamed to `app0.zar`. Keep the original archive unchanged and avoid extracting the full game.

## Goals

- Add a separate **Browse .zar...** action alongside the existing dump-folder picker; typed/pasted paths accept either form.
- Validate a selected archive before enabling Install: parse its footer/index, reject wrapped `app0/` roots, require the same executable/modules/metadata/data content as a usable dump, and decompress the required startup entries to catch damaged required data.
- Run validation off the UI thread and show genuine phase/byte progress (index, required entries, completion), with stale/cancelled results unable to enable Install.
- During setup, extract only startup files needed by the relinker into an app-owned staging directory, link/copy the original archive as `Build/RunHybrid/app0.zar`, and leave the remaining game data compressed.
- Preserve the selected `.zar` path in settings and make normal Repair/Rebuild and `--setup <path>` work with it.
- Add regression tests using real ZArchive fixtures for valid standalone archives, malformed archives, wrapped roots, missing entries, damaged required data, progress, and the folder-dump compatibility path.

## Non-goals / anti-goals

- Do not decompress every game-data block during the pre-install check; optional game data may only be discovered as damaged when read later.
- Do not unpack the whole archive, modify the source archive, or require users to rename it.
- Do not redesign RecompLauncher selection or unrelated launcher pages.

## Constraints

- Keep the .NET Framework 4.8 WPF application and its existing setup/relinker pipeline.
- Setup needs loose `eboot.bin`, original `sce_module/*.prx`, and metadata before the runtime starts. The archive supplies these through managed, temporary extraction rather than a user-provided sidecar dump.
- Runtime archive layout remains `app0/` plus sibling `app0.zar`; archive entries remain root-relative (`data/...`, not `app0/data/...`).
- The selected standalone path is a file, so path checks in validation, launch/repair, and settings must no longer assume every saved source is a directory.

## Approaches

1. **Chosen: quick required-entry validation.** Parse the complete index and decompress only entries needed for setup/startup plus metadata. Progress is determinate for the selected bytes but this does not certify all optional game-data blocks.
2. Rejected: full archive validation. It would detect damage everywhere, but would read/decompress the whole potentially large archive before Install.
3. Rejected: full extraction before reusing folder setup. It is simpler at one boundary but duplicates all game data and consumes tens of GB.

## Implementation outline

- Introduce a small archive source/reader service with safe index bounds, case-insensitive lookups, bounded streaming reads, extraction of selected entries, and cancellation/progress reporting. Use a maintained Zstandard.NET dependency rather than reimplementing compression.
- Extend `GameValidator` to branch between folder and archive sources while producing the existing `GameInfo`/warning results.
- Extend setup to materialize only required archive entries into a generated source directory, then reuse the existing `BuildApp0` and relinking stages; clean generated staging on rebuild/uninstall without touching the archive.
- Add visible quick-check progress to the wizard and keep Install hidden/disabled until successful validation. Preserve folder picker behavior.
- Update saved-source existence checks used by launch/repair, documentation, and release file lists for any added runtime dependency.
- Build the WPF launcher and run archive unit/regression tests; then deploy only verified launcher files and their dependency into the local AstroHybrid installation, preserving a backup of replaced files.
