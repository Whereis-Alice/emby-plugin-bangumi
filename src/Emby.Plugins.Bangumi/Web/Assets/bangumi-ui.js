// bangumi-ui.js - injected into dashboard-ui/index.html by scripts/inject-ui.ps1.
//
// Why a script and not a theme: Emby renders cast, characters and crew as one row of
// identical cards. cardbuilder.js writes the second line of a card from item.Role or
// item.PersonType and puts neither in the DOM as an attribute, and the People array the
// client receives per item carries only Name / Id / Role / Type / PrimaryImageTag - no
// ProviderIds. There is nothing in the markup that tells a character apart from a voice
// actor, so no stylesheet and no theme can split that row. This script asks the plugin for
// the Bangumi shape instead and draws its own sections next to the native one.
//
// Ground rules, because this code lives inside a page it does not own:
//   * never throw into Emby - every entry point is wrapped
//   * never touch existing nodes except to toggle one class on the native people section
//   * no dependency on Emby internals beyond window.ApiClient and a handful of CSS class
//     names used for typography, so a server upgrade degrades to plain styling at worst

(function () {
    "use strict";

    var ROOT_CLASS = "bangumiUiSections";
    var STYLE_ID = "bangumiUiStyle";
    var NATIVE_HIDDEN_CLASS = "bangumiUiNativeHidden";

    var detailCache = {};
    var inFlight = {};
    var entityCache = {};
    var scheduleTimer = null;

    // A cold subject costs one Bangumi request per character, so a render can still be in
    // flight when the user opens the next show. Every run takes a token and drops its own
    // result once a newer run has started, instead of a single busy flag that would either
    // block the new page or let a stale payload win.
    var renderToken = 0;

    function log() {
        if (!window.BangumiUiDebug || !window.console) return;
        var args = Array.prototype.slice.call(arguments);
        args.unshift("[bangumi-ui]");
        window.console.log.apply(window.console, args);
    }

    // ---------------------------------------------------------------- helpers

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text != null && text !== "") node.textContent = String(text);
        return node;
    }

    function firstText() {
        for (var i = 0; i < arguments.length; i++) {
            var value = arguments[i];
            if (value != null && String(value).trim() !== "") return String(value).trim();
        }
        return "";
    }

    function imageUrl(raw, layout) {
        if (!raw) return null;
        if (!layout || !layout.ProxyImages) return raw;

        try {
            return window.ApiClient.getUrl("Bangumi/Ui/Image", { url: raw });
        } catch (err) {
            return raw;
        }
    }

    function setPoster(node, url) {
        if (!url) {
            node.classList.add("bgmui-posterEmpty");
            return;
        }

        // Preload so that a blocked or 404 image leaves the placeholder instead of a broken box.
        var probe = new Image();
        probe.onload = function () { node.style.backgroundImage = "url(" + JSON.stringify(url) + ")"; };
        probe.onerror = function () { node.classList.add("bgmui-posterEmpty"); };
        probe.src = url;
    }

    function externalLink(href, text) {
        var link = el("a", "bgmui-link", text);
        link.href = href;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        return link;
    }

    // ---------------------------------------------------------------- page probing

    function currentItemId() {
        var hash = window.location.hash || "";
        if (!/^#!?\/(item|details)\?/.test(hash)) return null;

        var match = /[?&]id=([^&]+)/.exec(hash);
        if (!match) return null;

        var id = decodeURIComponent(match[1]).trim();
        return /^[0-9]+$/.test(id) ? id : null;
    }

    /// The item view Emby is currently showing, plus the native people section we anchor to.
    /// Emby keeps previously visited views in the DOM, so the newest visible one wins.
    function findTarget() {
        var sections = document.querySelectorAll(".peopleSection");

        for (var i = sections.length - 1; i >= 0; i--) {
            var section = sections[i];
            var page = section.closest(".page, [data-role=page], .view");
            if (!page || page.classList.contains("hide")) continue;

            return { page: page, people: section };
        }

        return null;
    }

    // ---------------------------------------------------------------- style

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        if (!document.head) return;

        var link = document.createElement("link");
        link.id = STYLE_ID;
        link.rel = "stylesheet";

        try {
            link.href = window.ApiClient.getUrl("Bangumi/Ui/bangumi-ui.css");
        } catch (err) {
            link.href = "/emby/Bangumi/Ui/bangumi-ui.css";
        }

        document.head.appendChild(link);
    }

    // ---------------------------------------------------------------- data

    function loadDetail(itemId, refresh, nameBudget) {
        // Budget is part of the key: the fast pass and the translated pass are two payloads,
        // both on the server and here.
        var key = itemId + ":" + nameBudget;

        if (!refresh && detailCache[key]) return Promise.resolve(detailCache[key]);
        if (inFlight[key]) return inFlight[key];

        var params = { NameBudget: nameBudget };
        if (refresh) params.Refresh = true;
        var url = window.ApiClient.getUrl("Bangumi/Items/" + itemId + "/Detail", params);

        var promise = window.ApiClient.getJSON(url).then(function (data) {
            delete inFlight[key];
            detailCache[key] = data;
            return data;
        }, function (err) {
            delete inFlight[key];
            log("detail request failed", key, err);
            return null;
        });

        inFlight[key] = promise;
        return promise;
    }

    function forgetDetail(itemId) {
        var prefix = itemId + ":";
        for (var key in detailCache) {
            if (detailCache.hasOwnProperty(key) && key.indexOf(prefix) === 0) delete detailCache[key];
        }
    }

    function loadEntity(kind, id) {
        var key = kind + ":" + id;
        if (entityCache[key]) return Promise.resolve(entityCache[key]);

        var path = kind === "character" ? "Bangumi/Characters/" : "Bangumi/Persons/";
        var url = window.ApiClient.getUrl(path + id);

        return window.ApiClient.getJSON(url).then(function (data) {
            entityCache[key] = data;
            return data;
        }, function (err) {
            log("entity request failed", key, err);
            return null;
        });
    }

    // ---------------------------------------------------------------- sections

    function section(title, subtitle) {
        var wrapper = el("div", "verticalSection verticalSection-cards bgmui-section");

        var heading = el("h2", "sectionTitle sectionTitle-cards padded-left padded-left-page padded-right bgmui-title");
        heading.appendChild(el("span", null, title));
        if (subtitle) heading.appendChild(el("span", "bgmui-count", subtitle));
        wrapper.appendChild(heading);

        return wrapper;
    }

    function cardRow() {
        return el("div", "bgmui-row padded-left padded-left-page padded-right");
    }

    function card(kind, id, posterUrl, primary, secondary, layout) {
        var button = el("button", "bgmui-card");
        button.type = "button";

        var poster = el("div", "bgmui-poster");
        setPoster(poster, imageUrl(posterUrl, layout));
        button.appendChild(poster);

        button.appendChild(el("div", "bgmui-name", primary));
        if (secondary) button.appendChild(el("div", "bgmui-sub", secondary));

        var label = primary + (secondary ? " - " + secondary : "");
        button.setAttribute("aria-label", label);
        // 副行被 CSS 截断到 3 行, title 保证鼠标悬停能看到完整内容
        button.title = label;
        button.addEventListener("click", function () {
            openEntity(kind, id, primary);
        });

        return button;
    }

    function characterSections(data, root) {
        var characters = data.Characters || [];
        if (!characters.length) return;

        var layout = data.Layout || {};

        if (!layout.GroupCharactersByRelation) {
            appendCharacterRow(root, "角色", characters, layout);
            return;
        }

        // Bangumi only ever uses these three, but an unknown relation must still show up.
        var buckets = [];
        var index = {};

        characters.forEach(function (character) {
            var key = firstText(character.Relation, "其他");
            if (!index[key]) {
                index[key] = [];
                buckets.push(key);
            }
            index[key].push(character);
        });

        buckets.forEach(function (key) {
            appendCharacterRow(root, "角色 · " + key, index[key], layout);
        });
    }

    function appendCharacterRow(root, title, characters, layout) {
        var wrapper = section(title, String(characters.length));
        var row = cardRow();

        characters.forEach(function (character) {
            var name = firstText(character.NameCn, character.Name, "未命名");
            var actors = (character.Actors || []).map(function (actor) {
                return firstText(actor.NameCn, actor.Name);
            }).filter(function (value) { return value !== ""; });

            row.appendChild(card(
                "character",
                character.Id,
                character.Image,
                name,
                actors.length ? "CV " + actors.join(" / ") : null,
                layout));
        });

        wrapper.appendChild(row);
        root.appendChild(wrapper);
    }

    function voiceActorSection(data, root) {
        var layout = data.Layout || {};
        if (!layout.ShowVoiceActors) return;

        var actors = data.VoiceActors || [];
        if (!actors.length) return;

        var wrapper = section("声优", String(actors.length));
        var row = cardRow();

        actors.forEach(function (actor) {
            row.appendChild(card(
                "person",
                actor.Id,
                actor.Image,
                firstText(actor.NameCn, actor.Name, "未命名"),
                (actor.Roles || []).join(" / "),
                layout));
        });

        wrapper.appendChild(row);
        root.appendChild(wrapper);
    }

    /// Crew is a text list, not cards. A season routinely has 120+ credits across 25 jobs,
    /// and Bangumi has no photo for most production staff; rows of grey placeholders would be
    /// both taller and less readable than the job / names layout the Bangumi site itself uses.
    function staffSection(data, root) {
        var layout = data.Layout || {};
        if (!layout.ShowStaffGroups) return;

        var groups = data.StaffGroups || [];
        if (!groups.length) return;

        var total = groups.reduce(function (sum, group) {
            return sum + (group.Persons || []).length;
        }, 0);

        var wrapper = section("制作人员", String(total));
        var list = el("div", "bgmui-staff padded-left padded-left-page padded-right");

        groups.forEach(function (group) {
            var row = el("div", "bgmui-staffRow");
            row.appendChild(el("div", "bgmui-staffPos", group.Position));

            var names = el("div", "bgmui-staffNames");
            (group.Persons || []).forEach(function (person, position) {
                if (position > 0) names.appendChild(el("span", "bgmui-sep", "、"));

                var button = el("button", "bgmui-nameButton", firstText(person.NameCn, person.Name, "未命名"));
                button.type = "button";
                button.addEventListener("click", function () {
                    openEntity("person", person.Id, person.Name);
                });
                names.appendChild(button);

                if (person.Eps) names.appendChild(el("span", "bgmui-eps", "(" + person.Eps + ")"));
            });

            row.appendChild(names);
            list.appendChild(row);
        });

        wrapper.appendChild(list);
        root.appendChild(wrapper);
    }

    function relatedSection(data, root) {
        var layout = data.Layout || {};
        if (!layout.ShowRelated) return;

        var related = data.Related || [];
        if (!related.length) return;

        var wrapper = section("关联条目", String(related.length));
        var row = cardRow();

        related.forEach(function (entry) {
            var link = el("a", "bgmui-card");
            link.href = entry.Url;
            link.target = "_blank";
            link.rel = "noopener noreferrer";

            var poster = el("div", "bgmui-poster");
            setPoster(poster, imageUrl(entry.Image, layout));
            link.appendChild(poster);

            link.appendChild(el("div", "bgmui-name", firstText(entry.NameCn, entry.Name, "未命名")));
            if (entry.Relation) link.appendChild(el("div", "bgmui-sub", entry.Relation));

            row.appendChild(link);
        });

        wrapper.appendChild(row);
        root.appendChild(wrapper);
    }

    function metaSection(data, root) {
        var layout = data.Layout || {};
        var parts = [];

        if (layout.ShowRating && data.RatingScore > 0) {
            parts.push("评分 " + data.RatingScore.toFixed(1));
            if (data.RatingRank > 0) parts.push("排名 #" + data.RatingRank);
            if (data.RatingTotal > 0) parts.push(data.RatingTotal + " 人评分");
        }

        if (data.Platform) parts.push(data.Platform);
        if (data.TotalEpisodes > 0) parts.push(data.TotalEpisodes + " 话");
        if (data.AirDate) parts.push(data.AirDate + " 开播");
        if (data.AirWeekday) parts.push(data.AirWeekday);

        var tags = layout.ShowTags ? (data.Tags || []) : [];
        if (!parts.length && !tags.length) return;

        var wrapper = el("div", "verticalSection bgmui-section bgmui-metaSection");
        var line = el("div", "bgmui-meta padded-left padded-left-page padded-right");

        line.appendChild(externalLink(data.SubjectUrl, "Bangumi"));
        parts.forEach(function (part) {
            line.appendChild(el("span", "bgmui-metaItem", part));
        });
        wrapper.appendChild(line);

        if (tags.length) {
            var chips = el("div", "bgmui-tags padded-left padded-left-page padded-right");
            tags.forEach(function (tag) {
                var chip = el("span", "bgmui-tag", tag.Name);
                if (tag.Count > 0) chip.appendChild(el("span", "bgmui-tagCount", tag.Count));
                chips.appendChild(chip);
            });
            wrapper.appendChild(chips);
        }

        root.appendChild(wrapper);
    }

    // ---------------------------------------------------------------- modal

    var openModal = null;

    function closeModal() {
        if (!openModal) return;

        var previous = openModal.previous;
        if (openModal.backdrop.parentNode) openModal.backdrop.parentNode.removeChild(openModal.backdrop);
        document.removeEventListener("keydown", openModal.onKeyDown, true);
        openModal = null;

        if (previous && previous.focus) {
            try { previous.focus(); } catch (err) { /* the node may be gone */ }
        }
    }

    function openEntity(kind, id, fallbackName) {
        if (!id) return;

        closeModal();

        var backdrop = el("div", "bgmui-backdrop");
        var dialog = el("div", "bgmui-modal");
        dialog.setAttribute("role", "dialog");
        dialog.setAttribute("aria-modal", "true");
        dialog.setAttribute("aria-label", fallbackName || "Bangumi");

        var close = el("button", "bgmui-close");
        close.type = "button";
        close.setAttribute("aria-label", "关闭");
        close.textContent = "\u00d7";
        close.addEventListener("click", closeModal);
        dialog.appendChild(close);

        var body = el("div", "bgmui-modalBody");
        body.appendChild(el("div", "bgmui-loading", "正在读取 Bangumi …"));
        dialog.appendChild(body);

        backdrop.appendChild(dialog);
        backdrop.addEventListener("click", function (event) {
            if (event.target === backdrop) closeModal();
        });

        function onKeyDown(event) {
            if (event.key === "Escape" || event.keyCode === 27) {
                event.stopPropagation();
                closeModal();
            }
        }

        openModal = { backdrop: backdrop, onKeyDown: onKeyDown, previous: document.activeElement };
        document.addEventListener("keydown", onKeyDown, true);
        document.body.appendChild(backdrop);

        try { close.focus(); } catch (err) { /* not focusable yet */ }

        loadEntity(kind, id).then(function (entity) {
            if (!openModal || openModal.backdrop !== backdrop) return;

            body.innerHTML = "";
            if (!entity || !entity.Id) {
                body.appendChild(el("div", "bgmui-loading", "Bangumi 没有返回这条资料。"));
                return;
            }

            renderEntity(body, entity, kind);
        });
    }

    function renderEntity(body, entity, kind) {
        var layout = { ProxyImages: true };

        var header = el("div", "bgmui-entityHeader");

        var poster = el("div", "bgmui-entityPoster");
        setPoster(poster, imageUrl(entity.Image, layout));
        header.appendChild(poster);

        var titles = el("div", "bgmui-entityTitles");
        titles.appendChild(el("h3", "bgmui-entityName", firstText(entity.NameCn, entity.Name, "未命名")));
        if (entity.NameCn && entity.Name && entity.NameCn !== entity.Name) {
            titles.appendChild(el("div", "bgmui-entityAlt", entity.Name));
        }

        var facts = [];
        if (entity.Gender) facts.push(entity.Gender === "male" ? "男" : entity.Gender === "female" ? "女" : entity.Gender);
        if (entity.BirthDate) facts.push(entity.BirthDate);
        if (entity.DeathDate) facts.push("卒 " + entity.DeathDate);
        if (entity.BloodType) facts.push(entity.BloodType + " 型");
        if (entity.BirthPlace) facts.push(entity.BirthPlace);
        if (entity.Career && entity.Career.length) facts.push(entity.Career.join(" / "));

        if (facts.length) titles.appendChild(el("div", "bgmui-entityFacts", facts.join(" · ")));

        titles.appendChild(externalLink(
            entity.Url, kind === "character" ? "在 Bangumi 上查看角色" : "在 Bangumi 上查看人物"));
        header.appendChild(titles);
        body.appendChild(header);

        if (entity.Summary) {
            body.appendChild(el("p", "bgmui-entitySummary", entity.Summary));
        }

        if (entity.Aliases && entity.Aliases.length) {
            var aliases = el("div", "bgmui-entityRow");
            aliases.appendChild(el("div", "bgmui-entityKey", "别名"));
            aliases.appendChild(el("div", "bgmui-entityValue", entity.Aliases.join("、")));
            body.appendChild(aliases);
        }

        (entity.Infobox || []).forEach(function (entry) {
            if (entry.Key === "简体中文名" || entry.Key === "别名") return;

            var row = el("div", "bgmui-entityRow");
            row.appendChild(el("div", "bgmui-entityKey", entry.Key));
            row.appendChild(el("div", "bgmui-entityValue", (entry.Values || []).join("、")));
            body.appendChild(row);
        });
    }

    // ---------------------------------------------------------------- orchestration

    function clear(page) {
        var existing = page.querySelectorAll("." + ROOT_CLASS);
        for (var i = 0; i < existing.length; i++) {
            existing[i].parentNode.removeChild(existing[i]);
        }

        var hidden = page.querySelectorAll("." + NATIVE_HIDDEN_CLASS);
        for (var j = 0; j < hidden.length; j++) {
            hidden[j].classList.remove(NATIVE_HIDDEN_CLASS);
        }
    }

    function render(target, itemId, data) {
        clear(target.page);

        if (!data || !data.SubjectId) {
            target.page.removeAttribute("data-bangumi-ui");
            return;
        }

        var root = el("div", ROOT_CLASS);

        metaSection(data, root);
        characterSections(data, root);
        voiceActorSection(data, root);
        staffSection(data, root);
        relatedSection(data, root);

        if (!root.childNodes.length) {
            target.page.removeAttribute("data-bangumi-ui");
            return;
        }

        target.people.parentNode.insertBefore(root, target.people);
        target.page.setAttribute("data-bangumi-ui", itemId);

        // Only worth hiding once we actually produced something to replace it with.
        if (data.Layout && data.Layout.HideNativePeople) {
            target.people.classList.add(NATIVE_HIDDEN_CLASS);
        }

        log("rendered subject", data.SubjectId, "for item", itemId);
    }

    // 聚合端点即使走快通道也要几秒, 期间条目页看上去和没装插件一样, 用户会以为坏了。
    // 先占好位置再发请求, 让"正在加载"这件事本身是可见的。
    function skeleton(target, itemId) {
        clear(target.page);

        var root = el("div", ROOT_CLASS + " bgmui-loading");
        var wrapper = section("Bangumi", "加载中…");
        var row = cardRow();

        for (var i = 0; i < 6; i++) {
            var placeholder = el("div", "bgmui-card bgmui-skeletonCard");
            placeholder.appendChild(el("div", "bgmui-poster bgmui-skeleton"));
            placeholder.appendChild(el("div", "bgmui-skeleton bgmui-skeletonLine"));
            placeholder.appendChild(el("div", "bgmui-skeleton bgmui-skeletonLine bgmui-skeletonLine-short"));
            row.appendChild(placeholder);
        }

        wrapper.appendChild(row);
        root.appendChild(wrapper);

        target.people.parentNode.insertBefore(root, target.people);
        // 立刻打标记: 骨架屏自己也会触发 MutationObserver, 没有标记就会自激循环。
        target.page.setAttribute("data-bangumi-ui", itemId);
    }

    function run() {
        var itemId = currentItemId();
        if (!itemId) return;

        var target = findTarget();
        if (!target) return;

        if (target.page.getAttribute("data-bangumi-ui") === itemId &&
            target.page.querySelector("." + ROOT_CLASS)) {
            return;
        }

        var token = ++renderToken;
        ensureStyle();
        skeleton(target, itemId);

        function stillCurrent() {
            return token === renderToken && currentItemId() === itemId;
        }

        function phase(budget) {
            return loadDetail(itemId, false, budget).then(function (data) {
                if (!stillCurrent()) return null;

                var current = findTarget();
                if (!current) return null;

                try {
                    render(current, itemId, data);
                } catch (err) {
                    log("render failed", err);
                    return null;
                }

                return data;
            }, function (err) {
                log("phase failed", budget, err);
                return null;
            });
        }

        // 两阶段: 第一遍预算 0, 服务端只发 4 个 Bangumi 请求, 几秒内出全部栏位
        // (未命中中文名的角色先显示日文原名); 第二遍按配置预算补中文名后整体重渲染。
        phase(0).then(function (data) {
            if (!data || !data.SubjectId || !stillCurrent()) return;

            var lookups = data.Layout ? (data.Layout.CharacterNameLookups || 0) : 0;
            if (lookups > 0) phase(lookups);
        });
    }

    function schedule() {
        if (scheduleTimer) clearTimeout(scheduleTimer);
        scheduleTimer = setTimeout(function () {
            scheduleTimer = null;
            try {
                run();
            } catch (err) {
                log("run failed", err);
            }
        }, 150);
    }

    function start() {
        window.addEventListener("hashchange", schedule);
        document.addEventListener("viewshow", schedule, true);

        // The item page is built asynchronously and Emby reuses views, so neither event alone
        // is enough. run() is a cheap no-op once a page is already rendered, which keeps the
        // observer from feeding on its own insertions.
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });

        window.BangumiUi = {
            refresh: function () {
                var itemId = currentItemId();
                if (!itemId) return;

                forgetDetail(itemId);
                entityCache = {};

                var target = findTarget();
                if (target) {
                    clear(target.page);
                    target.page.removeAttribute("data-bangumi-ui");
                }

                // Refresh=true 只用来失效服务端缓存, 真正的重绘仍走 run() 的两阶段。
                loadDetail(itemId, true, 0).then(schedule);
            },
            clearCache: function () {
                detailCache = {};
                entityCache = {};
            }
        };

        schedule();
        log("started");
    }

    function whenReady() {
        if (window.ApiClient && window.ApiClient.getUrl && document.body) {
            try {
                start();
            } catch (err) {
                log("start failed", err);
            }
            return;
        }

        setTimeout(whenReady, 200);
    }

    whenReady();
})();
