using System;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Emby.Plugins.Bangumi.Web;

namespace Emby.Plugins.Bangumi.Tasks
{
    /// <summary>
    /// Runs <see cref="BangumiUiInjector"/> once per server start.
    ///
    /// This exists as its own entry point rather than as part of
    /// <see cref="BangumiUiPrewarmEntryPoint"/> because the two have nothing in common but their
    /// timing: one talks to the library and the network, this one touches a single file on disk and
    /// is finished in a millisecond.
    /// </summary>
    public class BangumiUiInjectEntryPoint : IServerEntryPoint, IDisposable
    {
        private readonly ILogger _logger;

        public BangumiUiInjectEntryPoint(ILogManager logManager)
        {
            _logger = logManager.GetLogger(BangumiConstants.PluginName);
        }

        public void Run()
        {
            BangumiUiInjector.Apply(Plugin.CurrentOptions(), _logger);
        }

        public void Dispose()
        {
        }
    }
}
