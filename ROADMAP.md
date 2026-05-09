# tiktop – Development Roadmap

Cílem je konzolová aplikace podobná `iftop`, která se připojí na MikroTik router přes tik4net, streamuje data z `/tool/torch`, resolvuje DNS jména protistrany a vše zobrazuje v terminálu. Běží na Windows i Linuxu (.NET 9+).

---

## Fáze 1 – Konfigurace přes CLI argumenty

**Aktuální stav:** host, user, pass i interface jsou natvrdo v `Program.cs`.

**Co udělat:**
- Přijímat parametry z příkazové řádky:
  ```
  tiktop --host 192.168.4.1 --user admin --pass secret --interface "ether1 - WAN" [--ssl]
  ```
- Fallback na interaktivní dotaz při chybějících parametrech.
- Zvažit `System.CommandLine` (NuGet) nebo ruční parsování `args[]`.
- Přidat volitelný `--port` (default API=8728, API-SSL=8729).

---

## Fáze 2 – Přechod na typovaný `ToolTorch` z tik4net.objects

**Aktuální stav:** tiktop používá nízkoúrovňové `ITikCommand.ExecuteAsync` a parsuje raw slovník words (včetně `.section`).

**Problém:** `ToolTorch` entita v tik4net nemá namapované pole `.section` (používá se pro rozlišení časového snímku dat).

**Co udělat:**
1. **V projektu tik4net** – přidat do `ToolTorch.cs` vlastnost:
   ```csharp
   [TikProperty(".section", IsReadOnly = true)]
   public long SectionNr { get; private set; }
   ```
   A odděleně zachytit řádek bez `src-address` (celkový součet sekce) – buď další entita, nebo speciální příznak.

2. **V tiktop** – přepsat `MikrotikWrapper.StartListening` na:
   ```csharp
   connection.LoadAsync<ToolTorch>(onItem, onError,
       connection.CreateParameter("interface", iface),
       connection.CreateParameter("port", "any"),
       connection.CreateParameter("src-address", "0.0.0.0/0"),
       connection.CreateParameter("dst-address", "0.0.0.0/0"));
   ```
3. `DataStack.AddRow` bude přijímat `ToolTorch` místo `IReadOnlyDictionary<string, string>`.

---

## Fáze 3 – DNS resolving

**Cíl:** Vedle IP adres zobrazovat hostname protistrany (jako iftop).

**Klíčové požadavky:**
- Nesmí blokovat UI ani příjem dat z torch.
- Cache: jednou resolvovat, výsledek si pamatovat (i negativní – NXDOMAIN).
- Timeout: DNS dotaz max ~1s, jinak zobrazit jen IP.

**Návrh:**
```csharp
class DnsCache
{
    // IP → hostname nebo null (pokud se nepodařilo)
    ConcurrentDictionary<string, string?> _cache;

    public string? GetOrResolve(string ip)
    {
        if (_cache.TryGetValue(ip, out var name)) return name;
        // spustit jako fire-and-forget Task, do cache zapsat výsledek
        _ = Task.Run(async () => {
            try {
                var entry = await Dns.GetHostEntryAsync(ip);
                _cache[ip] = entry.HostName;
            } catch {
                _cache[ip] = null;
            }
        });
        return null; // první průchod vrátí null, příště už bude hostname
    }
}
```
- `DataSnapshotIpRow` nebo `Visualiser` si z cache vyžádá hostname při renderování.
- Hostname zkrátit na rozumnou délku (např. 30 znaků).

---

## Fáze 4 – Cross-platform konzolové UI

**Aktuální stav:** `Visualiser` používá `Console.SetCursorPosition` + `Console.Write`, což funguje i na Linuxu. `System.Drawing` byl již odstraněn.

**Co zkontrolovat / vylepšit:**

### Barvy
- `Console.ForegroundColor` / `BackgroundColor` fungují cross-platform.
- Pro pokročilejší styling (gradient, bar chart) zvažit ANSI escape kódy – standardně fungují na Linux terminálech a Windows Terminal (Win10+):
  ```csharp
  // Zapnout VT processing na Windows
  if (OperatingSystem.IsWindows())
      Console.OutputEncoding = Encoding.UTF8; // + SetConsoleMode s ENABLE_VIRTUAL_TERMINAL_PROCESSING
  ```
- Alternativa: knihovna **Spectre.Console** (NuGet) – abstrahuje barvy a layout cross-platform, nabízí live rendering.

### Resize terminálu
- Aktuálně `NrOfItems` reaguje na `Console.WindowHeight` při každém překreslení – to je správné.
- Při zmenšení okna zajistit, že se nepíše mimo buffer (SafePrefix + `Console.WindowWidth` check – již implementováno).

### Překreslování
- Aktuálně 1× za sekundu přes `System.Threading.Timer`.
- Zvážit `PeriodicTimer` (.NET 6+) v async kontextu pro čistší lifecycle.

### Bar chart
- Grafický sloupcový graf šířky proportional k peak hodnotě – zobrazit pro každou IP (TX i RX).
- Pomocí Unicode blokových znaků: `█ ▉ ▊ ▋ ▌ ▍ ▎ ▏` pro sub-char přesnost.

---

## Fáze 5 – Uživatelská interakce

**Cíl:** Základní ovládání klávesnicí bez nutnosti ukončit aplikaci.

| Klávesa | Akce |
|---------|------|
| `q` / `Ctrl+C` | Ukončit |
| `p` | Přepnout řazení: by TX / by RX / by total |
| `r` | Reset peak hodnot |
| `d` | Přepnout zobrazení: s DNS / jen IP |
| `+` / `-` | Zvětšit / zmenšit počet zobrazených řádků |

Implementace: samostatné vlákno čtoucí `Console.ReadKey(intercept: true)` v nekonečné smyčce.

---

## Fáze 6 – Konfigurace připojení (typ ApiSsl vs. Api)

**Aktuální stav:** natvrdo `TikConnectionType.ApiSsl`.

- Na základě `--ssl` / `--no-ssl` CLI parametru volit `TikConnectionType.ApiSsl` nebo `TikConnectionType.Api`.
- Při chybě připojení zobrazit srozumitelnou chybovou hlášku (host nedostupný, špatné heslo, SSL certifikát atd.).
- Zkusit automatický fallback: zkus SSL, při chybě nabídni plain API.

---

## Fáze 7 – Publish a distribuce

- `dotnet publish -r win-x64 -p:PublishSingleFile=true --self-contained`
- `dotnet publish -r linux-x64 -p:PublishSingleFile=true --self-contained`
- Výsledkem je jediný spustitelný soubor bez závislosti na instalaci .NET.
- Zvážit GitHub Actions pro automatický build obou platforem.

---

## Náměty / nápady (neimplementováno)

- **Relativní bary** – aktuálně se každý bar škáluje na globální `PeakTotal`. Alternativa: každé připojení má vlastní peak, bar vždy dobře využívá šířku. Riziko: ztráta vzájemného srovnání velikostí provozů.

---

## Poznámky k architektuře

- **Datový tok** zůstává: MikroTik → `MikrotikWrapper` → `DataStack` → `DataSnapshot` → `Visualiser`.
- `DataStack` agreguje data po `.section` (= 1 sekunda). Logika oken (2s/10s/40s) zůstává správná.
- DNS cache je ortogonální vrstva – `Visualiser` se ptá cache při renderování, cache resolvuje na pozadí.
- Vše thread-safe přes `lock` (stávající přístup) nebo `ConcurrentDictionary` pro DNS cache.
