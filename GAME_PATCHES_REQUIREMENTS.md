# Game Patches - Reverse Engineering Requirements

## Overview

Many of the game-specific crashes and issues in AstroHybrid require **game patches** that modify the game's executable (`eboot.elf`) at specific memory addresses. These patches are created through **reverse engineering (RE)** of the game binary.

## Why RE is Required

Game patches work by:
1. Finding the exact memory address in `eboot.elf` where a bug occurs
2. Creating a patch that modifies the instruction at that address
3. Applying the patch at runtime via the hybrid loader

**Without the actual game dump (eboot.elf), it is impossible to:**
- Locate the correct addresses for patches
- Verify patches work correctly
- Test patches against different game versions

## Current State

The patches in `Source/game-patches/level_fixes.toml` contain **PLACEHOLDER addresses** (e.g., `0xDEADBEEF`). These are templates only and **will not work** until real addresses are found through RE.

### Issues Requiring Game Patches (Need RE)

| Issue | Description | Level/Location | Status |
|-------|-------------|----------------|--------|
| #164 | Crash at specific level | Unknown | Needs RE |
| #150 | Crash on level load | Unknown | Needs RE |
| #148 | Crash in level 2-3 | Level 2-3 | Needs RE |
| #134 | Crash in level 3-2 | Level 3-2 | Needs RE |
| #132 | Crash in level 4-1 | Level 4-1 | Needs RE |
| #130 | Freeze at level end | Multiple levels | Needs RE |
| #116 | Crash in level 1-2 | Level 1-2 | Needs RE |
| #89 | Crash in Gorilla Nebula | Gorilla Nebula | Needs RE |
| #75 | Crash in construction site | Construction Site | Needs RE |
| #72 | Crash in sand level | Sand level | Needs RE |
| #69 | Crash in water level | Water level | Needs RE |
| #126 | Physics glitch | Multiple | Needs RE |
| #80 | Texture corruption | Multiple | Needs RE |
| #47 | Audio crash | Multiple | Needs RE |
| #43 | Save corruption | Multiple | Needs RE |
| #53 | Boss fight crash | Boss levels | Needs RE |
| #66 | Cutscene crash | Cutscenes | Needs RE |

## How to Contribute Game Patches

### Option 1: Provide Game Dump for RE
If you can legally provide the `eboot.elf` from your game dump, a reverse engineer can:
1. Analyze the binary for crash addresses
2. Create working patches
3. Submit them as a PR

### Option 2: Do RE Yourself
Tools needed:
- **IDA Pro** / **Ghidra** (disassembler)
- **PS5 SDK knowledge** (for understanding syscalls)
- **Game dump** (legally obtained)

Process:
1. Load `eboot.elf` in IDA/Ghidra
2. Find crash address from logs (e.g., `PC: 0x12345678`)
3. Analyze surrounding code to understand the bug
4. Create a patch (NOP, branch fix, value change)
5. Test in-game
6. Submit PR with correct address

### Option 3: Upstream Fixes
Some issues may be fixed in the **Kyty/AnyPS5 emulators** themselves rather than game patches. These go in `Source/hybrid/patches/`.

## Template Format

Game patches use TOML format in `Source/game-patches/level_fixes.toml`:

```toml
[game_patch.level_2_3_crash]
description = "Fix crash in level 2-3 (issue #148)"
module = "eboot.elf"
address = 0x12345678  # REPLACE WITH REAL ADDRESS
original_bytes = "48 8B 05 00 00 00 00"  # REPLACE WITH REAL BYTES
patched_bytes = "48 8B 05 00 00 00 00"   # REPLACE WITH FIXED BYTES
enabled = false  # Set to true after verification
```

## Legal Notice

**Reverse engineering game binaries may violate Terms of Service or EULA.**
- Only RE games you legally own
- Do not share copyrighted game binaries
- Patches (byte changes) are generally acceptable to share
- Full `eboot.elf` files must never be shared

## For Maintainers

When merging game patches:
1. Verify the patch comes from a trusted source
2. Confirm the address matches the game version (01.019.000 PPSA21567)
3. Test the patch doesn't break other levels
4. Update `enabled = true` in the TOML file
5. Document which issue it fixes

## Related Issues

- **Upstream emulator fixes**: `Source/hybrid/patches/0050-0053.patch`
- **Launcher crashes**: Fixed in `AstroHybrid.App/` (C# code)
- **Performance issues**: May need both upstream + game patches