using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DnsClient;
using tiktop.Data;
using tiktop.Helpers;

namespace tiktop
{
    class Program
    {
        static void Main(string[] args)
        {
            var cfg = ConnectionConfig.FromArgs(args);

            var dnsOptions = cfg.DnsServer != null
                ? new LookupClientOptions(IPAddress.Parse(cfg.DnsServer)) { Timeout = TimeSpan.FromSeconds(1) }
                : new LookupClientOptions { Timeout = TimeSpan.FromSeconds(1) };
            var dnsCache = new DnsCache(new LookupClient(dnsOptions));

            MikrotikWrapper mikrotik;
            try
            {
                mikrotik = new MikrotikWrapper(cfg.Host, cfg.User, cfg.Pass, cfg.UseSsl, cfg.ResolvedPort);
            }
            catch (Exception ex)
            {
                ShowFatalError("Connection failed", GetFriendlyError(ex));
                Environment.Exit(1);
                return;
            }

            using (mikrotik)
            {
                IReadOnlyList<IPNetwork> localNetworks;
                try
                {
                    localNetworks = mikrotik.GetLocalNetworks(cfg.Interface);
                }
                catch (Exception ex)
                {
                    ShowFatalError("Failed to query interfaces", GetFriendlyError(ex));
                    Environment.Exit(1);
                    return;
                }

                var stack = new DataStack(localNetworks);
                var stopped = new ManualResetEventSlim(false);

                using (var visualiser = new Visualiser(dnsCache))
                {
                    mikrotik.StartListening(
                        cfg.Interface,
                        torch => stack.AddRow(torch),
                        ex =>
                        {
                            visualiser.SetStatus($"Disconnected – {GetFriendlyError(ex)}", ConsoleColor.Red);
                            stopped.Set();
                        });

                    using var timer = new Timer(_ =>
                    {
                        var snapshot = stack.CreateSnapshot(visualiser.NrOfItems);
                        visualiser.Draw(snapshot);
                    }, null, 0, 1000);

                    // Main loop: q/Esc to quit, also unblocks on disconnect
                    while (!stopped.Wait(100))
                    {
                        if (!Console.KeyAvailable) continue;
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.Escape)
                            break;
                    }

                    // If disconnected, give user a moment to read the status message
                    if (stopped.IsSet)
                        Thread.Sleep(2000);
                }
            }
        }

        private static string GetFriendlyError(Exception ex)
        {
            // Unwrap aggregate / inner exceptions
            var inner = ex.InnerException ?? ex;
            string msg = inner.Message;

            if (inner is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused  => "Connection refused — is the RouterOS API service enabled?",
                    SocketError.TimedOut           => "Connection timed out — check the host address and firewall rules",
                    SocketError.HostNotFound       => "Host not found — check the address",
                    SocketError.NetworkUnreachable => "Network unreachable — check routing",
                    _                              => $"Network error ({se.SocketErrorCode}): {msg}"
                };
            }

            // tik4net wraps auth failures in the message text
            if (msg.IndexOf("not logged in",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("invalid user",     StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("wrong password",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("login failure",    StringComparison.OrdinalIgnoreCase) >= 0)
                return "Authentication failed — check username and password";

            if (msg.IndexOf("ssl",              StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("tls",              StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("certificate",      StringComparison.OrdinalIgnoreCase) >= 0)
                return $"SSL/TLS error: {msg}  (try --no-ssl)";

            return msg;
        }

        private static void ShowFatalError(string title, string detail)
        {
            Console.ResetColor();
            Console.WriteLine();

            int W = Math.Min(Console.WindowWidth, 80);
            string border = new string('─', W - 2);

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"┌{border}┐");
            Console.WriteLine($"│  {("ERROR: " + title).PadRight(W - 4)}  │");
            Console.WriteLine($"├{border}┤");

            // Word-wrap detail
            foreach (var line in WordWrap(detail, W - 6))
                Console.WriteLine($"│  {line.PadRight(W - 4)}  │");

            Console.WriteLine($"└{border}┘");
            Console.ResetColor();
        }

        private static System.Collections.Generic.IEnumerable<string> WordWrap(string text, int width)
        {
            if (width <= 0) { yield return text; yield break; }
            while (text.Length > width)
            {
                int split = text.LastIndexOf(' ', width);
                if (split <= 0) split = width;
                yield return text[..split].TrimEnd();
                text = text[split..].TrimStart();
            }
            yield return text;
        }
    }
}
