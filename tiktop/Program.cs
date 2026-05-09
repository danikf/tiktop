using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using tiktop.Data;

namespace tiktop
{
    class Program
    {
        static void Main(string[] args)
        {
            DataStack stack = new DataStack();
            using (var mikrotik = new MikrotikWrapper("192.168.1.1", "danik", "secret"))
            {
                using (var visualiser = new Visualiser())
                {
                    mikrotik.StartListening("ether1 - WAN", responseWords => stack.AddRow(responseWords));

                    Timer timer = new Timer(obj =>
                    {
                        var snapshot = stack.CreateSnapshot(visualiser.NrOfItems);
                        visualiser.Draw(snapshot);
                    }, null, 0, 1000);

                    Console.ReadKey();
                }
            }
        }
    }
}
