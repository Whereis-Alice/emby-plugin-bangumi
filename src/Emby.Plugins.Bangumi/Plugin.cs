using System;
using Emby.Plugins.Bangumi.Api;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Bangumi
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>
    {
        private readonly ILogger _logger;
        private readonly BangumiApiClient _api;
        private readonly IApplicationHost _applicationHost;
        private readonly object _libraryManagerLock = new object();
        private ILibraryManager _libraryManager;
        private bool _libraryManagerResolved;

        public Plugin(IApplicationHost applicationHost, ILogManager logManager)
            : base(applicationHost)
        {
            _applicationHost = applicationHost;
            _logger = logManager.GetLogger(BangumiConstants.PluginName);
            // GetOptions is passed as a factory rather than a snapshot so that saving the
            // configuration page takes effect without restarting the server.
            _api = new BangumiApiClient(_logger, () => GetOptions());
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => BangumiConstants.PluginName;

        public override string Description =>
            "Fetches anime metadata from Bangumi (bgm.tv): series, seasons, episodes, artwork, staff and voice actors.";

        public ILogger Logger => _logger;

        public BangumiApiClient Api => _api;

        public PluginOptions Options => GetOptions();

        protected override void OnOptionsSaved(PluginOptions options)
        {
            // Proxy / token / user agent / timeout all live on the HttpClient handler,
            // which cannot be mutated after the first request. Drop it and rebuild lazily.
            _api.InvalidateTransport();
            _logger.Info(
                "Bangumi options saved (api={0}, proxy={1}, token={2}, episodeMode={3}, order={4})",
                options.ApiBaseUrl,
                string.IsNullOrWhiteSpace(options.ProxyUrl) ? "<system>" : options.ProxyUrl,
                string.IsNullOrWhiteSpace(options.AccessToken) ? "<none>" : "<set>",
                options.EpisodeNumberMode,
                options.ProviderOrder);
            base.OnOptionsSaved(options);
        }

        /// <summary>
        /// Providers are constructed by Emby's DI container, which does not know about this
        /// plugin instance. They resolve the shared client through here.
        /// </summary>
        internal static BangumiApiClient RequireApi()
        {
            var instance = Instance;
            if (instance == null)
            {
                throw new InvalidOperationException(
                    "Bangumi plugin instance is not available yet. This means Emby constructed a metadata " +
                    "provider before loading the plugin; please report it with the server log.");
            }

            return instance._api;
        }

        internal static PluginOptions CurrentOptions()
        {
            var instance = Instance;
            return instance != null ? instance.GetOptions() : new PluginOptions();
        }

        /// <summary>
        /// The library, when the host is willing to hand it over.
        ///
        /// Providers only receive an <see cref="MediaBrowser.Controller.Providers.ItemLookupInfo"/>,
        /// which carries the parsed name but not the original-language title another scraper may
        /// already have stored. That title is the single best Bangumi search key available, so the
        /// providers look the item back up by path. Resolution failure is not fatal anywhere:
        /// every caller treats null as "no extra hints".
        /// </summary>
        internal static ILibraryManager TryLibraryManager()
        {
            var instance = Instance;
            if (instance == null) return null;

            lock (instance._libraryManagerLock)
            {
                if (instance._libraryManagerResolved) return instance._libraryManager;
                instance._libraryManagerResolved = true;

                try
                {
                    if (instance._applicationHost != null)
                    {
                        instance._libraryManager = instance._applicationHost.TryResolve<ILibraryManager>();
                    }
                }
                catch (Exception ex)
                {
                    instance._logger.ErrorException("Bangumi could not resolve ILibraryManager", ex);
                }

                return instance._libraryManager;
            }
        }
    }
}