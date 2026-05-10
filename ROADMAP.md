# tiktop – Development Roadmap

Cílem je konzolová aplikace podobná `iftop`, která se připojí na MikroTik router přes tik4net, streamuje data z `/tool/torch`, resolvuje DNS jména protistrany a vše zobrazuje v terminálu. Běží na Windows i Linuxu (.NET 9+).

---

## ✅ Hotovo

### P0 – Opravené bugy
- **dstPort bug** – `DataStack.AddRow` přijímá typovaný `ToolTorch`, porty jsou správně odděleny.
- **Off-by-one sloupec** – nová `ComputeLayout()` garantuje `2*aw + bw + 28 == W` přesně, bez driftu.
- **Zelený artefakt `?`** – `ColoredRow` správně resetuje barvu po každém renderu.

### P1 – Základní použitelnost
- **CLI argumenty** – `--host`, `--user`, `--pass`, `--interface`, `--port`, `--no-ssl`, `--count`, `--dns-server`
- **Profily** – `--profile`, `--save-as`, `--list-profiles`, `--delete-profile`, `--no-save` (DPAPI/AES-GCM šifrování hesel)
- **Interaktivní dotaz** – fallback na prompt při chybějících parametrech + výběr interface ze seznamu
- **Klávesová interakce** – `q` ukončit, `p` sort, `r` reset peaků, `d` resolve mode, `t` TX/RX, `b` bary, `L` log škála, `+`/`-` počet řádků
- **DNS resolving** – async `DnsCache` s TTL (10 min pozitivní / 30 s negativní), DnsClient library
- **Error handling** – `GetFriendlyError` s přátelskými hláškami pro Socket/auth/SSL chyby

### P3 – Vizuální vylepšení
- **Unicode block chars** – `█ ▉ ▊ ▋ ▌ ▍ ▎ ▏` pro subchar přesnost v barech
- **Fullwidth scale header** – separator `└───────┴──┴──┴──┴──┴` táhne přes celou šířku terminálu
- **Logaritmická škála** – `L` přepíná lineární ↔ log (`Math.Log`) pro bary i footer mini-bary
- **Toggle bar graph** – `b` skryje/zobrazí bary (main i footer), bez blikání přes TintedRow
- **Footer mini-bary** – 16-znakové barevné bary v patičce TX/RX/TOTAL vedle cur/peak hodnot
- **Bars širší** – nová `ComputeLayout`: aw = min(35, (W-28)/4), bw = W-2\*aw-28 (cca 50 % šířky terminálu jde na bary)

### P5 – Infrastruktura (hotovo)
- **Typovaný `ToolTorch`** – `LoadAsync<ToolTorch>` v `MikrotikWrapper`, `SectionNr` v tik4net
- **SSL/plain + error handling** – `--no-ssl`, `GetFriendlyError`, interaktivní prompt
- **`GetLocalNetworks`** – detekce lokální sítě pro správnou normalizaci TX/RX směru
- **Service name resolution** – `FormatPort`: 28 well-known portů (ssh, https, rdp, …)
- **ResolveMode** (3 stavy: `dns+svc` / `ip+port` / `ip+svc`)
- **DisplayMode** (Both/TxOnly/RxOnly) – klávesa `t`
- **ShortenHostname** – smart zkrácení, zachovává poslední 2 domény

---

## ▶ Zbývá implementovat

### ✅ P2 – Core UX (hotovo)

| Funkce | Popis |
|--------|-------|
| **Sort by window (`1`/`2`/`3`)** | `1`=2s, `2`=10s, `3`=40s – výběr průměrového okna pro řazení; status ukazuje `sort:Total/10s` |
| **Freeze display order (`o`)** | Pořadí řádků se zamkne, data se aktualizují; unfreeze = obnoví dynamické řazení |
| **Freeze/pause (`f` nebo mezerník)** | Zamrazí celý displej (data se sbírají); status badge se aktualizuje i při pauze |
| **Bits vs Bytes (`B` = shift+b)** | Přepíná `b/Kb/Mb` ↔ ×8 (bits mode), indikováno `\| bits` ve status |
| **Fix: TakeLast** | `CreateSnapshot` nyní používá `TakeLast(N)` pro okna – průměry jsou z nejnovějších N sekcí, ne nejstarších |
| **SortByWindowAvg** | Při SortWindow=Medium/Long se třídí podle průměru přes okno, ne jen poslední sekundu |

---

### ✅ P4 – Pokročilé funkce (hotovo)

| # | Funkce | Popis |
|---|--------|-------|
| 5 | **Agregace by src/dst (`a`)** | `a` cykluje: None → BySrc (skupiny dle lokální IP) → ByDst (skupiny dle vzdálené IP); `[*]` indikuje agregovaný řádek |
| 6 | **Scroll (`j`/`k`)** | `j` scrolluje dolů, `k` nahoru; snapshot automaticky načítá dostatek řádků; offset se zobrazuje ve statusu jako `↓N` |

### P4 – Zbývá

| # | Funkce | Popis |
|---|--------|-------|
| 7 | ✅ **Live host filter (`/`)** | `/` otevře inline filter; Enter potvrdí, Esc nebo `/` vymaže; filtruje dle IP i DNS jména; status ukazuje `/text` |
| 8 | ✅ **Kumulativní celkový přenos** | 4. sloupec v řádcích (per-IP cumulative TX/RX od spuštění) + cumulative v patičce TX/RX/TOTAL; layout rozšířen na `2*aw + bw + 36 = W` |
| 9 | **Port-based grouping** | Seskupit toky podle dst portu – vidět celkový HTTPS/HTTP/SSH traffic agregovaně |

---

### P5 – Distribuce (zbývá)

| # | Funkce | Popis |
|---|--------|-------|
| 10 | **Single-file publish** | `dotnet publish -r win-x64/linux-x64 -p:PublishSingleFile=true --self-contained` |
| 11 | **GitHub Actions CI** | Automatický build win-x64 + linux-x64 při push/release |

---

## Klávesové zkratky (aktuální stav)

| Klávesa | Akce |
|---------|------|
| `q` / `Esc` | Ukončit |
| `p` | Cyklovat řazení: Total → TX → RX |
| `1` / `2` / `3` | Průměrové okno pro řazení: instant → 2s → 10s → 40s |
| `r` | Reset peak hodnot |
| `a` | Cyklovat agregaci: None → BySrc → ByDst |
| `d` | Cyklovat resolve mode: dns+svc → ip+port → ip+svc |
| `t` | Cyklovat display: Both → TX-only → RX-only |
| `b` | Zapnout/vypnout bar grafy |
| `B` | Přepnout bits / bytes |
| `L` | Přepnout lineární ↔ logaritmická škála |
| `o` | Zmrazit pořadí řádků (data se aktualizují) |
| `f` / `Space` | Pauza / pokračování displeje |
| `j` / `k` | Scrollovat seznam dolů / nahoru |
| `+` / `-` | Zvětšit / zmenšit počet zobrazených řádků |

---

## Poznámky k architektuře

- **Datový tok:** MikroTik → `MikrotikWrapper` → `DataStack` → `DataSnapshot` → `Visualiser`
- `DataStack` agreguje data po `.section` (= 1 sekunda). Logika oken (2s/10s/40s) je v `CreateSnapshot`.
- `DnsCache` je ortogonální vrstva – `Visualiser` se ptá cache při renderování, cache resolvuje na pozadí.
- `Visualiser` je celý thread-safe přes `_lockObj`; `Draw` i všechny toggle metody běží pod stejným lockem.
- `ComputeLayout` garantuje přesné `2*aw + bw + 28 == W` – žádný pixel drift.
