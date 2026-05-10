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
        const int footerHeight = 4 + 1; // separator + TX + RX + TOTAL + last-line guard

        private readonly DnsCache _dnsCache;
        private readonly object _lockObj = new object();
        private volatile bool _isDisposed = false;
        private bool _firstDraw = true;

        // Dirty-row buffer
        private string[] _rowBuf = Array.Empty<string>();
        private int _bufW, _bufH;

        private string _statusMessage = "Connected";
        private ConsoleColor _statusColor = ConsoleColor.Green;
        private ResolveMode _resolveMode = ResolveMode.DnsService;
        private int? _countOverride;
        private DisplayMode _displayMode = DisplayMode.Both;
        private bool _logScale    = false;
        private bool _showBars    = true;
        private bool _paused      = false;
        private bool _bitsMode    = false;
        private bool _freezeOrder = false;
        private int  _scrollOffset = 0;
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
        public bool FreezeOrder => _freezeOrder;
        public int  ScrollOffset => _scrollOffset;

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
            Console.CursorVisible = false;
        }

        public void SetStatus(string message, ConsoleColor color = ConsoleColor.Red)
        {
            lock (_lockObj) { _statusMessage = message; _statusColor = color; }
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

        public void ToggleFreezeOrder()
        {
            lock (_lockObj)
            {
                _freezeOrder = !_freezeOrder;
                if (!_freezeOrder) _frozenOrder = null; // clear on unfreeze
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

                // First draw or resize: clear and rebuild buffer.
                if (_firstDraw || W != _bufW || H != _bufH)
                {
                    Console.Clear();
                    _rowBuf = new string[H];
                    _bufW = W;
                    _bufH = H;
                    _firstDraw = false;
                }

                // Paused: hold the last snapshot so data rows don't change;
                // the status badge still re-renders each tick (dirty key changes).
                DataSnapshot snap = _paused ? _lastSnapshot : data;
                if (!_paused) _lastSnapshot = data;

                // Apply freeze-order: capture order on first tick, then reorder by it.
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

                // Clamp scroll offset to valid range.
                _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, displayItems.Length - 1));

                var (aw, bw) = ComputeLayout();
                var savedFg = Console.ForegroundColor;
                try
                {
                    int row = DrawHeader(snap, aw, bw, 0);
                    DrawItems(displayItems, snap.PeakTotal, NrOfItems, aw, bw, row);
                    DrawFooter(snap, aw, bw, _bufH - footerHeight);
                }
                finally
                {
                    Console.ForegroundColor = savedFg;
                }
            }
        }

        private static string GetRowKey(DataSnapshotIpRow r) =>
            $"{r.LastSection.SrcAddress}:{r.LastSection.SrcPort}-{r.LastSection.DstAddress}:{r.LastSection.DstPort}";

        // ── Layout ────────────────────────────────────────────────────────────

        // Row: {local:<aw>} => {remote+port:<aw>} {bar:<bw>} {avg2s} {avg10s} {avg40s}
        // Width: aw + 4 + aw + 1 + bw + 1 + (6+2+6+2+6) = 2*aw + bw + 28
        private (int aw, int bw) ComputeLayout()
        {
            int W = _bufW;
            // Address columns: 1/4 of space each, capped at 35 — bars take the rest.
            // Formula guarantees 2*aw + bw + 28 == W (exact, no drift).
            int aw = Math.Max(10, Math.Min(35, (W - 28) / 4));
            int bw = Math.Max(10, W - 2 * aw - 28);
            return (aw, bw);
        }

        // ── Header ────────────────────────────────────────────────────────────

        private int DrawHeader(DataSnapshot data, int aw, int bw, int startRow)
        {
            int W = _bufW;
            int barStart = 2 * aw + 5;
            long peak = data.PeakTotal;
            double step = peak / 5.0;

            // Row 0: scale labels + clock (right-aligned)
            char[] labelLine = new string(' ', W).ToCharArray();
            for (int i = 1; i <= 5; i++)
            {
                string label = FormatHelper.FormatTraffic((long)(step * i), _bitsMode).TrimStart();
                int tickCol = barStart + (int)Math.Round(bw * i / 5.0) - 1;
                int s = tickCol - label.Length + 1;
                for (int j = 0; j < label.Length; j++)
                    if (s + j >= 0 && s + j < W) labelLine[s + j] = label[j];
            }
            // Clock at the far right
            string clock = DateTime.Now.ToString("HH:mm:ss");
            for (int j = 0; j < clock.Length; j++)
            {
                int c = W - clock.Length + j;
                if (c >= 0 && c < W) labelLine[c] = clock[j];
            }
            PlainRow(startRow, new string(labelLine));

            // Row 1: └── (full width) ──┴────┴────┴────┴────┴── (tick marks over bar)
            char[] sep = new string('─', W).ToCharArray();
            sep[0] = '└';
            for (int i = 1; i <= 5; i++)
            {
                int tickCol = barStart + (int)Math.Round(bw * i / 5.0) - 1;
                if (tickCol >= 0 && tickCol < W) sep[tickCol] = '┴';
            }
            PlainRow(startRow + 1, new string(sep));

            return startRow + 2;
        }

        // ── Items ─────────────────────────────────────────────────────────────

        private void DrawItems(DataSnapshotIpRow[] items, long peak, int cnt, int aw, int bw, int startRow)
        {
            int row = startRow;

            foreach (var ip in items.Skip(_scrollOffset).Take(cnt))
            {
                bool useDns     = _resolveMode == ResolveMode.DnsService;
                bool useSvcName = _resolveMode != ResolveMode.IpPort;
                bool isAgg      = ip.LastSection.SrcPort == "*";

                string local;
                string remote;
                if (isAgg)
                {
                    // Aggregated row: one side has "*" as a wildcard address.
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

                    // Remote: address + destination port (service name or raw number)
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

                // Prefix is exactly 2*aw+5 chars so columns always align.
                string txPfx = $"{local.PadRight(aw)} => {remote.PadRight(aw)} ";
                string rxPfx = $"{"".PadRight(aw)} <= {"".PadRight(aw)} ";
                string txSfx = Sfx(txS, txM, txL);
                string rxSfx = Sfx(rxS, rxM, rxL);

                if (_displayMode != DisplayMode.RxOnly)
                {
                    if (_showBars)
                        ColoredRow(row++, txPfx, RenderBar(ip.LastSection.Tx, peak, bw, _logScale), ConsoleColor.Green, txSfx);
                    else
                        TintedRow(row++, txPfx + new string(' ', bw) + txSfx, ConsoleColor.Green);
                }
                if (_displayMode != DisplayMode.TxOnly)
                {
                    if (_showBars)
                        ColoredRow(row++, rxPfx, RenderBar(ip.LastSection.Rx, peak, bw, _logScale), ConsoleColor.Cyan, rxSfx);
                    else
                        TintedRow(row++, rxPfx + new string(' ', bw) + rxSfx, ConsoleColor.Cyan);
                }
            }

            // Clear leftover rows from previous renders
            int end = startRow + cnt * RowsPerItem;
            while (row < end) PlainRow(row++, "");
        }

        private string Sfx(long a, long b, long c) =>
            $" {FormatHelper.FormatTraffic(a, _bitsMode)}  {FormatHelper.FormatTraffic(b, _bitsMode)}  {FormatHelper.FormatTraffic(c, _bitsMode)}";

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter(DataSnapshot data, int aw, int bw, int startRow)
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

            int ratesCol = 2 * aw + 5 + bw + 1;

            // TX (green) / RX (cyan) / TOTAL (default)
            ConsoleColor[] colors = { ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Gray };
            string[]   lbls    = { "TX:", "RX:", "TOTAL:" };
            long[]     actuals = { data.ActualTx, data.ActualRx, data.ActualTx + data.ActualRx };
            long[]     peaks   = { data.PeakTx,   data.PeakRx,   data.PeakTotal };
            double[][] avgs    = { data.TxAvgs,   data.RxAvgs,   data.TotalAvgs };

            const int miniBarW = 16;
            for (int i = 0; i < 3; i++)
            {
                string left  = $"{lbls[i]}  cur:{FormatHelper.FormatTraffic(actuals[i], _bitsMode)}   peak:{FormatHelper.FormatTraffic(peaks[i], _bitsMode)}";
                string rates = $"{FormatHelper.FormatTraffic((long)avgs[i][0], _bitsMode)}  {FormatHelper.FormatTraffic((long)avgs[i][1], _bitsMode)}  {FormatHelper.FormatTraffic((long)avgs[i][2], _bitsMode)}";
                if (_showBars)
                {
                    string prefix  = (left + "  ").PadRight(ratesCol - miniBarW);
                    string miniBar = RenderBar(actuals[i], peaks[i], miniBarW, _logScale);
                    ColoredRow(row++, prefix, miniBar, colors[i], rates);
                }
                else
                {
                    TintedRow(row++, $"{left.PadRight(ratesCol)}{rates}", colors[i]);
                }
            }
        }

        // ── Bar renderer ──────────────────────────────────────────────────────

        private static string RenderBar(long value, long peak, int width, bool logScale = false)
        {
            if (width <= 0) return "";
            if (peak <= 0)  return new string('░', width);

            double ratio = logScale && value > 0
                ? Math.Log(value + 1.0) / Math.Log(peak + 1.0)
                : (double)value / peak;
            ratio = Math.Min(1.0, ratio);
            double filled    = ratio * width * 8;
            int    fullBlocks = (int)(filled / 8);
            int    partial    = (int)(filled % 8);
            char[] partials   = { '\0', '▏', '▎', '▍', '▌', '▋', '▊', '▉' };

            var sb = new StringBuilder(width);
            sb.Append('█', fullBlocks);
            if (partial > 0 && fullBlocks < width) sb.Append(partials[partial]);
            int used = fullBlocks + (partial > 0 ? 1 : 0);
            sb.Append('░', Math.Max(0, width - used));
            return sb.ToString();
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

        // Row with a single uniform foreground color (dirty-checked with color prefix).
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

        // Row with colored bar: filled part in barColor, empty ░ in DarkGray.
        private void ColoredRow(int row, string prefix, string bar, ConsoleColor barColor, string suffix)
        {
            if (row >= _bufH - 1) return;
            int W = _bufW;

            string full = (prefix + bar + suffix).SafePrefix(W).PadRight(W);
            if (_rowBuf[row] == full) return;
            _rowBuf[row] = full;
            Console.SetCursorPosition(0, row);

            // prefix
            string p = prefix.SafePrefix(W);
            Console.Write(p);
            int col = p.Length;

            // bar: filled chars in barColor, empty ░ in DarkGray
            if (col < W)
            {
                string b = bar.SafePrefix(W - col);
                int emptyStart = b.IndexOf('░');
                string filledPart = emptyStart >= 0 ? b[..emptyStart] : b;
                string emptyPart  = emptyStart >= 0 ? b[emptyStart..] : "";

                if (filledPart.Length > 0)
                {
                    Console.ForegroundColor = barColor;
                    Console.Write(filledPart);
                    col += filledPart.Length;
                }
                if (emptyPart.Length > 0 && col < W)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(emptyPart.SafePrefix(W - col));
                    col += emptyPart.Length;
                }
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            // suffix
            if (col < W)
                Console.Write(suffix.SafePrefix(W - col).PadRight(W - col));
        }

        public void Dispose()
        {
            lock (_lockObj)
            {
                Console.CursorVisible = true;
                _isDisposed = true;
            }
        }
    }
}
