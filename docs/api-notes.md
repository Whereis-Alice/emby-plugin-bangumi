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
| GET | `/search/subject/{keyword}?type=2&responseGroup=large&max_results=N` | 旧版搜索。**不是「v0 失败时的兜底」，而是必须跑的第二轮**，见 1.1。返回的是残缺条目 |
| GET | `/v0/persons/{id}` | 人物详情。中文名、性别、生日、出生地、身高、别名全在 `infobox` 里，不是顶层字段 |
| GET | `/v0/characters/{id}` | 角色详情。同上，`infobox` 里的「简体中文名」是唯一的中文名来源 |
| POST | `/v0/search/persons` | body `{"keyword":"..."}`，用于 Emby「识别」界面按名字找人物 |
| GET | `/v0/persons/{id}/subjects` | 该人物参与的条目（插件未用，留作反查） |
| GET | `/v0/persons/{id}/characters` | 该人物配过的角色（插件未用） |
| GET | `/v0/characters/{id}/persons` | 该角色的所有声优（插件未用） |

`/v0/subjects/{id}` 返回的字段：
`date, platform, images, summary, name, name_cn, tags, infobox, rating,
total_episodes, collection, id, eps, meta_tags, volumes, series, locked, nsfw, type`。

`type` 枚举里 **2 = Anime**；`/v0/episodes` 的 `type` 里 **0 = 本篇**，1 = SP，2 = OP，3 = ED，
4 = 预告/宣传，5 = MAD，6 = 其他。

### 1.1 🔑 `POST /v0/search/subjects` 的索引有真实缺口

v0 搜索会漏条目，而且漏得毫无规律。实测（2026-08-31，直连 `api.bgm.tv`，无 Token）：

| 关键词 | `POST /v0/search/subjects` | `GET /search/subject/{kw}` |
|---|---|---|
| `ギルティホール` | 查不到；只返回 `罪恶王冠`、`罪恶装备` 等字面相近项 | **第 1 条 = `516604` 罪恶之渊** |
| `ハーレムきゃんぷっ！` | 查不到 | **第 1 条 = `395537` 后宫露营！** |

- 与 NSFW **无关**：请求体里加 `filter.nsfw: true` 结果不变。
- 与 Token **无关**：旧版接口匿名即可拿到。
- 与打分**无关**：候选池里根本没有正确条目。

所以正确的用法是把旧版接口当作**第二轮 pass**：第一轮 v0 跑完所有关键词，若最高标题分
仍低于「精确命中」阈值，就用同一批关键词再跑一轮旧版接口，两轮并进同一个候选池后统一排序。
写成「v0 返回空数组时才用旧版」是无效的——v0 几乎总能返回一堆字面相近的垃圾，永远不为空。

### 1.2 旧版搜索返回的是残缺条目

`responseGroup=large` 也只有
`id` / `name` / `name_cn` / `summary` / `air_date` / `images` / `rating` / `eps`，
**没有 `infobox` / `tags` / `meta_tags` / `platform`**。直接拿这个对象去做字段映射，
studios / genres / tags 会静默变空。选中之后必须再 `GET /v0/subjects/{id}` 补全。

对比之下 `POST /v0/search/subjects` 的 `data[]` 元素与 `/v0/subjects/{id}` 同构，
不需要补。

### 1.3 受限条目的子资源返回 404，不是 401

无 Token 时，NSFW 条目的 `/v0/subjects/{id}/persons` 与 `/v0/subjects/{id}/characters`
都返回 `404 {"title":"Not Found","description":"resource can't be found in the database or has been removed"}`，
而 `/v0/subjects/{id}` 本身**照常返回**。实测 404 的：`516604`、`621602`、`395537`、`295001`；
同一时刻 `312298` = 55 persons / 4 characters、`345802` = 88 persons 正常。

后果是「刮削看起来成功了，但演员表只有 infobox 里那两三条」，且与「这个条目本来就没录人员」
在响应上完全无法区分。填 Token 即可恢复。

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
4. 全都没有编号时，退化为按列表顺序取第 N 个；
5. 候选全部失败且 `N` 大于累计集数时，走整条 franchise 链找 `sort == N`（见 3.1）。

### 3.1 🔑 `sort` 是全系列绝对号，长番的文件名用的就是它

国创长番按年拆条目，`ep` 逐条重置而 `sort` 从不重置。实测《仙逆》：

| 条目 | 名称 | `eps` | `ep` | `sort` | 首集分集 ID |
|---|---|---|---|---|---|
| 345802 | 仙逆 | 24 | 1 – 24 | 1 – 24 | 1251516 |
| 481211 | 仙逆 年番 | 52 | 1 – 52 | **25 – 76** | 1302376 |
| 526970 | 仙逆 年番2 | 52 | 1 – 52 | **77 – 128** | 1461085 |
| 630676 | 仙逆 年番3 | 52 | 1 – 52 | **129 – 180** | 1650084 |

GM-Team 的文件名是 `…[仙逆]…[2023][147]…`，即全系列第 147 集。`147` 在任何一个条目的
`ep` 里都不存在，但它就是 `630676` 的 `sort == 147`（`ep 19`，分集 ID `1650102`）。

