using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{ 
    class Visualiser : IDisposable
    {
        //// https://social.msdn.microsoft.com/Forums/vstudio/en-US/8478fa4e-ee5d-400c-81f8-63691896e224/drawing-on-console?forum=csharpgeneral
        //[DllImport("user32.dll", CharSet = CharSet.Auto)]
        //public static extern IntPtr GetDC(IntPtr hWnd);

        const int headerHeight = 2;
        const int footerHeight = 4 + 1 /*Nejde psat na posledni radek*/;
        //const int itemsHeight = 25 - headerHeight - footerHeight;

        private object lockObj = new object();
        private volatile bool isDisposed = false;
        //private Graphics graphics;
        //private BufferedGraphics bufferedGraphics;

        private ConsoleColor backgroudColor;
        private ConsoleColor foregroundColor;

        public int NrOfItems => (Console.WindowHeight - headerHeight - footerHeight) / 2;

        public Visualiser()
        {
            Console.CursorVisible = false;
            backgroudColor = Console.BackgroundColor;
            foregroundColor = Console.ForegroundColor;

            ////alocate console buffer
            //Process process = Process.GetCurrentProcess();
            //graphics = Graphics.FromHdc(GetDC(process.MainWindowHandle));
            //BufferedGraphicsContext context = BufferedGraphicsManager.Current;
            //context.MaximumBuffer = new Size(Console.WindowWidth, Console.WindowHeight);
            //bufferedGraphics = context.Allocate(graphics, new Rectangle(0, 0, 320, 200));
        }

        public void Draw(DataSnapshot data)
        {
            lock (lockObj)
            {
                if (!isDisposed)
                {
                    //https://en.wikipedia.org/wiki/Box-drawing_character
                    //header
                    Console.SetCursorPosition(0, 0);
                    DrawHeader(data);

                    //items
                    Console.SetCursorPosition(0, headerHeight);
                    int itemsHeight = Console.WindowHeight - headerHeight - footerHeight;
                    DrawItems(data, itemsHeight / 2);

                    //footer
                    Console.SetCursorPosition(0, Console.WindowHeight - footerHeight);
                    DrawFooter(data);
                }
            }
        }

        //const string line15 = "───────────────";

        private void DrawHeader(DataSnapshot data)
        {
            double nr = data.PeakTotal / 5;
            string nr1 = FormatHelper.FormatTraffic((int)nr, true);
            string nr2 = FormatHelper.FormatTraffic((int)nr * 2, true);
            string nr3 = FormatHelper.FormatTraffic((int)nr * 3, true);
            string nr4 = FormatHelper.FormatTraffic((int)nr * 4, true);
            string nr5 = FormatHelper.FormatTraffic((int)nr * 5, true);

            WriteRow($"                { nr1}          { nr2}          { nr3}          { nr4}    { nr5}");
            WriteRow($"└───────────────┴───────────────┴───────────────┴───────────────┴───────────────");            
        }

        private void DrawItems(DataSnapshot data, int cnt)
        {
            foreach(var ip in data.TopIpTraffic.Take(cnt))
            {
                WriteRow($"{ip.LastSection.SrcAddress.SafePrefix(29).PadRight(29)} => {ip.LastSection.DstAddress.SafePrefix(29).PadRight(29)}   {Frm3Nrs(new double[] { 1, 2, 3 })}");
                WriteRow($"                              <= {ip.LastSection.DstAddress.SafePrefix(29).PadRight(29)}   {Frm3Nrs(new double[] { 1, 2, 3 })}");
            }
        }

        private void DrawFooter(DataSnapshot data)
        {
            string tx = FormatHelper.FormatTraffic(data.ActualTx);
            string rx = FormatHelper.FormatTraffic(data.ActualRx);
            string tot = FormatHelper.FormatTraffic(data.ActualTx + data.ActualRx);
            string pTx = FormatHelper.FormatTraffic(data.PeakTx);
            string pRx = FormatHelper.FormatTraffic(data.PeakRx);
            string pTt = FormatHelper.FormatTraffic(data.PeakTotal);

            WriteRow($"────────────────────────────────────────────────────────────────────────────────");
            WriteRow($"TX:                   {  tx}  peak: { pTx}        rates:  {Frm3Nrs(data.TxAvgs)}");
            WriteRow($"RX:                   {  rx}        { pRx}                {Frm3Nrs(data.RxAvgs)}");
            WriteRow($"TOTAL:                { tot}        { pTt}                {Frm3Nrs(data.TotalAvgs)}");
            //                                                                    { nr1}  { nr2}  { nr3}
        }

        private string Frm3Nrs(double[] items)
        {
            string nr1 = FormatHelper.FormatTraffic((long)items[0]);
            string nr2 = FormatHelper.FormatTraffic((long)items[1]);
            string nr3 = FormatHelper.FormatTraffic((long)items[2]);

            return $"{ nr1}  { nr2}  { nr3}";
        }

        private void WriteRow(string str)
        {
            str = str.SafePrefix(Console.WindowWidth).PadRight(Console.WindowWidth);

            if (Console.CursorTop < Console.WindowHeight - 1)
                Console.Write(str);
            else
                ; //skip
        }

        public void Dispose()
        {
            lock (lockObj)
            {
                //bufferedGraphics.Dispose();
                //graphics.Dispose();
                Console.CursorVisible = true;
                isDisposed = true;
            }
        }
    }
}
