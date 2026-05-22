using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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
            // Auto-detect: when neither --ssl nor --no-ssl nor a custom --port was given,
            // try SSL first (3 s), then plain, before giving up.
            bool autoDetect = !cfg.SslExplicit && cfg.Port == null;
            if (autoDetect)
            {
                const int AutoTimeoutMs = 3000;
                try
                {
                    mikrotik = TryConnect(cfg.Host, cfg.User, cfg.Pass, useSsl: true, port: 8729, AutoTimeoutMs);
                }
                catch (Exception sslEx) when (ShouldTryFallback(sslEx))
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"  SSL failed ({GetShortError(sslEx)}), trying plain… ");
                    Console.ResetColor();
                    try
                    {
                        mikrotik = TryConnect(cfg.Host, cfg.User, cfg.Pass, useSsl: false, port: 8728, AutoTimeoutMs);
                        cfg.UseSsl = false;
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("connected.");
                        Console.WriteLine("  Note: plain (unencrypted) API — consider enabling API-SSL.");
                        Console.ResetColor();
                    }
                    catch (Exception plainEx)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"failed ({GetShortError(plainEx)}).");
                        Console.ResetColor();
                        Console.WriteLine();
                        ShowFatalError("Connection failed",
                            $"Could not connect to {cfg.Host} — tried SSL (port 8729) and plain API (port 8728).");
                        ShowApiSetupHelp(ssl: true);
                        Environment.Exit(1);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Non-retriable: auth failure, host not found, etc.
                    ShowFatalError("Connection failed", GetFriendlyError(ex));
                    Environment.Exit(1);
                    return;
                }
            }
            else
            {
                try
                {
                    mikrotik = new MikrotikWrapper(cfg.Host, cfg.User, cfg.Pass, cfg.UseSsl, cfg.ResolvedPort);
                }
                catch (Exception ex)
                {
                    ShowFatalError("Connection failed", GetFriendlyError(ex));
                    ShowApiSetupHelp(ssl: cfg.UseSsl);
                    Environment.Exit(1);
                    return;
                }
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
                        visualiser.SetSortState(stack.SortMode, stack.SortWindow, stack.AggregateMode);
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

                        // '?' toggles the help overlay
                        if (key.KeyChar == '?')
                        {
                            visualiser.ToggleHelp();
                            continue;
                        }

                        // While help is shown: only Esc (close) and Q (quit) are active
                        if (visualiser.HelpMode)
                        {
                            if (key.Key == ConsoleKey.Escape) visualiser.ToggleHelp();
                            else if (key.Key == ConsoleKey.Q) goto exitLoop;
                            continue;
                        }

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

                            case ConsoleKey.D4:
                            case ConsoleKey.NumPad4:
                                stack.SetSortWindow(SortWindow.Cumulative);
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
            try { new ProfileManager().Save("_last", cfg, savePassword: !cfg.SaveNoPass); }
            catch { /* non-fatal */ }
        }

        private static void SaveNamedProfile(ConnectionConfig cfg)
        {
            bool savePass;
            if (cfg.SaveNoPass)
            {
                savePass = false;
            }
            else
            {
                Console.Write($"Save password in profile '{cfg.SaveAs}'? [y/N]: ");
                savePass = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
            }
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

        // Run a blocking connection attempt on a thread-pool thread and enforce a wall-clock timeout.
        // If the timeout fires first we dispose the connection if it eventually arrives (cleanup).
        private static MikrotikWrapper TryConnect(string host, string user, string pass, bool useSsl, int port, int timeoutMs)
        {
            Exception? caught = null;
            MikrotikWrapper? wrapper = null;

            var task = Task.Run(() =>
            {
                try { wrapper = new MikrotikWrapper(host, user, pass, useSsl, port); }
                catch (Exception ex) { caught = ex; }
            });

            if (!task.Wait(timeoutMs))
            {
                task.ContinueWith(_ => wrapper?.Dispose());
                throw new TimeoutException("Connection timed out");
            }

            if (caught != null) throw caught;
            return wrapper!;
        }

        // Returns true when a fallback to the other SSL mode is worth attempting.
        private static bool ShouldTryFallback(Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            string msg = inner.Message;

            // Credentials are wrong regardless of SSL mode — no point retrying.
            if (msg.IndexOf("not logged in",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("invalid user",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("wrong password",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("login failure",   StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            // Routing/DNS issues affect both ports equally.
            if (inner is SocketException se)
                return se.SocketErrorCode != SocketError.HostNotFound &&
                       se.SocketErrorCode != SocketError.NetworkUnreachable;

            // Timeout, connection refused, SSL errors → try the other mode.
            return true;
        }

        private static string GetShortError(Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            if (inner is TimeoutException) return "timed out";
            if (inner is SocketException se)
                return se.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused  => "connection refused",
                    SocketError.TimedOut           => "timed out",
                    SocketError.HostNotFound       => "host not found",
                    SocketError.NetworkUnreachable => "network unreachable",
                    _                              => se.SocketErrorCode.ToString()
                };
            if (inner.Message.IndexOf("ssl",         StringComparison.OrdinalIgnoreCase) >= 0 ||
                inner.Message.IndexOf("tls",         StringComparison.OrdinalIgnoreCase) >= 0 ||
                inner.Message.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0)
                return "SSL/TLS error";
            string s = inner.Message;
            return s.Length > 60 ? s[..60] + "…" : s;
        }

        private static void ShowApiSetupHelp(bool ssl)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (ssl)
            {
                Console.WriteLine("  RouterOS API-SSL setup (run in Winbox terminal or SSH):");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  1) Create a self-signed certificate:");
                Console.WriteLine("       /certificate add name=api-ssl common-name=api-ssl \\");
                Console.WriteLine("         key-usage=digital-signature,key-encipherment days-valid=3650");
                Console.WriteLine("       /certificate sign api-ssl");
                Console.WriteLine();
                Console.WriteLine("  2) Enable API-SSL service on port 8729:");
                Console.WriteLine("       /ip service set api-ssl port=8729 certificate=api-ssl disabled=no");
                Console.WriteLine();
                Console.WriteLine("  3) (optional) Restrict to your management network:");
                Console.WriteLine("       /ip service set api-ssl address=<mgmt-subnet>/24");
                Console.WriteLine();
                Console.WriteLine("  After setup, retry:   tiktop");
                Console.WriteLine("  Or without SSL:       tiktop --no-ssl");
            }
            else
            {
                Console.WriteLine("  RouterOS plain API setup (run in Winbox terminal or SSH):");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  1) Enable the API service on port 8728:");
                Console.WriteLine("       /ip service set api port=8728 disabled=no");
                Console.WriteLine();
                Console.WriteLine("  2) (optional) Restrict to your management network:");
                Console.WriteLine("       /ip service set api address=<mgmt-subnet>/24");
                Console.WriteLine();
                Console.WriteLine("  After setup, retry:   tiktop --no-ssl");
                Console.WriteLine("  Or with SSL instead:  tiktop");
            }
            Console.ResetColor();
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
