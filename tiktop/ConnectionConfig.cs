using System;
using System.Collections.Generic;
using System.Linq;
using tik4net;

namespace tiktop
{
    public class ConnectionConfig
    {
        public string Host      { get; set; } = "";
        public string User      { get; set; } = "";
        public string Pass      { get; set; } = "";
        public string Interface { get; set; } = "";
        public bool   UseSsl    { get; set; } = true;
        /// <summary>True when --no-ssl (or --ssl) was explicitly passed on the command line.</summary>
        public bool   SslExplicit { get; private set; }
        public int?   Port      { get; set; }
        public int?   Count     { get; set; }
        public string? DnsServer { get; set; }

        public bool   SwapDirection { get; set; }
        public bool   Debug        { get; private set; }

        // Profile actions (not part of the connection itself)
        public string? ProfileName  { get; private set; }
        public string? SaveAs       { get; private set; }
        /// <summary>When true, the connection is NOT auto-saved to the host profile.</summary>
        public bool    NoSave       { get; private set; }
        /// <summary>When true, auto-save and --save-as omit the password.</summary>
        public bool    SaveNoPass   { get; private set; }
        /// <summary>When true, always show the profile picker even when auto-connect would fire.</summary>
        public bool    PickProfile  { get; private set; }

        public int ResolvedPort => Port ?? (UseSsl ? 8729 : 8728);

        // ── Entry point ───────────────────────────────────────────────────────

        public static ConnectionConfig FromArgs(string[] args)
        {
            var cfg      = new ConnectionConfig();
            var profiles = new ProfileManager();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h": case "--help": case "-?": case "/?":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    case "--reset":
                        profiles.Reset();
                        Console.WriteLine("All saved profiles deleted.");
                        Environment.Exit(0);
                        break;
                    case "-H": case "--host":
                        ParseHostArg(NextArg(args, ref i, "--host"), cfg);
                        break;
                    case "-u": case "--user":
                        cfg.User = NextArg(args, ref i, "--user");
                        break;
                    case "-p": case "--pass":
                        cfg.Pass = NextArg(args, ref i, "--pass");
                        break;
                    case "-i": case "--interface":
                        cfg.Interface = NextArg(args, ref i, "--interface");
                        break;
                    case "--port":
                        cfg.Port = int.Parse(NextArg(args, ref i, "--port"));
                        break;
                    case "--no-ssl":
                        cfg.UseSsl = false;
                        cfg.SslExplicit = true;
                        break;
                    case "--ssl":
                        cfg.UseSsl = true;
                        cfg.SslExplicit = true;
                        break;
                    case "-n": case "--count":
                        cfg.Count = int.Parse(NextArg(args, ref i, "--count"));
                        break;
                    case "-d": case "--dns-server":
                        cfg.DnsServer = NextArg(args, ref i, "--dns-server");
                        break;
                    case "--profile":
                        cfg.ProfileName = NextArg(args, ref i, "--profile");
                        break;
                    case "--save-as":
                        cfg.SaveAs = NextArg(args, ref i, "--save-as");
                        break;
                    case "--no-save":
                    case "--private":
                        cfg.NoSave = true;
                        break;
                    case "--save-no-pass":
                        cfg.SaveNoPass = true;
                        break;
                    case "--pick-profile":
                        cfg.PickProfile = true;
                        break;
                    case "--debug":
                        cfg.Debug = true;
                        break;
                    case "--list-profiles":
                        ListProfiles(profiles);
                        Environment.Exit(0);
                        break;
                    case "--delete-profile":
                        DeleteProfile(profiles, NextArg(args, ref i, "--delete-profile"));
                        Environment.Exit(0);
                        break;
                    default:
                        // First non-flag positional argument → host (optionally with port)
                        if (!args[i].StartsWith("-") && string.IsNullOrEmpty(cfg.Host))
                            ParseHostArg(args[i], cfg);
                        else
                        {
                            Console.Error.WriteLine($"Unknown argument: {args[i]}");
                            PrintHelp();
                            Environment.Exit(1);
                        }
                        break;
                }
            }

