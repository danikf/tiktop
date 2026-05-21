# tiktop

A console-based network traffic monitor for MikroTik routers, inspired by `iftop`.

Connects to a RouterOS device via the API, streams live traffic data from `/tool/torch`, and renders a real-time top-talkers view with bar charts and moving averages — directly in the terminal.

```
    2.0Mb          4.0Mb          6.0Mb          8.0Mb         10.0Mb
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴
192.168.1.5   => ec2-1-2-3.eu              3.1Mb   3.0Mb   2.8Mb   5.2Gb
              <=                           512Kb   490Kb   450Kb   1.1Gb
192.168.1.12  => one.one.one.one           1.5Mb   1.4Mb   1.2Mb   3.3Gb
              <=                           210Kb   200Kb   190Kb 820.0Mb
──────────────────────────[ sort:Total | dns+svc | q p 1-3 r a / d t b B L o f j/k ± ]
TX:   cur:   4.6Mb   peak:  10.1Mb    3.1Mb   3.0Mb   2.8Mb   6.3Gb
RX:   cur: 722.0Kb   peak:   2.3Mb  722.0Kb 690.0Kb 640.0Kb   1.9Gb
TOTAL:cur:   5.3Mb   peak:  12.4Mb    3.8Mb   3.7Mb   3.4Mb   8.2Gb
```

*(Traffic level is shown as a colored background spanning the row — green for TX, cyan for RX — with inverted black text on the highlighted portion, iftop-style.)*

![tiktop screenshot](docs/sample.png)

## Features

- **Live top-talkers view** — connections ranked by Total / TX / RX traffic, sortable by current or 2 s / 10 s / 40 s window average
- **iftop-style background bars** — traffic level shown as a colored background spanning the full row width (green TX, cyan RX); inverted black text on the colored portion. Scale ticks span the full terminal width. Footer TX / RX / TOTAL rows use the same style
- **Three moving averages** per connection: 2 s, 10 s, 40 s, plus **cumulative total since start** as a 4th column
- **Linear / logarithmic scale** — toggle with `L` for better visibility of mixed traffic sizes
- **Reverse DNS lookup** — resolves remote hostnames in the background with TTL cache; cycle between DNS+service, raw IP+port, IP+service modes
- **TX / RX display modes** — show both directions or only TX / RX (doubles visible connections)
- **Bits or bytes** — toggle between `b/Kb/Mb` and `bit/Kbit/Mbit` display
- **Freeze & pause** — freeze row order to keep stable positions; pause the entire display while data keeps accumulating
- **Named connection profiles** — auto-saved `_last` + named profiles with encrypted passwords (DPAPI on Windows, AES-GCM on Linux/macOS)
- **Dynamic layout** — adapts to terminal width and height automatically
- **Friendly error messages** — connection refused, authentication failure, SSL errors
- **Cross-platform** — Windows, Linux, macOS (.NET 9+)

## Requirements

- **.NET 9** SDK or runtime
- **MikroTik router** with RouterOS API enabled (`/ip service` → `api` or `api-ssl`)
- Network reachability to the router API port (default: 8729 SSL / 8728 plain)

## Installation

### Build from source

```bash
git clone <repo>
cd tiktop/tiktop
dotnet build
dotnet run -- --host 192.168.1.1 --user admin
```

### Single-file publish

```bash
# Windows
dotnet publish tiktop/tiktop.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained -o publish/win

# Linux / macOS
dotnet publish tiktop/tiktop.csproj -c Release -r linux-x64 -p:PublishSingleFile=true --self-contained -o publish/linux
```

The result is a single executable with no .NET runtime dependency.

### Pre-built binaries

GitHub Actions builds `win-x64` and `linux-x64` self-contained binaries automatically on every push. Tagged releases (`v*`) publish them as GitHub Release assets — download from the **Releases** page.

## Usage

```
tiktop [options]
```

All parameters are optional — missing values are resolved from saved profiles or prompted interactively.