注意 `sort` 的绝对性**只在续集链内成立，且不保证跨越 OVA**：`124341`（ARIA The AVVENIRE，
3 集）的 `sort` 是 1 – 3，并没有接在 ARIA The ORIGINATION 之后。所以按 `sort` 兜底时必须
遍历整条链而不是靠算偏移。

### 3.2 分集常常两个标题都是空的

国创和网络放送条目的分集 `name` / `name_cn` **经常同时为空字符串**，v0 接口和旧接口都一样。
实测 `312298`（诛仙）全 3 集、`630676`（仙逆 年番3）全 52 集的两个名字字段都是空。
插件因此在标题为空时按命中的集号生成「第 01 集」/「第 01 話」——否则 Emby 会保留它当初
从文件名生成的名字，而那对一批解析不出集号的文件是**同一个**「第 01 集」。

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

### 5.3 条目的 persons / characters 都没有中文名

`/v0/subjects/{id}/persons` 和 `/v0/subjects/{id}/characters` 返回的 `name` 都是原文
（`関根明良`、`ナツキ・スバル`）。中文译名只存在于各自详情的 `infobox`「简体中文名」里，
必须对每个 id 再打一次 `/v0/persons/{id}` / `/v0/characters/{id}`。

552533 一个条目就有 **330** 条人员记录，无条件展开会把请求数放大两个数量级。插件的做法是
**带预算地做**：「中文译名查询上限」（默认 40）限制每个条目最多补多少条详情，且

- 角色名默认开（`TranslateCharacterNames = true`）——「菜月昴」「艾米莉娅」值得，
  而且角色数（几十）远小于人员数（几百）；
- 人名默认关（`TranslatePersonNames = false`）——日文人名本来就是汉字，`小林裕介`
  和「小林裕介」没有区别；
- 详情响应进同一个 TTL 缓存，所以一部番的多个季度共享结果，超预算的部分保留原文。

### 5.4 公司被记成「人物」

`製作`、`动画制作`、`出品方`、`製作協力` 这些 relation 下面挂的是 KADOKAWA、WHITE FOX、
フジテレビ 这类法人，但它们在 `/v0/subjects/{id}/persons` 里的 `type` **仍然是 1（个人）**。
按 `type` 过滤没用，只能按 relation 名字挡：插件维护一个公司关系集合，命中的一律不导入为
人物，公司信息只经 infobox 进 `Studios`。不挡的话演员表里会出现「作曲：KADOKAWA」。

### 5.5 Emby 只有 8 种 `PersonType`

反射确认 Emby 4.9 的 `PersonType` 只有 `Actor / Director / Writer / Producer /
GuestStar / Composer / Conductor / Lyricist` 八个值，而 Bangumi 的 relation 有上百种
（总作画监督、色彩设计、道具设计、制作进行…）。并且 `PersonInfo` **没有 `SortOrder`**，
列表顺序就是插入顺序。所以插件的策略是：

- 能精确映射的映射掉（监督/演出/分镜 → Director，系列构成/脚本/原作 → Writer，
  音乐/主题歌作曲/编曲 → Composer，作词 → Lyricist，各类制片/企画 → Producer）；
- 映射不掉的按「未识别职位的处理」落到 Producer，**职位原文写进 `Role`**，信息不丢；
- 未映射职位内部再排一次序（副导演/总作画监督/人物设定 → 各类监督 → 作画监督/设定 →
  … → `原画` → 制作进行/协力），保证被上限截断的一定是末流职位；
- 同一个人身兼多职时**只出现一条**，`PersonType` 取优先级最高的那个职位，其余职位并入
  `Role`（`篠原正寛 :: 分镜 / OP・ED 演出 / OP・ED 分镜 / 导演`）。同一个人既是声优又是
  制作人员时，以声优那条为准，职位并入角色名。

### 5.6 🔑 大量角色的 `actors` 是空数组

`/v0/subjects/{id}/characters` 的每一项都有 `actors` 字段，但它经常是 `[]`：配角、
客串、以及漫画 / 小说改的原作角色都可能没有关联声优（社区数据的自然状态，不是错误）。
按 `actors` 非空过滤演员表会静默丢掉它们。

对本人媒体库里 42 个 subject 逐个打 `/persons` + `/characters` 的全量实测
（4 个 NSFW 条目无 Token → 三个接口全 404，见 1.3）：

