using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Data
{
    public struct DataSnapshot
    {
        private readonly long _actualTx;
        private readonly long _actualRx;
        private readonly long _peakTx;
        private readonly long _peakRx;
        private readonly long _peakTotal;
        private readonly double[] _txAvgs;
        private readonly double[] _rxAvgs;
        private readonly double[] _totalAvgs;
        private readonly DataSnapshotIpRow[] _topIpTraffic;

        public long ActualTx => _actualTx;
        public long ActualRx => _actualRx;
        public long PeakTx => _peakTx;
        public long PeakRx => _peakRx;
        public long PeakTotal => _peakTotal;
        public double[] TxAvgs => _txAvgs;
        public double[] RxAvgs => _rxAvgs;
        public double[] TotalAvgs => _totalAvgs;
        public DataSnapshotIpRow[] TopIpTraffic => _topIpTraffic;


        public DataSnapshot(long actualTx, long actualRx, long peakTx, long peakRx, long peakTotal,
            double[] txAvgs, double[] rxAvgs, double[] totalAvgs, IEnumerable<DataSnapshotIpRow> topIpTraffic)
        {
            _actualTx = actualTx;
            _actualRx = actualRx;
            _peakTx = peakTx;
            _peakRx = peakRx;
            _peakTotal = peakTotal;

            _txAvgs = txAvgs;
            _rxAvgs = rxAvgs;
            _totalAvgs = totalAvgs;

            _topIpTraffic = topIpTraffic.ToArray();
        }

        public DataSnapshot(long peakTx, long peakRx, long peakTotal)
        {
            _actualTx = 0;
            _actualRx = 0;
            _peakTx = peakTx;
            _peakRx = peakRx;
            _peakTotal = peakTotal;

            _txAvgs = new double[] { 0, 0, 0 };
            _rxAvgs = new double[] { 0, 0, 0 };
            _totalAvgs = new double[] { 0, 0, 0 };

            _topIpTraffic = new DataSnapshotIpRow[] { };
        }
    }
}
