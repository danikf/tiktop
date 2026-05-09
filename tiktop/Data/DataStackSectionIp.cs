using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Data
{
    public class DataStackSectionIp
    {
        private string _srcAddress;
        private string _srcPort;
        private string _dstAddress;
        private string _dstPort;
        private long _rx;
        private long _tx;
        private long _total;

        public string SrcAddress => _srcAddress;
        public string SrcPort => _srcPort;
        public string DstAddress => _dstAddress;
        public string DstPort => _dstPort;
        public long Tx => _tx;
        public long Rx => _rx;
        public long Total => _total;


        public DataStackSectionIp(string srcAddress, string srcPort, string dstAddress, string dstPort, long rx, long tx)
        {
            _srcAddress = srcAddress;
            _srcPort = srcPort;
            _dstAddress = dstAddress;
            _dstPort = dstPort;
            _rx = rx;
            _tx = tx;

            _total = tx + rx;
        }

        internal void Increase(long tx, long rx)
        {
            _tx += tx;
            _rx += rx;

            _total = _tx + _rx;
        }
    }
}
