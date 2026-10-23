# 0070: Unify compilation host evidence classification

## Status

Accepted.

## Context

Source and retained legacy launch separately classified observed Excel/VBE
versions, Windows bitness and exact process architecture. Their precedence and
X86-on-X64 judgments diverged. This comparison does not establish a current
Doctor defect or justify deleting the retained legacy path.

## Decision

Use one stateless classifier for both callers. A known 64-bit-process/32-bit-OS
contradiction or incompatible supported Excel/VBE generation pair yields
Mismatch before any missing fact yields Unknown. With no contradiction,
unsupported or missing evidence is Unknown. Sufficient consistent evidence is
Verified using the existing Excel 9–12/VBA6 and Excel 14+/VBA7 rules and VBE 6/7.

X86 on 64-bit Windows is valid. Win64 follows X64/Arm64 process architecture;
Win32 remains true for supported Windows hosts, including 64-bit Excel.

Acquisition, COM release and lifetime remain with existing owners. Source live
custom constants remain unproved; saved-package values do not fill that gap.
This decision changes neither target eligibility nor live admission order.

## Verification

A shared matrix runs through the classifier and both production callers,
including unknown facts combined with known contradictions. Existing environment
factory tests preserve live versus saved constant authority.
