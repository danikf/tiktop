using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Data
{
    public class DataStack
    {
        private const int MAX_SECTIONS_CACHE_SIZE = 50;

        private object _lockObj = new object();
        private Dictionary<long, DataStackSection> _itemsPerSection = new Dictionary<long, DataStackSection>(); //<index, Item>
        private long _txPeak;
        private long _rxPeak;
        private long _totalPeak;

        public DataStack()
        {

        }

        public void AddRow(IReadOnlyDictionary<string, string> items)
        {
            long sectionIndex = long.Parse(items[".section"]);

            lock (_lockObj)
            {
                if (items.ContainsKey("src-address"))
                { //.tag=1|mac-protocol=ip|src-address=10.43.94.205|src-port=53 (dns)|dst-address=10.43.109.114|dst-port=50587|tx=616|rx=4296|tx-packets=1|rx-packets=1|.section=9
                    string srcAddress = items["src-address"];
                    string srcPort = items["src-port"];
                    string dstAddress = items["dst-address"];
                    string dstPort = items["src-port"];
                    long tx = long.Parse(items["tx"]);
                    long rx = long.Parse(items["rx"]);
                    AddIpTraffic(sectionIndex, srcAddress, srcPort, dstAddress, dstPort, tx, rx);
                }
                else
                { //.tag=1|tx=18960|rx=49360|tx-packets=12|rx-packets=12|.section=10
                    long tx = long.Parse(items["tx"]);
                    long rx = long.Parse(items["rx"]);
                    AddTotalTraffic(sectionIndex, tx, rx);
                }
            }
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

            var topIpTraffic = lastFinalizedSection.GetTopIps(nrOfItems).Select(i=> 
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