**Connection:**

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--host <ip>` | `-H` | prompted | Router IP address or hostname |
| `--user <name>` | `-u` | prompted | RouterOS username |
| `--pass <password>` | `-p` | prompted | RouterOS password (masked input) |
| `--interface <name>` | `-i` | picker | Interface to monitor (interactive list if omitted) |
| `--port <port>` | | 8729 / 8728 | API port (default depends on `--no-ssl`) |
| `--no-ssl` | | SSL on | Use plain (non-SSL) API connection |
| `--count <n>` | `-n` | auto | Number of connection rows to display |
| `--dns-server <ip>` | `-d` | system DNS | Custom DNS server for reverse lookups |

**Profiles:**

| Option | Description |
|--------|-------------|
| `--profile <name>` | Load a saved profile directly (skips picker) |
| `--pick-profile` | Force profile picker even when auto-connect would fire |
| `--save-as <name>` | Save current params as a named profile after connecting |
| `--save-no-pass` | Save profile / auto-save `_last` without the password |
| `--list-profiles` | List all saved profiles and exit |
| `--delete-profile <name>` | Delete a saved profile and exit |
| `--no-save` / `--private` | Do not auto-save this connection as `_last` |
| `--help` | `-h` | Show help and exit |

### Examples

```bash
# First run — prompts for all values, then saves them as _last
tiktop

# Subsequent runs — zero interactions if _last is complete
tiktop

# Force profile picker to switch to a different profile
tiktop --pick-profile

# Load a named profile directly
tiktop --profile home-router

# Fully specified on CLI
tiktop -H 192.168.1.1 -u admin -p secret -i "ether1 - WAN"

# Save named profile without storing password (will prompt each run)
tiktop --save-as office --save-no-pass
```

## Connection profiles

tiktop remembers your last connection and supports named profiles so you never have to retype parameters.

### Startup behaviour

| Situation | Interactions |
|-----------|-------------|
| No profiles saved | Prompts for all fields (first-run experience) |
| Only `_last` exists, complete + password saved | **0** — auto-connects, prints a one-line status |
| Only `_last` exists, no password saved | Profile picker (Enter) + password prompt = **2** |
| Multiple profiles exist | Profile picker, default = `_last` → **1** (+ password if not saved) |
| `--private` / `--no-save` | Prompts for all fields, nothing saved |

### Profile picker

When multiple profiles are saved (or `_last` is incomplete), tiktop shows a numbered menu:

```
Profiles:
  1) _last      192.168.1.1  danik  ether1 - WAN  [pass]
  2) <NEW>      new connection
  3) home       192.168.1.1  admin  ether1         [pass]  2026-05-08
  4) office     10.0.0.1     admin  ether2                  2026-04-15
Select [1]:
```

Press **Enter** to accept the default (`_last`), or type a number. `[pass]` means the password is stored and will not be prompted.

### Auto-connect (0 interactions)

If only `_last` exists and is fully complete (host, user, interface, password), tiktop connects immediately:

```
[_last] 192.168.1.1  danik  ether1 - WAN  (--pick-profile to switch)
```

Use `--pick-profile` to force the picker anyway (e.g. to switch to a different router).

### Named profiles

```bash
# Save current connection as a named profile (asks whether to include password)
tiktop --save-as home-router

# Save without password (will prompt on each use)
tiktop --save-as home-router --save-no-pass

# Load a named profile directly, bypassing the picker
tiktop --profile home-router

# List / delete
tiktop --list-profiles
tiktop --delete-profile home-router
```

### Password security

| Platform | Method |
|----------|--------|
| Windows  | [DPAPI](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) — encrypted with your Windows user account key |
| Linux / macOS | AES-GCM with machine+user derived key; config file permissions set to `600` |

Profiles are stored in:
- **Windows:** `%APPDATA%\tiktop\profiles.json`
- **Linux / macOS:** `~/.config/tiktop/profiles.json`

## Keyboard controls

| Key | Action |
|-----|--------|
| `q` / `Esc` | Quit |
| `p` | Cycle sort order: **Total → TX → RX** |
| `1` / `2` / `3` | Sort window: **instant → 2 s avg → 10 s avg → 40 s avg**; status shows `sort:Total/10s` |
| `r` | Reset all peak values |
| `a` | Cycle aggregation: **None → by-src (local IP) → by-dst (remote IP) → by-port (dst port)**; aggregated rows show `[*]` for wildcard sides; port mode shows service name (https/ssh/rdp…) in remote column |
| `d` | Cycle address/port display: **dns+svc → ip+port → ip+svc** |
| `t` | Cycle display mode: **Both → TX only → RX only** (doubles visible connections) |
| `b` | Toggle background bar highlighting on/off |
| `B` | Toggle bits / bytes (`Mb` ↔ `Mbit`) |
| `L` | Toggle linear / logarithmic scale |
| `o` | Freeze row order (positions locked, data still updates; press again to unfreeze) |
| `f` / `Space` | Pause / resume display (data keeps accumulating; press again to resume) |
| `/` | Open inline filter — type a substring of IP or hostname, `Enter` confirms, `Esc` or `/` clears; active filter shown as `/text` in status |
| `j` | Scroll down one row |
| `k` | Scroll up one row |
| `+` | Show one more row |
| `-` | Show one fewer row |

## Display layout

```
{scale labels at 20%/40%/60%/80%/100% of peak, spanning full width}
└────────┴────────┴────────┴────────┴────────┴  ← ticks from col 0 to W

