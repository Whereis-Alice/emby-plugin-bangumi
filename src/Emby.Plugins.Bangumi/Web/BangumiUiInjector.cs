using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Bangumi.Web
{
    /// <summary>
    /// Keeps the one line the browser needs inside Emby's index.html, so that installing the
    /// plugin is the whole installation.
    ///
    /// Emby lets a plugin add server endpoints, scheduled tasks and dashboard pages, but it has no
    /// hook for shipping script to the web client: the branding options carry CSS only, and CSS
    /// cannot tell one people card from another (the native cards are structurally identical, no
    /// data-type, no data-role), which is the whole reason this plugin renders its own sections.
    /// Editing <c>dashboard-ui/index.html</c> is therefore the only way in, and asking every user to
    /// run a PowerShell script by hand is both a Windows-only answer and something an Emby upgrade
    /// silently undoes.
    ///
    /// So the plugin does it itself, on every server start:
    ///
    /// * idempotent - the tag carries a marker attribute, an up-to-date file is left untouched;
    /// * self-healing - an upgrade that replaces index.html loses the marker, the next start puts
    ///   it back;
    /// * reversible - turning the feature off removes the line again, and the file is copied to
    ///   <c>index.html.bangumi-bak-&lt;timestamp&gt;</c> before any write (a failed backup aborts the
    ///   write, and no backup is ever deleted);
    /// * harmless when it cannot work - a read-only web directory, a missing file or an index.html
    ///   without a &lt;/head&gt; produce one warning naming the manual fallback, never an exception
    ///   into the host.
    /// </summary>
    internal static class BangumiUiInjector
    {
        internal const string MarkerAttribute = "data-bangumi-ui-inject";

        /// <summary>
        /// Relative on purpose. index.html is served from <c>&lt;base&gt;/web/</c>, so "../emby/..."
        /// resolves to the API root whether or not the server sits behind a reverse proxy with a
        /// base url, while a leading slash only works for a server mounted at the root.
        /// </summary>
        private const string ScriptTag =
            "<script src=\"../emby/Bangumi/Ui/bangumi-ui.js\" " + MarkerAttribute + "=\"1\"></script>";

        private const string Anchor = "</head>";

        private const string BackupInfix = ".bangumi-bak-";

        /// <summary>Matches any generation of the injected tag, including the leading indent.</summary>
        private static readonly Regex ExistingTag = new Regex(
            @"[ \t]*<script\b[^>]*\bdata-bangumi-ui-inject\b[^>]*>\s*</script>[ \t]*(\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly object Gate = new object();

        /// <summary>
        /// Brings index.html in line with the current options. Called from the server entry point
        /// at startup and again whenever the configuration page is saved, so toggling the feature
        /// does not need a restart. Never throws.
        /// </summary>
        internal static void Apply(PluginOptions options, ILogger logger)
        {
            if (logger == null) return;
            if (options == null) options = new PluginOptions();

            if (!options.UiAutoInjectScript)
            {
                logger.Debug("Bangumi UI: 自动注入已关闭，index.html 保持现状。");
                return;
            }

            lock (Gate)
            {
                try
                {
                    ApplyCore(options, logger);
                }
                catch (Exception ex)
                {
                    logger.ErrorException(
                        "Bangumi UI: 自动注入 index.html 失败，条目页面的角色栏不会出现。" +
                        "可以手动运行 scripts/inject-ui.ps1，或在插件设置里关掉「自动注入前端脚本」。",
                        ex);
                }
            }
        }

        private static void ApplyCore(PluginOptions options, ILogger logger)
        {
            var candidates = Candidates(options);
            string path = null;
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) { path = candidate; break; }
            }

            if (path == null)
            {
                logger.Warn(
                    "Bangumi UI: 找不到 dashboard-ui/index.html，跳过自动注入（刮削不受影响）。" +
                    "请在插件设置的「dashboard-ui 目录」里填绝对路径。试过：{0}",
                    string.Join(" | ", candidates));
                return;
            }

            var hadBom = HasUtf8Bom(path);
            var original = File.ReadAllText(path);
            var wanted = options.EnableBangumiUi;

            if (wanted && original.Contains(ScriptTag))
            {
                logger.Debug("Bangumi UI: {0} 已是最新的注入状态。", path);
                return;
            }

            var stripped = ExistingTag.Replace(original, string.Empty);
            string patched;

            if (wanted)
            {
                var at = stripped.IndexOf(Anchor, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    logger.Warn(
                        "Bangumi UI: {0} 里没有 </head>，不敢改，跳过自动注入。",
                        path);
                    return;
                }

                patched = stripped.Substring(0, at) + ScriptTag + stripped.Substring(at);
            }
            else
            {
                if (stripped == original)
                {
                    logger.Debug("Bangumi UI: 独立栏位已关闭，index.html 里本来也没有注入行。");
                    return;
                }

                patched = stripped;
            }

            if (patched == original) return;

            // A backup that cannot be written aborts the edit: index.html belongs to the server,
            // not to this plugin, and there must always be a way back.
            Backup(path, logger);
            WriteAtomic(path, patched, hadBom);

            if (wanted)
            {
                logger.Info(
                    "Bangumi UI: 已把前端脚本注入 {0}。浏览器请强制刷新一次（Ctrl+F5）；" +
                    "Emby 升级覆盖 index.html 后，下次启动会自动补回。",
                    path);
            }
            else
            {
                logger.Info("Bangumi UI: 已从 {0} 移除前端脚本引用。", path);
            }
        }

        /// <summary>
        /// Where index.html can live, most explicit first. The manual option wins, then the paths
        /// the host reports (which covers the Windows portable and installer layouts, the official
        /// Docker image at /system, the Linux packages under /opt/emby-server/system and the
        /// Synology package target), then two absolute fallbacks for hosts that report something
        /// unexpected.
        /// </summary>
        private static List<string> Candidates(PluginOptions options)
        {
            var list = new List<string>();

            var manual = options.UiDashboardDirectory;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                manual = manual.Trim().Trim('"');
                list.Add(manual.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? manual
                    : Path.Combine(manual, "index.html"));
            }

            var paths = Plugin.TryResolve<IApplicationPaths>();
            if (paths != null)
            {
                AddLayouts(list, paths.ProgramSystemPath);

                var serverPaths = paths as MediaBrowser.Controller.IServerApplicationPaths;
                if (serverPaths != null) AddLayouts(list, serverPaths.ApplicationResourcesPath);
            }

            AddLayouts(list, AppContext.BaseDirectory);
            list.Add("/system/dashboard-ui/index.html");
            list.Add("/opt/emby-server/system/dashboard-ui/index.html");

            var unique = new List<string>();
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (!unique.Contains(item, StringComparer.OrdinalIgnoreCase)) unique.Add(item);
            }

            return unique;
        }

        private static void AddLayouts(List<string> list, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;

            list.Add(Path.Combine(directory, "dashboard-ui", "index.html"));

            // Some layouts keep dashboard-ui next to the binaries directory rather than inside it.
            try
            {
                var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrWhiteSpace(parent)) list.Add(Path.Combine(parent, "dashboard-ui", "index.html"));
            }
            catch (ArgumentException)
            {
                // A path the runtime will not parse is simply not a candidate.
            }
        }

        private static void Backup(string path, ILogger logger)
        {
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var destination = path + BackupInfix + stamp;
                File.Copy(path, destination, true);
                logger.Info("Bangumi UI: index.html 已备份到 {0}", destination);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "无法备份 " + path + "，为安全起见放弃本次注入（目录可能是只读的）。",
                    ex);
            }
        }

        private static void WriteAtomic(string path, string content, bool withBom)
        {
            var temp = path + ".bangumi-tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(withBom));
            File.Move(temp, path, true);
        }

        private static bool HasUtf8Bom(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[3];
                    if (stream.Read(head, 0, 3) < 3) return false;
                    return head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
