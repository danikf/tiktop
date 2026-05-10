using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace tiktop
{
    /// <summary>
    /// Stores named connection profiles in the user's application data directory.
    /// Passwords are encrypted with DPAPI on Windows (user-scoped) or AES-GCM with a
    /// machine+user derived key on other platforms (file permissions set to 600).
    /// </summary>
    public class ProfileManager
    {
        public static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tiktop");

        private static readonly string ConfigFile = Path.Combine(ConfigDir, "profiles.json");

        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        private ProfilesRoot _root;

        public ProfileManager() => _root = Load();

        public IReadOnlyDictionary<string, StoredProfile> Profiles => _root.Profiles;

        // ── Read ───────────────────────────────────────────────────────────────

        public StoredProfile? Get(string name) =>
            _root.Profiles.TryGetValue(name, out var p) ? p : null;

        public StoredProfile? GetLast() =>
            _root.LastUsed != null ? Get(_root.LastUsed) : null;

        public string? LastUsedName => _root.LastUsed;

        // Returns profiles in display order: _last first, then others by SavedAt desc.
        public List<(string Name, StoredProfile Profile)> GetProfilesSorted()
        {
            var result = new List<(string, StoredProfile)>();
            if (_root.Profiles.TryGetValue("_last", out var last))
                result.Add(("_last", last));

            var others = _root.Profiles
                .Where(kvp => kvp.Key != "_last")
                .OrderByDescending(kvp => kvp.Value.SavedAt ?? DateTime.MinValue)
                .Select(kvp => (kvp.Key, kvp.Value));

            result.AddRange(others);
            return result;
        }

        // ── Write ──────────────────────────────────────────────────────────────

        public void Save(string name, ConnectionConfig cfg, bool savePassword)
        {
            var profile = new StoredProfile
            {
                Host      = cfg.Host,
                User      = cfg.User,
                Interface = cfg.Interface,
                Port      = cfg.Port,
                UseSsl    = cfg.UseSsl,
                DnsServer = cfg.DnsServer,
                Count     = cfg.Count,
                SavedAt   = DateTime.UtcNow,
                PasswordProtected = savePassword && !string.IsNullOrEmpty(cfg.Pass)
                    ? Encrypt(cfg.Pass)
                    : null
            };
            _root.Profiles[name] = profile;
            _root.LastUsed = name;
            Persist();
        }

        public void Delete(string name)
        {
            _root.Profiles.Remove(name);
            if (_root.LastUsed == name) _root.LastUsed = null;
            Persist();
        }

        // ── Crypto ─────────────────────────────────────────────────────────────

        public static string? Encrypt(string password)
        {
            byte[] plain = Encoding.UTF8.GetBytes(password);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    byte[] enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                    return "dpapi:" + Convert.ToBase64String(enc);
                }
                catch { /* fall through */ }
            }

            // AES-GCM with machine+user derived key (+ file permissions 600 on Unix)
            byte[] key   = DeriveKey();
            byte[] nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            byte[] tag   = new byte[AesGcm.TagByteSizes.MaxSize];
            byte[] ct    = new byte[plain.Length];

            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plain, ct, tag);

            byte[] combined = new byte[nonce.Length + tag.Length + ct.Length];
            nonce.CopyTo(combined, 0);
            tag.CopyTo(combined, nonce.Length);
            ct.CopyTo(combined, nonce.Length + tag.Length);
            return "aes:" + Convert.ToBase64String(combined);
        }

        public static string? Decrypt(string? blob)
        {
            if (blob == null) return null;
            try
            {
                if (blob.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                {
                    byte[] data = Convert.FromBase64String(blob[6..]);
                    byte[] dec  = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dec);
                }
                if (blob.StartsWith("aes:"))
                {
                    byte[] combined = Convert.FromBase64String(blob[4..]);
                    int nl = AesGcm.NonceByteSizes.MaxSize, tl = AesGcm.TagByteSizes.MaxSize;
                    byte[] nonce = combined[..nl];
                    byte[] tag   = combined[nl..(nl + tl)];
                    byte[] ct    = combined[(nl + tl)..];
                    byte[] plain = new byte[ct.Length];
                    using var aes = new AesGcm(DeriveKey(), tl);
                    aes.Decrypt(nonce, ct, tag, plain);
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch { }
            return null;
        }

        private static byte[] DeriveKey() =>
            SHA256.HashData(Encoding.UTF8.GetBytes(
                Environment.MachineName + "|" + Environment.UserName));

        // ── Persistence ────────────────────────────────────────────────────────

        private ProfilesRoot Load()
        {
            if (!File.Exists(ConfigFile)) return new ProfilesRoot();
            try
            {
                return JsonSerializer.Deserialize<ProfilesRoot>(
                    File.ReadAllText(ConfigFile)) ?? new ProfilesRoot();
            }
            catch { return new ProfilesRoot(); }
        }

        private void Persist()
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigFile, JsonSerializer.Serialize(_root, JsonOpts));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(ConfigFile,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { }
            }
        }
    }

    // ── Data models ────────────────────────────────────────────────────────────

    public class ProfilesRoot
    {
        [JsonPropertyName("lastUsed")]
        public string? LastUsed { get; set; }

        [JsonPropertyName("profiles")]
        public Dictionary<string, StoredProfile> Profiles { get; set; } = new();
    }

    public class StoredProfile
    {
        [JsonPropertyName("host")]      public string?   Host              { get; set; }
        [JsonPropertyName("user")]      public string?   User              { get; set; }
        [JsonPropertyName("password")]  public string?   PasswordProtected { get; set; }
        [JsonPropertyName("interface")] public string?   Interface         { get; set; }
        [JsonPropertyName("port")]      public int?      Port              { get; set; }
        [JsonPropertyName("useSsl")]    public bool      UseSsl            { get; set; } = true;
        [JsonPropertyName("dnsServer")] public string?   DnsServer         { get; set; }
        [JsonPropertyName("count")]     public int?      Count             { get; set; }
        [JsonPropertyName("savedAt")]   public DateTime? SavedAt           { get; set; }
    }
}
