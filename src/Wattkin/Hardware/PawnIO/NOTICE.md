# PawnIO modules

`IntelMSR.bin` and `IntelMCHBAR.bin` are the official, signed modules of
[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) release
[0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11) (2026-08-30), unmodified,
licensed under the GNU LGPL 2.1 or later (`COPYING`). Source: `IntelMSR.p` and `IntelMCHBAR.p` in
that repository.

| File | SHA-256 |
| --- | --- |
| IntelMSR.bin | `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f` |
| IntelMCHBAR.bin | `3f82b832d99b4aac37d2a20fdb7c9baa2a3bc0488612c9019c9484eb0e8a6eae` |

The PawnIO driver (installed separately: `winget install namazso.PawnIO`) only runs modules signed by
its author, so these files can't be swapped for code of one's own. Wattkin embeds them and talks to the
driver only through its device I/O control interface.

What they allow, by their own source: IntelMSR reads a fixed list of power and thermal MSRs and writes
only a few of them, including `MSR_PKG_POWER_LIMIT` (0x610), the one Wattkin writes. IntelMCHBAR only
reads the memory controller's registers (Wattkin reads the firmware's copy of the power limits at
MCHBAR + 0x59A0).
