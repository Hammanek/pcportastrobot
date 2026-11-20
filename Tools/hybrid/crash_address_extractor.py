#!/usr/bin/env python3
"""
Crash Address Extractor for AstroHybrid

Parses hybrid-run.out.log and hybrid-run.err.log files to extract:
- Crash addresses (PC/RIP values)
- Module information
- Stack traces
- Vulkan validation layer messages
- Generates patch templates for level_fixes.toml

Usage:
  python -I Tools/hybrid/crash_address_extractor.py --logs Logs/
  python -I Tools/hybrid/crash_address_extractor.py --logs Logs/ --output patches.toml
  python -I Tools/hybrid/crash_address_extractor.py --logs Logs/ --analyze-vulkan
"""

import sys
import pathlib
import re
import argparse
import json
from typing import List, Dict, Optional, Set
from dataclasses import dataclass, field
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
    vulkan_errors: List[str] = field(default_factory=list)
    vulkan_warnings: List[str] = field(default_factory=list)
    exception_type: Optional[str] = None
    thread_id: Optional[int] = None
    backtrace_symbols: List[str] = field(default_factory=list)


# Known Astro Bot / AstroHybrid crash patterns
CRASH_PATTERNS = {
    'vulkan_device_lost': [
        re.compile(r'VK_ERROR_DEVICE_LOST', re.IGNORECASE),
        re.compile(r'VK_ERROR_OUT_OF_DEVICE_MEMORY', re.IGNORECASE),
        re.compile(r'VK_ERROR_DEVICE_LOST.*?code\s*(\d+)', re.IGNORECASE),
    ],
    'access_violation': [
        re.compile(r'ExceptionCode=0*c0000005', re.IGNORECASE),
        re.compile(r'Access violation', re.IGNORECASE),
        re.compile(r'Segmentation fault', re.IGNORECASE),
    ],
    'stack_overflow': [
        re.compile(r'ExceptionCode=0*c00000fd', re.IGNORECASE),
        re.compile(r'Stack overflow', re.IGNORECASE),
    ],
    'invalid_instruction': [
        re.compile(r'ExceptionCode=0*c000001d', re.IGNORECASE),
        re.compile(r'Invalid instruction', re.IGNORECASE),
    ],
    'assertion_failure': [
        re.compile(r'Assertion failed', re.IGNORECASE),
        re.compile(r'assert.*failed', re.IGNORECASE),
    ],
    'abort': [
        re.compile(r'abort\(\)', re.IGNORECASE),
        re.compile(r'Aborted', re.IGNORECASE),
    ],
}

# Patterns for extracting addresses
ADDRESS_PATTERNS = [
    # Windows exception
    re.compile(r'ExceptionCode=([0-9a-fA-Fx]+).*?ExceptionAddress=([0-9a-fA-Fx]+)', re.IGNORECASE),
    # Register dumps
    re.compile(r'(?:RIP|PC|EIP|InstructionPointer|RSP|RBP)\s*[:=]\s*([0-9a-fA-Fx]+)', re.IGNORECASE),
    # Vulkan error with address
    re.compile(r'VK_ERROR_DEVICE_LOST.*?(?:at|address)\s*([0-9a-fA-Fx]+)', re.IGNORECASE),
    # Game crash
    re.compile(r'(?:Crash|Fault|Exception).*?(?:at|address)\s+([0-9a-fA-F]{8,16})', re.IGNORECASE),
    # Stack trace addresses (12-16 hex chars)
    re.compile(r'(?:^|\s)(0x)?([0-9a-fA-F]{12,16})(?:\s|$)'),
    # Module+offset format
    re.compile(r'(\w+\.dll|\w+\.exe|\w+\.so)\+?([0-9a-fA-Fx]+)', re.IGNORECASE),
]

VULKAN_VALIDATION_PATTERNS = [
    re.compile(r'VUID-[A-Za-z0-9-]+', re.IGNORECASE),
    re.compile(r'Validation Error:.*', re.IGNORECASE),
    re.compile(r'Validation Warning:.*', re.IGNORECASE),
    re.compile(r'VK_LAYER_.*?Error', re.IGNORECASE),
    re.compile(r'VK_LAYER_.*?Warning', re.IGNORECASE),
]


def detect_crash_type(content: str) -> List[str]:
    """Detect crash types from log content."""
    crash_types = []
    for crash_type, patterns in CRASH_PATTERNS.items():
        for pattern in patterns:
            if pattern.search(content):
                crash_types.append(crash_type)
                break
    return crash_types


