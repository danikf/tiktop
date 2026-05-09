using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DnsClient;

namespace tiktop.Helpers
{
    public class DnsCache
    {
        private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

        private record CacheEntry(string? Hostname, DateTime ExpiresAt);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private readonly ILookupClient _dns;

        public DnsCache(ILookupClient dns)
        {
            _dns = dns;
        }

        /// <summary>
        /// Returns cached hostname or null. Launches async resolution on first call for a given IP.
        /// null means "not yet resolved" or "no reverse record" — caller should display the IP instead.
        /// </summary>
        public string? TryGet(string ip)
        {
            if (_cache.TryGetValue(ip, out var entry))
            {
                if (entry.ExpiresAt > DateTime.UtcNow)
                    return entry.Hostname;

                // Expired: mark in-progress and re-resolve; return stale value in the meantime.
                var inProgress = entry with { ExpiresAt = DateTime.MaxValue };
                if (_cache.TryUpdate(ip, inProgress, entry))
                    _ = ResolveAsync(ip);

                return entry.Hostname;
            }

            if (_cache.TryAdd(ip, new CacheEntry(null, DateTime.MaxValue)))
                _ = ResolveAsync(ip);

            return null;
        }

        private async Task ResolveAsync(string ip)
        {
            string? resolved = null;
            var ttl = NegativeTtl;

            try
            {
                if (IPAddress.TryParse(ip, out var addr))
                {
                    var result = await _dns.QueryReverseAsync(addr);
                    var name = result.Answers.PtrRecords().FirstOrDefault()?.PtrDomainName?.Value?.TrimEnd('.');
                    if (!string.IsNullOrEmpty(name) && name != ip)
                    {
                        resolved = name;
                        ttl = PositiveTtl;
                    }
                }
            }
            catch { }

            _cache[ip] = new CacheEntry(resolved, DateTime.UtcNow + ttl);
        }
    }
}
