using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Bangumi.Web;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Bangumi.Tasks
{
    /// <summary>
    /// Builds the item page payload for every Bangumi matched item in the library ahead of time.
    ///
    /// One payload is a subject request, a character request, a staff request, a related request and
    /// then one request per character whose Chinese name has to be looked up - around 43 calls at the
    /// three per second the Bangumi API tolerates, so roughly half a minute of waiting the first time
    /// somebody opens a series. Doing that pass at night turns it into an instant panel.
    ///
    /// The cache lifetime doubles as the refresh interval: this task only builds what is missing or
    /// expired, so a run over an already warm library finishes in seconds and a subject is refreshed
    /// once its entry ages out. Newly added series do not have to wait for the next run:
    /// <see cref="BangumiUiPrewarmEntryPoint"/> watches the library events and builds those within a
    /// minute. This task remains the safety net - it catches whatever the events missed (server was
    /// down, queue was full, a build failed) and it is what actually refreshes expired entries.
    /// </summary>
    public class BangumiUiPrewarmTask : IScheduledTask, IConfigurableScheduledTask
    {
        private static readonly string[] SubjectItemTypes = { "Series", "Season", "Movie" };

        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public BangumiUiPrewarmTask(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logManager = logManager;
            _logger = logManager.GetLogger(BangumiConstants.PluginName);
        }

        public string Key => "BangumiUiPrewarm";

        public string Name => "Bangumi：预热条目页面缓存";

        public string Category => BangumiConstants.PluginName;

        public string Description =>
            "提前把媒体库里每个 Bangumi 条目的角色、声优、制作人员和关联条目取好并写入本地缓存，打开条目页面时直接显示。只处理没缓存过或缓存已过期的条目。新加入的番剧不用等这里——插件挂在媒体库事件上，入库后一分钟内就会自己预热，这个任务负责刷新过期条目和补上漏掉的。";

        public bool IsEnabled
        {
            get
            {
                var options = Plugin.CurrentOptions();
                return options.EnableBangumiUi && options.UiPrewarmCache;
            }
        }

        public bool IsHidden => false;

        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Half an hour after the person metadata task so the two do not fight over the
            // single request slot the API client hands out.
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks,
                },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.CurrentOptions();

            if (!options.EnableBangumiUi || !options.UiPrewarmCache)
            {
                _logger.Info("Bangumi UI prewarm: 已关闭，跳过。");
                progress.Report(100);
                return;
            }

            var nameBudget = Math.Max(0, options.UiCharacterNameLookups);

            int scanned;
            var subjects = CollectSubjects(out scanned);

            var pending = new List<KeyValuePair<int, string>>();
            var alreadyWarm = 0;

            foreach (var subject in subjects)
            {
                if (BangumiUiCache.GetDetail(subject.Key, nameBudget) != null)
                {
                    alreadyWarm++;
                    continue;
                }

                pending.Add(subject);
            }

            var limit = Math.Max(0, options.UiPrewarmLimit);
            var skippedByLimit = 0;
            if (limit > 0 && pending.Count > limit)
            {
                skippedByLimit = pending.Count - limit;
                pending.RemoveRange(limit, skippedByLimit);
            }

            _logger.Info(
                "Bangumi UI prewarm: 扫描 {0} 个条目，{1} 个 Bangumi 条目，{2} 个缓存仍有效，{3} 个待预热{4}。",
                scanned.ToString(CultureInfo.InvariantCulture),
                subjects.Count.ToString(CultureInfo.InvariantCulture),
                alreadyWarm.ToString(CultureInfo.InvariantCulture),
                pending.Count.ToString(CultureInfo.InvariantCulture),
                skippedByLimit > 0
                    ? "（超出上限，本次留下 " + skippedByLimit.ToString(CultureInfo.InvariantCulture) + " 个给下次）"
                    : string.Empty);

            if (pending.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var built = 0;
            var failed = 0;
            var characters = 0;

            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var subjectId = pending[i].Key;
                var title = pending[i].Value;

                try
                {
                    var detail = await BangumiUiService
                        .PrewarmAsync(subjectId, options, _logManager, cancellationToken)
                        .ConfigureAwait(false);

                    built++;
                    if (detail != null && detail.Characters != null) characters += detail.Characters.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.ErrorException(
                        "Bangumi UI prewarm: 「" + title + "」(subject " +
                        subjectId.ToString(CultureInfo.InvariantCulture) + ") 预热失败", ex);
                }

                progress.Report((i + 1) * 100.0 / pending.Count);
            }

            _logger.Info(
                "Bangumi UI prewarm: 完成，预热 {0} 个条目、{1} 个角色，失败 {2} 个，磁盘缓存共 {3} 个文件。",
                built.ToString(CultureInfo.InvariantCulture),
                characters.ToString(CultureInfo.InvariantCulture),
                failed.ToString(CultureInfo.InvariantCulture),
                BangumiUiCache.DiskEntryCount().ToString(CultureInfo.InvariantCulture));

            progress.Report(100);
        }

        /// <summary>
        /// Every distinct Bangumi subject the library can resolve, with a name for the log.
        ///
        /// Seasons and movies are included alongside series because a season that was matched on its
        /// own carries its own subject id, and that is the payload its page asks for. Episodes are
        /// not: they have no id of their own and walk up to one of these.
        /// </summary>
        private List<KeyValuePair<int, string>> CollectSubjects(out int scanned)
        {
            var result = new List<KeyValuePair<int, string>>();
            var seen = new HashSet<int>();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = SubjectItemTypes,
                Recursive = true,
                EnableTotalRecordCount = false,
            };

            BaseItem[] items;
            try
            {
                items = _libraryManager.GetItemList(query);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Bangumi UI prewarm: 枚举媒体库失败", ex);
                scanned = 0;
                return result;
            }

            scanned = items == null ? 0 : items.Length;
            if (items == null) return result;

            foreach (var item in items)
            {
                if (item == null || item.ProviderIds == null) continue;

                string raw;
                if (!item.ProviderIds.TryGetValue(BangumiConstants.ProviderId, out raw)) continue;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                int subjectId;
                if (!int.TryParse(
                        raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out subjectId))
                {
                    continue;
                }

                if (subjectId <= 0 || !seen.Add(subjectId)) continue;

                result.Add(new KeyValuePair<int, string>(subjectId, item.Name ?? string.Empty));
            }

            return result;
        }
    }
}
