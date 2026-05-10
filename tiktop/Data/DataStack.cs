using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using tik4net.Objects.Tool;

namespace tiktop.Data
{
    public enum SortMode   { Total, Tx, Rx }
    public enum SortWindow { Short, Medium, Long }

    public class DataStack
    {
        private const int MAX_SECTIONS_CACHE_SIZE = 50;

        private readonly IReadOnlyList<IPNetwork> _localNetworks;
        private object _lockObj = new object();
        private Dictionary<long, DataStackSection> _itemsPerSection = new Dictionary<long, DataStackSection>(); //<index, Item>
        private long _txPeak;
        private long _rxPeak;
        private long _totalPeak;

        public SortMode   SortMode   { get; private set; } = SortMode.Total;
        public SortWindow SortWindow { get; private set; } = SortWindow.Short;

        public DataStack(IReadOnlyList<IPNetwork> localNetworks)
        {
            _localNetworks = localNetworks;
        }

        public void CycleSortMode()
        {
            lock (_lockObj)
                SortMode = (SortMode)(((int)SortMode + 1) % 3);
        }

        public void SetSortWindow(SortWindow window)
        {
            lock (_lockObj) SortWindow = window;
        }

        public void ResetPeaks()
        {
            lock (_lockObj)
                _txPeak = _rxPeak = _totalPeak = 0;
        }

        public void AddRow(ToolTorch torch)
        {
            lock (_lockObj)
            {
                if (string.IsNullOrEmpty(torch.SrcAddress))
                {
                    AddTotalTraffic(torch.SectionNr, torch.Tx, torch.Rx);
                    return;
                }

                // Normalize: local address is always treated as src so flows aggregate correctly
                // regardless of which direction initiated the connection.
                if (_localNetworks.Count > 0 && IsLocal(torch.DstAddress) && !IsLocal(torch.SrcAddress))
                    AddIpTraffic(torch.SectionNr, torch.DstAddress, torch.DstPort, torch.SrcAddress, torch.SrcPort, torch.Rx, torch.Tx);
                else
                    AddIpTraffic(torch.SectionNr, torch.SrcAddress, torch.SrcPort, torch.DstAddress, torch.DstPort, torch.Tx, torch.Rx);
            }
        }

        private bool IsLocal(string address)
        {
            return IPAddress.TryParse(address, out var ip)
                && _localNetworks.Any(n => n.Contains(ip));
        }

        private void AddTotalTraffic(long section, long tx, long rx)
        {
            DataStackSection? item;
            if (!_itemsPerSection.TryGetValue(section, out item))
            {
                item = new DataStackSection(section);
                _itemsPerSection.Add(section, item);
            }
            item.AddTotalTraffic(tx, rx);

            //remove older sections
            var sectionsToRemove = _itemsPerSection.Keys.Where(k => k < section - MAX_SECTIONS_CACHE_SIZE).ToArray();
            foreach(long sectionToRemove in sectionsToRemove)
            {
                _itemsPerSection.Remove(sectionToRemove);
            }
        }

        private void AddIpTraffic(long section, string srcAddress, string srcPort, string dstAddress, string dstPort, long tx, long rx)
        {
            DataStackSection? item;
            if (!_itemsPerSection.TryGetValue(section, out item))
            {
                item = new DataStackSection(section);
                _itemsPerSection.Add(section, item);
            }
            item.AddIpTraffic(srcAddress, srcPort, dstAddress, dstPort, tx, rx);
        }

