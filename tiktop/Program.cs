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

                // Auto-save as _last (with encrypted password), unless --no-save was passed
                if (!cfg.NoSave)
                    SaveLastProfile(cfg);

                // If --save-as was requested, save named profile now
                if (cfg.SaveAs != null)
                    SaveNamedProfile(cfg);

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

                    void UpdateStatus()
                    {
                        string winLabel = stack.SortWindow switch {
                            SortWindow.Medium => "/10s",
                            SortWindow.Long   => "/40s",
                            _                 => "",
                        };
                        string sort    = stack.SortMode.ToString() + winLabel;
                        string resolve = visualiser.ResolveMode switch {
                            ResolveMode.DnsService => "dns+svc",
                            ResolveMode.IpPort     => "ip+port",
                            ResolveMode.IpService  => "ip+svc",
                            _                      => "?"
                        };
                        string disp    = visualiser.DisplayMode switch {
                            DisplayMode.TxOnly => " | TX-only",
                            DisplayMode.RxOnly => " | RX-only",
                            _                  => ""
                        };
                        string scale   = visualiser.LogScale    ? " | log"    : "";
                        string bars    = visualiser.ShowBars    ? ""          : " | no-bars";
                        string bits    = visualiser.BitsMode    ? " | bits"   : "";
                        string agg     = stack.AggregateMode switch {
                            AggregateMode.BySrc  => " | agg:src",
                            AggregateMode.ByDst  => " | agg:dst",
                            AggregateMode.ByPort => " | agg:port",
                            _                    => "",
                        };
                        string filter  = !string.IsNullOrEmpty(visualiser.FilterText) ? $" | /{visualiser.FilterText}" : "";
                        string scroll  = visualiser.ScrollOffset > 0 ? $" | ↓{visualiser.ScrollOffset}" : "";
                        string freeze  = visualiser.FreezeOrder ? " | frozen" : "";
                        string pause   = visualiser.Paused      ? " | PAUSED" : "";
                        visualiser.SetStatus(
                            $"sort:{sort} | {resolve}{disp}{scale}{bars}{bits}{agg}{filter}{scroll}{freeze}{pause} | q p 1-3 r a / d t b B L o f j/k ±",
                            ConsoleColor.Green);
                    }

                    UpdateStatus();

                    using var timer = new Timer(_ =>
                    {
                        var snapshot = stack.CreateSnapshot(visualiser.NrOfItems + visualiser.ScrollOffset);
                        visualiser.Draw(snapshot);
                    }, null, 0, 1000);

                    // Main loop: q/Esc to quit, also unblocks on disconnect
                    while (!stopped.Wait(100))
                    {
                        if (!Console.KeyAvailable) continue;
                        var key = Console.ReadKey(intercept: true);

                        // Filter input mode: '/' opens inline filter, Enter confirms, Esc clears.
                        if (key.KeyChar == '/')
                        {
                            var fb = new System.Text.StringBuilder(visualiser.FilterText);
                            // If filter already active, clear it; otherwise enter input mode.
                            if (fb.Length > 0) { visualiser.SetFilter(""); UpdateStatus(); continue; }
                            visualiser.SetStatus("filter: _", ConsoleColor.Yellow);
                            while (!stopped.IsSet)
                            {
                                if (!Console.KeyAvailable) { System.Threading.Thread.Sleep(10); continue; }
                                var fk = Console.ReadKey(intercept: true);
                                if (fk.Key == ConsoleKey.Enter)
                                    break;
                                if (fk.Key == ConsoleKey.Escape)
                                    { fb.Clear(); break; }
                                if (fk.Key == ConsoleKey.Backspace && fb.Length > 0)
                                    fb.Remove(fb.Length - 1, 1);
                                else if (!char.IsControl(fk.KeyChar))
                                    fb.Append(fk.KeyChar);
                                visualiser.SetFilter(fb.ToString());
                                visualiser.SetStatus($"filter: {fb}_", ConsoleColor.Yellow);
                            }
                            visualiser.SetFilter(fb.ToString());
                            UpdateStatus();
                            continue;
                        }

                        switch (key.Key)
                        {
                            case ConsoleKey.Q:
                            case ConsoleKey.Escape:
                                goto exitLoop;

                            case ConsoleKey.P:
                                stack.CycleSortMode();
                                UpdateStatus();
                                break;

                            case ConsoleKey.R:
                                stack.ResetPeaks();
                                break;

                            case ConsoleKey.D:
                                visualiser.CycleResolveMode();
                                UpdateStatus();
                                break;

                            case ConsoleKey.T:
                                visualiser.CycleDisplayMode();
                                UpdateStatus();
                                break;

                            case ConsoleKey.Add:
                            case ConsoleKey.OemPlus:
                                visualiser.AdjustRowCount(+1);
                                break;

                            case ConsoleKey.Subtract:
                            case ConsoleKey.OemMinus:
                                visualiser.AdjustRowCount(-1);
                                break;

                            case ConsoleKey.B:
                                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                                    visualiser.ToggleBitsMode();  // B = bits/bytes
                                else
                                    visualiser.ToggleBars();      // b = bar graph
                                UpdateStatus();
                                break;

                            case ConsoleKey.L:
                                visualiser.ToggleLogScale();
                                UpdateStatus();
                                break;

                            case ConsoleKey.D1:
                            case ConsoleKey.NumPad1:
                                stack.SetSortWindow(SortWindow.Short);
                                UpdateStatus();
                                break;

                            case ConsoleKey.D2:
                            case ConsoleKey.NumPad2:
                                stack.SetSortWindow(SortWindow.Medium);
                                UpdateStatus();
                                break;

                            case ConsoleKey.D3:
                            case ConsoleKey.NumPad3:
                                stack.SetSortWindow(SortWindow.Long);
                                UpdateStatus();
                                break;

                            case ConsoleKey.O:
                                visualiser.ToggleFreezeOrder();
                                UpdateStatus();
                                break;

                            case ConsoleKey.F:
                            case ConsoleKey.Spacebar:
                                visualiser.TogglePause();
                                UpdateStatus();
                                break;

                            case ConsoleKey.A:
                                stack.CycleAggregateMode();
                                visualiser.ResetScroll();
                                UpdateStatus();
                                break;

                            case ConsoleKey.J:
                                visualiser.ScrollDown();
                                UpdateStatus();
                                break;

                            case ConsoleKey.K:
                                visualiser.ScrollUp();
                                UpdateStatus();
                                break;
                        }
                    }
                    exitLoop:

                    // If disconnected, give user a moment to read the status message
                    if (stopped.IsSet)
                        Thread.Sleep(2000);
                }
            }
        }

        private static void SaveLastProfile(ConnectionConfig cfg)
        {
            try { new ProfileManager().Save("_last", cfg, savePassword: true); }
            catch { /* non-fatal */ }
        }

        private static void SaveNamedProfile(ConnectionConfig cfg)
        {
            Console.Write($"Save password in profile '{cfg.SaveAs}'? [y/N]: ");
            bool savePass = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
            try
            {
                new ProfileManager().Save(cfg.SaveAs!, cfg, savePassword: savePass);
                string note = savePass ? " (password encrypted)" : " (no password saved)";
                Console.WriteLine($"Profile '{cfg.SaveAs}' saved.{note}");
                Console.WriteLine($"Location: {ProfileManager.ConfigDir}");
                Thread.Sleep(1500);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: could not save profile: {ex.Message}");
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
