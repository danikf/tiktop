using System;
using System.Linq;
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
        const int headerHeight = 2;
        const int footerHeight = 5 + 1; // separator + controls + TX + RX + TOTAL + last-line guard

        private readonly DnsCache _dnsCache;
        private readonly object _lockObj = new object();
        private volatile bool _isDisposed = false;
        private bool _firstDraw = true;

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

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
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
                var savedFg = Console.ForegroundColor;
                var savedBg = Console.BackgroundColor;
                try
                {
                    int row = DrawHeader(snap, aw, 0);
                    DrawItems(displayItems, snap.PeakTotal, NrOfItems, aw, row);
                    DrawFooter(snap, aw, _bufH - footerHeight);
                }
                finally
                {
                    Console.ForegroundColor = savedFg;
                    Console.BackgroundColor = savedBg;
                }
            }
        }

        private static string GetRowKey(DataSnapshotIpRow r) =>
            $"{r.LastSection.SrcAddress}:{r.LastSection.SrcPort}-{r.LastSection.DstAddress}:{r.LastSection.DstPort}";

        // ── Layout ────────────────────────────────────────────────────────────

        // Row: {local:<aw>} => {remote+port:<aw>} <padding> {avg2s} {avg10s} {avg40s} {cumul}
        // Fixed content: 2*aw + 5 + 31 = 2*aw + 36; remaining W - 2*aw - 36 chars are padding
        // (background color covers full width proportionally, including padding and sfx)
        private int ComputeLayout()
        {
            return Math.Max(10, Math.Min(35, (_bufW - 36) / 2));
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

            return startRow + 2;
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

                string txPfx = $"{local.PadRight(aw)} => {remote.PadRight(aw)} ";
                string rxPfx = $"{"".PadRight(aw)} <= {"".PadRight(aw)} ";
                string txSfx = Sfx(txS, txM, txL, ip.CumulativeTx);
                string rxSfx = Sfx(rxS, rxM, rxL, ip.CumulativeRx);

                // sfx is right-aligned; padding between prefix and sfx gets covered by background
                string txText = txPfx.PadRight(_bufW - txSfx.Length) + txSfx;
                string rxText = rxPfx.PadRight(_bufW - rxSfx.Length) + rxSfx;

                if (_displayMode != DisplayMode.RxOnly)
                {
                    if (_showBars)
                        BgRow(row++, txText, BarLen(ip.LastSection.Tx, peak), ConsoleColor.Green);
                    else
                        TintedRow(row++, txText, ConsoleColor.Green);
                }
                if (_displayMode != DisplayMode.TxOnly)
                {
                    if (_showBars)
                        BgRow(row++, rxText, BarLen(ip.LastSection.Rx, peak), ConsoleColor.Cyan);
                    else
                        TintedRow(row++, rxText, ConsoleColor.Cyan);
                }
            }

            // Clear leftover rows from previous renders
            int end = startRow + cnt * RowsPerItem;
            while (row < end) PlainRow(row++, "");
        }

        private string Sfx(long a, long b, long c, long cum) =>
            $" {FormatHelper.FormatTraffic(a, _bitsMode)}  {FormatHelper.FormatTraffic(b, _bitsMode)}  {FormatHelper.FormatTraffic(c, _bitsMode)}  {FormatHelper.FormatTraffic(cum, _bitsMode)}";

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
            if (row < _bufH - 1 && _rowBuf[row] != sepKey)
            {
                _rowBuf[row] = sepKey;
                Console.SetCursorPosition(0, row);
                Console.Write(dashes.SafePrefix(W));
                int col = dashes.Length;
                if (col < W)
                {
                    Console.ForegroundColor = badgeColor;
                    Console.Write(badge.SafePrefix(W - col));
                    Console.ForegroundColor = ConsoleColor.Gray;
                }
            }
            row++;

            DrawControls(row++);

            ConsoleColor[] colors    = { ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Gray };
            string[]   lbls          = { "TX:", "RX:", "TOTAL:" };
            long[]     actuals       = { data.ActualTx, data.ActualRx, data.ActualTx + data.ActualRx };
            long[]     peaks         = { data.PeakTx,   data.PeakRx,   data.PeakTotal };
            double[][] avgs          = { data.TxAvgs,   data.RxAvgs,   data.TotalAvgs };
            long[]     cumulatives   = { data.CumulativeTx, data.CumulativeRx, data.CumulativeTotal };

            for (int i = 0; i < 3; i++)
            {
                string left  = $"{lbls[i]}  cur:{FormatHelper.FormatTraffic(actuals[i], _bitsMode)}   peak:{FormatHelper.FormatTraffic(peaks[i], _bitsMode)}";
                string rates = Sfx((long)avgs[i][0], (long)avgs[i][1], (long)avgs[i][2], cumulatives[i]);
                string text  = left.PadRight(W - rates.Length) + rates;

                if (_showBars)
                    BgRow(row++, text, BarLen(actuals[i], peaks[i]), colors[i]);
                else
                    TintedRow(row++, text, colors[i]);
            }
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

        private void PlainRow(int row, string content)
        {
            if (row >= _bufH - 1) return;
            string padded = content.SafePrefix(_bufW).PadRight(_bufW);
            if (_rowBuf[row] == padded) return;
            _rowBuf[row] = padded;
            Console.SetCursorPosition(0, row);
            Console.Write(padded);
        }

        // Row with a single uniform foreground color (no background).
        private void TintedRow(int row, string content, ConsoleColor fg)
        {
            if (row >= _bufH - 1) return;
            string padded = content.SafePrefix(_bufW).PadRight(_bufW);
            string key    = $"{(int)fg}|{padded}";
            if (_rowBuf[row] == key) return;
            _rowBuf[row] = key;
            Console.SetCursorPosition(0, row);
            Console.ForegroundColor = fg;
            Console.Write(padded);
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        // Row with background color covering the first barLen columns (iftop style).
        // Filled part: bg=barColor, fg=Black. Empty part: bg=Black, fg=barColor.
        private void BgRow(int row, string text, int barLen, ConsoleColor barColor)
        {
            if (row >= _bufH - 1) return;
            int W = _bufW;
            string padded = text.SafePrefix(W).PadRight(W);
            string key    = $"bg{barLen}|{(int)barColor}|{padded}";
            if (_rowBuf[row] == key) return;
            _rowBuf[row] = key;
            Console.SetCursorPosition(0, row);

            int fill = Math.Clamp(barLen, 0, W);
            if (fill > 0)
            {
                Console.BackgroundColor = barColor;
                Console.ForegroundColor = ConsoleColor.Black;
                Console.Write(padded[..fill]);
            }
            if (fill < W)
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = barColor;
                Console.Write(padded[fill..]);
            }
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        // ── Controls row ──────────────────────────────────────────────────────

        private void DrawControls(int row)
        {
            if (row >= _bufH - 1) return;

            int W        = _bufW;
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
                ("p",  ":" + sortVal,                                      _sortMode != SortMode.Total),
                ("1",  hints ? ":2s"    : "",                             _sortWindow == SortWindow.Short),
                ("2",  hints ? ":10s"   : "",                             _sortWindow == SortWindow.Medium),
                ("3",  hints ? ":40s"   : "",                             _sortWindow == SortWindow.Long),
                ("r",  hints ? " reset" : "",                              false),
                ("a",  aggVal,                                             _aggregateMode != AggregateMode.None),
                ("/",  filtVal,                                            !string.IsNullOrEmpty(_filterText)),
                ("d",  dnsVal,                                             _resolveMode != ResolveMode.DnsService),
                ("t",  dispVal,                                            _displayMode != DisplayMode.Both),
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
                              $"|{_scrollOffset}|{_filterText}|{W}";
            if (_rowBuf[row] == dirtyKey) return;
            _rowBuf[row] = dirtyKey;

            Console.SetCursorPosition(0, row);
            int col = 0;

            foreach (var (key, val, active) in chips)
            {
                if (col + 1 + key.Length > W) break;

                // Leading space
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(' ');
                col++;

                // Key letter
                Console.BackgroundColor = active ? ConsoleColor.Yellow   : ConsoleColor.DarkGray;
                Console.ForegroundColor = active ? ConsoleColor.Black    : ConsoleColor.White;
                Console.Write(key);
                col += key.Length;

                // Value / hint text
                if (val.Length > 0 && col + val.Length < W)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = active ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
                    Console.Write(val);
                    col += val.Length;
                }
            }

            // Pad to end of row
            if (col < W)
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(new string(' ', W - col));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        public void Dispose()
        {
            lock (_lockObj)
            {
                Console.BackgroundColor = ConsoleColor.Black;
                Console.CursorVisible = true;
                _isDisposed = true;
            }
        }
    }
}
