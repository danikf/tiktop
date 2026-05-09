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
        public int?   Port      { get; set; }
        public int?   Count     { get; set; }
        public string? DnsServer { get; set; }

        // Profile actions (not part of the connection itself)
        public string? ProfileName { get; private set; }
        public string? SaveAs      { get; private set; }
        /// <summary>When true, the connection is NOT auto-saved to the _last profile.</summary>
        public bool    NoSave      { get; private set; }

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
                    case "-h": case "--help":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    case "-H": case "--host":
                        cfg.Host = NextArg(args, ref i, "--host");
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
                    case "--list-profiles":
                        ListProfiles(profiles);
                        Environment.Exit(0);
                        break;
                    case "--delete-profile":
                        DeleteProfile(profiles, NextArg(args, ref i, "--delete-profile"));
                        Environment.Exit(0);
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        PrintHelp();
                        Environment.Exit(1);
                        break;
                }
            }

            // Load a named profile (all fields, silent – like providing CLI args)
            if (cfg.ProfileName != null)
            {
                var profile = profiles.Get(cfg.ProfileName);
                if (profile == null)
                {
                    Console.Error.WriteLine($"Profile '{cfg.ProfileName}' not found.");
                    ListProfiles(profiles);
                    Environment.Exit(1);
                }
                cfg.ApplyProfile(profile, applyPassword: true);
            }

            // Prompt for any missing fields, showing last-used values as defaults
            StoredProfile? defaults = cfg.ProfileName == null ? profiles.GetLast() : null;
            PromptMissing(cfg, defaults);

            return cfg;
        }

        // Apply a stored profile to any fields not already set
        public void ApplyProfile(StoredProfile p, bool applyPassword = false)
        {
            if (string.IsNullOrEmpty(Host)      && p.Host      != null) Host      = p.Host;
            if (string.IsNullOrEmpty(User)      && p.User      != null) User      = p.User;
            if (string.IsNullOrEmpty(Interface) && p.Interface != null) Interface = p.Interface;
            if (Port      == null && p.Port      != null) Port      = p.Port;
            if (DnsServer == null && p.DnsServer != null) DnsServer = p.DnsServer;
            if (Count     == null && p.Count     != null) Count     = p.Count;
            UseSsl = p.UseSsl;

            if (applyPassword && string.IsNullOrEmpty(Pass) && p.PasswordProtected != null)
                Pass = ProfileManager.Decrypt(p.PasswordProtected) ?? "";
        }

        // ── Interactive prompts ───────────────────────────────────────────────

        private static void PromptMissing(ConnectionConfig cfg, StoredProfile? defaults)
        {
            cfg.Host = PromptField("Host", cfg.Host, defaults?.Host);
            cfg.User = PromptField("Username", cfg.User, defaults?.User);

            if (string.IsNullOrWhiteSpace(cfg.Pass))
            {
                // Try decrypting the saved password first
                string? savedPass = defaults?.PasswordProtected != null
                    ? ProfileManager.Decrypt(defaults.PasswordProtected) : null;

                if (savedPass != null)
                {
                    Console.Write("Password [saved]: ");
                    string input = ReadMasked();
                    Console.WriteLine();
                    cfg.Pass = string.IsNullOrEmpty(input) ? savedPass : input;
                }
                else
                {
                    Console.Write("Password: ");
                    cfg.Pass = ReadMasked();
                    Console.WriteLine();
                }
            }

            if (string.IsNullOrWhiteSpace(cfg.Interface))
            {
                string? defaultIface = defaults?.Interface;
                cfg.Interface = PickInterface(cfg, defaultIface);
            }
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
            var interfaces = FetchInterfaces(cfg);

            if (interfaces.Count == 0)
            {
                return PromptField("Interface", "", defaultIface ?? "ether1");
            }

            int defaultIndex = defaultIface != null
                ? interfaces.FindIndex(x => x.Name == defaultIface)
                : interfaces.FindIndex(x => x.DefaultName == "ether1");
            if (defaultIndex < 0) defaultIndex = 0;

            Console.WriteLine("Available interfaces:");
            for (int i = 0; i < interfaces.Count; i++)
            {
                var (name, defName) = interfaces[i];
                string label = name == defName ? name : $"{name}  (default-name: {defName})";
                string marker = i == defaultIndex ? "* " : "  ";
                Console.WriteLine($" {marker}{i + 1}) {label}");
            }

            Console.Write($"Select interface [{defaultIndex + 1}]: ");
            string sel = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(sel))
                return interfaces[defaultIndex].Name;
            if (int.TryParse(sel, out int choice) && choice >= 1 && choice <= interfaces.Count)
                return interfaces[choice - 1].Name;
            return sel; // user typed a name directly
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

        // ── Profile management helpers ────────────────────────────────────────

        private static void ListProfiles(ProfileManager profiles)
        {
            if (profiles.Profiles.Count == 0)
            {
                Console.WriteLine("No saved profiles.");
                Console.WriteLine($"Use --save-as <name> to create one. Config dir: {ProfileManager.ConfigDir}");
                return;
            }

            Console.WriteLine("Saved profiles:");
            foreach (var (name, p) in profiles.Profiles)
            {
                string marker   = name == profiles.LastUsedName ? "*" : " ";
                string passNote = p.PasswordProtected != null ? " [password saved]" : "";
                string iface    = p.Interface != null ? $"  {p.Interface}" : "";
                Console.WriteLine($"  {marker} {name,-20} {p.Host}  {p.User}{iface}{passNote}");
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
            Console.WriteLine("Connection:");
            Console.WriteLine("  -H, --host <ip>           Router IP or hostname");
            Console.WriteLine("  -u, --user <name>         Username");
            Console.WriteLine("  -p, --pass <password>     Password (prompted if omitted)");
            Console.WriteLine("  -i, --interface <name>    Interface to monitor");
            Console.WriteLine("      --port <port>         API port (default: 8729 SSL / 8728 plain)");
            Console.WriteLine("      --no-ssl              Use plain (non-SSL) API connection");
            Console.WriteLine("  -n, --count <n>           Number of rows to display");
            Console.WriteLine("  -d, --dns-server <ip>     DNS server for reverse lookups");
            Console.WriteLine();
            Console.WriteLine("Profiles:");
            Console.WriteLine("      --profile <name>      Load a saved profile");
            Console.WriteLine("      --save-as <name>      Save current params as a named profile");
            Console.WriteLine("      --list-profiles       List all saved profiles and exit");
            Console.WriteLine("      --delete-profile <n>  Delete a saved profile and exit");
            Console.WriteLine("      --no-save             Do not auto-save connection as _last profile");
            Console.WriteLine();
            Console.WriteLine("  -h, --help                Show this help and exit");
            Console.WriteLine();
            Console.WriteLine($"Profiles are stored in: {ProfileManager.ConfigDir}");
        }
    }
}
