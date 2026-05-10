using System.Collections.Generic;
using System.Linq;

namespace tiktop.Data
{
    public class DataSnapshotIpRow
    {
        public DataStackSectionIp   LastSection    { get; }
        public DataStackSectionIp[] ShortRange     { get; }
        public DataStackSectionIp[] MediumRange    { get; }
        public DataStackSectionIp[] LongRange      { get; }
        public long                 CumulativeTx   { get; }
        public long                 CumulativeRx   { get; }
        public long                 CumulativeTotal => CumulativeTx + CumulativeRx;

        public DataSnapshotIpRow(DataStackSectionIp lastSection,
            IEnumerable<DataStackSectionIp> shortRange,
            IEnumerable<DataStackSectionIp> mediumRange,
            IEnumerable<DataStackSectionIp> longRange,
            long cumulativeTx = 0, long cumulativeRx = 0)
        {
            LastSection  = lastSection;
            ShortRange   = shortRange.ToArray();
            MediumRange  = mediumRange.ToArray();
            LongRange    = longRange.ToArray();
            CumulativeTx = cumulativeTx;
            CumulativeRx = cumulativeRx;
        }
    }
}
