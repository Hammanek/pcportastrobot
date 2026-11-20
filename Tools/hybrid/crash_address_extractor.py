#!/usr/bin/env python3
"""
Crash Address Extractor for AstroHybrid

Parses hybrid-run.out.log and hybrid-run.err.log files to extract:
- Crash addresses (PC/RIP values)
- Module information
- Stack traces
- Generates patch templates for level_fixes.toml

Usage:
  python -I Tools/hybrid/crash_address_extractor.py --logs Logs/
  python -I Tools/hybrid/crash_address_extractor.py --logs Logs/ --output patches.toml
"""

import sys
import pathlib
import re
import argparse
from typing import List, Dict, Optional
from dataclasses import dataclass
from datetime import datetime


@dataclass
class CrashInfo:
    """Information extracted from a crash log."""
    timestamp: str
    pc_address: Optional[int]
    module: str
    stack_trace: List[int]
    error_code: Optional[str]
    log_file: str


def parse_crash_log(log_path: pathlib.Path) -> List[CrashInfo]:
    """Parse a log file for crash information."""
    crashes = []
    content = log_path.read_text(encoding='utf-8', errors='ignore')
    
    # Pattern for Vulkan Device Lost / Error 321
    # Pattern for segmentation faults with PC address
    # Pattern for Windows exception addresses
    
    # Common crash patterns in hybrid logs
    patterns = [
        # Windows exception: ExceptionCode=0xc0000005, ExceptionAddress=0x...
        re.compile(r'ExceptionCode=([0-9a-fA-Fx]+).*?ExceptionAddress=([0-9a-fA-Fx]+)', re.IGNORECASE),
        # RIP/PC register dump: RIP=0x... or PC=0x...
        re.compile(r'(?:RIP|PC|EIP|InstructionPointer)\s*[:=]\s*([0-9a-fA-Fx]+)', re.IGNORECASE),
        # Vulkan Device Lost with error code
        re.compile(r'VK_ERROR_DEVICE_LOST.*?(?:Error|code)\s*(\d+)', re.IGNORECASE),
        # Game crash with address
        re.compile(r'Crash.*?(?:at|address)\s*([0-9a-fA-Fx]+)', re.IGNORECASE),
        # Stack trace addresses
        re.compile(r'(?:^|\s)([0-9a-fA-F]{8,16})(?:\s|$)', re.MULTILINE),
    ]
    
    lines = content.split('\n')
    current_crash = None
    stack_addresses = []
    
    for i, line in enumerate(lines):
        # Check for crash indicators
        if any(keyword in line.lower() for keyword in ['crash', 'exception', 'fault', 'device lost', 'segfault', 'abort', 'terminate']):
            # Look for PC/RIP address in this line or nearby lines
            pc_match = re.search(r'(?:RIP|PC|EIP|InstructionPointer)\s*[:=]\s*([0-9a-fA-Fx]+)', line, re.IGNORECASE)
            if not pc_match:
                pc_match = re.search(r'ExceptionAddress=([0-9a-fA-Fx]+)', line, re.IGNORECASE)
            if not pc_match:
                pc_match = re.search(r'(?:at|address)\s+([0-9a-fA-F]{8,16})', line, re.IGNORECASE)
            
            pc_addr = int(pc_match.group(1), 0) if pc_match else None
            
            # Check for error code
            error_match = re.search(r'(?:Error|code|VK_ERROR_DEVICE_LOST)\s*[:=]?\s*(\d+)', line, re.IGNORECASE)
            error_code = error_match.group(1) if error_match else None
            
            current_crash = CrashInfo(
                timestamp=datetime.now().isoformat(),
                pc_address=pc_addr,
                module='eboot.elf',  # Default assumption
                stack_trace=[],
                error_code=error_code,
                log_file=log_path.name
            )
            stack_addresses = []
        
        # Collect potential stack addresses (8-16 hex chars)
        if current_crash:
            # Look for stack trace lines
            stack_matches = re.findall(r'\b([0-9a-fA-F]{12,16})\b', line)
            for match in stack_matches:
                addr = int(match, 16)
                # Filter reasonable addresses (not 0, not too small)
                if addr > 0x10000 and addr < 0x7FFFFFFFFFFF:
                    stack_addresses.append(addr)
            
            # If we have a PC address and some stack addresses, save the crash
            if current_crash.pc_address and len(stack_addresses) >= 3:
                current_crash.stack_trace = stack_addresses[:20]  # Limit to 20
                crashes.append(current_crash)
                current_crash = None
                stack_addresses = []
    
    return crashes


