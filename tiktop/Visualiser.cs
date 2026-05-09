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

        private string _statusMessage = "Connected";
        private ConsoleColor _statusColor = ConsoleColor.Green;
        private bool _showDns = true;
        private int? _countOverride;

        private int NrOfItemsAuto => Math.Max(0, (Console.WindowHeight - headerHeight - footerHeight) / 2);
        public int NrOfItems => _countOverride.HasValue
            ? Math.Min(_countOverride.Value, NrOfItemsAuto)
            : NrOfItemsAuto;
        public bool ShowDns => _showDns;

        public void SetStatus(string message, ConsoleColor color = ConsoleColor.Red)
        {
            lock (_lockObj)
            {
                _statusMessage = message;
                _statusColor = color;
            }
        }

        public void ToggleDns()
        {
            lock (_lockObj)
                _showDns = !_showDns;
        }

        public void AdjustRowCount(int delta)
        {
            lock (_lockObj)
            {
                int current = _countOverride ?? NrOfItemsAuto;
                _countOverride = Math.Max(1, current + delta);
            }
        }

        public Visualiser(DnsCache dnsCache)
        {
            _dnsCache = dnsCache;
            Console.CursorVisible = false;
        }

        public void Draw(DataSnapshot data)
        {
            lock (_lockObj)
            {
                if (_isDisposed) return;

                var (addrWidth, barWidth) = ComputeLayout();
                var savedColor = Console.ForegroundColor;
                try
                {
                    Console.SetCursorPosition(0, 0);
                    DrawHeader(data, addrWidth, barWidth);

                    Console.SetCursorPosition(0, headerHeight);
                    int itemLines = Console.WindowHeight - headerHeight - footerHeight;
                    DrawItems(data, itemLines / 2, addrWidth, barWidth);

                    Console.SetCursorPosition(0, Console.WindowHeight - footerHeight);
                    DrawFooter(data, addrWidth, barWidth);
                }
                finally
                {
                    Console.ForegroundColor = savedColor;
                }
            }
        }

        // Layout: {local:<aw>} => {remote:<aw>} {bar:<bw>} {avg2s} {avg10s} {avg40s}
        // Width:   aw + 4 + aw + 1 + bw + 1 + 6+2+6+2+6 = 2*aw + bw + 28
        private (int addrWidth, int barWidth) ComputeLayout()
        {
            int W = Console.WindowWidth;
            int barWidth = Math.Max(10, Math.Min(40, (W - 28) / 3));
            int addrWidth = Math.Max(10, (W - barWidth - 28) / 2);
            return (addrWidth, barWidth);
        }

        private void DrawHeader(DataSnapshot data, int addrWidth, int barWidth)
        {
            int W = Console.WindowWidth;
            int barStart = 2 * addrWidth + 5; // column where bar begins
            long peak = data.PeakTotal;
            double step = peak / 5.0;

            // Line 1: scale labels right-aligned at 20% / 40% / 60% / 80% / 100% of bar
            char[] labelLine = new string(' ', W).ToCharArray();
            for (int i = 1; i <= 5; i++)
            {
                string label = FormatHelper.FormatTraffic((long)(step * i)).TrimStart();
                int tickCol = barStart + (int)Math.Round(barWidth * i / 5.0) - 1;
                int start = tickCol - label.Length + 1;
                for (int j = 0; j < label.Length; j++)
                {
                    int c = start + j;
                    if (c >= 0 && c < W)
                        labelLine[c] = label[j];
                }
            }
            WriteRow(new string(labelLine));

            // Line 2: └────┴────┴────┴────┴
            char[] sep = new string(' ', W).ToCharArray();
            int anchorCol = barStart - 1;
            if (anchorCol >= 0 && anchorCol < W) sep[anchorCol] = '└';
            for (int i = barStart; i < barStart + barWidth && i < W; i++)
                sep[i] = '─';
            for (int i = 1; i <= 5; i++)
            {
                int tickCol = barStart + (int)Math.Round(barWidth * i / 5.0) - 1;
                if (tickCol >= 0 && tickCol < W)
                    sep[tickCol] = '┴';
            }
            WriteRow(new string(sep));
        }

        private void DrawItems(DataSnapshot data, int cnt, int addrWidth, int barWidth)
        {
            long peak = data.PeakTotal;
            int drawn = 0;

            foreach (var ip in data.TopIpTraffic.Take(cnt))
            {
                string local  = FormatHelper.ShortenHostname(
                    (_showDns ? _dnsCache.TryGet(ip.LastSection.SrcAddress) : null) ?? ip.LastSection.SrcAddress, addrWidth);
                string remote = FormatHelper.ShortenHostname(
                    (_showDns ? _dnsCache.TryGet(ip.LastSection.DstAddress) : null) ?? ip.LastSection.DstAddress, addrWidth);

                long txShort  = (long)ip.ShortRange.Average(s => (double)s.Tx);
                long txMedium = (long)ip.MediumRange.Average(s => (double)s.Tx);
                long txLong   = (long)ip.LongRange.Average(s => (double)s.Tx);
                long rxShort  = (long)ip.ShortRange.Average(s => (double)s.Rx);
                long rxMedium = (long)ip.MediumRange.Average(s => (double)s.Rx);
                long rxLong   = (long)ip.LongRange.Average(s => (double)s.Rx);

                string txPrefix = $"{local.PadRight(addrWidth)} => {remote.PadRight(addrWidth)} ";
                string rxPrefix = $"{"".PadRight(addrWidth)} <= {"".PadRight(addrWidth)} ";
                string txSuffix = $" {FormatHelper.FormatTraffic(txShort)}  {FormatHelper.FormatTraffic(txMedium)}  {FormatHelper.FormatTraffic(txLong)}";
                string rxSuffix = $" {FormatHelper.FormatTraffic(rxShort)}  {FormatHelper.FormatTraffic(rxMedium)}  {FormatHelper.FormatTraffic(rxLong)}";

                DrawItemRow(txPrefix, RenderBar(ip.LastSection.Tx, peak, barWidth), ConsoleColor.Green, txSuffix);
                DrawItemRow(rxPrefix, RenderBar(ip.LastSection.Rx, peak, barWidth), ConsoleColor.Cyan,  rxSuffix);
                drawn++;
            }

            // Clear leftover rows from previous renders
            for (int i = drawn; i < cnt; i++)
            {
                WriteRow("");
                WriteRow("");
            }
        }

        // Renders a proportional bar using Unicode block characters for sub-char precision.
        // Full: █   Partial: ▉▊▋▌▍▎▏   Empty: ░
        private static string RenderBar(long value, long peak, int width)
        {
            if (width <= 0) return "";
            if (peak <= 0) return new string('░', width);

            double ratio = Math.Min(1.0, (double)value / peak);
            double filled = ratio * width * 8; // in eighths of a character
            int fullBlocks = (int)(filled / 8);
            int partial = (int)(filled % 8);
            char[] partialChars = { '\0', '▏', '▎', '▍', '▌', '▋', '▊', '▉' };

            var sb = new StringBuilder(width);
            sb.Append('█', fullBlocks);
            if (partial > 0 && fullBlocks < width)
                sb.Append(partialChars[partial]);
            int used = fullBlocks + (partial > 0 ? 1 : 0);
            sb.Append('░', Math.Max(0, width - used));
            return sb.ToString();
        }

        private void DrawItemRow(string prefix, string bar, ConsoleColor barColor, string suffix)
        {
            if (Console.CursorTop >= Console.WindowHeight - 1) return;
            int W = Console.WindowWidth;

            var p = prefix.SafePrefix(W);
            Console.Write(p);
            int col = p.Length;

            if (col < W)
            {
                var b = bar.SafePrefix(W - col);
                Console.ForegroundColor = barColor;
                Console.Write(b);
                Console.ForegroundColor = ConsoleColor.Gray;
                col += b.Length;
            }

            if (col < W)
                Console.Write(suffix.SafePrefix(W - col).PadRight(W - col));
        }

        private void DrawFooter(DataSnapshot data, int addrWidth, int barWidth)
        {
            int W = Console.WindowWidth;

            // Separator with status badge on the right
            string badge;
            ConsoleColor badgeColor;
            lock (_lockObj)
            {
                badge = $"[ {_statusMessage} ]";
                badgeColor = _statusColor;
            }
            badge = badge.SafePrefix(W - 4);
            string sepLeft = new string('─', Math.Max(0, W - badge.Length));
            if (Console.CursorTop < Console.WindowHeight - 1)
            {
                Console.Write(sepLeft.SafePrefix(Console.WindowWidth));
                int col = sepLeft.Length;
                if (col < W)
                {
                    Console.ForegroundColor = badgeColor;
                    Console.Write(badge.SafePrefix(W - col));
                    Console.ForegroundColor = ConsoleColor.Gray;
                }
            }

            // "rates:" label starts where the bar starts, to align with item numbers
            int ratesCol = 2 * addrWidth + 5 + barWidth + 1;

            string[] labels  = { "TX:", "RX:", "TOTAL:" };
            long[]   actuals = { data.ActualTx, data.ActualRx, data.ActualTx + data.ActualRx };
            long[]   peaks   = { data.PeakTx,   data.PeakRx,   data.PeakTotal };
            double[][] avgs  = { data.TxAvgs,   data.RxAvgs,   data.TotalAvgs };

            for (int i = 0; i < 3; i++)
            {
                string cur  = FormatHelper.FormatTraffic(actuals[i]);
                string peak = FormatHelper.FormatTraffic(peaks[i]);
                string a0   = FormatHelper.FormatTraffic((long)avgs[i][0]);
                string a1   = FormatHelper.FormatTraffic((long)avgs[i][1]);
                string a2   = FormatHelper.FormatTraffic((long)avgs[i][2]);

                string left  = $"{labels[i]}  cur:{cur}   peak:{peak}";
                string rates = $"{a0}  {a1}  {a2}";
                WriteRow($"{left.PadRight(ratesCol)}{rates}");
            }
        }

        private void WriteRow(string str)
        {
            str = str.SafePrefix(Console.WindowWidth).PadRight(Console.WindowWidth);
            if (Console.CursorTop < Console.WindowHeight - 1)
                Console.Write(str);
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
