using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Data
{
    public class DataStackSection
    {
        private object _lockObj = new object();
        private readonly long _sectionNr;
        private readonly Dictionary<string, DataStackSectionIp> _ipItems = new Dictionary<string, DataStackSectionIp>();
        private long _totalTx;
        private long _totalRx;
        private bool _isFinalized;

        public long SectionNr => _sectionNr;
        public long TotalTx => _totalTx;
        public long TotalRx => _totalRx;

        public bool IsFinalized => _isFinalized;

        public DataStackSection(long sectionNr)
        {
            _sectionNr = sectionNr;
        }

        internal void AddIpTraffic(string srcAddress, string srcPort, string dstAddress, string dstPort, long tx, long rx)
        {
            //System.Diagnostics.Debug.Assert(!_isFinalized, "Souctova radka se ocekava jako posledni");
            var key = ConstructKey(srcAddress, srcPort, dstAddress, dstPort);

            DataStackSectionIp ipItem;
            if (!_ipItems.TryGetValue(key, out ipItem))
            {
                ipItem = new DataStackSectionIp(srcAddress, srcPort, dstAddress, dstPort, rx, tx);
                _ipItems.Add(key, ipItem);
            }
            else
            {
                ipItem.Increase(tx, rx);
            }
        }

        private static string ConstructKey(string srcAddress, string srcPort, string dstAddress, string dstPort)
        {
            return $"{srcAddress}:{srcPort}-{dstAddress}:{dstPort}";
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

        internal DataStackSectionIp GetIpTraffic(DataStackSectionIp ip)
        {
            var key = ConstructKey(ip.SrcAddress, ip.SrcPort, ip.DstAddress, ip.DstPort);

            DataStackSectionIp result;
            if (!_ipItems.TryGetValue(key, out result))
                result = new DataStackSectionIp(ip.SrcAddress, ip.SrcPort, ip.DstAddress, ip.DstPort, 0, 0);

            return result;
        }

        //private void RemoveOldItems(DateTime removeOlderThan)
        //{
        //    for (int i = _ipItems.Count - 1; i >= 0; i--)
        //    {
        //        if (_ipItems[i].When < removeOlderThan)
        //        {
        //            _ipItems.RemoveAt(i);
        //        }
        //    }
        //}

        //internal void AddMeasurement(DateTime when, long tx, long rx)
        //{

        //    DateTime removeOlderThan = when.AddSeconds(-MAX_DURATION_SEC);
        //    lock (_lockObj)
        //    {
        //        RemoveOldItems(removeOlderThan);
        //        _ipItems.Add(measurement);
        //    }
        //}
    }
}