{local} => {remote}   {2s avg}  {10s avg}  {40s avg}  {cumul}   ← TX row
          <=           {2s avg}  {10s avg}  {40s avg}  {cumul}   ← RX row
...
──────────────────────[ sort:Total | dns+svc | status ]
TX:    cur: {now}   peak: {peak}   {2s}  {10s}  {40s}  {cumul}
RX:    cur: {now}   peak: {peak}   {2s}  {10s}  {40s}  {cumul}
TOTAL: cur: {now}   peak: {peak}   {2s}  {10s}  {40s}  {cumul}
```

- **Background bar scale** is relative to the all-time peak total traffic. The colored background spans proportionally from the left edge of the terminal across the full row width, including the address text and statistics columns.
- **TX row** (green background) shows outgoing traffic from the local side.
- **RX row** (cyan background) shows incoming traffic to the local side.
- **Local / remote** addresses are determined by comparing against the monitored interface's subnet. If DNS is enabled, hostnames are shown once resolved (long names are intelligently shortened to keep the last two domain components).
- **Moving averages** use windows of 2 s (last 2 sections), 10 s, and 40 s.
- The **footer status badge** shows current sort mode and DNS state.
- On **disconnect**, the badge turns red with the error message; the app waits 2 s before exiting.

## Architecture

Data flows in one direction:

```
MikroTik router
    │  /tool/torch (RouterOS API, SSL)
    ▼
MikrotikWrapper          — opens connection, streams ToolTorch records
    │
    ▼
DataStack                — accumulates records by .section (≈ 1 s slots),
    │                      maintains rolling 50-section cache, tracks peaks
    ▼
DataSnapshot             — immutable snapshot: actual/peak TX+RX,
    │                      3 moving-window averages, top-N IP rows
    ▼
Visualiser               — renders to console via cursor positioning,
                           no flicker, adapts to terminal size
```

### Key components

| File | Role |
|------|------|
| `Program.cs` | Entry point, CLI parsing, keyboard loop, error handling |
| `ConnectionConfig.cs` | CLI argument parsing, interactive prompts, interface picker |
| `MikrotikWrapper.cs` | RouterOS API connection, `/tool/torch` streaming, local subnet detection |
| `Data/DataStack.cs` | Core accumulator with sort mode and peak tracking |
| `Data/DataStackSection.cs` | One time-slice of traffic data, per-connection aggregation |
| `Data/DataSnapshot.cs` | Immutable snapshot passed to the renderer |
| `Visualiser.cs` | Console renderer: bar chart, header scale, footer totals |
| `Helpers/DnsCache.cs` | Fire-and-forget async reverse DNS with TTL-based expiry |
| `Helpers/FormatHelper.cs` | Traffic formatting (b/Kb/Mb/Gb/Tb), hostname shortening |

## Dependencies

| Package | Purpose |
|---------|---------|
| [tik4net](https://github.com/danikovsky/tik4net) | RouterOS API client (project reference) |
| [DnsClient](https://github.com/MichaCo/DnsClient.NET) | Async DNS reverse lookups |

## RouterOS setup

Enable the API service on the router:

```
/ip service set api disabled=no
/ip service set api-ssl disabled=no
```

Create a read-only user for monitoring (recommended):

```
/user group add name=monitor policy=read,api
/user add name=monitor password=secret group=monitor
```

Then run:

```bash
tiktop --host 192.168.1.1 --user monitor --pass secret
```
