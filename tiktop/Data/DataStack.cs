using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using tik4net.Objects.Tool;

namespace tiktop.Data
{
    public enum SortMode { Total, Tx, Rx }

    public class DataStack
    {
        private const int MAX_SECTIONS_CACHE_SIZE = 50;

        private readonly IReadOnlyList<IPNetwork> _localNetworks;
        private object _lockObj = new object();
        private Dictionary<long, DataStackSection> _itemsPerSection = new Dictionary<long, DataStackSection>(); //<index, Item>
        private long _txPeak;
        private long _rxPeak;
        private long _totalPeak;

        public SortMode SortMode { get; private set; } = SortMode.Total;

        public DataStack(IReadOnlyList<IPNetwork> localNetworks)
        {
            _localNetworks = localNetworks;
        }

        public void CycleSortMode()
        {
            lock (_lockObj)
                SortMode = (SortMode)(((int)SortMode + 1) % 3);
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
            DataStackSection item;
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
            DataStackSection item;
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

            DataStackSection lastFinalizedSection = null;
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

            var topIpTraffic = lastFinalizedSection.GetTopIps(nrOfItems, SortMode).Select(i=>
                new DataSnapshotIpRow(i, 
                    items.Take(shortWindowCnt).Select(ii=>ii.GetIpTraffic(i)).ToArray(),
                    items.Take(mediumWindowCnt).Select(ii => ii.GetIpTraffic(i)).ToArray(),
                    items.Take(longWindowCnt).Select(ii => ii.GetIpTraffic(i)).ToArray()
                    ));

            return new DataSnapshot(
               lastFinalizedSection.TotalTx, lastFinalizedSection.TotalRx,
              _txPeak, _rxPeak, _totalPeak,
              new double[] { items.Take(shortWindowCnt).Average(i => i.TotalTx), items.Take(mediumWindowCnt).Max(i => i.TotalTx), items.Take(longWindowCnt).Max(i => i.TotalTx) },
              new double[] { items.Take(shortWindowCnt).Max(i => i.TotalRx), items.Take(mediumWindowCnt).Max(i => i.TotalRx), items.Take(longWindowCnt).Max(i => i.TotalRx) },
              new double[] { items.Take(shortWindowCnt).Max(i => i.TotalTx + i.TotalRx), items.Take(mediumWindowCnt).Max(i => i.TotalTx + i.TotalRx), items.Take(longWindowCnt).Max(i => i.TotalTx + i.TotalRx) },
              topIpTraffic
              );
        }
    }
}