        internal DataSnapshot CreateSnapshot(int nrOfItems)
        {
            const int shortWindowCnt = 2;
            const int mediumWindowCnt = 10;
            const int longWindowCnt = 40;

            DataStackSection? lastFinalizedSection = null;
            DataStackSection[] items;
            lock (_lockObj)
            {
                foreach (var section in _itemsPerSection.Values)
                {
                    if (section.IsFinalized && (lastFinalizedSection == null || section.SectionNr > lastFinalizedSection.SectionNr))
                        lastFinalizedSection = section;
                }

                if (lastFinalizedSection == null)
                    return new DataSnapshot(_txPeak, _rxPeak, _totalPeak); //empty snapshot           

                items = _itemsPerSection
                    //.Where(iPair => iPair.Key > lastFinalizedSection.SectionNr - longWindowCnt)
                    .Where(iPair => iPair.Value.IsFinalized)
                    .Select(iPair => iPair.Value).ToArray();

                _txPeak = Math.Max(_txPeak, lastFinalizedSection.TotalTx);
                _rxPeak = Math.Max(_rxPeak, lastFinalizedSection.TotalRx);
                _totalPeak = Math.Max(_totalPeak, lastFinalizedSection.TotalTx + lastFinalizedSection.TotalRx);
            }

            items = items.OrderBy(iPair => iPair.SectionNr).ToArray(); //sort after lock

            // Use TakeLast so windows contain the MOST RECENT N sections, not oldest.
            var shortWindow  = items.TakeLast(shortWindowCnt).ToArray();
            var mediumWindow = items.TakeLast(mediumWindowCnt).ToArray();
            var longWindow   = items.TakeLast(longWindowCnt).ToArray();

            // Select and sort top IPs according to the chosen sort window.
            IEnumerable<DataStackSectionIp> sortedIps = SortWindow switch
            {
                SortWindow.Medium => SortByWindowAvg(lastFinalizedSection.GetAllIps(), mediumWindow, SortMode, nrOfItems),
                SortWindow.Long   => SortByWindowAvg(lastFinalizedSection.GetAllIps(), longWindow,   SortMode, nrOfItems),
                _                 => lastFinalizedSection.GetTopIps(nrOfItems, SortMode),
            };

            var topIpTraffic = sortedIps.Select(i =>
                new DataSnapshotIpRow(i,
                    shortWindow .Select(ii => ii.GetIpTraffic(i)).ToArray(),
                    mediumWindow.Select(ii => ii.GetIpTraffic(i)).ToArray(),
                    longWindow  .Select(ii => ii.GetIpTraffic(i)).ToArray()
                ));

            static double SafeAvg(DataStackSection[] w, Func<DataStackSection, double> fn) =>
                w.Length > 0 ? w.Average(fn) : 0;
            static double SafeMax(DataStackSection[] w, Func<DataStackSection, double> fn) =>
                w.Length > 0 ? w.Max(fn) : 0;

            return new DataSnapshot(
                lastFinalizedSection.TotalTx, lastFinalizedSection.TotalRx,
                _txPeak, _rxPeak, _totalPeak,
                new double[] { SafeAvg(shortWindow, i => i.TotalTx), SafeMax(mediumWindow, i => i.TotalTx), SafeMax(longWindow, i => i.TotalTx) },
                new double[] { SafeAvg(shortWindow, i => i.TotalRx), SafeMax(mediumWindow, i => i.TotalRx), SafeMax(longWindow, i => i.TotalRx) },
                new double[] { SafeAvg(shortWindow, i => i.TotalTx + i.TotalRx), SafeMax(mediumWindow, i => i.TotalTx + i.TotalRx), SafeMax(longWindow, i => i.TotalTx + i.TotalRx) },
                topIpTraffic
            );
        }

        private static DataStackSectionIp[] SortByWindowAvg(
            IEnumerable<DataStackSectionIp> candidates,
            DataStackSection[] window,
            SortMode sort,
            int take)
        {
            if (window.Length == 0)
                return candidates.Take(take).ToArray();

            Func<DataStackSectionIp, double> avgFn = sort switch
            {
                SortMode.Tx => ip => window.Average(s => (double)s.GetIpTraffic(ip).Tx),
                SortMode.Rx => ip => window.Average(s => (double)s.GetIpTraffic(ip).Rx),
                _            => ip => window.Average(s => (double)s.GetIpTraffic(ip).Total),
            };
            return candidates.OrderByDescending(avgFn).Take(take).ToArray();
        }
    }
}