def extract_vulkan_validation_messages(content: str) -> tuple[List[str], List[str]]:
    """Extract Vulkan validation layer errors and warnings."""
    errors = []
    warnings = []
    lines = content.split('\n')
    for line in lines:
        for pattern in VULKAN_VALIDATION_PATTERNS:
            match = pattern.search(line)
            if match:
                if 'error' in line.lower() or 'vuid-' in line.lower():
                    errors.append(line.strip())
                else:
                    warnings.append(line.strip())
                break
    return errors, warnings


def parse_crash_log(log_path: pathlib.Path) -> List[CrashInfo]:
    """Parse a log file for crash information."""
    crashes = []
    content = log_path.read_text(encoding='utf-8', errors='ignore')
    
    # Detect crash types
    crash_types = detect_crash_type(content)
    
    # Extract Vulkan validation messages
    vulkan_errors, vulkan_warnings = extract_vulkan_validation_messages(content)
    
    lines = content.split('\n')
    current_crash = None
    stack_addresses = []
    backtrace_symbols = []
    
    for i, line in enumerate(lines):
        # Check for crash indicators
        if any(keyword in line.lower() for keyword in ['crash', 'exception', 'fault', 'device lost', 'segfault', 'abort', 'terminate', 'assertion failed', 'vk_error']):
            # Look for PC/RIP address in this line or nearby lines
            pc_addr = None
            exception_type = None
            thread_id = None
            
            # Try all address patterns
            for pattern in ADDRESS_PATTERNS:
                match = pattern.search(line)
                if match:
                    # Extract address from different pattern groups
                    if match.lastindex >= 2 and 'ExceptionCode' in pattern.pattern:
                        pc_addr = int(match.group(2), 0)
                        exception_type = f"ExceptionCode={match.group(1)}"
                    elif match.lastindex >= 2 and (match.group(1).endswith('.dll') or match.group(1).endswith('.exe') or match.group(1).endswith('.so')):
                        # Module+offset format
                        pc_addr = int(match.group(2), 0)
                        exception_type = f"Module={match.group(1)}"
                    elif match.group(1) and match.group(1).startswith('0x'):
                        pc_addr = int(match.group(1), 16)
                    elif match.group(2):
                        pc_addr = int('0x' + match.group(2), 16)
                    else:
                        pc_addr = int(match.group(1), 0)
                    break
            
            # Check for thread ID
            thread_match = re.search(r'(?:Thread|TID|tid)\s*[:=]\s*(\d+)', line, re.IGNORECASE)
            if thread_match:
                thread_id = int(thread_match.group(1))
            
            # Check for error code
            error_match = re.search(r'(?:Error|code|VK_ERROR_DEVICE_LOST)\s*[:=]?\s*(\d+)', line, re.IGNORECASE)
            error_code = error_match.group(1) if error_match else None
            
            current_crash = CrashInfo(
                timestamp=datetime.now().isoformat(),
                pc_address=pc_addr,
                module='eboot.elf',  # Default assumption
                stack_trace=[],
                error_code=error_code,
                log_file=log_path.name,
                vulkan_errors=vulkan_errors,
                vulkan_warnings=vulkan_warnings,
                exception_type=exception_type,
                thread_id=thread_id,
                backtrace_symbols=[]
            )
            stack_addresses = []
            backtrace_symbols = []
        
        # Collect potential stack addresses and backtrace symbols
        if current_crash:
            # Look for stack trace lines (addresses with symbols)
            # Format: 0xADDR  Module+OFFSET  Symbol
            symbol_match = re.search(r'(0x[0-9a-fA-F]{8,16})\s+(\S+)\s+(\S+)', line)
            if symbol_match:
                addr = int(symbol_match.group(1), 16)
                if addr > 0x10000 and addr < 0x7FFFFFFFFFFF:
                    stack_addresses.append(addr)
                    backtrace_symbols.append(f"{symbol_match.group(1)} {symbol_match.group(2)} {symbol_match.group(3)}")
            else:
                # Just addresses
                stack_matches = re.findall(r'\b(0x)?([0-9a-fA-F]{12,16})\b', line)
                for match in stack_matches:
                    addr_str = match[1] if match[0] else '0x' + match[1]
                    addr = int(addr_str, 16)
                    if addr > 0x10000 and addr < 0x7FFFFFFFFFFF:
                        stack_addresses.append(addr)
            
            # If we have a PC address and some stack addresses, save the crash
            if current_crash.pc_address and len(stack_addresses) >= 3:
                current_crash.stack_trace = stack_addresses[:30]
                current_crash.backtrace_symbols = backtrace_symbols[:30]
                crashes.append(current_crash)
                current_crash = None
                stack_addresses = []
                backtrace_symbols = []
    
    # If we found crash types but no specific crashes, create a generic entry
    if crash_types and not crashes:
        crashes.append(CrashInfo(
            timestamp=datetime.now().isoformat(),
            pc_address=None,
            module='eboot.elf',
            stack_trace=[],
            error_code=None,
            log_file=log_path.name,
            vulkan_errors=vulkan_errors,
            vulkan_warnings=vulkan_warnings,
            exception_type=', '.join(crash_types),
            backtrace_symbols=[]
        ))
    
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
    
    vulkan_info = ""
    if crash.vulkan_errors:
        vulkan_info += f"\n# Vulkan Errors: {len(crash.vulkan_errors)}"
        for err in crash.vulkan_errors[:5]:
            vulkan_info += f"\n#   {err}"
    if crash.vulkan_warnings:
        vulkan_info += f"\n# Vulkan Warnings: {len(crash.vulkan_warnings)}"
    
    backtrace_info = ""
    if crash.backtrace_symbols:
        backtrace_info = "\n# Backtrace:\n# " + "\n# ".join(crash.backtrace_symbols[:10])
    
    template = f"""# Crash patch template{issue_ref} - Generated from {crash.log_file}
# Crash Type: {crash.exception_type or 'Unknown'}
# PC Address: {pc_hex}
# Thread ID: {crash.thread_id or 'Unknown'}
# Error Code: {crash.error_code or 'Unknown'}{vulkan_info}
# Stack trace: {len(crash.stack_trace)} addresses{backtrace_info}

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


def generate_json_report(crashes: List[CrashInfo]) -> str:
    """Generate a JSON report for programmatic consumption."""
    report = {
        'generated': datetime.now().isoformat(),
        'total_crashes': len(crashes),
        'crashes': []
    }
    for crash in crashes:
        report['crashes'].append({
            'timestamp': crash.timestamp,
            'pc_address': f"0x{crash.pc_address:X}" if crash.pc_address else None,
            'module': crash.module,
            'stack_trace': [f"0x{addr:X}" for addr in crash.stack_trace],
            'backtrace_symbols': crash.backtrace_symbols,
            'error_code': crash.error_code,
            'exception_type': crash.exception_type,
            'thread_id': crash.thread_id,
            'log_file': crash.log_file,
            'vulkan_errors': crash.vulkan_errors,
            'vulkan_warnings': crash.vulkan_warnings,
        })
    return json.dumps(report, indent=2)


def main():
    parser = argparse.ArgumentParser(description='Extract crash addresses from hybrid logs')
    parser.add_argument('--logs', type=pathlib.Path, default=pathlib.Path('Logs'),
                        help='Directory containing log files (default: Logs/)')
    parser.add_argument('--output', type=pathlib.Path, default=None,
                        help='Output TOML file (default: print to stdout)')
    parser.add_argument('--output-json', type=pathlib.Path, default=None,
                        help='Output JSON report file')
    parser.add_argument('--issue-numbers', type=str, default=None,
                        help='Comma-separated issue numbers for each crash (e.g., "148,134,132")')
    parser.add_argument('--analyze-vulkan', action='store_true',
                        help='Enable detailed Vulkan validation layer analysis')
    
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
              f"Type={crash.exception_type or 'N/A'}, "
              f"Error={crash.error_code or 'N/A'}, "
              f"Stack={len(crash.stack_trace)} addresses, "
              f"Vulkan Errors={len(crash.vulkan_errors)}")
    
    issue_numbers = None
    if args.issue_numbers:
        issue_numbers = [n.strip() for n in args.issue_numbers.split(',')]
    
    # Generate TOML
    toml_content = generate_patches_toml(crashes, issue_numbers)
    
    if args.output:
        args.output.write_text(toml_content, encoding='utf-8')
        print(f"\nGenerated patch templates: {args.output}")
        print("Edit this file and add verified patches to Source/game-patches/level_fixes.toml")
    else:
        print("\n" + "="*60)
        print(toml_content)
    
    # Generate JSON report
    if args.output_json:
        json_content = generate_json_report(crashes)
        args.output_json.write_text(json_content, encoding='utf-8')
        print(f"\nGenerated JSON report: {args.output_json}")
    
    return 0


if __name__ == '__main__':
    sys.exit(main())