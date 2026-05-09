using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Helpers
{
    public static class FormatHelper
    {
        public static string FormatTraffic(long bps, bool padRight = false)
        {
            //https://stackoverflow.com/questions/281640/how-do-i-get-a-human-readable-file-size-in-bytes-abbreviation-using-net
            double tmpNr = bps;

            string[] sizes = { "b", "Kb", "Mb", "Gb", "Tb" };
            int order = 0;
            while (tmpNr >= 1024 && order < sizes.Length - 1)
            {
                order++;
                tmpNr = tmpNr / 1024;
            }

            string result = tmpNr.ToString().SafePrefix(4) + sizes[order];
            if (padRight)
                return result.PadRight(6);
            else
                return result.PadLeft(6);
        }

        //public static long BpsFromMikrotikFormat(string bps)
        //{
        //    return 1000;
        //}

        public static string SafePrefix(this string str, int length)
        {
            if (string.IsNullOrEmpty(str))
                return str;
            else if (str.Length <= length)
                return str; 
            else
                return str.Substring(0, length);
        }
    }
}