def extract_addresses_from_logs(logs_dir: pathlib.Path) -> List[CrashInfo]:
    """Extract crash addresses from all log files in a directory."""
    all_crashes = []
    
    log_files = list(logs_dir.glob('*.log')) + list(logs_dir.glob('*.txt'))
    for log_file in log_files:
        crashes = parse_crash_log(log_file)
        all_crashes.extend(crashes)
        if crashes:
            print(f"Found {len(crashes)} crash(es) in {log_file.name}")
    
    return all_crashes


def generate_patch_template(crash: CrashInfo, issue_number: Optional[str] = None) -> str:
    """Generate a TOML patch template from crash info."""
    pc_hex = f"0x{crash.pc_address:X}" if crash.pc_address else "0xDEADBEEF"
    issue_ref = f" #{issue_number}" if issue_number else ""
    
    template = f"""# Crash patch template{issue_ref} - Generated from {crash.log_file}
# PC Address: {pc_hex}
# Error Code: {crash.error_code or 'Unknown'}
# Stack trace: {len(crash.stack_trace)} addresses
# First few stack addresses: {', '.join(f'0x{addr:X}' for addr in crash.stack_trace[:5])}

[[patch]]
name        = "crash_fix_{pc_hex.replace('0x', '').lower()}_{datetime.now().strftime('%Y%m%d')}"
target      = "eboot.elf"
va          = {pc_hex}  # REPLACE: This is the crash PC address - verify with RE
expected    = "xxxxxxxx"  # REPLACE: Original bytes at this address (use RE to find)
value       = "xxxxxxxx"  # REPLACE: Patched bytes (fix the crash)
description = f"Fix crash at {pc_hex} (issue{issue_ref}) - NEEDS RE VERIFICATION"

"""
    return template


def generate_patches_toml(crashes: List[CrashInfo], issue_numbers: List[str] = None) -> str:
    """Generate a complete patches.toml file from crash data."""
    header = f"""# Auto-generated crash patch templates for AstroHybrid
# Generated: {datetime.now().isoformat()}
# Source: {len(crashes)} crash(es) from log files
# 
# IMPORTANT: These are TEMPLATES with PLACEHOLDER addresses and bytes.
# You MUST use reverse engineering (IDA Pro/Ghidra) on eboot.elf to:
# 1. Verify the crash address is correct
# 2. Find the actual instruction bytes at that address
# 3. Determine the correct fix (NOP, branch fix, value change, etc.)
# 4. Test the patch in-game
# 
# Never enable a patch without RE verification!

format = 1

"""
    
    patches = []
    for i, crash in enumerate(crashes):
        issue_num = issue_numbers[i] if issue_numbers and i < len(issue_numbers) else None
        patches.append(generate_patch_template(crash, issue_num))
    
    return header + '\n'.join(patches)


def main():
    parser = argparse.ArgumentParser(description='Extract crash addresses from hybrid logs')
    parser.add_argument('--logs', type=pathlib.Path, default=pathlib.Path('Logs'),
                        help='Directory containing log files (default: Logs/)')
    parser.add_argument('--output', type=pathlib.Path, default=None,
                        help='Output TOML file (default: print to stdout)')
    parser.add_argument('--issue-numbers', type=str, default=None,
                        help='Comma-separated issue numbers for each crash (e.g., "148,134,132")')
    
    args = parser.parse_args()
    
    if not args.logs.exists():
        print(f"Logs directory not found: {args.logs}")
        return 1
    
    print(f"Scanning logs in: {args.logs}")
    crashes = extract_addresses_from_logs(args.logs)
    
    if not crashes:
        print("No crashes found in logs.")
        print("Make sure you have hybrid-run.out.log and hybrid-run.err.log from a crash.")
        return 0
    
    print(f"\nTotal crashes found: {len(crashes)}")
    for i, crash in enumerate(crashes):
        print(f"  Crash {i+1}: PC={f'0x{crash.pc_address:X}' if crash.pc_address else 'Unknown'}, "
              f"Error={crash.error_code or 'N/A'}, Stack={len(crash.stack_trace)} addresses")
    
    issue_numbers = None
    if args.issue_numbers:
        issue_numbers = [n.strip() for n in args.issue_numbers.split(',')]
    
    toml_content = generate_patches_toml(crashes, issue_numbers)
    
    if args.output:
        args.output.write_text(toml_content, encoding='utf-8')
        print(f"\nGenerated patch templates: {args.output}")
        print("Edit this file and add verified patches to Source/game-patches/level_fixes.toml")
    else:
        print("\n" + "="*60)
        print(toml_content)
    
    return 0


if __name__ == '__main__':
    sys.exit(main())