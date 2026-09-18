// Emby episode browser. Local Emby data only; links use the original item detail route.
// Pure selection rules are also exported to Node for regression tests.
(function () {
    "use strict";

    function numberOf(item) {
        return item.IndexNumber == null ? null : Number(item.IndexNumber);
    }
    function labelOf(item) {
        var n = numberOf(item);
        if (n == null) return "未编号";
        return String(n) + (item.IndexNumberEnd > n ? "–" + item.IndexNumberEnd : "");
    }
    function normalize(items) {
        var seen = {};
        return items.filter(function (item) {
            if (!item || !item.Id || item.Type !== "Episode" || item.IsMissing ||
                item.IsVirtualItem || item.LocationType === "Virtual" || seen[item.Id]) return false;
            seen[item.Id] = true;
            return true;
        }).sort(function (a, b) {
            var sa = a.ParentIndexNumber == null ? 1 : a.ParentIndexNumber;
            var sb = b.ParentIndexNumber == null ? 1 : b.ParentIndexNumber;
            return sa - sb || (numberOf(a) == null ? Infinity : numberOf(a)) -
                (numberOf(b) == null ? Infinity : numberOf(b)) || String(a.Id).localeCompare(String(b.Id));
        });
    }
    function stamp(item) {
        return Date.parse((item.UserData || {}).LastPlayedDate || "") || 0;
    }
    function recommendation(items) {
        if (!items.length) return null;
        var watched = items.filter(function (e) { return (e.UserData || {}).Played; });
        var watchedIndexes = watched.map(function (e) { return items.indexOf(e); });
        var lastWatchedIndex = watchedIndexes.length ? Math.max.apply(Math, watchedIndexes) : -1;
        var latestWatchedStamp = watched.reduce(function (latest, e) {
            return Math.max(latest, stamp(e));
        }, 0);
        var resumable = items.filter(function (e) {
            var u = e.UserData || {};
            if (u.Played || !(u.PlaybackPositionTicks > 0)) return false;
            var index = items.indexOf(e), playedAt = stamp(e);
            // Emby can retain a partial position after a later episode was watched.
            // Do not let that old residue pull the navigator backwards. A real replay
            // is still respected when its timestamp is newer than the latest watched
            // episode; without timestamps, episode order is the safest fallback.
            if (!watched.length) return true;
            if (playedAt > 0 && latestWatchedStamp > 0) return playedAt > latestWatchedStamp;
            return index > lastWatchedIndex;
        });
        if (resumable.length) return resumable.sort(function (a, b) {
            return stamp(b) - stamp(a) || items.indexOf(b) - items.indexOf(a);
        })[0];
        if (!watched.length) return items[0];
        // Some manually marked episodes have no LastPlayedDate. In that case use the last
        // watched episode in episode order, rather than jumping to an old unmarked episode 1.
        watched.sort(function (a, b) { return stamp(b) - stamp(a) || items.indexOf(b) - items.indexOf(a); });
        var last = watched[0], index = items.indexOf(last);
        for (var i = index + 1; i < items.length; i++) {
            if (!(items[i].UserData || {}).Played) return items[i];
        }
        return last;
    }
    function fingerprint(items) {
        return items.map(function (e) {
            var u = e.UserData || {};
            return [e.Id, !!u.Played, u.LastPlayedDate || "", Math.floor((u.PlaybackPositionTicks || 0) / 10000000)].join(":");
        }).join("|");
    }
    function selectInitial(items, saved, explicitId) {
        var id = explicitId || (saved && saved.progress === fingerprint(items) ? saved.itemId : null);
        return items.filter(function (e) { return e.Id === id; })[0] || recommendation(items);
    }
    function initialSeason(items, saved, explicitSeason) {
        var recommended = recommendation(items);
        return explicitSeason || (saved && saved.progress === fingerprint(items) ? saved.seasonId : null) ||
            (recommended && (recommended.SeasonId || "number-" + (recommended.ParentIndexNumber == null ? 1 : recommended.ParentIndexNumber)));
    }
    function pageSize(mode) { return mode === "numbers" ? 50 : 12; }
    function pageOf(items, id, mode) {
        var index = items.findIndex(function (e) { return e.Id === id; });
        return Math.floor(Math.max(0, index) / pageSize(mode));
    }
    function findNumber(items, text) {
        // Exact episode numbering, including combined E01-E02. No nearest-number guesses.
        if (!/^\d+(?:\.\d+)?$/.test(String(text).trim())) return null;
        var n = Number(text);
        return items.filter(function (e) {
            var start = numberOf(e);
            return start != null && n >= start && n <= (e.IndexNumberEnd || start);
        })[0] || null;
    }
    var model = { normalize: normalize, recommendation: recommendation, fingerprint: fingerprint,
        selectInitial: selectInitial, initialSeason: initialSeason, pageSize: pageSize, pageOf: pageOf, findNumber: findNumber, labelOf: labelOf };
    if (typeof module !== "undefined" && module.exports) module.exports = model;
    if (typeof window === "undefined" || !window.document) return;
    if (window.BangumiEpisodes) return;

    var active = null, timer = null, generation = 0, config = null, configAt = 0, configScope = null;
    var memory = {}, HIDDEN = "bgmui-episodesReplaced";
    function node(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = String(text);
        return e;
    }
    function read(key) {
        try { return JSON.parse(window.localStorage.getItem(key)) || memory[key] || {}; }
        catch (err) { return memory[key] || {}; }
    }
    function write(key, data) {
        memory[key] = data;
        try { window.localStorage.setItem(key, JSON.stringify(data)); } catch (err) { /* private browsing */ }
    }
    function route() {
        var hash = window.location.hash, match = /^#!?\/(?:item|details)\?/.test(hash) && /[?&]id=([^&]+)/.exec(hash);
        return match ? decodeURIComponent(match[1]) : null;
    }
    function visiblePage() {
        var pages = document.querySelectorAll(".itemView");
        for (var i = pages.length - 1; i >= 0; i--) {
            if (!pages[i].closest(".hide") && pages[i].getClientRects().length) return pages[i];
        }
        return null;
    }
    function scope(api) {
        return "bgmui:episodes:v1:" + encodeURIComponent(api.serverId()) + ":" + encodeURIComponent(api.getCurrentUserId());
    }
    function remember(ctx) {
        if (!ctx.group || !ctx.selected) return;
        var state = read(ctx.storage), history = state.history || {};
        var series = history[ctx.seriesId] || { seasons: {} };
        series.seasons = series.seasons || {};
        series.seasons[ctx.group.id] = { itemId: ctx.selected, progress: fingerprint(ctx.group.items) };
        series.seasonId = ctx.group.id;
        series.progress = fingerprint(ctx.items);
        series.updated = Date.now();
        history[ctx.seriesId] = series;
        // Bounded preferences: no tokens, media paths, or metadata blobs are stored here.
        var keys = Object.keys(history).sort(function (a, b) { return history[b].updated - history[a].updated; });
        keys.slice(100).forEach(function (k) { delete history[k]; });
        state.history = history;
        state.mode = ctx.mode;
        write(ctx.storage, state);
    }
    function cleanup() {
        if (!active) return;
        if (active.native) active.native.classList.remove(HIDDEN);
        if (active.root) active.root.remove();
        active = null;
    }
    function isCurrent(ctx) {
        return active === ctx && route() === ctx.id && ctx.page.isConnected &&
            scope(window.ApiClient) === ctx.storage && !ctx.page.closest(".hide");
    }
    function request(api, path, params) { return api.getJSON(api.getUrl(path, params || {})); }
    function options(api) {
        if (config && configScope === scope(api) && Date.now() - configAt < 60000) return Promise.resolve(config);
        return request(api, "Bangumi/Ui/Options").then(function (data) {
            config = data; configAt = Date.now(); configScope = scope(api); return data;
        });
    }
    function loadEpisodes(api, seriesId) {
        var items = [], offset = 0;
        function batch() {
            return request(api, "Shows/" + encodeURIComponent(seriesId) + "/Episodes", {
                UserId: api.getCurrentUserId(), StartIndex: offset, Limit: 250,
                Fields: "PrimaryImageAspectRatio,PremiereDate,UserDataLastPlayedDate,LocationType",
                IsMissing: false, IsVirtualItem: false, EnableUserData: true, ImageTypeLimit: 1
            }).then(function (data) {
                if (!data || !Array.isArray(data.Items)) throw new Error("Invalid episode response");
                items = items.concat(data.Items); offset += data.Items.length;
                if (data.Items.length && (data.TotalRecordCount == null ? data.Items.length === 250 : offset < data.TotalRecordCount)) {
                    if (offset >= 10000) throw new Error("Episode list exceeds supported limit");
                    return batch();
                }
                return normalize(items);
            });
        }
        return batch();
    }
    function groupsFor(items, seasons) {
        var groups = [], map = {};
        items.forEach(function (item) {
            var id = String(item.SeasonId || "number-" + (item.ParentIndexNumber == null ? 1 : item.ParentIndexNumber));
            if (!map[id]) {
                var season = seasons.filter(function (s) { return String(s.Id) === id; })[0];
                var n = season && season.IndexNumber != null ? season.IndexNumber : item.ParentIndexNumber;
                map[id] = { id: id, name: season ? season.Name : (n === 0 ? "特别篇" : "第 " + (n == null ? 1 : n) + " 季"), items: [] };
                groups.push(map[id]);
            }
            map[id].items.push(item);
        });
        return groups;
    }
    function chooseGroup(ctx, id, explicitId) {
        ctx.group = ctx.groups.filter(function (g) { return g.id === id; })[0] || ctx.groups[0];
        var series = (read(ctx.storage).history || {})[ctx.seriesId] || {};
        var saved = (series.seasons || {})[ctx.group.id];
        ctx.selected = selectInitial(ctx.group.items, saved, explicitId).Id;
        ctx.index = pageOf(ctx.group.items, ctx.selected, ctx.mode);
        remember(ctx);
    }
    function button(text, cls, action, title) {
        var b = node("button", "bgmui-epButton " + (cls || ""), text);
        b.type = "button";
        if (title) b.title = title;
        b.addEventListener("click", action);
        return b;
    }
    function picker(label, cls, entries, value, onChange) {
        var wrap = node("div", "bgmui-epPicker " + (cls || ""));
        var trigger = node("button", "bgmui-epPickerButton", "");
        trigger.type = "button";
        trigger.setAttribute("aria-label", label);
        trigger.setAttribute("aria-haspopup", "listbox");
        trigger.setAttribute("aria-expanded", "false");
        trigger.title = label;
        var menu = node("div", "bgmui-epPickerMenu");
        menu.setAttribute("role", "listbox");
        menu.setAttribute("aria-label", label);
        menu.hidden = true;
        var current = String(value);
        function close() {
            wrap.classList.remove("is-open");
            trigger.setAttribute("aria-expanded", "false");
            menu.hidden = true;
        }
        function open() {
            document.querySelectorAll(".bgmui-epPicker.is-open").forEach(function (e) {
                e.classList.remove("is-open");
                var b = e.querySelector(".bgmui-epPickerButton");
                var m = e.querySelector(".bgmui-epPickerMenu");
                if (b) b.setAttribute("aria-expanded", "false");
                if (m) m.hidden = true;
            });
            wrap.classList.add("is-open");
            trigger.setAttribute("aria-expanded", "true");
            menu.hidden = false;
        }
        function setCurrent(next) {
            current = String(next);
            var selected = entries.filter(function (e) { return String(e.value) === current; })[0] || entries[0];
            if (selected) trigger.textContent = selected.label;
            menu.querySelectorAll("[role=option]").forEach(function (e) {
                var isSelected = e.dataset.value === current;
                e.setAttribute("aria-selected", String(isSelected));
                e.classList.toggle("is-selected", isSelected);
            });
        }
        trigger.addEventListener("click", function (e) {
            e.stopPropagation();
            if (wrap.classList.contains("is-open")) close(); else open();
        });
        trigger.addEventListener("keydown", function (e) {
            if (e.key === "Escape") { close(); return; }
            if (e.key === "ArrowDown" || e.key === "Enter" || e.key === " ") {
                e.preventDefault(); open();
                var selected = menu.querySelector("[aria-selected=true]");
                if (selected) selected.focus();
            }
        });
        entries.forEach(function (entry) {
            var option = node("button", "bgmui-epPickerOption", entry.label);
            option.type = "button";
            option.dataset.value = String(entry.value);
            option.setAttribute("role", "option");
            option.addEventListener("click", function (e) {
                e.stopPropagation();
                var changed = current !== String(entry.value);
                setCurrent(entry.value); close(); trigger.focus();
                if (changed) onChange(entry.value);
            });
            option.addEventListener("keydown", function (e) {
                if (e.key === "Escape") { e.preventDefault(); close(); trigger.focus(); }
            });
            menu.appendChild(option);
        });
        wrap.appendChild(trigger); wrap.appendChild(menu); setCurrent(current);
        return wrap;
    }
    function itemHref(ctx, item) {
        return "#!/item?id=" + encodeURIComponent(item.Id) + "&serverId=" + encodeURIComponent(ctx.api.serverId());
    }
    function playbackItem(ctx, item) {
        return Object.assign({}, item, {
            ServerId: item.ServerId || ctx.api.serverId(),
            MediaType: item.MediaType || "Video"
        });
    }
    function trackSelection(ctx, item, card) {
        ctx.selected = item.Id;
        ctx.root.querySelectorAll(".bgmui-epCard.is-selected").forEach(function (e) {
            e.classList.remove("is-selected"); e.removeAttribute("aria-current");
        });
        card.classList.add("is-selected"); card.setAttribute("aria-current", "true");
        remember(ctx);
    }
    function playEpisode(ctx, item, card) {
        if (ctx.playing) return;
        ctx.playing = true;
        trackSelection(ctx, item, card);
        Emby.importModule("./modules/common/playback/playbackactions.js").then(function (module) {
            // Emby.importModule resolves the module's default export in 4.10. Older clients
            // may return the module namespace, so accept both shapes without hiding playback.
            var actions = module && module.default ? module.default : module;
            if (!actions || typeof actions.play !== "function") throw new Error("Emby playback actions unavailable");
            return actions.play({ items: [playbackItem(ctx, item)], fullscreen: true });
        }).catch(function (err) {
            if (window.BangumiUiDebug) console.warn("[bangumi-episodes] playback failed", err);
        }).then(function () {
            window.setTimeout(function () { ctx.playing = false; }, 500);
        });
    }
    function markPlayed(ctx, item, card, mark) {
        if (mark.disabled) return;
        var userId = ctx.api.getCurrentUserId(), wasPlayed = !!(item.UserData || {}).Played;
        var method = wasPlayed ? ctx.api.markUnplayed : ctx.api.markPlayed;
        if (typeof method !== "function") return;
        mark.disabled = true;
        Promise.resolve(method.call(ctx.api, userId, [item.Id])).then(function () {
            item.UserData = Object.assign({}, item.UserData || {}, wasPlayed ? {
                Played: false, PlaybackPositionTicks: 0, LastPlayedDate: ""
            } : {
                Played: true, PlaybackPositionTicks: 0, LastPlayedDate: new Date().toISOString()
            });
            remember(ctx);
            render(ctx, "card");
        }).catch(function (err) {
            if (window.BangumiUiDebug) console.warn("[bangumi-episodes] mark played failed", err);
            mark.disabled = false;
        });
    }
    function imageFor(ctx, item) {
        var tags = item.ImageTags || {}, id = item.Id, tag = tags.Primary;
        var poster = item.PrimaryImageAspectRatio > 0 && item.PrimaryImageAspectRatio < 1.2;
        if (!tag) {
            id = item.PrimaryImageItemId || item.SeriesId || ctx.seriesId;
            tag = item.PrimaryImageTag || item.SeriesPrimaryImageTag;
            poster = true;
        }
        return { url: tag ? ctx.api.getImageUrl(id, { type: "Primary", tag: tag, maxWidth: 600, quality: 90 }) : null,
            poster: poster };
    }
    function card(ctx, item) {
        var u = item.UserData || {}, current = item.Id === ctx.selected;
        var link = node("article", "bgmui-epCard" + (current ? " is-selected" : "") + (u.Played ? " is-watched" : ""));
        link.dataset.episode = item.Id;
        link.tabIndex = 0;
        if (current) link.setAttribute("aria-current", "true");
        var status = u.Played ? "已看" : u.PlaybackPositionTicks > 0 ? "观看中" : "未看";
        var name = labelOf(item) + ". " + item.Name;
        link.title = name + " · " + status;
        link.setAttribute("aria-label", name + "，" + status + "，播放分集");
        var visual = node("div", "bgmui-epVisual");
        var numeric = node("span", "bgmui-epNumber", labelOf(item));
        visual.appendChild(numeric);
        if (ctx.mode !== "numbers") {
            var picture = imageFor(ctx, item);
            if (picture.url) {
                var img = node("img", "bgmui-epImage" + (picture.poster ? " is-poster" : ""));
                img.alt = ""; img.loading = "lazy"; img.decoding = "async"; img.src = picture.url;
                img.addEventListener("error", function () { img.remove(); });
                visual.appendChild(img);
            }
            var enter = node("span", "bgmui-epEnter", "▶");
            enter.setAttribute("aria-hidden", "true");
            visual.appendChild(enter);
        }
        var check = node("button", "bgmui-epWatchedToggle" + (u.Played ? " is-watched" : ""), "✓");
        check.type = "button";
        check.title = u.Played ? "标记为未看" : "快速标记为已看";
        check.setAttribute("aria-label", name + "：" + check.title);
        check.setAttribute("aria-pressed", String(!!u.Played));
        check.addEventListener("click", function (e) {
            e.preventDefault(); e.stopPropagation(); markPlayed(ctx, item, link, check);
        });
        visual.appendChild(check);
        if (!u.Played && u.PlaybackPositionTicks > 0 && item.RunTimeTicks > 0) {
            var bar = node("span", "bgmui-epProgress");
            bar.style.width = Math.min(100, u.PlaybackPositionTicks / item.RunTimeTicks * 100) + "%";
            visual.appendChild(bar);
        }
        var details = node("a", "bgmui-epDetails", "详情");
        details.href = itemHref(ctx, item);
        details.title = "打开分集详情，可选择版本、音轨和字幕";
        details.setAttribute("aria-label", name + "：打开详情");
        details.addEventListener("click", function (e) { e.stopPropagation(); });
        visual.appendChild(details);
        link.appendChild(visual);
        var title = node("div", "bgmui-epName", name);
        link.appendChild(title);
        var facts = [], date = String(item.PremiereDate || "").slice(0, 10);
        if (/^\d{4}-\d{2}-\d{2}$/.test(date)) facts.push(date.replace(/-/g, "/"));
        if (item.RunTimeTicks > 0) facts.push(Math.round(item.RunTimeTicks / 600000000) + " 分钟");
        link.appendChild(node("div", "bgmui-epMeta", facts.join(" · ")));
        link.addEventListener("click", function (e) {
            if (e.target.closest(".bgmui-epDetails")) return;
            e.preventDefault(); e.stopPropagation(); playEpisode(ctx, item, link);
        });
        link.addEventListener("keydown", function (e) {
            if (e.key !== "Enter" && e.key !== " ") return;
            if (e.target.closest(".bgmui-epDetails")) return;
            e.preventDefault(); e.stopPropagation(); playEpisode(ctx, item, link);
        });
        link.addEventListener("focus", function () { trackSelection(ctx, item, link); });
        return link;
    }
    function render(ctx, focus) {
        if (!isCurrent(ctx)) return;
        var root = ctx.root;
        root.replaceChildren();
        root.dataset.mode = ctx.mode;
        var items = ctx.group.items, size = pageSize(ctx.mode);
        var pages = Math.ceil(items.length / size);
        ctx.index = Math.max(0, Math.min(ctx.index, pages - 1));
        var heading = node("div", "bgmui-epHeading");
        var headingText = node("div", "bgmui-epHeadingText");
        headingText.appendChild(node("h2", "sectionTitle", "选集"));
        headingText.appendChild(node("span", "bgmui-epCount", ctx.group.name + " · " + items.length + " 集"));
        heading.appendChild(headingText);
        var modes = node("div", "bgmui-epModes");
        modes.setAttribute("role", "group"); modes.setAttribute("aria-label", "选集显示方式");
        [["cards", "封面"], ["numbers", "数字"]].forEach(function (entry) {
            var b = button(entry[1], "", function () {
                ctx.mode = entry[0]; ctx.index = pageOf(items, ctx.selected, ctx.mode);
                remember(ctx); render(ctx, "mode-" + ctx.mode);
            });
            b.setAttribute("aria-pressed", String(ctx.mode === entry[0])); b.dataset.control = "mode-" + entry[0];
            modes.appendChild(b);
        });
        heading.appendChild(modes); root.appendChild(heading);

        var toolbar = node("div", "bgmui-epToolbar");
        var ranges = node("div", "bgmui-epRanges");
        if (ctx.groups.length > 1) {
            var seasons = picker("选择季度", "bgmui-epSeason", ctx.groups.map(function (g) {
                return { value: g.id, label: g.name };
            }), ctx.group.id, function (value) { chooseGroup(ctx, value); render(ctx, "season"); });
            seasons.querySelector(".bgmui-epPickerButton").dataset.control = "season";
            ranges.appendChild(seasons);
        }
        var prev = button("‹", "bgmui-epArrow", function () { turn(ctx, -1); }, "上一组分集");
        prev.setAttribute("aria-label", "上一组分集"); prev.disabled = ctx.index === 0; prev.dataset.control = "prev";
        ranges.appendChild(prev);
        var range = picker("选择集数区间", "", Array.from({ length: pages }, function (_, i) {
            var slice = items.slice(i * size, (i + 1) * size);
            return { value: String(i), label: labelOf(slice[0]) + " – " + labelOf(slice[slice.length - 1]) + " 集" };
        }), String(ctx.index), function (value) {
            ctx.index = Number(value); ctx.selected = items[ctx.index * size].Id;
            remember(ctx); render(ctx, "range");
        });
        range.querySelector(".bgmui-epPickerButton").dataset.control = "range";
        ranges.appendChild(range);
        var next = button("›", "bgmui-epArrow", function () { turn(ctx, 1); }, "下一组分集");
        next.setAttribute("aria-label", "下一组分集"); next.disabled = ctx.index >= pages - 1; next.dataset.control = "next";
        ranges.appendChild(next); toolbar.appendChild(ranges);

        var actions = node("div", "bgmui-epActions");
        var recommended = recommendation(items);
        var hasResume = recommended && !(recommended.UserData || {}).Played &&
            (recommended.UserData || {}).PlaybackPositionTicks > 0;
        var progress = button((hasResume ? "继续观看 · " : "下一集 · ") + labelOf(recommended), "bgmui-epResume", function () {
            select(ctx, recommended.Id, true);
        }, hasResume ? "定位到未看完的分集" : "定位到接下来要看的分集");
        progress.dataset.control = "progress"; actions.appendChild(progress);
        var form = node("form", "bgmui-epJump");
        var input = node("input", "bgmui-epInput");
        input.type = "text"; input.inputMode = "decimal"; input.placeholder = "集号";
        input.setAttribute("aria-label", "跳转到集号"); input.maxLength = 8; input.dataset.control = "jump";
        form.appendChild(input);
        var submit = node("button", "bgmui-epButton", "跳转"); submit.type = "submit"; form.appendChild(submit);
        form.addEventListener("submit", function (e) {
            e.preventDefault(); var match = findNumber(items, input.value);
            if (match) select(ctx, match.Id, true);
            else {
                message.textContent = "本季没有入库的第 " + (input.value.trim() || "？") + " 集";
                input.setAttribute("aria-invalid", "true"); input.focus();
            }
        });
        actions.appendChild(form); toolbar.appendChild(actions); root.appendChild(toolbar);
        var message = node("div", "bgmui-epMessage"); message.setAttribute("role", "status");
        message.setAttribute("aria-live", "polite"); root.appendChild(message);
        var grid = node("div", "bgmui-epGrid");
        grid.setAttribute("aria-label", ctx.group.name + "分集");
        items.slice(ctx.index * size, (ctx.index + 1) * size).forEach(function (item) { grid.appendChild(card(ctx, item)); });
        root.appendChild(grid);
        var foot = node("div", "bgmui-epFoot");
        foot.appendChild(node("span", "", "✓ 已看 · 下划线表示观看进度"));
        foot.appendChild(node("span", "", (ctx.index + 1) + " / " + pages));
        root.appendChild(foot);
        if (focus) {
            var target = focus === "card" ? root.querySelector(".bgmui-epCard.is-selected") :
                root.querySelector('[data-control="' + focus + '"]');
            if (target && !target.disabled) target.focus({ preventScroll: focus !== "card" });
        }
    }
    function select(ctx, id, focus) {
        ctx.selected = id; ctx.index = pageOf(ctx.group.items, id, ctx.mode);
        remember(ctx); render(ctx, focus ? "card" : null);
    }
    function turn(ctx, delta) {
        ctx.index += delta;
        ctx.selected = ctx.group.items[ctx.index * pageSize(ctx.mode)].Id;
        remember(ctx); render(ctx, delta > 0 ? "next" : "prev");
    }
    function refresh(ctx) {
        if (ctx.loading) return;
        ctx.loading = true;
        var api = ctx.api;
        Promise.all([loadEpisodes(api, ctx.seriesId), request(api, "Shows/" + encodeURIComponent(ctx.seriesId) + "/Seasons", {
            UserId: api.getCurrentUserId(), EnableImages: false, EnableUserData: false
        })]).then(function (results) {
            if (!isCurrent(ctx)) return;
            var groups = groupsFor(results[0], results[1].Items || []);
            if (!groups.length) { cleanup(); return; }
            var dataFingerprint = JSON.stringify(results);
            if (ctx.loaded && ctx.dataFingerprint === dataFingerprint) return;
            ctx.dataFingerprint = dataFingerprint;
            var first = !ctx.loaded;
            ctx.groups = groups;
            ctx.items = results[0];
            var state = read(ctx.storage), saved = (state.history || {})[ctx.seriesId] || {};
            ctx.mode = ctx.mode || (state.mode === "numbers" ? "numbers" : "cards");
            var explicitSeason = ctx.item.Type === "Season" ? ctx.item.Id : ctx.item.Type === "Episode" ? ctx.item.SeasonId : null;
            chooseGroup(ctx, initialSeason(ctx.items, saved, explicitSeason),
                first && ctx.item.Type === "Episode" ? ctx.item.Id : null);
            if (!ctx.root) {
                ctx.root = node("section", "bgmui-episodes verticalSection padded-left padded-left-page padded-right");
                ctx.root.setAttribute("aria-label", "增强选集");
                ctx.native.parentNode.insertBefore(ctx.root, ctx.native);
            }
            render(ctx);
            // Commit replacement only after a full successful render. Network/DOM failures leave
            // Emby's original section available. We never modify its children or playback handlers.
            ctx.native.classList.add(HIDDEN);
            ctx.loaded = true;
        }).catch(function (err) {
            if (!isCurrent(ctx)) return;
            if (window.BangumiUiDebug) console.warn("[bangumi-episodes]", err);
            if (!ctx.loaded) { ctx.failedAt = Date.now(); ctx.native.classList.remove(HIDDEN); if (ctx.root) ctx.root.remove(); ctx.root = null; }
        }).then(function () { ctx.loading = false; ctx.checkedAt = Date.now(); });
    }
    function run(recheck) {
        var api = window.ApiClient, id = route(), page = visiblePage();
        if (!api || !api.getCurrentUserId || !api.getCurrentUserId() || !id || !page) { cleanup(); generation++; return; }
        var storage = scope(api);
        if (active && active.id === id && active.page === page && active.storage === storage) {
            if (active.item && active.native) {
                if ((recheck && Date.now() - (active.checkedAt || 0) > 15000) ||
                    (!active.loaded && !active.loading && Date.now() - (active.failedAt || 0) > 30000)) refresh(active);
                return;
            }
            if (active.loading || active.loaded || Date.now() - (active.failedAt || 0) < 30000) return;
        }
        cleanup();
        var serial = ++generation;
        var ctx = { id: id, page: page, storage: storage, api: api, loading: true };
        active = ctx;
        Promise.all([options(api), api.getItem(api.getCurrentUserId(), id)]).then(function (result) {
            if (serial !== generation || !isCurrent(ctx)) return;
            var item = result[1];
            if (!result[0].EpisodeNavigator || !item || !/^(Series|Season|Episode)$/.test(item.Type)) {
                ctx.loaded = true; ctx.loading = false; return;
            }
            ctx.item = item;
            ctx.seriesId = item.Type === "Series" ? item.Id : item.SeriesId;
            ctx.native = page.querySelector(item.Type === "Series" ? ".seriesItemsSection" :
                item.Type === "Season" ? ".trackListSection" : ".moreFromSeasonSection");
            if (!ctx.native || !ctx.seriesId) { ctx.failedAt = Date.now(); ctx.loading = false; return; }
            ctx.loading = false;
            refresh(ctx);
        }).catch(function () {
            if (active === ctx) { ctx.failedAt = Date.now(); ctx.loading = false; }
        });
    }
    function schedule() {
        if (timer) return;
        timer = setTimeout(function () { timer = null; try { run(false); } catch (err) { cleanup(); } }, 120);
    }
    function recheck() {
        if (document.hidden) return;
        var editing = active && active.root && active.root.contains(document.activeElement) &&
            /^(INPUT|SELECT)$/.test(document.activeElement.tagName);
        try { if (!editing) run(true); }
        catch (err) { cleanup(); }
    }
    document.addEventListener("click", function (e) {
        if (e.target.closest && e.target.closest(".bgmui-epPicker")) return;
        document.querySelectorAll(".bgmui-epPicker.is-open").forEach(function (pickerRoot) {
            pickerRoot.classList.remove("is-open");
            var trigger = pickerRoot.querySelector(".bgmui-epPickerButton");
            var menu = pickerRoot.querySelector(".bgmui-epPickerMenu");
            if (trigger) trigger.setAttribute("aria-expanded", "false");
            if (menu) menu.hidden = true;
        });
    }, true);
    window.BangumiEpisodes = { refresh: recheck };
    window.addEventListener("hashchange", schedule);
    document.addEventListener("viewshow", schedule, true);
    window.addEventListener("focus", recheck);
    document.addEventListener("visibilitychange", recheck);
    new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
    schedule();
})();
