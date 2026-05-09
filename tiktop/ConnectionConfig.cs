using System;
using System.Collections.Generic;
using System.Linq;
using tik4net;

namespace tiktop
{
    public class ConnectionConfig
    {
        public string Host { get; set; } = "";
        public string User { get; set; } = "";
        public string Pass { get; set; } = "";
        public string Interface { get; set; } = "";
        public bool UseSsl { get; set; } = true;
        public int? Port { get; set; }
        public int? Count { get; set; }
        public string? DnsServer { get; set; }

        public int ResolvedPort => Port ?? (UseSsl ? 8729 : 8728);

        public static ConnectionConfig FromArgs(string[] args)
        {
            var cfg = new ConnectionConfig();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h":
                    case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;

                    case "-H":
                    case "--host":
                        cfg.Host = NextArg(args, ref i, "--host");
                        break;

                    case "-u":
                    case "--user":
                        cfg.User = NextArg(args, ref i, "--user");
                        break;

                    case "-p":
                    case "--pass":
                        cfg.Pass = NextArg(args, ref i, "--pass");
                        break;

                    case "-i":
                    case "--interface":
                        cfg.Interface = NextArg(args, ref i, "--interface");
                        break;

                    case "--port":
                        cfg.Port = int.Parse(NextArg(args, ref i, "--port"));
                        break;

                    case "--no-ssl":
                        cfg.UseSsl = false;
                        break;

                    case "-n":
                    case "--count":
                        cfg.Count = int.Parse(NextArg(args, ref i, "--count"));
                        break;

                    case "-d":
                    case "--dns-server":
                        cfg.DnsServer = NextArg(args, ref i, "--dns-server");
                        break;

                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        PrintHelp();
                        Environment.Exit(1);
                        break;
                }
            }

            PromptMissing(cfg);
            return cfg;
        }

        private static void PromptMissing(ConnectionConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.Host))
            {
                Console.Write("Host (router IP/hostname): ");
                cfg.Host = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(cfg.User))
            {
                Console.Write("Username: ");
                cfg.User = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(cfg.Pass))
            {
                Console.Write("Password: ");
                cfg.Pass = ReadMasked();
                Console.WriteLine();
            }

            if (string.IsNullOrWhiteSpace(cfg.Interface))
            {
                cfg.Interface = PickInterface(cfg);
            }
        }

        private static string PickInterface(ConnectionConfig cfg)
        {
            var interfaces = FetchInterfaces(cfg);

            if (interfaces.Count == 0)
            {
                Console.Write("Interface [ether1]: ");
                var fallback = Console.ReadLine()?.Trim() ?? "";
                return string.IsNullOrEmpty(fallback) ? "ether1" : fallback;
            }

            // prefer interface whose default-name is "ether1"
            int defaultIndex = interfaces.FindIndex(x => x.DefaultName == "ether1");
            if (defaultIndex < 0) defaultIndex = 0;

            Console.WriteLine("Available interfaces:");
            for (int i = 0; i < interfaces.Count; i++)
            {
                var (name, defaultName) = interfaces[i];
                var label = name == defaultName ? name : $"{name}  (default-name: {defaultName})";
                Console.WriteLine($"  {i + 1}) {label}");
            }

            Console.Write($"Select interface [{defaultIndex + 1}]: ");
            var input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(input))
                return interfaces[defaultIndex].Name;

            if (int.TryParse(input, out int choice) && choice >= 1 && choice <= interfaces.Count)
                return interfaces[choice - 1].Name;

            // user typed a name directly
            return input;
        }

        private static List<(string Name, string DefaultName)> FetchInterfaces(ConnectionConfig cfg)
        {
            try
            {
                var connType = cfg.UseSsl ? TikConnectionType.ApiSsl : TikConnectionType.Api;
                using var conn = ConnectionFactory.OpenConnection(connType, cfg.Host, cfg.ResolvedPort, cfg.User, cfg.Pass);
                var rows = conn.CreateCommand("/interface/print").ExecuteList("name", "default-name");
                return rows
                    .Select(r => (
                        r.GetResponseField("name"),
                        r.GetResponseFieldOrDefault("default-name", r.GetResponseField("name"))
                    ))
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: could not fetch interface list ({ex.Message})");
                return new List<(string, string)>();
            }
        }

        private static string ReadMasked()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Remove(sb.Length - 1, 1);
                }
                else
                {
                    sb.Append(key.KeyChar);
                }
            }
            return sb.ToString();
        }

        private static string NextArg(string[] args, ref int i, string name)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {name}");
                Environment.Exit(1);
            }
            return args[++i];
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: tiktop [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -H, --host <ip>        Router IP or hostname (required)");
            Console.WriteLine("  -u, --user <name>      Username (required)");
            Console.WriteLine("  -p, --pass <password>  Password (prompted if omitted)");
            Console.WriteLine("  -i, --interface <name> Interface to monitor (prompted if omitted, default: ether1)");
            Console.WriteLine("      --port <port>      API port (default: 8729 SSL / 8728 plain)");
            Console.WriteLine("      --no-ssl           Use plain (non-SSL) API connection");
            Console.WriteLine("  -n, --count <n>        Number of rows to display (default: auto from window height)");
            Console.WriteLine("  -d, --dns-server <ip>  DNS server for reverse lookups (default: system DNS)");
            Console.WriteLine("  -h, --help             Show this help and exit");
        }
    }
}
