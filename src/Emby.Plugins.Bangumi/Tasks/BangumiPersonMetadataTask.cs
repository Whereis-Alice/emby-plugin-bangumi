using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Plugins.Bangumi.Tasks
{
    /// <summary>
    /// Emby creates a person row as soon as a series credits somebody, but that row only carries the
    /// name and the thumbnail that came with the credit. Everything behind /v0/persons/{id} - the
    /// biography, the birthday, the birth place, the full size portrait - is only written when the
    /// person item itself is refreshed, and nothing in Emby triggers that on its own: the person
    /// pages stay empty until somebody opens each one and hits refresh.
    ///
    /// This task does that pass in bulk for the people a Bangumi scrape linked, so a freshly scraped
    /// library ends up with complete person pages instead of 600 bare names.
    /// </summary>
    public class BangumiPersonMetadataTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public BangumiPersonMetadataTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger(BangumiConstants.PluginName);
        }

        public string Key => "BangumiPersonMetadata";

        public string Name => "Bangumi：补全人物元数据";

        public string Category => BangumiConstants.PluginName;

        public string Description =>
            "为 Bangumi 刮削出来的声优与制作人员补上简介、生日、出生地和头像。" +
            "只处理带 Bangumi 人物 id 或角色 id、且人物页还是空白的条目，已有的内容不会被覆盖。";

        public bool IsEnabled => Plugin.CurrentOptions().ImportPersonMetadata;

        public bool IsHidden => false;

        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Runs after the nightly library scan has had time to create the new person rows.
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerDaily,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
                },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.CurrentOptions();

            if (!options.ImportPersonMetadata)
            {
                _logger.Info("Bangumi person task: 人物页面元数据已关闭，跳过。");
                progress.Report(100);
                return;
            }

            int scanned;
            var pending = CollectPending(options, out scanned);

            if (pending.Count == 0)
            {
                _logger.Info(
                    "Bangumi person task: 扫描 {0} 个人物，没有需要补全的条目。",
                    scanned.ToString(CultureInfo.InvariantCulture));
                progress.Report(100);
                return;
            }

            _logger.Info(
                "Bangumi person task: 扫描 {0} 个人物，{1} 个待补全（每次请求间隔 {2} ms）。",
                scanned.ToString(CultureInfo.InvariantCulture),
                pending.Count.ToString(CultureInfo.InvariantCulture),
                Math.Max(0, options.RequestIntervalMs).ToString(CultureInfo.InvariantCulture));

            var refreshed = 0;
            var gotOverview = 0;
            var gotPortrait = 0;
            var failed = 0;

            for (var i = 0; i < pending.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var person = pending[i];
                var hadOverview = !string.IsNullOrWhiteSpace(person.Overview);
                var hadPortrait = person.HasImage(ImageType.Primary, 0);

                try
                {
                    // FullRefresh so the Bangumi provider is actually queried, but nothing is
                    // replaced: whatever another provider or the user already wrote stays put.
                    var refreshOptions = new MetadataRefreshOptions(_fileSystem)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllMetadata = false,
                        ReplaceAllImages = false,
                        IsAutomated = true,
                    };

                    await _providerManager
                        .RefreshFullItem(person, refreshOptions, cancellationToken)
                        .ConfigureAwait(false);

                    refreshed++;

                    var after = _libraryManager.GetItemById(person.InternalId) as Person;
                    if (after != null)
                    {
                        if (!hadOverview && !string.IsNullOrWhiteSpace(after.Overview)) gotOverview++;
                        if (!hadPortrait && after.HasImage(ImageType.Primary, 0)) gotPortrait++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.ErrorException(
                        "Bangumi person task: 刷新「" + person.Name + "」失败", ex);
                }

                progress.Report((i + 1) * 100.0 / pending.Count);
            }

            _logger.Info(
                "Bangumi person task: 完成，刷新 {0} 个，新增简介 {1} 个，新增头像 {2} 个，失败 {3} 个。",
                refreshed.ToString(CultureInfo.InvariantCulture),
                gotOverview.ToString(CultureInfo.InvariantCulture),
                gotPortrait.ToString(CultureInfo.InvariantCulture),
                failed.ToString(CultureInfo.InvariantCulture));

            progress.Report(100);
        }

        /// <summary>
        /// Every person that carries a Bangumi person id and still looks unscraped. A missing
        /// overview is the reliable "never refreshed" signal, because the provider always writes at
        /// least the fact list. A missing portrait is not: Bangumi simply has no picture for a large
        /// part of the staff, so retrying those every night would be hundreds of pointless requests
        /// unless the user asks for it.
        /// </summary>
        private List<Person> CollectPending(PluginOptions options, out int scanned)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Person" },
                Recursive = true,
                EnableTotalRecordCount = false,
            };

            var items = _libraryManager.GetItemList(query);
            scanned = items == null ? 0 : items.Length;

            var pending = new List<Person>();
            if (items == null) return pending;

            var limit = Math.Max(0, options.PersonTaskLimit);

            foreach (var item in items)
            {
                var person = item as Person;
                if (person == null) continue;

                if (person.ProviderIds == null) continue;

                // Rows standing in for a role with no registered voice actor are keyed by character
                // id instead, and their pages are just as empty until they get refreshed.
                if (!HasBangumiId(person, BangumiConstants.PersonProviderId) &&
                    !HasBangumiId(person, BangumiConstants.CharacterProviderId))
                {
                    continue;
                }

                var needsOverview = string.IsNullOrWhiteSpace(person.Overview);
                var needsPortrait = options.PersonTaskRetryMissingPortraits
                    && !person.HasImage(ImageType.Primary, 0);

                if (!needsOverview && !needsPortrait) continue;

                pending.Add(person);
                if (limit > 0 && pending.Count >= limit) break;
            }

            return pending;
        }

        private static bool HasBangumiId(Person person, string key)
        {
            string value;
            return person.ProviderIds.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value);
        }
    }
}