| subject | 名称 | persons | characters | type=1 | 无声优(type=1) |
|---|---|---|---|---|---|
| `120236` | 排球少年 第二季 | 177 | 150 | 150 | **22** |
| `84171` | 排球少年 | 271 | 78 | 78 | 8 |
| `124341` | 水星领航员 The AVVENIRE | 144 | 21 | 20 | 8 |
| `325767` | 感谢对战。 | 108 | 29 | 29 | 7 |
| `568572` | 黄泉使者 | 469 | 91 | 91 | 6 |
| `525565` | 相反的你和我 | 222 | 36 | 36 | 4 |
| `329948` | 水星领航员 The BENEDIZIONE | 157 | 18 | 17 | 3 |
| `510753` | 靠死亡游戏混饭吃。 | 256 | 53 | 53 | 3 |
| `513089` | 为美好的世界献上祝福！第三季 BONUS STAGE | 77 | 22 | 22 | 3 |
| `598058` | 超超超超超喜欢你的 100 个女朋友 第三季 | 110 | 66 | 65 | 3 |
| `304087` | 水星领航员 The CREPUSCOLO | 180 | 22 | 21 | 2 |
| `474371` | 异兽魔都 第二季 | 343 | 50 | 50 | 2 |
| `611077` | 名侦探光之美少女！ | 421 | 68 | 68 | 2 |
| `27332` / `545917` / `5649` / `607340` / `617123` / `638497` | | | | | 各 1 |
| `345802` | 仙逆 | 88 | 2 | 2 | 0 |
| `630676` | 仙逆 年番3（不在库中） | 79 | **0** | 0 | — |

38 个可访问条目里 **19 个**存在无声优角色，合计 **79** 个。
`characters` 与 `persons` 为 0 的只有链外的 `630676`。

`type` 字段区分实体种类：**1 = 角色，2 = 机体，3 = 舰船，4 = 组织**。
`124341` 里的「ARIAカンパニー」是 `type = 4`，不该进演员表。

### 5.7 character 与 person 是两套 ID 空间

`/v0/characters/{id}` 与 `/v0/persons/{id}` 的 id 互不相干（`出云晓` 的角色 id 是
`11608`，和任何 person id 无关）。两者的 JSON 结构高度相似——都有
`name / gender / blood_type / birth_year / birth_mon / birth_day / infobox /
images / summary`——但 character **没有 `career`，也没有卒日**（`/v0/persons` 才有
`career` 与 infobox「逝世」）。混用会得到毫无关系的人。

因此插件用独立的 `BangumiCharacter` provider key，并在人物刮削器里先判 character 再判
person；头像刮削器先试 person 端点，空了再试 character 端点。

`blood_type` 是整数：**1 = A，2 = B，3 = AB，4 = O**。

### 5.8 季度条目的人员和系列条目不一样

同一 franchise 的各季 subject 各有各的人员表，不是同一份数据。`水星领航员` 三个条目
实测（写入 Emby 后统计）：

| Emby 项 | subject | 人员条数 | 雅典娜·葛络俐的声优 |
|---|---|---|---|
| Series 39 | `124341` AVVENIRE | 120 | 川上とも子 / 河井英里 |
| Season 2 | `304087` CREPUSCOLO | 120 | 佐藤利奈 |
| Season 3 | `329948` BENEDIZIONE | 116 | 佐藤利奈 |

只刮系列条目、让季度继承显示，会让整部番只剩最后一季（或主条目那一季）的班底。
注意 `/emby/Items?Ids={seasonId}&Fields=People` 对 Season 会**回落显示系列的人员**，
所以验证「季度是否真的写入了自己的人员」必须**对比人员名单内容**，光比条数会被骗。

### 5.9 🔑 Token 前后的实测差值（受限条目）

1.3 只说了受限条目的子资源会 404。填上 Access Token 之后的完整实测（本地 4 部受限条目，
`IncludeNsfw = true`）：

| 条目 | 名称 | `/episodes` | `/characters` | 其中 `actors` 为空的 type=1 | `/persons` |
|---|---|---|---|---|---|
| `516604` | 罪恶之渊 | 8 | 9 | 4 | 10 |
| `621602` | 淫狱团地 | 12 | 21 | 0 | 13 |
| `295001` | EVERGLOW LAND | 8 | 3 | 1 | 45 |
| `395537` | 后宫露营！ | 8 | 5 | 1 | 23 |

无 Token 时这四行**全部是 404**。落到 Emby 里的结果：38 集分集元数据从 0 变成全部命中
（37 集有简介，`295001` 有一集 Bangumi 本身没写），系列人员表从 2 / 3 / 6 / 5 条变成
13 / 20 / 35 / 22 条，无声优角色也按 4 / 0 / 1 / 1 如实进了演员表。

⚠️ Token 只在**插件启动时**从配置读一次，改完要重启 Emby Server 才生效。

### 5.10 `/emby/Items?Fields=People` 不返回 `People[].ProviderIds`

这是 Emby 侧的坑，但会直接坑掉验证脚本。该端点的 `People` 元素只有
`Name` / `Id` / `Role` / `Type` / `PrimaryImageTag` 五个字段，**没有 `ProviderIds`**，
所以「有多少条角色是以角色本人身份入表的」不能靠 `ProviderIds.BangumiCharacter` 数。
可用的判据是 `Role`：正常声优的 `Role` 是它配的角色名，而角色本人入表时 `Role` 写的是
Bangumi 的关系标签，只会是 `主角` / `配角` / `客串` 三个值之一。实测排球少年第二季按此判据
数出 22 条，与 `/v0/subjects/120236/characters` 里 `actors` 为空的 type=1 角色数**完全一致**。

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