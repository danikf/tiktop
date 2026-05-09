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

        public DataStackSectionIp LastSection => _lastSection;
        public DataStackSectionIp[] ShortRange => _shortRange;
        public DataStackSectionIp[] MediumRange => _mediumRange;
        public DataStackSectionIp[] LongRange => _longRange;

        public DataSnapshotIpRow(DataStackSectionIp lastSection, IEnumerable<DataStackSectionIp> shortRange, IEnumerable<DataStackSectionIp> mediumRange, IEnumerable<DataStackSectionIp> longRange)
        {
            _lastSection = lastSection;
            _shortRange = shortRange.ToArray();
            _mediumRange = mediumRange.ToArray();
            _longRange = longRange.ToArray();
        }
    }
}
