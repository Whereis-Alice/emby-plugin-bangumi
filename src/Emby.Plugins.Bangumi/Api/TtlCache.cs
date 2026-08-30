using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Emby.Plugins.Bangumi.Api
{
    /// <summary>
    /// Tiny expiring cache. Exists so that a single library scan (which asks the same
    /// subject for its series, its season, every episode and its images) results in one
    /// API call instead of dozens.
    /// </summary>
    internal sealed class TtlCache
    {
        private readonly ConcurrentDictionary<string, Entry> _entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private long _operations;

        private sealed class Entry
        {
            public string Payload;
            public DateTime ExpiresUtc;
        }

        public bool TryGet(string key, out string payload)
        {
            payload = null;
            if (!_entries.TryGetValue(key, out var entry)) return false;

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                _entries.TryRemove(key, out _);
                return false;
            }

            payload = entry.Payload;
            return true;
        }

        public void Set(string key, string payload, TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero) return;

            _entries[key] = new Entry { Payload = payload, ExpiresUtc = DateTime.UtcNow.Add(ttl) };

            // Amortised sweep; a metadata scan can otherwise grow this unbounded.
            if (System.Threading.Interlocked.Increment(ref _operations) % 256 != 0) return;

            var now = DateTime.UtcNow;
            var stale = new List<string>();
            foreach (var pair in _entries)
            {
                if (pair.Value.ExpiresUtc <= now) stale.Add(pair.Key);
            }

            foreach (var key2 in stale) _entries.TryRemove(key2, out _);
        }

        public void Clear() => _entries.Clear();

        public int Count => _entries.Count;
    }
}