            // Infer SSL mode from well-known ports when not set explicitly by a flag
            if (!cfg.SslExplicit && cfg.Port.HasValue)
            {
                if      (cfg.Port == 8729) { cfg.UseSsl = true;  cfg.SslExplicit = true; }
                else if (cfg.Port == 8728) { cfg.UseSsl = false; cfg.SslExplicit = true; }
            }

            // Explicit --profile: apply and fill any remaining missing fields
            if (cfg.ProfileName != null)
            {
                var profile = profiles.Get(cfg.ProfileName);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Profile '{cfg.ProfileName}' not found.");
                    ListProfiles(profiles);
                    Environment.Exit(1);
                }
                cfg.ApplyProfile(profile!, applyPassword: true);
                PromptMissing(cfg);
                return cfg;
            }

            // Private / no-save mode: skip profile system entirely
            if (cfg.NoSave)
            {
                PromptMissing(cfg);
                return cfg;
            }

            // Normal startup: profile-based
            StartWithProfiles(cfg, profiles);
            return cfg;
        }

        // ── Profile-based startup ─────────────────────────────────────────────

        private static void StartWithProfiles(ConnectionConfig cfg, ProfileManager profiles)
        {
            // Host already known from CLI → load its profile directly, no picker needed
            if (!string.IsNullOrEmpty(cfg.Host))
            {
                TryApplyHostProfile(cfg, profiles);
                PromptMissing(cfg);
                return;
            }

            var sorted = profiles.GetProfilesSorted();

            // No saved profiles → prompt host first, then try to load its profile
            if (sorted.Count == 0)
            {
                cfg.Host = PromptField("Host", "", null);
                TryApplyHostProfile(cfg, profiles);
                PromptMissing(cfg);
                return;
            }

            // One or more profiles → always show picker so user can choose or create new
            var chosen = ShowProfilePicker(sorted);
            if (chosen == null)
            {
                // <NEW>: prompt host, then try to load its profile
                cfg.Host = PromptField("Host", "", null);
                TryApplyHostProfile(cfg, profiles);
            }
            else
            {
                cfg.ApplyProfile(chosen, applyPassword: true);
            }
            PromptMissing(cfg);
        }

        // If a profile named cfg.Host exists, merge it into cfg (CLI args already take precedence).
        private static void TryApplyHostProfile(ConnectionConfig cfg, ProfileManager profiles)
        {
            if (string.IsNullOrEmpty(cfg.Host)) return;
            var p = profiles.Get(cfg.Host);
            if (p != null) cfg.ApplyProfile(p, applyPassword: true);
        }

        // Returns the chosen StoredProfile, or null for <NEW>.
        private static StoredProfile? ShowProfilePicker(
            List<(string Name, StoredProfile Profile)> sortedProfiles)
        {
            var items = new List<(string Label, StoredProfile? Profile)>();

            foreach (var (name, p) in sortedProfiles)
            {
                string pass  = p.PasswordProtected != null ? " [pass]" : "";
                string iface = p.Interface != null ? $"  {p.Interface}" : "";
                string date  = p.SavedAt.HasValue ? $"  {p.SavedAt.Value:yyyy-MM-dd}" : "";
                items.Add(($"{name,-18} {p.User}{iface}{pass}{date}", p));
            }

            items.Add(("<NEW>  enter a new host", null));

            Console.WriteLine("Profiles:");
            for (int i = 0; i < items.Count; i++)
                Console.WriteLine($"  {i + 1}) {items[i].Label}");

            Console.Write("Select [1]: ");
            string sel = Console.ReadLine()?.Trim() ?? "";

            if (!int.TryParse(sel, out int choice) || choice < 1 || choice > items.Count)
                choice = 1;

            return items[choice - 1].Profile;
        }

        // Apply a stored profile to any fields not already set via CLI
        public void ApplyProfile(StoredProfile p, bool applyPassword = false)
        {
            if (string.IsNullOrEmpty(Host)      && p.Host      != null) Host      = p.Host;
            if (string.IsNullOrEmpty(User)      && p.User      != null) User      = p.User;
            if (string.IsNullOrEmpty(Interface) && p.Interface != null) Interface = p.Interface;
            if (Port      == null && p.Port      != null) Port      = p.Port;
            if (DnsServer == null && p.DnsServer != null) DnsServer = p.DnsServer;
            if (Count     == null && p.Count     != null) Count     = p.Count;
            if (!SslExplicit) UseSsl = p.UseSsl;

            SwapDirection = p.SwapDirection;

            if (applyPassword && string.IsNullOrEmpty(Pass) && p.PasswordProtected != null)
                Pass = ProfileManager.Decrypt(p.PasswordProtected) ?? "";
        }

        // ── Interactive prompts ───────────────────────────────────────────────

        private static void PromptMissing(ConnectionConfig cfg)
        {
            cfg.Host = PromptField("Host",     cfg.Host, null);
            cfg.User = PromptField("Username", cfg.User, null);

            if (string.IsNullOrWhiteSpace(cfg.Pass))
            {
                string context = !string.IsNullOrEmpty(cfg.User) && !string.IsNullOrEmpty(cfg.Host)
                    ? $" [{cfg.User}@{cfg.Host}]"
                    : "";
                Console.Write($"Password{context}: ");
                cfg.Pass = ReadMasked();
                Console.WriteLine();
            }

            if (string.IsNullOrWhiteSpace(cfg.Interface))
                cfg.Interface = PickInterface(cfg, null);
        }

        private static string PromptField(string label, string current, string? defaultVal)
        {
            if (!string.IsNullOrWhiteSpace(current)) return current;

            if (!string.IsNullOrEmpty(defaultVal))
            {
                Console.Write($"{label} [{defaultVal}]: ");
                string input = Console.ReadLine()?.Trim() ?? "";
                return string.IsNullOrEmpty(input) ? defaultVal : input;
            }

            Console.Write($"{label}: ");
            return Console.ReadLine()?.Trim() ?? "";
        }

        // ── Interface picker ──────────────────────────────────────────────────

        private static string PickInterface(ConnectionConfig cfg, string? defaultIface)
        {
            List<(string Name, string DefaultName)> interfaces;
            while (true)
            {
                try
                {
                    interfaces = FetchInterfaces(cfg);
                    break;
                }
                catch (Exception ex) when (IsAuthError(ex))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Authentication failed: {(ex.InnerException ?? ex).Message}");
                    Console.ResetColor();
                    Console.Write($"Username [{cfg.User}]: ");
                    string u = Console.ReadLine()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(u)) cfg.User = u;
                    Console.Write("Password: ");
                    cfg.Pass = ReadMasked();
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    // Connection failed and no fallback is available — aborting is safer than
                    // prompting for an interface name that can never be verified.
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Cannot connect to {cfg.Host}: {(ex.InnerException ?? ex).Message}");
                    Console.ResetColor();
                    Environment.Exit(1);
                    return ""; // unreachable
                }
            }

            if (interfaces.Count == 0)
                return PromptField("Interface", "", defaultIface ?? "ether1");

            int defaultIndex = defaultIface != null
                ? interfaces.FindIndex(x => x.Name == defaultIface)
                : interfaces.FindIndex(x => x.DefaultName == "ether1");
            if (defaultIndex < 0) defaultIndex = 0;

            Console.WriteLine("Available interfaces:");
            for (int i = 0; i < interfaces.Count; i++)
            {
                var (name, defName) = interfaces[i];
                string label  = name == defName ? name : $"{name}  (default-name: {defName})";
                string marker = i == defaultIndex ? "* " : "  ";
                Console.WriteLine($" {marker}{i + 1}) {label}");
            }

            Console.Write($"Select interface [{defaultIndex + 1}]: ");
            string sel = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(sel))
                return interfaces[defaultIndex].Name;
            if (int.TryParse(sel, out int choice) && choice >= 1 && choice <= interfaces.Count)
                return interfaces[choice - 1].Name;
            return sel;
        }

        private static List<(string Name, string DefaultName)> FetchInterfaces(ConnectionConfig cfg)
        {
            // Local helper — opens a fresh connection and lists interfaces.
            List<(string Name, string DefaultName)> DoFetch(bool useSsl, int port)
            {
                var connType = useSsl ? TikConnectionType.ApiSsl : TikConnectionType.Api;
                // tik4net 4.0: self-signed RouterOS certs are rejected unless opted in explicitly.
                var setup = new TikConnectionSetup(cfg.Host, cfg.User, cfg.Pass)
                {
                    Port = port,
                    AllowInvalidCertificate = true,
                };
                using var conn = setup.Create(connType);
                var rows = conn.CreateCommand("/interface/print").ExecuteList("name", "default-name");
                return rows
                    .Select(r => (
                        r.GetResponseField("name"),
                        r.GetResponseFieldOrDefault("default-name", r.GetResponseField("name"))
                    ))
                    .ToList();
            }

            bool autoDetect = !cfg.SslExplicit && cfg.Port == null;

            try
            {
                return DoFetch(cfg.UseSsl, cfg.ResolvedPort);
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                throw; // PickInterface re-prompts credentials
            }
            catch (Exception ex) when (autoDetect && MikrotikWrapper.ShouldTryFallback(ex))
            {
                // Primary mode failed — try the opposite SSL mode (mirrors main auto-detect)
                bool fallbackSsl  = !cfg.UseSsl;
                int  fallbackPort = fallbackSsl ? 8729 : 8728;
                try
                {
                    var result = DoFetch(fallbackSsl, fallbackPort);
                    // Fallback worked: update config so the main connection skips auto-detect
                    cfg.UseSsl     = fallbackSsl;
                    cfg.SslExplicit = true;
                    return result;
                }
                catch (Exception inner) when (IsAuthError(inner))
                {
                    throw; // PickInterface re-prompts credentials
                }
                // Fallback connection failure propagates → PickInterface aborts
            }
            // Explicit mode connection failure propagates → PickInterface aborts
        }

        private static bool IsAuthError(Exception ex)
        {
            string msg = (ex.InnerException ?? ex).Message;
            return msg.IndexOf("not logged in",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("invalid user",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("wrong password", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   msg.IndexOf("login failure",  StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ── Profile management helpers ────────────────────────────────────────

        private static void ListProfiles(ProfileManager profiles)
        {
            var sorted = profiles.GetProfilesSorted();
            if (sorted.Count == 0)
            {
                Console.WriteLine("No saved profiles.");
                Console.WriteLine($"Use --save-as <name> to create one. Config dir: {ProfileManager.ConfigDir}");
                return;
            }

            Console.WriteLine("Saved profiles:");
            foreach (var (name, p) in sorted)
            {
                string marker   = name == profiles.LastUsedName ? "*" : " ";
                string passNote = p.PasswordProtected != null ? " [pass]" : "";
                string iface    = p.Interface != null ? $"  {p.Interface}" : "";
                string date     = p.SavedAt.HasValue ? $"  ({p.SavedAt.Value:yyyy-MM-dd})" : "";
                Console.WriteLine($"  {marker} {name,-20} {p.User}{iface}{passNote}{date}");
            }
            Console.WriteLine($"\nConfig: {ProfileManager.ConfigDir}");
        }

        private static void DeleteProfile(ProfileManager profiles, string name)
        {
            if (profiles.Get(name) == null)
            {
                Console.Error.WriteLine($"Profile '{name}' not found.");
                Environment.Exit(1);
            }
            profiles.Delete(name);
            Console.WriteLine($"Profile '{name}' deleted.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string ReadMasked()
        {
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                { if (sb.Length > 0) sb.Remove(sb.Length - 1, 1); }
                else
                { sb.Append(key.KeyChar); }
            }
            return sb.ToString();
        }

        // Parses "host", "host:port", or "[ipv6]:port" and writes results into cfg.
        // Existing cfg.Port is only overwritten when the arg itself contains a port.
        private static void ParseHostArg(string arg, ConnectionConfig cfg)
        {
            // [::1]:8729  — bracketed IPv6 with optional port
            if (arg.StartsWith("["))
            {
                int close = arg.IndexOf(']');
                if (close > 0 && close + 1 < arg.Length && arg[close + 1] == ':' &&
                    int.TryParse(arg[(close + 2)..], out int p6))
                {
                    cfg.Host = arg[1..close];
                    cfg.Port = p6;
                }
                else
                {
                    cfg.Host = close > 0 ? arg[1..close] : arg;
                }
                return;
            }

            // host:port — single colon only (bare IPv6 has more than one)
            int colon = arg.IndexOf(':');
            if (colon > 0 && arg.IndexOf(':', colon + 1) < 0 &&
                int.TryParse(arg[(colon + 1)..], out int p))
            {
                cfg.Host = arg[..colon];
                cfg.Port = p;
                return;
            }

            cfg.Host = arg;
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
            Console.WriteLine("Usage: tiktop [<host>[:<port>]] [options]");
            Console.WriteLine();
            Console.WriteLine("Connection:");
            Console.WriteLine("  -H, --host <host>[:<port>]  Router IP/hostname; port optional (e.g. 192.168.1.1:8728)");
            Console.WriteLine("  -u, --user <name>           Username");
            Console.WriteLine("  -p, --pass <password>       Password (prompted if omitted)");
            Console.WriteLine("  -i, --interface <name>      Interface to monitor");
            Console.WriteLine("      --port <port>           API port (default: 8729 SSL / 8728 plain)");
            Console.WriteLine("      --ssl                   Force SSL connection (skip auto-detect)");
            Console.WriteLine("      --no-ssl                Force plain (non-SSL) connection (skip auto-detect)");
            Console.WriteLine("  -n, --count <n>             Number of rows to display");
            Console.WriteLine("  -d, --dns-server <ip>       DNS server for reverse lookups");
            Console.WriteLine();
            Console.WriteLine("Profiles:");
            Console.WriteLine("      --profile <name>        Load a saved profile");
            Console.WriteLine("      --pick-profile          Show profile picker (even if auto-connect would fire)");
            Console.WriteLine("      --save-as <name>        Save current params as a named profile");
            Console.WriteLine("      --save-no-pass          Save profile/auto-save without password");
            Console.WriteLine("      --list-profiles         List all saved profiles and exit");
            Console.WriteLine("      --delete-profile <n>    Delete a saved profile and exit");
            Console.WriteLine("      --reset                 Delete all saved profiles and exit");
            Console.WriteLine("      --no-save               Do not auto-save connection to host profile");
            Console.WriteLine("      --private               Alias for --no-save");
            Console.WriteLine();
            Console.WriteLine("      --debug                 Crash on torch error with full diagnostics");
            Console.WriteLine();
            Console.WriteLine("  -h, --help, -?, /?          Show this help and exit");
            Console.WriteLine();
            Console.WriteLine("Startup behaviour:");
            Console.WriteLine("  Host given           → loads profile for that host automatically");
            Console.WriteLine("  No host, 0 profiles  → prompts host, then remaining fields");
            Console.WriteLine("  No host, 1+ profiles → shows picker (profile name = host address)");
            Console.WriteLine();
            Console.WriteLine($"Profiles stored in: {ProfileManager.ConfigDir}");
        }
    }
}
