using System;
using System.Collections.Generic;
using System.Linq;

namespace tiktop.Data
{
    public class DataStackSection
    {
        private readonly long _sectionNr;
        private readonly Dictionary<string, DataStackSectionIp> _ipItems = new Dictionary<string, DataStackSectionIp>();
        private long _totalTx;
        private long _totalRx;
        private bool _isFinalized;

        public long SectionNr  => _sectionNr;
        public long TotalTx    => _totalTx;
        public long TotalRx    => _totalRx;
        public bool IsFinalized => _isFinalized;

        public DataStackSection(long sectionNr)
        {
            _sectionNr = sectionNr;
        }

        internal void AddIpTraffic(string srcAddress, string srcPort, string dstAddress, string dstPort, long tx, long rx)
        {
            var key = ConstructKey(srcAddress, srcPort, dstAddress, dstPort);

            if (!_ipItems.TryGetValue(key, out var ipItem))
            {
                ipItem = new DataStackSectionIp(srcAddress, srcPort, dstAddress, dstPort, tx, rx);
                _ipItems.Add(key, ipItem);
            }
            else
            {
                ipItem.Increase(tx, rx);
            }
        }

        internal void AddTotalTraffic(long totalTx, long totalRx)
        {
            _totalTx = totalTx;
            _totalRx = totalRx;
            _isFinalized = true;
        }

        internal IEnumerable<DataStackSectionIp> GetTopIps(int nrOfItems, SortMode sort = SortMode.Total)
        {
            Func<DataStackSectionIp, long> key = sort switch
            {
                SortMode.Tx => i => i.Tx,
                SortMode.Rx => i => i.Rx,
                _           => i => i.Total,
            };
            return _ipItems.Values.OrderByDescending(key).Take(nrOfItems);
        }

        internal IEnumerable<DataStackSectionIp> GetAllIps() => _ipItems.Values;

        internal DataStackSectionIp GetIpTraffic(DataStackSectionIp ip)
        {
            var key = ConstructKey(ip.SrcAddress, ip.SrcPort, ip.DstAddress, ip.DstPort);
            return _ipItems.TryGetValue(key, out var result)
                ? result
                : new DataStackSectionIp(ip.SrcAddress, ip.SrcPort, ip.DstAddress, ip.DstPort, 0, 0);
        }

        internal DataStackSectionIp GetAggregatedBySrc(string srcAddress)
        {
            long tx = 0, rx = 0;
            foreach (var ip in _ipItems.Values)
                if (ip.SrcAddress == srcAddress) { tx += ip.Tx; rx += ip.Rx; }
            return new DataStackSectionIp(srcAddress, "*", "*", "*", tx, rx);
        }

        internal DataStackSectionIp GetAggregatedByDst(string dstAddress)
        {
            long tx = 0, rx = 0;
            foreach (var ip in _ipItems.Values)
                if (ip.DstAddress == dstAddress) { tx += ip.Tx; rx += ip.Rx; }
            return new DataStackSectionIp("*", "*", dstAddress, "*", tx, rx);
        }

        internal DataStackSectionIp GetAggregatedByPort(string dstPort)
        {
            long tx = 0, rx = 0;
            foreach (var ip in _ipItems.Values)
                if (ip.DstPort == dstPort) { tx += ip.Tx; rx += ip.Rx; }
            return new DataStackSectionIp("*", "*", "*", dstPort, tx, rx);
        }

        private static string ConstructKey(string srcAddress, string srcPort, string dstAddress, string dstPort)
            => $"{srcAddress}:{srcPort}-{dstAddress}:{dstPort}";
    }
}
