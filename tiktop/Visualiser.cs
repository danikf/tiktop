using System;
using System.Linq;
using System.Reflection;
using System.Text;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{
    public enum DisplayMode  { Both, TxOnly, RxOnly }

    /// <summary>
    /// Controls how addresses and ports are shown in item rows.
    /// DnsService  = hostname + service name  (e.g. ec2.amazonaws.com:https)
    /// IpPort      = raw IP   + port number   (e.g. 1.2.3.4:443)
    /// IpService   = raw IP   + service name  (e.g. 1.2.3.4:https)
    /// </summary>
    public enum ResolveMode { DnsService, IpPort, IpService }

    class Visualiser : IDisposable
    {
        const int headerHeight = 3; // scale labels + tick marks + column header
        const int footerHeight = 5; // separator + controls + TX + RX + TOTAL

        private readonly DnsCache _dnsCache;
        private readonly object _lockObj = new object();
        private readonly StringBuilder _frame = new(8192);
        private volatile bool _isDisposed = false;
        private bool _firstDraw = true;
        private bool _helpMode  = false;

        // Dirty-row buffer
        private string[] _rowBuf = Array.Empty<string>();
        private int _bufW, _bufH;

        private string _statusMessage = "Connected";
        private ConsoleColor _statusColor = ConsoleColor.Green;
        private SortMode      _sortMode      = SortMode.Total;
        private SortWindow    _sortWindow    = SortWindow.Medium;
        private AggregateMode _aggregateMode = AggregateMode.None;
        private ResolveMode _resolveMode = ResolveMode.DnsService;
        private int? _countOverride;
        private DisplayMode _displayMode = DisplayMode.Both;
        private bool   _logScale    = false;
        private bool   _showBars    = true;
        private bool   _paused      = false;
        private bool   _bitsMode    = false;
        private bool   _freezeOrder = false;
        private bool   _swapDirection = false;
        private int    _scrollOffset = 0;
        private string _filterText  = "";
        private string[]? _frozenOrder = null;
        private DataSnapshot _lastSnapshot = new DataSnapshot(0, 0, 0);

        private int WindowH => _bufH > 0 ? _bufH : Console.WindowHeight;
        private int RowsPerItem => _displayMode == DisplayMode.Both ? 2 : 1;
        private int NrOfItemsAuto => Math.Max(0, (WindowH - headerHeight - footerHeight) / RowsPerItem);
        public int NrOfItems => _countOverride.HasValue
            ? Math.Min(_countOverride.Value, NrOfItemsAuto)
            : NrOfItemsAuto;
        public ResolveMode ResolveMode  => _resolveMode;
        public DisplayMode DisplayMode  => _displayMode;
        public bool LogScale    => _logScale;
        public bool ShowBars    => _showBars;
        public bool Paused      => _paused;
        public bool BitsMode    => _bitsMode;
        public bool   FreezeOrder => _freezeOrder;
        public int    ScrollOffset => _scrollOffset;
        public string FilterText   => _filterText;
        public bool   HelpMode     => _helpMode;

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
            AnsiHelper.EnableVT();
            Console.CursorVisible = false;
        }

        public void SetStatus(string message, ConsoleColor color = ConsoleColor.Red)
        {
            lock (_lockObj) { _statusMessage = message; _statusColor = color; }
        }

        public void SetSortState(SortMode sort, SortWindow window, AggregateMode agg)
        {
            lock (_lockObj) { _sortMode = sort; _sortWindow = window; _aggregateMode = agg; }
        }

        public void CycleResolveMode()
        {
            lock (_lockObj)
                _resolveMode = (ResolveMode)(((int)_resolveMode + 1) % 3);
        }

        public void CycleDisplayMode()
        {
            lock (_lockObj)
                _displayMode = (DisplayMode)(((int)_displayMode + 1) % 3);
        }

        public void AdjustRowCount(int delta)
        {
            lock (_lockObj)
            {
                int current = _countOverride ?? NrOfItemsAuto;
                _countOverride = Math.Max(1, current + delta);
            }
        }

        public void ToggleLogScale()   { lock (_lockObj) _logScale    = !_logScale; }
        public void ToggleBars()       { lock (_lockObj) _showBars    = !_showBars; }
        public void TogglePause()      { lock (_lockObj) _paused      = !_paused; }
        public void ToggleBitsMode()   { lock (_lockObj) _bitsMode    = !_bitsMode; }

        public void ScrollDown() { lock (_lockObj) _scrollOffset++; }
        public void ScrollUp()   { lock (_lockObj) _scrollOffset = Math.Max(0, _scrollOffset - 1); }
        public void ResetScroll() { lock (_lockObj) _scrollOffset = 0; }

        public void SetFilter(string text)
        {
            lock (_lockObj) { _filterText = text; _scrollOffset = 0; }
        }

        public void ToggleFreezeOrder()
        {
            lock (_lockObj)
            {
                _freezeOrder = !_freezeOrder;
                if (!_freezeOrder) _frozenOrder = null;
            }
        }

        public void SetSwapDirection(bool v) { lock (_lockObj) _swapDirection = v; }

        public void ToggleHelp()
        {
            lock (_lockObj)
            {
                _helpMode = !_helpMode;
                if (_rowBuf.Length > 0) Array.Fill(_rowBuf, null!);
                // Draw help immediately so the screen updates without waiting for the next timer tick.
                if (_helpMode && _bufW > 0 && _bufH > 0)
                {
                    _frame.Clear();
                    DrawHelp();
                    FlushFrame();
                }
            }
        }

        // ── Main draw ─────────────────────────────────────────────────────────

        public void Draw(DataSnapshot data)
        {
            lock (_lockObj)
            {
                if (_isDisposed) return;

                int W = Console.WindowWidth;
                int H = Console.WindowHeight;

                if (_firstDraw || W != _bufW || H != _bufH)
                {
                    Console.Clear();
                    _rowBuf = new string[H];
                    _bufW = W;
                    _bufH = H;
                    _firstDraw = false;
                }

                _frame.Clear();

                if (_helpMode) { DrawHelp(); FlushFrame(); return; }

                DataSnapshot snap = _paused ? _lastSnapshot : data;
                if (!_paused) _lastSnapshot = data;

                DataSnapshotIpRow[] displayItems = snap.TopIpTraffic;
                if (_freezeOrder)
                {
                    if (_frozenOrder == null)
                    {
                        _frozenOrder = displayItems.Select(GetRowKey).ToArray();
                    }
                    else
                    {
                        var byKey = displayItems.ToDictionary(GetRowKey);
                        displayItems = _frozenOrder
                            .Where(k => byKey.ContainsKey(k))
                            .Select(k => byKey[k])
                            .ToArray();
                    }
                }

                _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, displayItems.Length - 1));

                int aw = ComputeLayout();
                int row = DrawHeader(snap, aw, 0);
                long barPeak = _sortWindow == SortWindow.Cumulative ? snap.CumulativeTotal : snap.PeakTotal;
                DrawItems(displayItems, barPeak, NrOfItems, aw, row);
                DrawFooter(snap, aw, _bufH - footerHeight);

                FlushFrame();
            }
        }

        private void FlushFrame()
        {
            if (_frame.Length > 0)
                Console.Out.Write(_frame);
        }

        private static string GetRowKey(DataSnapshotIpRow r) =>
            $"{r.LastSection.SrcAddress}:{r.LastSection.SrcPort}-{r.LastSection.DstAddress}:{r.LastSection.DstPort}";

        // ── Layout ────────────────────────────────────────────────────────────

        // Row: {local:<aw>} => {remote+port:<aw>} <padding> {avg2s} {avg10s} {avg40s} {cumul}
        // Fixed content: 2*aw + 5 + 35 = 2*aw + 40; remaining W - 2*aw - 40 chars are padding
        // (background color covers full width proportionally, including padding and sfx)
        private int ComputeLayout()
        {
            return Math.Max(10, Math.Min(35, (_bufW - 40) / 2));
        }

        // ── Header ────────────────────────────────────────────────────────────

        private int DrawHeader(DataSnapshot data, int aw, int startRow)
        {
            int W = _bufW;
            long peak = data.PeakTotal;
            double step = peak / 5.0;

            // Row 0: scale labels at W*1/5 .. W*5/5, clock right-aligned
            char[] labelLine = new string(' ', W).ToCharArray();
            for (int i = 1; i <= 5; i++)
            {
                string label = FormatHelper.FormatTraffic((long)(step * i), _bitsMode).TrimStart();
                int tickCol = (int)Math.Round(W * i / 5.0) - 1;
                int s = tickCol - label.Length + 1;
                for (int j = 0; j < label.Length; j++)
                    if (s + j >= 0 && s + j < W) labelLine[s + j] = label[j];
            }
            string clock = DateTime.Now.ToString("HH:mm:ss");
            for (int j = 0; j < clock.Length; j++)
            {
                int c = W - clock.Length + j;
                if (c >= 0 && c < W) labelLine[c] = clock[j];
            }
            PlainRow(startRow, new string(labelLine));

            // Row 1: └─────┴─────┴─────┴─────┴──── (tick marks at W*1/5 .. W)
            char[] sep = new string('─', W).ToCharArray();
            sep[0] = '└';
            for (int i = 1; i <= 5; i++)
            {
                int tickCol = (int)Math.Round(W * i / 5.0) - 1;
                if (tickCol >= 0 && tickCol < W) sep[tickCol] = '┴';
            }
            PlainRow(startRow + 1, new string(sep));

            // Row 2: column header  (address labels left, value labels right)
            string colPfx = $"{"local".PadRight(aw)} ── {"remote".PadRight(aw)} ";
            DrawColHeader(startRow + 2, colPfx, W);

            return startRow + 3;
        }

        private void DrawColHeader(int row, string prefix, int W)
        {
            if (row >= _bufH) return;
            int Weff = EffW(row);

            // Each label segment: separator + label right-justified in 7 chars
            // Total suffix = 1 + 7 + 2 + 7 + 2 + 7 + 2 + 7 = 35 chars
            const int sfxLen = 35;
            string prefixPadded = prefix.PadRight(Weff - sfxLen).SafePrefix(Weff - sfxLen);

            int activeCol = _sortWindow switch {
                SortWindow.Short      => 0,
                SortWindow.Long       => 2,
                SortWindow.Cumulative => 3,
                _                     => 1,
            };

            string dirtyKey = $"ch|{activeCol}|{Weff}|{prefixPadded}";
            if (_rowBuf[row] == dirtyKey) return;
            _rowBuf[row] = dirtyKey;

            AnsiHelper.AppendMove(_frame, row);
            _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
            _frame.Append(AnsiHelper.Fg(ConsoleColor.DarkGray));
            _frame.Append(prefixPadded);

            string[] labels = { "2s", "10s", "40s", "total" };
            for (int i = 0; i < labels.Length; i++)
            {
                bool isActive = i == activeCol;
                _frame.Append(AnsiHelper.Fg(ConsoleColor.DarkGray));
                _frame.Append(i == 0 ? " " : "  ");
                _frame.Append(AnsiHelper.Fg(isActive ? ConsoleColor.Yellow : ConsoleColor.DarkGray));
                _frame.Append($"{labels[i],7}");
            }
            _frame.Append(AnsiHelper.Reset);
        }

        // ── Items ─────────────────────────────────────────────────────────────

        private bool MatchesFilter(DataSnapshotIpRow row)
        {
            if (string.IsNullOrEmpty(_filterText)) return true;
            var ip = row.LastSection;
            if (ip.SrcAddress.Contains(_filterText, StringComparison.OrdinalIgnoreCase)) return true;
            if (ip.DstAddress.Contains(_filterText, StringComparison.OrdinalIgnoreCase)) return true;
            var h = _dnsCache.TryGet(ip.DstAddress);
            if (h != null && h.Contains(_filterText, StringComparison.OrdinalIgnoreCase)) return true;
            var hs = _dnsCache.TryGet(ip.SrcAddress);
            if (hs != null && hs.Contains(_filterText, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void DrawItems(DataSnapshotIpRow[] items, long peak, int cnt, int aw, int startRow)
        {
            int row = startRow;

            var visible = string.IsNullOrEmpty(_filterText)
                ? items
                : items.Where(MatchesFilter).ToArray();

            foreach (var ip in visible.Skip(_scrollOffset).Take(cnt))
            {
                bool useDns     = _resolveMode == ResolveMode.DnsService;
                bool useSvcName = _resolveMode != ResolveMode.IpPort;
                bool isAgg     = ip.LastSection.SrcPort == "*";
                bool isPortAgg = isAgg && ip.LastSection.DstAddress == "*" && ip.LastSection.DstPort != "*";

                string local;
                string remote;
                if (isPortAgg)
                {
                    local  = FormatHelper.ShortenHostname("[*]", aw);
                    string portLabel = useSvcName
                        ? FormatHelper.FormatPort(ip.LastSection.DstPort).TrimStart(':')
                        : ip.LastSection.DstPort;
                    remote = portLabel.SafePrefix(aw);
                }
                else if (isAgg)
                {
                    string srcAddr = ip.LastSection.SrcAddress == "*" ? "[*]" : ip.LastSection.SrcAddress;
                    string dstAddr = ip.LastSection.DstAddress == "*" ? "[*]" : ip.LastSection.DstAddress;
                    if (useDns)
                    {
                        if (srcAddr != "[*]") srcAddr = _dnsCache.TryGet(srcAddr) ?? srcAddr;
                        if (dstAddr != "[*]") dstAddr = _dnsCache.TryGet(dstAddr) ?? dstAddr;
                    }
                    local  = FormatHelper.ShortenHostname(srcAddr, aw);
                    remote = FormatHelper.ShortenHostname(dstAddr, aw);
                }
                else
                {
                    local = FormatHelper.ShortenHostname(
                        (useDns ? _dnsCache.TryGet(ip.LastSection.SrcAddress) : null)
                        ?? ip.LastSection.SrcAddress, aw);

                    string dstHostname = (useDns ? _dnsCache.TryGet(ip.LastSection.DstAddress) : null)
                                         ?? ip.LastSection.DstAddress;
                    string portSuffix = useSvcName
                        ? FormatHelper.FormatPort(ip.LastSection.DstPort)
                        : (string.IsNullOrEmpty(ip.LastSection.DstPort) || ip.LastSection.DstPort == "0"
                            ? "" : ":" + ip.LastSection.DstPort);
                    string remoteHost = FormatHelper.ShortenHostname(dstHostname, Math.Max(1, aw - portSuffix.Length));
                    remote = (remoteHost + portSuffix).SafePrefix(aw);
                }

                long txS = (long)ip.ShortRange .Average(s => (double)s.Tx);
                long txM = (long)ip.MediumRange.Average(s => (double)s.Tx);
                long txL = (long)ip.LongRange  .Average(s => (double)s.Tx);
                long rxS = (long)ip.ShortRange .Average(s => (double)s.Rx);
                long rxM = (long)ip.MediumRange.Average(s => (double)s.Rx);
                long rxL = (long)ip.LongRange  .Average(s => (double)s.Rx);

                // Bar length reflects the same window used for sorting
                long txBar = _sortWindow switch {
                    SortWindow.Short      => txS,
                    SortWindow.Long       => txL,
                    SortWindow.Cumulative => ip.CumulativeTx,
                    _                     => txM,
                };
                long rxBar = _sortWindow switch {
                    SortWindow.Short      => rxS,
                    SortWindow.Long       => rxL,
                    SortWindow.Cumulative => ip.CumulativeRx,
                    _                     => rxM,
                };

                string txPfx = $"{local.PadRight(aw)} <= {remote.PadRight(aw)} ";
                string rxPfx = $"{"".PadRight(aw)} => {"".PadRight(aw)} ";
                string txSfx = Sfx(txS, txM, txL, ip.CumulativeTx);
                string rxSfx = Sfx(rxS, rxM, rxL, ip.CumulativeRx);

                // sfx is right-aligned; padding between prefix and sfx gets covered by background
                string txText = txPfx.PadRight(_bufW - txSfx.Length) + txSfx;
                string rxText = rxPfx.PadRight(_bufW - rxSfx.Length) + rxSfx;

                if (_displayMode != DisplayMode.RxOnly)
                {
                    if (_showBars)
                        BgRow(row++, txText, BarLen(txBar, peak), ConsoleColor.Green);
                    else
                        TintedRow(row++, txText, ConsoleColor.Green);
                }
                if (_displayMode != DisplayMode.TxOnly)
                {
                    if (_showBars)
                        BgRow(row++, rxText, BarLen(rxBar, peak), ConsoleColor.Cyan);
                    else
                        TintedRow(row++, rxText, ConsoleColor.Cyan);
                }
            }

            // Clear leftover rows from previous renders
            int end = startRow + cnt * RowsPerItem;
            while (row < end) PlainRow(row++, "");
        }

        private string Sfx(long a, long b, long c, long cum) =>
            $" {FormatHelper.FormatTraffic(a, _bitsMode)}  {FormatHelper.FormatTraffic(b, _bitsMode)}  {FormatHelper.FormatTraffic(c, _bitsMode)}  {FormatHelper.FormatTraffic(cum, _bitsMode, isRate: false)}";

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter(DataSnapshot data, int aw, int startRow)
        {
            int W = _bufW;
            int row = startRow;

            // Separator with status badge (colored)
            string badge;
            ConsoleColor badgeColor;
            lock (_lockObj) { badge = $"[ {_statusMessage} ]"; badgeColor = _statusColor; }
            badge = badge.SafePrefix(W - 4);
            string dashes = new string('─', Math.Max(0, W - badge.Length));
            string sepKey = dashes + badge;
            if (row < _bufH && _rowBuf[row] != sepKey)
            {
                _rowBuf[row] = sepKey;
                AnsiHelper.AppendMove(_frame, row);
                _frame.Append(AnsiHelper.Fg(ConsoleColor.Gray));
                _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
                _frame.Append(dashes.SafePrefix(W));
                int col = dashes.Length;
                if (col < W)
                {
                    _frame.Append(AnsiHelper.Fg(badgeColor));
                    _frame.Append(badge.SafePrefix(W - col));
                }
                _frame.Append(AnsiHelper.Reset);
            }
            row++;

            DrawControls(row++);

            ConsoleColor[] colors    = { ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Gray };
            string[]   lbls          = { "TX:", "RX:", "TOTAL:" };
            long[]     actuals       = { data.ActualTx, data.ActualRx, data.ActualTx + data.ActualRx };
            long[]     peaks         = { data.PeakTx,   data.PeakRx,   data.PeakTotal };
            double[][] avgs          = { data.TxAvgs,   data.RxAvgs,   data.TotalAvgs };
            long[]     cumulatives   = { data.CumulativeTx, data.CumulativeRx, data.CumulativeTotal };

            int footerAvgIdx = _sortWindow switch { SortWindow.Short => 0, SortWindow.Long => 2, _ => 1 };

            for (int i = 0; i < 3; i++)
            {
                string left  = $"{lbls[i]}  cur:{FormatHelper.FormatTraffic(actuals[i], _bitsMode)}   peak:{FormatHelper.FormatTraffic(peaks[i], _bitsMode)}";
                string rates = Sfx((long)avgs[i][0], (long)avgs[i][1], (long)avgs[i][2], cumulatives[i]);
                string text  = left.PadRight(W - rates.Length) + rates;

                if (_showBars)
                    BgRow(row++, text, BarLen((long)avgs[i][footerAvgIdx], peaks[i]), colors[i]);
                else
                    TintedRow(row++, text, colors[i]);
            }
        }

        // ── Help screen ───────────────────────────────────────────────────────

        private static readonly (string Key, string Desc)[][] HelpSections =
        {
            // section name is in Key field with empty Desc
            new[] { ("Sort & data", "") },
            new[] {
                ("p",         "cycle sort order: Total → TX → RX"),
                ("1",         "sort window: 2 s"),
                ("2",         "sort window: 10 s  (default)"),
                ("3",         "sort window: 40 s"),
                ("4",         "sort window: cumulative total since start"),
                ("r",         "reset all peak values"),
                ("a",         "aggregation: none → by src → by dst → by port"),
                ("/",         "filter by IP or hostname  (Enter confirm, Esc clear)"),
            },
            new[] { ("Display", "") },
            new[] {
                ("t",         "direction: TX+RX → TX only → RX only"),
                ("x",         "swap TX ↔ RX direction  (LAN interface fix, saves to profile)"),
                ("d",         "addresses: dns+svc → ip+port → ip+svc"),
                ("b",         "toggle bar chart highlight"),
                ("B",         "toggle bits / bytes  (Mb ↔ Mbit)"),
                ("L",         "toggle linear / logarithmic scale"),
                ("o",         "freeze row order  (data still updates)"),
            },
            new[] { ("Navigation", "") },
            new[] {
                ("j / k",     "scroll down / up"),
                ("+ / -",     "more / fewer rows"),
                ("f / Space", "pause / resume display"),
                ("q / Esc",   "quit"),
            },
            new[] { ("Credits", "") },
            new[] {
                ("tik4net",   "RouterOS API client  github.com/danikf/tik4net"),
                ("DnsClient", "DNS reverse lookups  github.com/MichaCo/DnsClient.NET"),
            },
        };

        private void DrawHelp()
        {
            string version = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0] ?? "";
            string title   = $"tiktop {version} — keyboard shortcuts";
            const string footer = "? or Esc  return to monitor";
            const int    keyW   = 12; // fixed key column width

            int W = _bufW;
            int H = _bufH;
            int row = 0;

            // Centered title
            string titleLine = title.PadLeft((W + title.Length) / 2).PadRight(W);
            HelpRow(row++, titleLine, ConsoleColor.White);
            PlainRow(row++, "");

            foreach (var group in HelpSections)
            {
                if (row >= H - 3) break;
                var (sectionName, noDesc) = group[0];

                if (string.IsNullOrEmpty(noDesc))
                {
                    // Section header with trailing dashes
                    string dashes = new string('─', Math.Max(2, W - sectionName.Length - 5));
                    HelpRow(row++, $"  {sectionName} {dashes}", ConsoleColor.Yellow);
                }
                else
                {
                    // Content rows: key in White, description in DarkGray
                    foreach (var (key, desc) in group)
                    {
                        if (row >= H - 3) break;
                        HelpKeyRow(row++, key, desc, keyW, W);
                    }
                    PlainRow(row++, "");
                }
            }

            // Footer pinned to second-to-last visible row
            int footerRow = H - 2;
            while (row < footerRow) PlainRow(row++, "");
            HelpRow(row, footer, ConsoleColor.DarkCyan);
        }

        private void HelpRow(int row, string text, ConsoleColor fg)
        {
            if (row >= _bufH) return;
            int W     = EffW(row);
            string padded = text.SafePrefix(W).PadRight(W);
            string bufKey = $"h{(int)fg}|{padded}";
            if (_rowBuf[row] == bufKey) return;
            _rowBuf[row] = bufKey;
            AnsiHelper.AppendMove(_frame, row);
            _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
            _frame.Append(AnsiHelper.Fg(fg));
            _frame.Append(padded);
            _frame.Append(AnsiHelper.Reset);
        }

        private void HelpKeyRow(int row, string key, string desc, int keyW, int W)
        {
            if (row >= _bufH) return;
            W = EffW(row);
            string keyPart  = $"  {key.PadRight(keyW)}  ";
            string descPart = desc.SafePrefix(Math.Max(1, W - keyPart.Length));
            string bufKey   = $"hk|{keyPart}|{descPart}";
            if (_rowBuf[row] == bufKey) return;
            _rowBuf[row] = bufKey;
            AnsiHelper.AppendMove(_frame, row);
            _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
            _frame.Append(AnsiHelper.Fg(ConsoleColor.White));
            _frame.Append(keyPart.SafePrefix(W));
            if (keyPart.Length < W)
            {
                _frame.Append(AnsiHelper.Fg(ConsoleColor.DarkGray));
                _frame.Append(descPart.PadRight(W - keyPart.Length).SafePrefix(W - keyPart.Length));
            }
            _frame.Append(AnsiHelper.Reset);
        }

        // ── Bar length ────────────────────────────────────────────────────────

        private int BarLen(long value, long peak)
        {
            if (peak <= 0 || value <= 0) return 0;
            double ratio = _logScale
                ? Math.Log(value + 1.0) / Math.Log(peak + 1.0)
                : (double)value / peak;
            return (int)(Math.Min(1.0, ratio) * _bufW);
        }

        // ── Row writers ───────────────────────────────────────────────────────

        // Available width for a row: last row gets W-1 to avoid terminal auto-scroll.
        private int EffW(int row) => row == _bufH - 1 ? _bufW - 1 : _bufW;

        private void PlainRow(int row, string content)
        {
            if (row >= _bufH) return;
            int W     = EffW(row);
            string padded = content.SafePrefix(W).PadRight(W);
            if (_rowBuf[row] == padded) return;
            _rowBuf[row] = padded;
            AnsiHelper.AppendMove(_frame, row);
            _frame.Append(AnsiHelper.Fg(ConsoleColor.Gray));
            _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
            _frame.Append(padded);
            _frame.Append(AnsiHelper.Reset);
        }

        // Row with a single uniform foreground color (no background).
        private void TintedRow(int row, string content, ConsoleColor fg)
        {
            if (row >= _bufH) return;
            int W     = EffW(row);
            string padded = content.SafePrefix(W).PadRight(W);
            string key    = $"{(int)fg}|{padded}";
            if (_rowBuf[row] == key) return;
            _rowBuf[row] = key;
            AnsiHelper.AppendMove(_frame, row);
            _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
            _frame.Append(AnsiHelper.Fg(fg));
            _frame.Append(padded);
            _frame.Append(AnsiHelper.Reset);
        }

        // Row with background color covering the first barLen columns (iftop style).
        // Filled part: bg=barColor, fg=Black. Empty part: bg=Black, fg=barColor.
        private void BgRow(int row, string text, int barLen, ConsoleColor barColor)
        {
            if (row >= _bufH) return;
            int W = EffW(row);
            string padded = text.SafePrefix(W).PadRight(W);
            string key    = $"bg{barLen}|{(int)barColor}|{padded}";
            if (_rowBuf[row] == key) return;
            _rowBuf[row] = key;
            AnsiHelper.AppendMove(_frame, row);
            int fill = Math.Clamp(barLen, 0, W);
            if (fill > 0)
            {
                _frame.Append(AnsiHelper.Bg(barColor));
                _frame.Append(AnsiHelper.Fg(ConsoleColor.Black));
                _frame.Append(padded, 0, fill);
            }
            if (fill < W)
            {
                _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
                _frame.Append(AnsiHelper.Fg(barColor));
                _frame.Append(padded, fill, W - fill);
            }
            _frame.Append(AnsiHelper.Reset);
        }

        // ── Controls row ──────────────────────────────────────────────────────

        private void DrawControls(int row)
        {
            if (row >= _bufH) return;

            int W        = EffW(row);
            bool hints   = W >= 110;

            string sortVal = _sortMode switch { SortMode.Tx => "TX", SortMode.Rx => "RX", _ => "Total" };
            string aggVal  = _aggregateMode switch {
                AggregateMode.BySrc  => ":src",
                AggregateMode.ByDst  => ":dst",
                AggregateMode.ByPort => ":port",
                _                    => "",
            };
            string dnsVal  = _resolveMode switch {
                ResolveMode.IpPort    => ":ip+port",
                ResolveMode.IpService => ":ip+svc",
                _                     => ":dns+svc",
            };
            string dispVal = _displayMode switch { DisplayMode.TxOnly => ":TX", DisplayMode.RxOnly => ":RX", _ => "" };
            string filtVal = !string.IsNullOrEmpty(_filterText) ? $":{_filterText}" : "";
            string jVal    = _scrollOffset > 0 ? $" ↓{_scrollOffset}" : (hints ? " ↓" : "");

            // Each chip: (key, value-after-key, isActive)
            // isActive → yellow bg+black fg on key; inactive → dark-gray bg+white fg
            (string key, string val, bool active)[] chips = {
                ("q",  hints ? " quit"  : "",                              false),
                ("?",  hints ? " help"  : "",                              false),
                ("p",  ":" + sortVal,                                      _sortMode != SortMode.Total),
                ("1",  hints ? ":2s"    : "",                             _sortWindow == SortWindow.Short),
                ("2",  hints ? ":10s"   : "",                             _sortWindow == SortWindow.Medium),
                ("3",  hints ? ":40s"   : "",                             _sortWindow == SortWindow.Long),
                ("4",  hints ? ":∑"     : "",                             _sortWindow == SortWindow.Cumulative),
                ("r",  hints ? " reset" : "",                              false),
                ("a",  aggVal,                                             _aggregateMode != AggregateMode.None),
                ("/",  filtVal,                                            !string.IsNullOrEmpty(_filterText)),
                ("d",  dnsVal,                                             _resolveMode != ResolveMode.DnsService),
                ("t",  dispVal,                                            _displayMode != DisplayMode.Both),
                ("x",  _swapDirection ? (hints ? " swap" : "") : "",      _swapDirection),
                ("b",  !_showBars   ? (hints ? " off"   : "") : "",       !_showBars),
                ("B",  _bitsMode    ? (hints ? " bits"  : "") : "",       _bitsMode),
                ("L",  _logScale    ? (hints ? " log"   : "") : "",       _logScale),
                ("o",  _freezeOrder ? (hints ? " frz"   : "") : "",       _freezeOrder),
                ("f",  _paused      ? (hints ? " pause" : "") : "",       _paused),
                ("j",  jVal,                                               _scrollOffset > 0),
                ("k",  hints ? " ↑" : "",                                 _scrollOffset > 0),
                ("±",  hints ? " rows" : "",                              false),
            };

            string dirtyKey = $"ctrl|{_sortMode}|{_sortWindow}|{_aggregateMode}|{_resolveMode}|{_displayMode}" +
                              $"|{_logScale}|{_showBars}|{_bitsMode}|{_freezeOrder}|{_paused}" +
                              $"|{_swapDirection}|{_scrollOffset}|{_filterText}|{W}";
            if (_rowBuf[row] == dirtyKey) return;
            _rowBuf[row] = dirtyKey;

            AnsiHelper.AppendMove(_frame, row);
            int col = 0;

            foreach (var (key, val, active) in chips)
            {
                if (col + 1 + key.Length > W) break;

                // Leading space
                _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
                _frame.Append(AnsiHelper.Fg(ConsoleColor.Gray));
                _frame.Append(' ');
                col++;

                // Key letter
                _frame.Append(AnsiHelper.Bg(active ? ConsoleColor.Yellow   : ConsoleColor.DarkGray));
                _frame.Append(AnsiHelper.Fg(active ? ConsoleColor.Black    : ConsoleColor.White));
                _frame.Append(key);
                col += key.Length;

                // Value / hint text
                if (val.Length > 0 && col + val.Length < W)
                {
                    _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
                    _frame.Append(AnsiHelper.Fg(active ? ConsoleColor.Yellow : ConsoleColor.DarkGray));
                    _frame.Append(val);
                    col += val.Length;
                }
            }

            // Pad to end of row
            if (col < W)
            {
                _frame.Append(AnsiHelper.Bg(ConsoleColor.Black));
                _frame.Append(AnsiHelper.Fg(ConsoleColor.Gray));
                _frame.Append(' ', W - col);
            }

            _frame.Append(AnsiHelper.Reset);
        }

        public void Dispose()
        {
            lock (_lockObj)
            {
                Console.Out.Write(AnsiHelper.Reset);
                Console.CursorVisible = true;
                _isDisposed = true;
            }
        }
    }
}
