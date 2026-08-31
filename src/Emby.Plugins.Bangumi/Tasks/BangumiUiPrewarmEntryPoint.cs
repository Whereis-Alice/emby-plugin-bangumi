using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Bangumi.Tasks
{
    /// <summary>
    /// Keeps the item page cache in step with the library instead of waiting for the night.
    ///
    /// <see cref="BangumiUiPrewarmTask"/> enumerates everything once a day, which is the right shape
    /// for refreshing an entire library but leaves a freshly added series loading the slow way until
    /// 04:30. Emby raises <see cref="ILibraryManager.ItemAdded"/> and <see cref="ILibraryManager.ItemUpdated"/>
    /// as the scanner and the scrapers work, so the same payload can be built within a minute of the
    /// item appearing.
    ///
    /// Two details make this cheap rather than chatty:
    ///
    /// * <c>ItemAdded</c> fires before any provider ran, so the item usually has no Bangumi id yet;
    ///   the id arrives with the <c>ItemUpdated</c> that follows the metadata write. Both events are
    ///   handled and the ones without an id are dropped after a type check.
    /// * A library scan raises many events for the same subject (series, then its seasons, then a
    ///   second write for images). Every hit is therefore parked in a small map keyed by subject id
    ///   with a deadline that is pushed forward on each new event, and a single background loop picks
    ///   up whatever has gone quiet for <see cref="Debounce"/>. Anything already cached is skipped
    ///   without a request.
    /// </summary>
    public class BangumiUiPrewarmEntryPoint : IServerEntryPoint, IDisposable
    {
        /// <summary>How long a subject has to stay quiet before its payload is built.</summary>
        private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(20);

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// A first scan of a large library would otherwise queue hundreds of subjects and hold the
        /// single request slot for hours. Past this point the nightly task is the better tool.
        /// </summary>
        private const int MaxQueueLength = 250;

        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<int, PendingSubject> _pending = new Dictionary<int, PendingSubject>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private Task _worker;
        private bool _subscribed;
        private int _dropped;

        public BangumiUiPrewarmEntryPoint(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = logManager.GetLogger(BangumiConstants.PluginName);
        }

        public void Run()
        {
            if (_libraryManager == null) return;

            // Subscribed unconditionally: the options are read inside the handler, so turning the
            // feature on takes effect without a restart, and while it is off an event costs one
            // type check.
            _libraryManager.ItemAdded += OnItemChanged;
            _libraryManager.ItemUpdated += OnItemChanged;
            _subscribed = true;

            _logger.Info(
                "Bangumi UI prewarm: 已挂上媒体库事件，新增或重新识别的条目会在安静 {0} 秒后自动预热。",
                ((int)Debounce.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            if (_subscribed && _libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemChanged;
                _libraryManager.ItemUpdated -= OnItemChanged;
                _subscribed = false;
            }

            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            lock (_sync)
            {
                _pending.Clear();
            }
        }

        // ------------------------------------------------------------------ events

        private void OnItemChanged(object sender, ItemChangeEventArgs e)
        {
            // Runs on Emby's own library threads: never throw, never block.
            try
            {
                var item = e == null ? null : e.Item;
                if (!IsSubjectItem(item)) return;

                var options = Plugin.CurrentOptions();
                if (!options.EnableBangumiUi || !options.UiPrewarmOnAdd) return;

                int subjectId;
                if (!TryGetSubjectId(item, out subjectId)) return;

                var nameBudget = Math.Max(0, options.UiCharacterNameLookups);
                if (BangumiUiCache.GetDetail(subjectId, nameBudget) != null)
                {
                    // Already warm - the common case during a routine library scan.
                    return;
                }

                Enqueue(subjectId, item.Name);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Bangumi UI prewarm: 处理媒体库事件失败", ex);
            }
        }

        private static bool IsSubjectItem(BaseItem item)
        {
            // The three types that carry a subject id of their own, same set as the nightly task.
            return item is Series || item is Season || item is Movie;
        }

        private static bool TryGetSubjectId(BaseItem item, out int subjectId)
        {
            subjectId = 0;
            if (item == null || item.ProviderIds == null) return false;

            string raw;
            if (!item.ProviderIds.TryGetValue(BangumiConstants.ProviderId, out raw)) return false;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            if (!int.TryParse(
                    raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out subjectId))
            {
                return false;
            }

            return subjectId > 0;
        }

        // ------------------------------------------------------------------ queue

        private void Enqueue(int subjectId, string name)
        {
            var due = DateTime.UtcNow + Debounce;

            lock (_sync)
            {
                PendingSubject existing;
                if (_pending.TryGetValue(subjectId, out existing))
                {
                    // Still being written to: wait for the writes to stop rather than building a
                    // payload for an id that is about to change again.
                    existing.DueUtc = due;
                    if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
                    return;
                }

                if (_pending.Count >= MaxQueueLength)
                {
                    _dropped++;
                    if (_dropped == 1 || _dropped % 100 == 0)
                    {
                        _logger.Warn(
                            "Bangumi UI prewarm: 队列已满（{0} 个），累计跳过 {1} 个条目，交给夜间预热任务。",
                            MaxQueueLength.ToString(CultureInfo.InvariantCulture),
                            _dropped.ToString(CultureInfo.InvariantCulture));
                    }

                    return;
                }

                _pending[subjectId] = new PendingSubject
                {
                    SubjectId = subjectId,
                    Name = name ?? string.Empty,
                    DueUtc = due,
                };

                _logger.Info(
                    "Bangumi UI prewarm: 「{0}」(subject {1}) 排入预热队列，{2} 秒后开始，队列共 {3} 个。",
                    name ?? string.Empty,
                    subjectId.ToString(CultureInfo.InvariantCulture),
                    ((int)Debounce.TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    _pending.Count.ToString(CultureInfo.InvariantCulture));

                // A completed task is left in place rather than nulled out; the check below starts
                // a fresh loop for it.
                if (_worker == null || _worker.IsCompleted)
                {
                    _worker = Task.Run(() => WorkerLoop(_shutdown.Token));
                }
            }
        }

        private PendingSubject TakeDue()
        {
            var now = DateTime.UtcNow;

            lock (_sync)
            {
                PendingSubject best = null;
                foreach (var entry in _pending.Values)
                {
                    if (entry.DueUtc > now) continue;
                    if (best == null || entry.DueUtc < best.DueUtc) best = entry;
                }

                if (best != null) _pending.Remove(best.SubjectId);
                return best;
            }
        }

        private int PendingCount()
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }

        private async Task WorkerLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

                    var next = TakeDue();
                    if (next == null)
                    {
                        // Nothing ready. Exit once the map is empty as well, so an idle server is
                        // not polling forever; the next event starts a new loop.
                        if (PendingCount() == 0) return;
                        continue;
                    }

                    await PrewarmOne(next, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Bangumi UI prewarm: 事件预热循环异常退出", ex);
            }
        }

        private async Task PrewarmOne(PendingSubject entry, CancellationToken cancellationToken)
        {
            var options = Plugin.CurrentOptions();
            if (!options.EnableBangumiUi || !options.UiPrewarmOnAdd) return;

            var nameBudget = Math.Max(0, options.UiCharacterNameLookups);
            if (BangumiUiCache.GetDetail(entry.SubjectId, nameBudget) != null)
            {
                // Somebody opened the page, or the nightly task got there first.
                return;
            }

            var startedUtc = DateTime.UtcNow;

            try
            {
                var detail = await BangumiUiService
                    .PrewarmAsync(entry.SubjectId, options, _logManager, cancellationToken)
                    .ConfigureAwait(false);

                var characters = detail == null || detail.Characters == null ? 0 : detail.Characters.Count;

                _logger.Info(
                    "Bangumi UI prewarm: 「{0}」(subject {1}) 预热完成，{2} 个角色，用时 {3} 秒，队列还剩 {4} 个。",
                    entry.Name,
                    entry.SubjectId.ToString(CultureInfo.InvariantCulture),
                    characters.ToString(CultureInfo.InvariantCulture),
                    ((int)(DateTime.UtcNow - startedUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    PendingCount().ToString(CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Left to the nightly task: retrying here would spin on a network outage.
                _logger.ErrorException(
                    "Bangumi UI prewarm: 「" + entry.Name + "」(subject " +
                    entry.SubjectId.ToString(CultureInfo.InvariantCulture) +
                    ") 事件预热失败，等夜间任务重试", ex);
            }
        }

        private sealed class PendingSubject
        {
            public int SubjectId { get; set; }

            public string Name { get; set; }

            public DateTime DueUtc { get; set; }
        }
    }
}
