using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugins.Bangumi.Web
{
    /// <summary>
    /// Two layer cache for the item page payload.
    ///
    /// Assembling one detail payload costs up to 43 Bangumi requests at roughly three per second,
    /// so the memory layer is the difference between an instant panel and a half minute wait. The
    /// disk layer exists because the memory layer dies with the process: without it every server
    /// restart throws the whole library back to cold start, which is exactly what a user who was
    /// promised "cached locally" does not expect. Only the detail payload is persisted; the wiki
    /// popups are single requests and not worth a file each.
    ///
    /// Files live under <c>&lt;programdata&gt;/data/bangumi-ui-cache</c>, one per subject. They are
    /// disposable by design: deleting the folder costs one rebuild, nothing else.
    /// </summary>
    internal static class BangumiUiCache
    {
        private const int DiskFormatVersion = 1;

        private const string DirectoryName = "bangumi-ui-cache";

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private static readonly object PathLock = new object();

        private static long _operations;
        private static string _directory;
        private static bool _directoryResolved;

        private sealed class Entry
        {
            public object Payload;
            public DateTime ExpiresUtc;
        }

        /// <summary>What one subject file holds. Public setters: the Emby serialiser needs them.</summary>
        public sealed class DiskEnvelope
        {
            public int Version { get; set; }

            public int SubjectId { get; set; }

            public DateTime StoredUtc { get; set; }

            public DateTime ExpiresUtc { get; set; }

            public BangumiUiDetail Payload { get; set; }
        }

        // ------------------------------------------------------------------ memory only

        public static object Get(string key)
        {
            Entry entry;
            if (!Entries.TryGetValue(key, out entry)) return null;

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                Entry removed;
                Entries.TryRemove(key, out removed);
                return null;
            }

            return entry.Payload;
        }

        public static void Set(string key, object payload, TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero || payload == null) return;

            Entries[key] = new Entry { Payload = payload, ExpiresUtc = DateTime.UtcNow.Add(ttl) };

            if (Interlocked.Increment(ref _operations) % 64 != 0) return;

            var now = DateTime.UtcNow;
            foreach (var pair in Entries.ToArray())
            {
                if (pair.Value.ExpiresUtc > now) continue;

                Entry removed;
                Entries.TryRemove(pair.Key, out removed);
            }
        }

        // ------------------------------------------------------------------ detail (memory + disk)

        /// <summary>
        /// The cached payload for a subject, from memory first and from disk second, or null.
        /// A payload built with a smaller name budget than asked for is treated as a miss: it
        /// would render with Japanese character names where Chinese ones were configured.
        /// </summary>
        public static BangumiUiDetail GetDetail(int subjectId, int minimumNameBudget)
        {
            var key = DetailKey(subjectId);

            var memory = Get(key) as BangumiUiDetail;
            if (memory != null) return memory.NameBudget >= minimumNameBudget ? memory : null;

            var envelope = ReadDisk(subjectId);
            if (envelope == null || envelope.Payload == null) return null;

            // Promote to memory even when the budget is too small: the caller is about to rebuild
            // and overwrite it, and a second reader in the meantime should not hit the disk again.
            Entries[key] = new Entry { Payload = envelope.Payload, ExpiresUtc = envelope.ExpiresUtc };

            return envelope.Payload.NameBudget >= minimumNameBudget ? envelope.Payload : null;
        }

        /// <summary>
        /// Stores a payload in both layers. A payload with a smaller name budget never replaces a
        /// richer one that is still fresh, so the two phase client cannot downgrade the cache.
        /// </summary>
        public static void SetDetail(int subjectId, BangumiUiDetail detail, TimeSpan ttl)
        {
            if (detail == null || ttl <= TimeSpan.Zero) return;

            var key = DetailKey(subjectId);

            var existing = Get(key) as BangumiUiDetail;
            if (existing != null && existing.NameBudget > detail.NameBudget) return;

            var expires = DateTime.UtcNow.Add(ttl);
            Set(key, detail, ttl);
            WriteDisk(subjectId, detail, expires);
        }

        /// <summary>Number of subject files currently on disk, for task reporting. -1 if unavailable.</summary>
        public static int DiskEntryCount()
        {
            var directory = CacheDirectory();
            if (directory == null) return -1;

            try
            {
                return Directory.GetFiles(directory, "subject-*.json").Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string DetailKey(int subjectId)
        {
            return "detail:" + subjectId.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ disk

        private static DiskEnvelope ReadDisk(int subjectId)
        {
            var path = FilePath(subjectId);
            if (path == null) return null;

            var serializer = Plugin.TryResolve<IJsonSerializer>();
            if (serializer == null) return null;

            try
            {
                if (!File.Exists(path)) return null;

                var envelope = serializer.DeserializeFromFile<DiskEnvelope>(path);
                if (envelope == null || envelope.Version != DiskFormatVersion) return null;

                if (envelope.ExpiresUtc <= DateTime.UtcNow)
                {
                    // Stale: drop the file so the folder does not grow forever with subjects that
                    // left the library. Only ever touches files this class wrote.
                    TryDelete(path);
                    return null;
                }

                return envelope;
            }
            catch (Exception ex)
            {
                Log("Bangumi UI could not read cache file " + path, ex);
                TryDelete(path);
                return null;
            }
        }

        private static void WriteDisk(int subjectId, BangumiUiDetail detail, DateTime expiresUtc)
        {
            var path = FilePath(subjectId);
            if (path == null) return;

            var serializer = Plugin.TryResolve<IJsonSerializer>();
            if (serializer == null) return;

            var envelope = new DiskEnvelope
            {
                Version = DiskFormatVersion,
                SubjectId = subjectId,
                StoredUtc = DateTime.UtcNow,
                ExpiresUtc = expiresUtc,
                Payload = detail
            };

            // Write beside the target and move into place: a half written file read by the next
            // request would be indistinguishable from corruption.
            var temp = path + ".tmp";

            try
            {
                serializer.SerializeToFile(envelope, temp);

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Log("Bangumi UI could not write cache file " + path, ex);
                TryDelete(temp);
            }
        }

        private static string FilePath(int subjectId)
        {
            var directory = CacheDirectory();
            if (directory == null) return null;

            return Path.Combine(
                directory, "subject-" + subjectId.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        private static string CacheDirectory()
        {
            lock (PathLock)
            {
                if (_directoryResolved) return _directory;
                _directoryResolved = true;

                try
                {
                    var paths = Plugin.TryResolve<IApplicationPaths>();
                    if (paths == null || string.IsNullOrWhiteSpace(paths.DataPath)) return null;

                    var directory = Path.Combine(paths.DataPath, DirectoryName);
                    Directory.CreateDirectory(directory);
                    _directory = directory;
                }
                catch (Exception ex)
                {
                    Log("Bangumi UI could not prepare its cache folder", ex);
                    _directory = null;
                }

                return _directory;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A cache file that refuses to go away is harmless.
            }
        }

        private static void Log(string message, Exception ex)
        {
            var instance = Plugin.Instance;
            if (instance == null) return;

            instance.Logger.ErrorException(message, ex);
        }
    }
}
