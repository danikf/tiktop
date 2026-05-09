# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

**tiktop** is a console-based network traffic monitor (similar to `iftop`) for MikroTik routers. It connects to a MikroTik device via the RouterOS API, runs `/tool/torch` to stream live traffic data, and renders a real-time top-talkers view in the terminal.

## Build & Run

This is a C# .NET Framework 4.6.2 console application. Build with MSBuild or Visual Studio:

```
msbuild tiktop.sln
tiktop\bin\Debug\tiktop.exe
```

NuGet restore happens automatically via MSBuild. The `tik4net` package (v3.0.1) provides the RouterOS API client.

## Architecture

Data flows in one direction: MikroTik → DataStack → DataSnapshot → Visualiser.

- **`MikrotikWrapper`** — Opens an SSL API connection to the router and calls `/tool/torch` asynchronously. Each response word-set is passed via callback.

- **`DataStack`** — The core accumulator. Torch responses arrive tagged with a `.section` index (one section ≈ one second). `DataStack` groups rows by section into `DataStackSection` objects, keeping a rolling cache of the last 50 sections. A section is "finalized" when its aggregate total-traffic row arrives (the row without `src-address`).

- **`DataStackSection`** — One time-slice. Holds per-connection `DataStackSectionIp` entries keyed by `src:port-dst:port`. Finalized by `AddTotalTraffic()`.

- **`DataSnapshot`** — Immutable value struct produced by `DataStack.CreateSnapshot()` every second (on a `Timer`). Contains actual/peak TX+RX, three moving-window averages (short=2s, medium=10s, long=40s), and the top-N IP rows.

- **`DataSnapshotIpRow`** — Per-connection row in a snapshot, holding the latest `DataStackSectionIp` plus short/medium/long window arrays for bar-graph rendering.

- **`Visualiser`** — Renders to the console using cursor positioning (no screen-clear flicker). Layout: header (scale markers), per-IP rows (top talkers), footer (TX/RX/Total with averages). `NrOfItems` is calculated dynamically from `Console.WindowHeight`.

- **`Helpers/FormatHelper`** — `FormatTraffic()` converts bytes to human-readable (b/Kb/Mb/Gb/Tb). `SafePrefix()` is a string extension for safe truncation.

- **`Helpers/ColorConversionHelper`** — Color utilities (currently unused in rendering).

## Key details

- Router credentials are hardcoded in `Program.cs` (`192.168.1.1`, `danik`, `secret`) — change before running in a different environment.
- The monitored interface is also hardcoded: `"ether1 - WAN"`.
- `DataStackSection` uses `IsFinalized` as a signal; only finalized sections are included in snapshots or averages.
- There is a known copy-paste bug in `DataStack.AddRow`: `dstPort` is read from `items["src-port"]` instead of `items["dst-port"]`.
