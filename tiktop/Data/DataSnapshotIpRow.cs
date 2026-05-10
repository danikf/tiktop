using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Data
{
    public class DataSnapshotIpRow
    {
        DataStackSectionIp _lastSection;
        DataStackSectionIp[] _shortRange;
        DataStackSectionIp[] _mediumRange;
        DataStackSectionIp[] _longRange;
        long _cumulativeTx;
        long _cumulativeRx;

        public DataStackSectionIp LastSection => _lastSection;
        public DataStackSectionIp[] ShortRange => _shortRange;
        public DataStackSectionIp[] MediumRange => _mediumRange;
        public DataStackSectionIp[] LongRange => _longRange;
        public long CumulativeTx => _cumulativeTx;
        public long CumulativeRx => _cumulativeRx;
        public long CumulativeTotal => _cumulativeTx + _cumulativeRx;

        public DataSnapshotIpRow(DataStackSectionIp lastSection,
            IEnumerable<DataStackSectionIp> shortRange,
            IEnumerable<DataStackSectionIp> mediumRange,
            IEnumerable<DataStackSectionIp> longRange,
            long cumulativeTx = 0, long cumulativeRx = 0)
        {
            _lastSection = lastSection;
            _shortRange = shortRange.ToArray();
            _mediumRange = mediumRange.ToArray();
            _longRange = longRange.ToArray();
            _cumulativeTx = cumulativeTx;
            _cumulativeRx = cumulativeRx;
        }
    }
}
