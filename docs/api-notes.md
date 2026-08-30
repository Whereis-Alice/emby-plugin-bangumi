# Bangumi API 实测笔记

本文件记录本插件开发过程中对 [Bangumi API](https://bangumi.github.io/api/) 的实测结论。
所有数据于 **2026-08-30** 通过 `https://api.bgm.tv` 实际请求取得。Bangumi 是维基式站点，
条目内容随时可能被编辑，下方具体数字仅作为「当时的事实」与回归测试基准。

## 0. 两个必须先满足的前提

1. **必须使用正经 User-Agent。** Bangumi 会拒绝通用 UA（`curl/*`、`Mozilla/5.0` 裸值、
   .NET 默认 UA 等），返回 403 或空响应。推荐格式 `用户名/仓库名 (联系方式)`。
   本插件默认值见 `BangumiConstants.DefaultUserAgent`。
2. **中国大陆以外 / 被 GFW 影响的网络需要代理。** `api.bgm.tv` 在部分网络下不可直连。
   插件的「代理地址」选项会应用到 `HttpClientHandler.Proxy`。

PowerShell 探测模板：

```powershell
Invoke-RestMethod -Uri 'https://api.bgm.tv/v0/subjects/552533' `
  -Proxy 'http://127.0.0.1:7890' `
  -Headers @{ 'User-Agent' = 'Whereis-Alice/emby-plugin-bangumi (dev probe)'; 'Accept' = 'application/json' }
```

> 注意：UA 里不要写 `(+https://...)` 这种形式，`Invoke-WebRequest`/`Invoke-RestMethod`
> 的头部校验会直接报错。

## 1. 端点清单

| 方法 | 端点 | 说明 |
|---|---|---|
| POST | `/v0/search/subjects?limit=N&offset=0` | body `{"keyword":"...","sort":"match","filter":{"type":[2],"nsfw":false}}`。`data[]` 中的元素与 `/v0/subjects/{id}` 同构，可直接复用模型 |
| GET | `/v0/subjects/{id}` | 条目详情 |
| GET | `/v0/episodes?subject_id={id}&limit=100&offset=N` | 分集列表；**不带 `type` 返回全部类型**（含 SP/OP/ED），`type=0` 只返回本篇 |
| GET | `/v0/subjects/{id}/persons` | 制作人员。552533 实测 **330 条** |
| GET | `/v0/subjects/{id}/characters` | 角色与声优。552533 实测 **38 条，全部带 `actors[]`** |
| GET | `/v0/subjects/{id}/subjects` | 关联条目，`relation` 实测值包括 `续集`/`前传`/`书籍`/`片头曲`/`片尾曲`/`原声集` |
| GET | `/search/subject/{keyword}?type=2&responseGroup=large&max_results=N` | 旧版搜索，作为 `/v0/search` 失败时的兜底 |

`/v0/subjects/{id}` 返回的字段：
`date, platform, images, summary, name, name_cn, tags, infobox, rating,
total_episodes, collection, id, eps, meta_tags, volumes, series, locked, nsfw, type`。

`type` 枚举里 **2 = Anime**；`/v0/episodes` 的 `type` 里 **0 = 本篇**，1 = SP，2 = OP，3 = ED，
4 = 预告/宣传，5 = MAD，6 = 其他。

## 2. 反序列化陷阱

### 2.1 `infobox` 的 value 是多态的

同一个数组里，`value` 可能是字符串，也可能是 `[{ "k": "...", "v": "..." }]`：

```json
"infobox": [
  { "key": "动画制作", "value": "サイエンスSARU" },
  { "key": "别名", "value": [ { "k": "", "v": "Tenmaku no Jaadougaru" } ] }
]
```

因此模型里 `value` 必须声明为 `JsonElement`，再由
`BangumiInfoboxItem.Values()` 展平成 `string[]`。用 `string` 会直接抛
`JsonException`。

### 2.2 `ep` / `sort` 是小数

分集编号可以是 `5.5`（半集、总集篇）。必须用 `double`，不能用 `int`。

### 2.3 `/v0/users/-/collections/{sid}` 里的 `-` 不能用

文档暗示 `-` 表示当前用户，实际会 404。必须显式传用户名。

## 3. 🔑 `ep` 与 `sort` 的语义差异

Bangumi 每个分集有两个编号：

- **`ep`**：该条目（≈ 一季）内的集号，**每个条目都从 1 重新开始**。
- **`sort`**：**整个系列的连续编号**，跨条目单调递增。

这是本插件最核心的一条事实。实测：

| 条目 | 内容 | `ep` 范围 | `sort` 范围 |
|---|---|---|---|
| 278826 | Re:Zero 第二季 | 1 – 13 | **26 – 38** |
| 316247 | Re:Zero 第二季 后半部分 | 1 – 12 | **39 – 50** |
| 633836 | Re:Zero 第四季 夺还篇 | 1 – 8 | **78 – 85** |
| 552533 | 穹庐下的魔女（第一部作品） | 1 – 12 | 1 – 12（此时二者相同） |

推论：对于被 Emby 合并成一个 25 集季度的 Re:Zero 第二季，**第 14 集既不是
`ep == 14` 也不是 `sort == 14`**。它是 316247 的 `ep == 1`。
插件的 `BangumiEpisodeProvider.Match` 因此按下面的顺序尝试：

1. 在当前候选条目里找 `ep == N`；
2. 找不到则找 `sort == N`；
3. 若前序候选条目已累计 `preceding` 集，用 `N - preceding` 重复第 1、2 步
   （日志里记作 `via ep -13`）；
4. 全都没有编号时，退化为按列表顺序取第 N 个。

## 4. 🔑 一季会被拆成多个条目

Bangumi 按「放送批次」建条目，分割播出（split cour）的一季在 Bangumi 上是两条。
插件用 `/v0/subjects/{id}/subjects` 的 `续集` 关系构建链，再按标题里的季度标记
（`第N季`/`2nd season`/`Season N`/`II`…）归组。

### Re:Zero 参考链（回归测试基准）

```
140001  Re:ゼロから始める異世界生活            / Re：从零开始的异世界生活        2016-04-03  26 集  marker=null
278826  … 2nd season                          / … 第二季                        2020-07-08  13 集  marker=2
316247  … 2nd season 後半クール                / … 第二季 后半部分               2021-01-06  12 集  marker=2
425998  … 3rd season 襲擊編                    / … 第三季 袭击篇                 2024-10-02   8 集  marker=3
510728  … 3rd season 反擊編                    / … 第三季 反击篇                            marker=3
547888  … 4th season 喪失編                    / … 第四季 丧失篇                 2026-04-08  11 集  marker=4
633836  … 4th season 奪還編                    / … 第四季 夺还篇                 2026-08-12   8 集  marker=4
```

从 140001 出发的续集链实测为 7 跳全连通：

```
140001 -> 278826 -> 316247 -> 425998 -> 510728 -> 547888 -> 633836
```

关键点：

- **根条目没有季度标记。** 不能靠 `marker == 1` 找第一季。
- **不能按跳数计算季度。** 链上第 3 个条目（index 2）是 316247 = 第二季后半，
  而不是第三季。`BangumiSeasonResolver.ResolveByOrdinal` 保留了这个错误行为，
  但只作为最后兜底，并在日志里标注 `chain ordinal`。
- 正确做法是 `ResolveFromChain`：找到 `marker == N` 的条目，然后向后吸收
  `marker` 相同的、或「无 marker 但标题含分割线索」的后续条目。
  第二季因此解析为 `[278826, 316247]`，第四季为 `[547888, 633836]`。
- 季度标记优先从 `name_cn` 解析，`name` 兜底。

分割线索正则覆盖：`後半`/`后半`/`前半`/`後編`/`前編`/`第N クール`/`2nd cour`/
`part 2`/`後期`/`下巻` 等。

## 5. 字段到 Emby 的映射

| Bangumi | Emby | 备注 |
|---|---|---|
| `name_cn` / `name` | `Name` / `OriginalTitle` | 由「优先使用中文标题」决定方向 |
| `summary` | `Overview` | |
| `date` | `PremiereDate` + `ProductionYear` | |
| `rating.score` | `CommunityRating` | Bangumi 是 10 分制，Emby 也是，直接写入 |
| `meta_tags` | `Genres` + `Tags` | 低基数的策展标签，但**一半不是类型**，见 5.1 |
| `tags` | `Tags` | 用户标签，带 `count`，按热度截断到「标签数量上限」 |
| `infobox` 的 `动画制作` / `アニメーション制作` | `Studios` | 首选。为空时才退回 `制作` / `製作`，见 5.2 |
| `infobox` 的 `放送星期` / `放送开始` | `Series.AirDays` / `AirTime` | |
| `infobox` 的 `片长` | `Movie.RunTimeTicks` | 剧场版才写 |
| `/persons` 的 `relation` | `PersonType` | `导演`→Director，`脚本`→Writer，`音乐`→Composer，`原作`→Writer 等 |
| `/characters` 的 `actors[]` | `PersonType.Actor` + `Role` = 角色名 | `/characters` **不返回 `name_cn`**，角色名只有原文 |
| `images.large`/`common`/`medium` | `ImageType.Primary` | `BangumiImages.Best()` 逐级降级 |

`platform` 用于给搜索结果打分：剧场版刮削器给 `剧场版` +15、`OVA` +5、`TV` −15，
剧集刮削器反之。

### 5.1 `meta_tags` 不是 Genre 集合

`meta_tags` 是官方策展的，基数低（一般 3–6 条），比用户 `tags` 干净得多——但它把三类
互不相干的信息混在同一个数组里：

| 类别 | 实测值 |
|---|---|
| 播出平台 | `TV` `WEB` `OVA` `OAD` `剧场版` `动态漫画` `短片` `PV` `CM` |
| 制作国家 | `日本` `中国` `美国` `韩国` `中国香港` `中国台湾` `欧美` `其他` |
| 原作类型 | `漫画改` `小说改` `轻小说改` `游戏改` `动画改` `绘本改` `视觉小说改` `原创` `特摄` |
| **真正的类型** | `历史` `科幻` `奇幻` `日常` `悬疑` … |

552533《穹庐下的魔女》的 `meta_tags` = `TV, 日本, 漫画改, 历史`：四条里只有一条是类型。
全部写进 Emby 的 `Genres` 会让类型筛选彻底失效（每部番都带 `TV` 和 `日本`），
所以插件默认用一份黑名单把前三类过滤掉，只留 `历史`；被过滤的仍然进 `Tags`。

### 5.2 infobox `製作` 的值经常是一坨

`动画制作` / `アニメーション制作` 的值基本干净（552533 → `サイエンスSARU`）。
`製作` 就不是了，同一个字符串里可能同时含製作委員会、括号里的出资方、分号后面的
一串製作人姓名：

```
天幕のジャードゥーガル製作委員会(テレビ朝日、CyberAgent、秋田書店、テレビ朝日ミュージック、BS朝日)；柳井寛史、崎田康平、青村陽介、阿部知司
```

只按 `、` 拆会得到 `天幕のジャードゥーガル製作委員会(テレビ朝日` 和 `BS朝日)；柳井寛史`
这种断头字符串。处理方式：优先只读动画制作类键；全空时才退回 `制作`/`製作`，
并把 `（）()；;` 也当分隔符，单项长度 > 60 丢弃，总数上限 8。

### 5.3 `/v0/subjects/{id}/persons` 没有中文名

返回的 `name` 是原文（`関根明良`、`山田尚子`）。中文名要对每个人再打一次
`/v0/persons/{id}`；552533 一个条目就有 **330** 条人员记录，为了译名把请求数放大
两个数量级不划算，所以插件直接写原文名。

## 6. 限速与错误处理

- 插件默认两次请求最小间隔 **340 ms**（`SemaphoreSlim` + 时间戳），一次媒体库
  扫描不会打爆配额。
- 429 / 5xx / 超时 / 网络错误按指数退避重试，默认 2 次。
- **404 返回 `null` 而不抛异常**——刮削器把「条目不存在」当正常结果。
- 条目 / 分集 / 人员响应进内存 TTL 缓存（默认 30 分钟），因为 Emby 刮一个季度时
  会对同一条目反复发起相同请求。

## 7. 为什么需要这个插件

TMDB / TheTVDB 把一部番的所有季合并成一个条目下的 `Season 1..N`，中文动画的
后续季常年缺失或错位。Bangumi 每季独立建条目，条目粒度与「一次放送」对齐，
下列在 TMDB 上刮不全或刮错的番，用 Bangumi 都能拿到正确数据：

- 异世界四重奏 第三季
- 异兽魔都 第二季
- 相反的你和我 第二季
- 超超超超超喜欢你的 100 个女朋友 第三季
- Re:Zero 第四季 夺还篇
- Clevatess II