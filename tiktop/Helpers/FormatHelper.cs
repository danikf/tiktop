using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tiktop.Helpers
{
    public static class FormatHelper
    {
        // isRate=true  → append "s" to unit (e.g. "MBs" = MB/s) for per-second columns
        // isRate=false → plain unit (e.g. "MB")  for cumulative total column
        public static string FormatTraffic(long bps, bool bits = false, bool padRight = false, bool isRate = true)
        {
            double tmpNr = bits ? bps * 8.0 : bps;
            string[] sizes = bits
                ? new[] { "b", "Kb", "Mb", "Gb", "Tb" }
                : new[] { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            while (tmpNr >= 1024 && order < sizes.Length - 1)
            {
                order++;
                tmpNr /= 1024;
            }

            string unit   = sizes[order] + (isRate ? "s" : "");
            // Fill all available chars with digits so PadLeft never adds a leading space
            // that would make the column width appear inconsistent across rows.
            int    numLen = 7 - unit.Length;
            string result = tmpNr.ToString($"F{numLen - 2}").SafePrefix(numLen) + unit;
            if (padRight)
                return result.PadRight(7);
            else
                return result.PadLeft(7);
        }

        //public static long BpsFromMikrotikFormat(string bps)
        //{
        //    return 1000;
        //}

        /// <summary>
        /// Shortens a hostname to fit within maxLen characters.
        /// Prefers keeping the last two domain components ("…compute.amazonaws.com")
        /// over hard truncation from the right.
        /// </summary>
        public static string ShortenHostname(string name, int maxLen)
        {
            if (name.Length <= maxLen) return name;

            var parts = name.Split('.');
            if (parts.Length >= 2)
            {
                var suffix = ">" + parts[^2] + "." + parts[^1];   // "…second-to-last.last"
                if (suffix.Length <= maxLen) return suffix;
            }

            return name[..maxLen];
        }

        /// <summary>
        /// Returns ":servicename" for well-known ports, or ":portnumber" for others.
        /// Returns "" for port "0" or empty.
        /// </summary>
        public static string FormatPort(string port)
        {
            if (string.IsNullOrEmpty(port) || port == "0") return "";
            string name = port switch
            {
                "20"   => "ftp-data",
                "21"   => "ftp",
                "22"   => "ssh",
                "23"   => "telnet",
                "25"   => "smtp",
                "53"   => "dns",
                "67"   => "dhcp",
                "80"   => "http",
                "110"  => "pop3",
                "123"  => "ntp",
                "143"  => "imap",
                "179"  => "bgp",
                "389"  => "ldap",
                "443"  => "https",
                "465"  => "smtps",
                "500"  => "ike",
                "587"  => "submission",
                "636"  => "ldaps",
                "993"  => "imaps",
                "995"  => "pop3s",
                "1194" => "ovpn",
                "1723" => "pptp",
                "3306" => "mysql",
                "3389" => "rdp",
                "5060" => "sip",
                "5432" => "pgsql",
                "8080" => "http-alt",
                "8443" => "https-alt",
                _      => port,
            };
            return ":" + name;
        }

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
