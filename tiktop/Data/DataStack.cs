using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using tik4net.Objects.Tool;

namespace tiktop.Data
{
    public enum SortMode      { Total, Tx, Rx }
    public enum SortWindow    { Short, Medium, Long, Cumulative }
    public enum AggregateMode { None, BySrc, ByDst, ByPort }

    public class DataStack
    {
        private const int MAX_SECTIONS_CACHE_SIZE = 50;

        private readonly IReadOnlyList<IPNetwork> _localNetworks;
        private readonly object _lockObj = new object();
        private Dictionary<long, DataStackSection> _itemsPerSection = new Dictionary<long, DataStackSection>(); //<index, Item>
        private long _txPeak;
        private long _rxPeak;
        private long _totalPeak;
        private long _cumulativeTx;
        private long _cumulativeRx;
        private long _lastCumulativeSection = -1;
        private Dictionary<string, (long tx, long rx)> _cumulativeIp = new Dictionary<string, (long, long)>();

        public SortMode      SortMode      { get; private set; } = SortMode.Total;
        public SortWindow    SortWindow    { get; private set; } = SortWindow.Medium;
        public AggregateMode AggregateMode { get; private set; } = AggregateMode.None;
        public bool          SwapDirection { get { lock (_lockObj) return _swapDirection; } }

        private bool _swapDirection = false;

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

        public void CycleAggregateMode()
        {
            lock (_lockObj)
                AggregateMode = (AggregateMode)(((int)AggregateMode + 1) % 4);
        }

        public void ResetPeaks()
        {
            lock (_lockObj)
                _txPeak = _rxPeak = _totalPeak = 0;
        }

        public void ToggleSwapDirection()
        {
            lock (_lockObj)
            {
                _swapDirection = !_swapDirection;
                _itemsPerSection.Clear();
                _txPeak = _rxPeak = _totalPeak = 0;
                _cumulativeTx = _cumulativeRx = 0;
                _lastCumulativeSection = -1;
                _cumulativeIp.Clear();
            }
        }

