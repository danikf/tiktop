using System;
using System.Linq;
using System.Text;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{
    public enum DisplayMode { Both, TxOnly, RxOnly }

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
        private bool _showDns = true;
        private int? _countOverride;
        private DisplayMode _displayMode = DisplayMode.Both;

        private int WindowH => _bufH > 0 ? _bufH : Console.WindowHeight;
        private int RowsPerItem => _displayMode == DisplayMode.Both ? 2 : 1;
        private int NrOfItemsAuto => Math.Max(0, (WindowH - headerHeight - footerHeight) / RowsPerItem);
        public int NrOfItems => _countOverride.HasValue
            ? Math.Min(_countOverride.Value, NrOfItemsAuto)
            : NrOfItemsAuto;
        public bool ShowDns => _showDns;
        public DisplayMode DisplayMode => _displayMode;

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
            Console.CursorVisible = false;
        }

        public void SetStatus(string message, ConsoleColor color = ConsoleColor.Red)
        {
            lock (_lockObj) { _statusMessage = message; _statusColor = color; }
        }

        public void ToggleDns() { lock (_lockObj) _showDns = !_showDns; }

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

                var (aw, bw) = ComputeLayout();
                var savedFg = Console.ForegroundColor;
                try
                {
                    int row = DrawHeader(data, aw, bw, 0);
                    DrawItems(data, NrOfItems, aw, bw, row);
                    DrawFooter(data, aw, bw, _bufH - footerHeight);
                }
                finally
                {
                    Console.ForegroundColor = savedFg;
                }
            }
        }

        // ── Layout ────────────────────────────────────────────────────────────

        // Row: {local:<aw>} => {remote+port:<aw>} {bar:<bw>} {avg2s} {avg10s} {avg40s}
        // Width: aw + 4 + aw + 1 + bw + 1 + (6+2+6+2+6) = 2*aw + bw + 28
        private (int aw, int bw) ComputeLayout()
        {
            int W = _bufW;
            int bw = Math.Max(10, Math.Min(40, (W - 28) / 3));
            int aw = Math.Max(10, (W - bw - 28) / 2);
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
                string label = FormatHelper.FormatTraffic((long)(step * i)).TrimStart();
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

            // Row 1: └────┴────┴────┴────┴
            char[] sep = new string(' ', W).ToCharArray();
            int anchor = barStart - 1;
            if (anchor >= 0 && anchor < W) sep[anchor] = '└';
            for (int i = barStart; i < barStart + bw && i < W; i++) sep[i] = '─';
            for (int i = 1; i <= 5; i++)
            {
                int tickCol = barStart + (int)Math.Round(bw * i / 5.0) - 1;
                if (tickCol >= 0 && tickCol < W) sep[tickCol] = '┴';
            }
            PlainRow(startRow + 1, new string(sep));

            return startRow + 2;
        }

        // ── Items ─────────────────────────────────────────────────────────────

        private void DrawItems(DataSnapshot data, int cnt, int aw, int bw, int startRow)
        {
            long peak = data.PeakTotal;
            int row = startRow;

            foreach (var ip in data.TopIpTraffic.Take(cnt))
            {
                string local = FormatHelper.ShortenHostname(
                    (_showDns ? _dnsCache.TryGet(ip.LastSection.SrcAddress) : null)
                    ?? ip.LastSection.SrcAddress, aw);

                // Remote: hostname + destination port
                string hostname = (_showDns ? _dnsCache.TryGet(ip.LastSection.DstAddress) : null)
                                  ?? ip.LastSection.DstAddress;
                string portSuffix = FormatHelper.FormatPort(ip.LastSection.DstPort);
                string remoteHost = FormatHelper.ShortenHostname(hostname, Math.Max(1, aw - portSuffix.Length));
                string remote     = (remoteHost + portSuffix).SafePrefix(aw);

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
                    ColoredRow(row++, txPfx, RenderBar(ip.LastSection.Tx, peak, bw), ConsoleColor.Green, txSfx);
                if (_displayMode != DisplayMode.TxOnly)
                    ColoredRow(row++, rxPfx, RenderBar(ip.LastSection.Rx, peak, bw), ConsoleColor.Cyan,  rxSfx);
            }

            // Clear leftover rows from previous renders
            int end = startRow + cnt * RowsPerItem;
            while (row < end) PlainRow(row++, "");
        }

        private static string Sfx(long a, long b, long c) =>
            $" {FormatHelper.FormatTraffic(a)}  {FormatHelper.FormatTraffic(b)}  {FormatHelper.FormatTraffic(c)}";

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

            for (int i = 0; i < 3; i++)
            {
                string left  = $"{lbls[i]}  cur:{FormatHelper.FormatTraffic(actuals[i])}   peak:{FormatHelper.FormatTraffic(peaks[i])}";
                string rates = $"{FormatHelper.FormatTraffic((long)avgs[i][0])}  {FormatHelper.FormatTraffic((long)avgs[i][1])}  {FormatHelper.FormatTraffic((long)avgs[i][2])}";
                TintedRow(row++, $"{left.PadRight(ratesCol)}{rates}", colors[i]);
            }
        }

        // ── Bar renderer ──────────────────────────────────────────────────────

        private static (int filled, int empty) BarSplit(long value, long peak, int width)
        {
            if (peak <= 0 || width <= 0) return (0, width);
            double ratio  = Math.Min(1.0, (double)value / peak);
            double filled = ratio * width * 8;
            int fullBlocks = (int)(filled / 8);
            int partial    = (int)(filled % 8);
            int usedChars  = fullBlocks + (partial > 0 ? 1 : 0);
            return (usedChars, Math.Max(0, width - usedChars));
        }

        private static string RenderBar(long value, long peak, int width)
        {
            if (width <= 0) return "";
            if (peak <= 0)  return new string('░', width);

            double ratio     = Math.Min(1.0, (double)value / peak);
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
