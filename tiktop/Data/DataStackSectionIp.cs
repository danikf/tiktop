using System;

namespace tiktop.Data
{
    public class DataStackSectionIp
    {
        private readonly string _srcAddress;
        private readonly string _srcPort;
        private readonly string _dstAddress;
        private readonly string _dstPort;
        private long _tx;
        private long _rx;

        public string SrcAddress => _srcAddress;
        public string SrcPort    => _srcPort;
        public string DstAddress => _dstAddress;
        public string DstPort    => _dstPort;
        public long   Tx         => _tx;
        public long   Rx         => _rx;
        public long   Total      => _tx + _rx;

        public DataStackSectionIp(string srcAddress, string srcPort, string dstAddress, string dstPort, long tx, long rx)
        {
            _srcAddress = srcAddress;
            _srcPort    = srcPort;
            _dstAddress = dstAddress;
            _dstPort    = dstPort;
            _tx = tx;
            _rx = rx;
        }

        internal void Increase(long tx, long rx)
        {
            _tx += tx;
            _rx += rx;
        }
    }
}