        public void AddRow(ToolTorch torch)
        {
            lock (_lockObj)
            {
                if (string.IsNullOrEmpty(torch.SrcAddress))
                {
                    // On LAN interfaces torch TX = router→LAN = download; swap restores local perspective.
                    AddTotalTraffic(torch.SectionNr,
                        _swapDirection ? torch.Rx : torch.Tx,
                        _swapDirection ? torch.Tx : torch.Rx);
                    return;
                }

                // Normalize: local address is always treated as src so flows aggregate correctly
                // regardless of which direction initiated the connection.
                if (_localNetworks.Count > 0 && IsLocal(torch.DstAddress) && !IsLocal(torch.SrcAddress))
                    AddIpTraffic(torch.SectionNr, torch.DstAddress, torch.DstPort, torch.SrcAddress, torch.SrcPort, torch.Rx, torch.Tx);
                else if (_swapDirection)
                    AddIpTraffic(torch.SectionNr, torch.SrcAddress, torch.SrcPort, torch.DstAddress, torch.DstPort, torch.Rx, torch.Tx);
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
                    .Where(kv => kv.Value.IsFinalized)
                    .Select(kv => kv.Value).ToArray();

                _txPeak = Math.Max(_txPeak, lastFinalizedSection.TotalTx);
                _rxPeak = Math.Max(_rxPeak, lastFinalizedSection.TotalRx);
                _totalPeak = Math.Max(_totalPeak, lastFinalizedSection.TotalTx + lastFinalizedSection.TotalRx);

                // Accumulate cumulative totals for newly finalized sections.
                foreach (var section in _itemsPerSection.Values)
                {
                    if (!section.IsFinalized || section.SectionNr <= _lastCumulativeSection) continue;
                    _cumulativeTx += section.TotalTx;
                    _cumulativeRx += section.TotalRx;
                    foreach (var ip in section.GetAllIps())
                    {
                        string k = $"{ip.SrcAddress}:{ip.SrcPort}-{ip.DstAddress}:{ip.DstPort}";
                        _cumulativeIp[k] = _cumulativeIp.TryGetValue(k, out var c)
                            ? (c.tx + ip.Tx, c.rx + ip.Rx)
                            : (ip.Tx, ip.Rx);
                    }
                    _lastCumulativeSection = Math.Max(_lastCumulativeSection, section.SectionNr);
                }
            }

            items = items.OrderBy(iPair => iPair.SectionNr).ToArray(); //sort after lock

            // Use TakeLast so windows contain the MOST RECENT N sections, not oldest.
            var shortWindow  = items.TakeLast(shortWindowCnt).ToArray();
            var mediumWindow = items.TakeLast(mediumWindowCnt).ToArray();
            var longWindow   = items.TakeLast(longWindowCnt).ToArray();

            // Select and sort top IPs according to the chosen sort window and aggregate mode.
            IEnumerable<DataSnapshotIpRow> topIpTraffic;
            if (AggregateMode != AggregateMode.None)
            {
                // Key extractor and row factory differ per aggregate mode.
                Func<DataStackSectionIp, string> groupKey = AggregateMode switch {
                    AggregateMode.BySrc  => ip => ip.SrcAddress,
                    AggregateMode.ByDst  => ip => ip.DstAddress,
                    _                    => ip => ip.DstPort,        // ByPort
                };
                Func<string, DataStackSectionIp> makeAgg = AggregateMode switch {
                    AggregateMode.BySrc  => k => new DataStackSectionIp(k,   "*", "*", "*", 0, 0),
                    AggregateMode.ByDst  => k => new DataStackSectionIp("*", "*", k,   "*", 0, 0),
                    _                    => k => new DataStackSectionIp("*", "*", "*", k,   0, 0),
                };
                Func<DataStackSection, string, DataStackSectionIp> winLookup = AggregateMode switch {
                    AggregateMode.BySrc  => (s, k) => s.GetAggregatedBySrc(k),
                    AggregateMode.ByDst  => (s, k) => s.GetAggregatedByDst(k),
                    _                    => (s, k) => s.GetAggregatedByPort(k),
                };
                Func<string, (long tx, long rx)> cumulLookup = AggregateMode switch {
                    AggregateMode.BySrc  => k => GetCumulativeBySrc(k),
                    AggregateMode.ByDst  => k => GetCumulativeByDst(k),
                    _                    => k => GetCumulativeByPort(k),
                };

                // Collect unique group keys from the relevant window so that connections
                // absent in the last section but active within the window still appear.
                // Current (last-section) values are retrieved via winLookup so they read 0
                // when the group is absent in that second, while window averages stay stable.
                var candidateGroupKeys = SortWindow switch {
                    SortWindow.Medium     => mediumWindow.SelectMany(s => s.GetAllIps().Select(groupKey)).Distinct(),
                    SortWindow.Long       => longWindow  .SelectMany(s => s.GetAllIps().Select(groupKey)).Distinct(),
                    SortWindow.Cumulative => longWindow  .SelectMany(s => s.GetAllIps().Select(groupKey)).Distinct(),
                    _                     => lastFinalizedSection.GetAllIps().Select(groupKey).Distinct(),
                };

                var aggIps = candidateGroupKeys.Select(k =>
                    {
                        var current = winLookup(lastFinalizedSection, k);
                        var agg = makeAgg(k);
                        agg.Increase(current.Tx, current.Rx);
                        return agg;
                    })
                    .ToArray();

                // Sort aggregated IPs.
                DataStackSectionIp[] sortedAgg = SortWindow switch
                {
                    SortWindow.Medium     => SortByWindowAvgAgg(aggIps, mediumWindow, SortMode, groupKey, winLookup, nrOfItems),
                    SortWindow.Long       => SortByWindowAvgAgg(aggIps, longWindow,   SortMode, groupKey, winLookup, nrOfItems),
                    SortWindow.Cumulative => SortAggCumulative(aggIps, SortMode, groupKey, cumulLookup, nrOfItems),
                    _                     => SortAggIps(aggIps, SortMode, nrOfItems),
                };

                topIpTraffic = sortedAgg.Select(aggIp =>
                {
                    string key = groupKey(aggIp);
                    var (cTx, cRx) = cumulLookup(key);
                    return new DataSnapshotIpRow(aggIp,
                        shortWindow .Select(s => winLookup(s, key)).ToArray(),
                        mediumWindow.Select(s => winLookup(s, key)).ToArray(),
                        longWindow  .Select(s => winLookup(s, key)).ToArray(),
                        cTx, cRx
                    );
                });
            }
            else
            {
                IEnumerable<DataStackSectionIp> sortedIps = SortWindow switch
                {
                    SortWindow.Medium     => SortByWindowAvg(CandidatesFromWindow(mediumWindow), mediumWindow, SortMode, nrOfItems),
                    SortWindow.Long       => SortByWindowAvg(CandidatesFromWindow(longWindow),   longWindow,   SortMode, nrOfItems),
                    SortWindow.Cumulative => SortByCumulative(CandidatesFromWindow(longWindow),  _cumulativeIp, SortMode, nrOfItems),
                    _                     => lastFinalizedSection.GetTopIps(nrOfItems, SortMode),
                };
                topIpTraffic = sortedIps.Select(i =>
                {
                    string k = $"{i.SrcAddress}:{i.SrcPort}-{i.DstAddress}:{i.DstPort}";
                    var (cTx, cRx) = _cumulativeIp.TryGetValue(k, out var c) ? c : (0L, 0L);
                    return new DataSnapshotIpRow(i,
                        shortWindow .Select(ii => ii.GetIpTraffic(i)).ToArray(),
                        mediumWindow.Select(ii => ii.GetIpTraffic(i)).ToArray(),
                        longWindow  .Select(ii => ii.GetIpTraffic(i)).ToArray(),
                        cTx, cRx
                    );
                });
            }

            static double SafeAvg(DataStackSection[] w, Func<DataStackSection, double> fn) =>
                w.Length > 0 ? w.Average(fn) : 0;
            static double SafeMax(DataStackSection[] w, Func<DataStackSection, double> fn) =>
                w.Length > 0 ? w.Max(fn) : 0;

            return new DataSnapshot(
                lastFinalizedSection.TotalTx, lastFinalizedSection.TotalRx,
                _txPeak, _rxPeak, _totalPeak,
                _cumulativeTx, _cumulativeRx,
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

        private (long tx, long rx) GetCumulativeBySrc(string srcAddress)
        {
            long tx = 0, rx = 0;
            foreach (var (k, v) in _cumulativeIp)
                if (k.StartsWith(srcAddress + ":")) { tx += v.tx; rx += v.rx; }
            return (tx, rx);
        }

        private (long tx, long rx) GetCumulativeByDst(string dstAddress)
        {
            string match = "-" + dstAddress + ":";
            long tx = 0, rx = 0;
            foreach (var (k, v) in _cumulativeIp)
                if (k.Contains(match)) { tx += v.tx; rx += v.rx; }
            return (tx, rx);
        }

        private (long tx, long rx) GetCumulativeByPort(string dstPort)
        {
            // Key format: "srcAddr:srcPort-dstAddr:dstPort"
            string suffix = ":" + dstPort;
            long tx = 0, rx = 0;
            foreach (var (k, v) in _cumulativeIp)
                if (k.EndsWith(suffix)) { tx += v.tx; rx += v.rx; }
            return (tx, rx);
        }

        private static DataStackSectionIp[] SortAggIps(
            IEnumerable<DataStackSectionIp> candidates,
            SortMode sort,
            int take)
        {
            Func<DataStackSectionIp, long> key = sort switch
            {
                SortMode.Tx => ip => ip.Tx,
                SortMode.Rx => ip => ip.Rx,
                _           => ip => ip.Total,
            };
            return candidates.OrderByDescending(key).Take(take).ToArray();
        }

        private static DataStackSectionIp[] SortByWindowAvgAgg(
            IEnumerable<DataStackSectionIp> candidates,
            DataStackSection[] window,
            SortMode sort,
            Func<DataStackSectionIp, string> groupKey,
            Func<DataStackSection, string, DataStackSectionIp> winLookup,
            int take)
        {
            if (window.Length == 0)
                return SortAggIps(candidates, sort, take);

            Func<DataStackSectionIp, double> avgFn = sort switch
            {
                SortMode.Tx => ip => { string k = groupKey(ip); return window.Average(s => (double)winLookup(s, k).Tx); },
                SortMode.Rx => ip => { string k = groupKey(ip); return window.Average(s => (double)winLookup(s, k).Rx); },
                _           => ip => { string k = groupKey(ip); return window.Average(s => (double)winLookup(s, k).Total); },
            };
            return candidates.OrderByDescending(avgFn).Take(take).ToArray();
        }

        private static DataStackSectionIp[] SortByCumulative(
            IEnumerable<DataStackSectionIp> candidates,
            Dictionary<string, (long tx, long rx)> cumulDict,
            SortMode sort,
            int take)
        {
            Func<DataStackSectionIp, long> key = ip =>
            {
                string k = $"{ip.SrcAddress}:{ip.SrcPort}-{ip.DstAddress}:{ip.DstPort}";
                if (!cumulDict.TryGetValue(k, out var c)) return 0;
                return sort switch { SortMode.Tx => c.tx, SortMode.Rx => c.rx, _ => c.tx + c.rx };
            };
            return candidates.OrderByDescending(key).Take(take).ToArray();
        }

        private static DataStackSectionIp[] SortAggCumulative(
            IEnumerable<DataStackSectionIp> candidates,
            SortMode sort,
            Func<DataStackSectionIp, string> groupKey,
            Func<string, (long tx, long rx)> cumulLookup,
            int take)
        {
            Func<DataStackSectionIp, long> key = ip =>
            {
                var (tx, rx) = cumulLookup(groupKey(ip));
                return sort switch { SortMode.Tx => tx, SortMode.Rx => rx, _ => tx + rx };
            };
            return candidates.OrderByDescending(key).Take(take).ToArray();
        }

        // Returns one representative DataStackSectionIp per unique connection seen across
        // the window, preferring the most recent section so addresses/ports are current.
        private static DataStackSectionIp[] CandidatesFromWindow(DataStackSection[] window)
        {
            var seen = new HashSet<string>();
            var result = new List<DataStackSectionIp>();
            for (int i = window.Length - 1; i >= 0; i--)
                foreach (var ip in window[i].GetAllIps())
                {
                    string key = $"{ip.SrcAddress}:{ip.SrcPort}-{ip.DstAddress}:{ip.DstPort}";
                    if (seen.Add(key)) result.Add(ip);
                }
            return result.ToArray();
        }
    }
}
