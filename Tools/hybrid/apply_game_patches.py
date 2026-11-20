#!/usr/bin/env python3
"""
Apply game patches from level_fixes.toml to the game executable.

This script reads game patches from Source/game-patches/level_fixes.toml
and applies them to AstroBot.exe (the relocated eboot.elf).

Usage: python -I Tools/hybrid/apply_game_patches.py [--input EXE] [--output EXE] [--patches TOML]
"""

import sys
import pathlib
import argparse
import re
from typing import List, Tuple, Optional

try:
    import tomllib
except ImportError:
    import tomli as tomllib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAME_PATCHES_TOML = ROOT / "Source" / "game-patches" / "level_fixes.toml"
DEFAULT_INPUT = ROOT / "Build" / "Run" / "AstroBot.exe"
DEFAULT_OUTPUT = ROOT / "Build" / "RunHybrid" / "AstroBot.exe"


def parse_patch_entry(patch: dict) -> Optional[Tuple[int, bytes, bytes, str]]:
    """Parse a single patch entry from TOML."""
    name = patch.get("name", "unknown")
    target = patch.get("target", "")
    va = patch.get("va", 0)
    expected_hex = patch.get("expected", "")
    value_hex = patch.get("value", "")
    description = patch.get("description", "")
    enabled = patch.get("enabled", True)

    if not enabled:
        print(f"  [SKIP] {name}: disabled")
        return None

    if target != "eboot.elf":
        print(f"  [SKIP] {name}: target {target} not supported")
        return None

    if va == 0x12345678:  # PLACEHOLDER address
        print(f"  [SKIP] {name}: PLACEHOLDER address (needs RE)")
        return None

    if not expected_hex or expected_hex == "xxxxxxxx":
        print(f"  [SKIP] {name}: PLACEHOLDER expected bytes (needs RE)")
        return None

    if not value_hex or value_hex == "xxxxxxxx":
        print(f"  [SKIP] {name}: PLACEHOLDER patched bytes (needs RE)")
        return None

    try:
        expected_bytes = bytes.fromhex(expected_hex.replace(" ", ""))
        patched_bytes = bytes.fromhex(value_hex.replace(" ", ""))
    except ValueError as e:
        print(f"  [ERROR] {name}: invalid hex bytes: {e}")
        return None

    if len(expected_bytes) != len(patched_bytes):
        print(f"  [ERROR] {name}: expected and patched bytes length mismatch")
        return None

    print(f"  [APPLY] {name}: 0x{va:X} ({len(expected_bytes)} bytes) - {description}")
    return (va, expected_bytes, patched_bytes, name)


def apply_patches(input_exe: pathlib.Path, output_exe: pathlib.Path, patches_toml: pathlib.Path) -> bool:
    """Apply all enabled patches from TOML to the executable."""
    if not patches_toml.exists():
        print(f"Patches file not found: {patches_toml}")
        return False

    if not input_exe.exists():
        print(f"Input executable not found: {input_exe}")
        return False

    print(f"Reading patches from: {patches_toml}")
    print(f"Input:  {input_exe}")
    print(f"Output: {output_exe}")

    try:
        with open(patches_toml, "rb") as f:
            data = tomllib.load(f)
    except Exception as e:
        print(f"Failed to parse TOML: {e}")
        return False

    # Read the input executable
    exe_data = bytearray(input_exe.read_bytes())
    print(f"Executable size: {len(exe_data)} bytes (0x{len(exe_data):X})")

    # Parse all patches
    patches = data.get("patch", [])
    if not patches:
        print("No patches found in TOML")
        return True

    applied = 0
    skipped = 0
    errors = 0

    for patch in patches:
        result = parse_patch_entry(patch)
        if result is None:
            skipped += 1
            continue

        va, expected_bytes, patched_bytes, name = result

        # Check if the address is within bounds
        if va + len(expected_bytes) > len(exe_data):
            print(f"  [ERROR] {name}: address 0x{va:X} + {len(expected_bytes)} out of bounds (exe size: 0x{len(exe_data):X})")
            errors += 1
            continue

        # Verify expected bytes match
        current_bytes = bytes(exe_data[va:va + len(expected_bytes)])
        if current_bytes != expected_bytes:
            print(f"  [ERROR] {name}: bytes at 0x{va:X} don't match expected")
            print(f"    Expected: {expected_bytes.hex()}")
            print(f"    Found:    {current_bytes.hex()}")
            errors += 1
            continue

        # Apply the patch
        exe_data[va:va + len(patched_bytes)] = patched_bytes
        applied += 1

    # Write the output executable
    output_exe.write_bytes(bytes(exe_data))
    print(f"\nSummary: {applied} applied, {skipped} skipped, {errors} errors")

    if errors > 0:
        return False

    return True


def main():
    parser = argparse.ArgumentParser(description="Apply game patches from level_fixes.toml")
    parser.add_argument("--input", type=pathlib.Path, default=DEFAULT_INPUT,
                        help="Input executable (default: Build/Run/AstroBot.exe)")
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT,
                        help="Output executable (default: Build/RunHybrid/AstroBot.exe)")
    parser.add_argument("--patches", type=pathlib.Path, default=GAME_PATCHES_TOML,
                        help="Patches TOML file (default: Source/game-patches/level_fixes.toml)")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show what would be patched without writing output")

    args = parser.parse_args()

    if args.dry_run:
        # Just parse and show what would be done
        if not args.patches.exists():
            print(f"Patches file not found: {args.patches}")
            return 1

        with open(args.patches, "rb") as f:
            data = tomllib.load(f)

        patches = data.get("patch", [])
        print(f"Found {len(patches)} patch entries in {args.patches}")
        for patch in patches:
            parse_patch_entry(patch)
        return 0

    success = apply_patches(args.input, args.output, args.patches)
    return 0 if success else 1


if __name__ == "__main__":
    sys.exit(main())