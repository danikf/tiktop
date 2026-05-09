using System;
using System.Linq;
using System.Text;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{
    class Visualiser : IDisposable
    {
        const int headerHeight = 2;
        const int footerHeight = 4 + 1; // separator + TX + RX + TOTAL + last-line guard

        private readonly DnsCache _dnsCache;
        private readonly object _lockObj = new object();
        private volatile bool _isDisposed = false;
        private bool _firstDraw = true;

        // Dirty-row buffer: stores what is currently on screen.
        // Only rows whose content changed are rewritten each frame.
        private string[] _rowBuf = Array.Empty<string>();
        private int _bufW, _bufH;

        private string _statusMessage = "Connected";
        private ConsoleColor _statusColor = ConsoleColor.Green;
        private bool _showDns = true;
        private int? _countOverride;

        // Use _bufH once initialised; fall back to live value before first draw.
        private int WindowH => _bufH > 0 ? _bufH : Console.WindowHeight;
        private int NrOfItemsAuto => Math.Max(0, (WindowH - headerHeight - footerHeight) / 2);
        public int NrOfItems => _countOverride.HasValue
            ? Math.Min(_countOverride.Value, NrOfItemsAuto)
            : NrOfItemsAuto;
        public bool ShowDns => _showDns;

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
            Console.CursorVisible = false;
        }

        public void SetStatus(string message, ConsoleColor color = ConsoleColor.Red)
        {
            lock (_lockObj) { _statusMessage = message; _statusColor = color; }
        }

        public void ToggleDns()   { lock (_lockObj) _showDns = !_showDns; }

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

                // First draw or terminal resize: clear everything and rebuild buffer.
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

        // Row format: {local:<aw>} => {remote:<aw>} {bar:<bw>} {avg2s} {avg10s} {avg40s}
        // Total width: aw + 4 + aw + 1 + bw + 1 + (6+2+6+2+6) = 2*aw + bw + 28
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
            int barStart = 2 * aw + 5; // column where bar begins
            long peak = data.PeakTotal;
            double step = peak / 5.0;

            // Row 0: scale labels right-aligned at 20%/40%/60%/80%/100% of bar
            char[] labels = new string(' ', W).ToCharArray();
            for (int i = 1; i <= 5; i++)
            {
                string label = FormatHelper.FormatTraffic((long)(step * i)).TrimStart();
                int tickCol = barStart + (int)Math.Round(bw * i / 5.0) - 1;
                int s = tickCol - label.Length + 1;
                for (int j = 0; j < label.Length; j++)
                    if (s + j >= 0 && s + j < W) labels[s + j] = label[j];
            }
            PlainRow(startRow, new string(labels));

            // Row 1: └────┴────┴────┴────┴
            char[] sep = new string(' ', W).ToCharArray();
            if (barStart - 1 >= 0 && barStart - 1 < W) sep[barStart - 1] = '└';
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
                string local  = FormatHelper.ShortenHostname(
                    (_showDns ? _dnsCache.TryGet(ip.LastSection.SrcAddress) : null)
                    ?? ip.LastSection.SrcAddress, aw);
                string remote = FormatHelper.ShortenHostname(
                    (_showDns ? _dnsCache.TryGet(ip.LastSection.DstAddress) : null)
                    ?? ip.LastSection.DstAddress, aw);

                long txS = (long)ip.ShortRange .Average(s => (double)s.Tx);
                long txM = (long)ip.MediumRange.Average(s => (double)s.Tx);
                long txL = (long)ip.LongRange  .Average(s => (double)s.Tx);
                long rxS = (long)ip.ShortRange .Average(s => (double)s.Rx);
                long rxM = (long)ip.MediumRange.Average(s => (double)s.Rx);
                long rxL = (long)ip.LongRange  .Average(s => (double)s.Rx);

                // Prefix is exactly 2*aw+5 chars, bar is exactly bw chars.
                string txPfx = $"{local.PadRight(aw)} => {remote.PadRight(aw)} ";
                string rxPfx = $"{"".PadRight(aw)} <= {"".PadRight(aw)} ";
                string txSfx = $" {FormatHelper.FormatTraffic(txS)}  {FormatHelper.FormatTraffic(txM)}  {FormatHelper.FormatTraffic(txL)}";
                string rxSfx = $" {FormatHelper.FormatTraffic(rxS)}  {FormatHelper.FormatTraffic(rxM)}  {FormatHelper.FormatTraffic(rxL)}";

                ColoredRow(row++, txPfx, RenderBar(ip.LastSection.Tx, peak, bw), ConsoleColor.Green, txSfx);
                ColoredRow(row++, rxPfx, RenderBar(ip.LastSection.Rx, peak, bw), ConsoleColor.Cyan,  rxSfx);
            }

            // Clear leftover rows from previous renders
            int end = startRow + cnt * 2;
            while (row < end) PlainRow(row++, "");
        }

        // ── Footer ────────────────────────────────────────────────────────────

        private void DrawFooter(DataSnapshot data, int aw, int bw, int startRow)
        {
            int W = _bufW;
            int row = startRow;

            // Separator line with status badge on the right (colored)
            string badge;
            ConsoleColor badgeColor;
            lock (_lockObj) { badge = $"[ {_statusMessage} ]"; badgeColor = _statusColor; }
            badge = badge.SafePrefix(W - 4);
            string sepDashes = new string('─', Math.Max(0, W - badge.Length));
            string sepKey    = sepDashes + badge;

            if (row < _bufH - 1 && _rowBuf[row] != sepKey)
            {
                _rowBuf[row] = sepKey;
                Console.SetCursorPosition(0, row);
                Console.Write(sepDashes.SafePrefix(W));
                int col = sepDashes.Length;
                if (col < W)
                {
                    Console.ForegroundColor = badgeColor;
                    Console.Write(badge.SafePrefix(W - col));
                    Console.ForegroundColor = ConsoleColor.Gray;
                }
            }
            row++;

            // TX / RX / TOTAL
            int ratesCol = 2 * aw + 5 + bw + 1;
            string[] lbls   = { "TX:", "RX:", "TOTAL:" };
            long[]   actuals = { data.ActualTx, data.ActualRx, data.ActualTx + data.ActualRx };
            long[]   peaks   = { data.PeakTx,   data.PeakRx,   data.PeakTotal };
            double[][] avgs  = { data.TxAvgs,   data.RxAvgs,   data.TotalAvgs };

            for (int i = 0; i < 3; i++)
            {
                string left  = $"{lbls[i]}  cur:{FormatHelper.FormatTraffic(actuals[i])}   peak:{FormatHelper.FormatTraffic(peaks[i])}";
                string rates = $"{FormatHelper.FormatTraffic((long)avgs[i][0])}  {FormatHelper.FormatTraffic((long)avgs[i][1])}  {FormatHelper.FormatTraffic((long)avgs[i][2])}";
                PlainRow(row++, $"{left.PadRight(ratesCol)}{rates}");
            }
        }

        // ── Bar renderer ──────────────────────────────────────────────────────

        // Full: █   Partial: ▉▊▋▌▍▎▏   Empty: ░
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

        // Write a plain-text row; skip if content unchanged (dirty check).
        private void PlainRow(int row, string content)
        {
            if (row >= _bufH - 1) return;
            string padded = content.SafePrefix(_bufW).PadRight(_bufW);
            if (_rowBuf[row] == padded) return;
            _rowBuf[row] = padded;
            Console.SetCursorPosition(0, row);
            Console.Write(padded);
        }

        // Write a row with a colored middle segment; skip if combined text unchanged.
        private void ColoredRow(int row, string prefix, string bar, ConsoleColor barColor, string suffix)
        {
            if (row >= _bufH - 1) return;
            int W = _bufW;

            // Combined text is the dirty-check key.
            string full = (prefix + bar + suffix).SafePrefix(W).PadRight(W);
            if (_rowBuf[row] == full) return;
            _rowBuf[row] = full;

            Console.SetCursorPosition(0, row);

            // prefix (plain)
            string p = prefix.SafePrefix(W);
            Console.Write(p);
            int col = p.Length;

            // bar (colored)
            if (col < W)
            {
                string b = bar.SafePrefix(W - col);
                Console.ForegroundColor = barColor;
                Console.Write(b);
                Console.ForegroundColor = ConsoleColor.Gray;
                col += b.Length;
            }

            // suffix + fill to end of line
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
