# Emby Bangumi 刮削器

给 Emby Server 用的 [Bangumi 番组计划](https://bgm.tv) 元数据刮削插件。
剧集、季度、分集、剧场版、封面、制作人员与声优全部走 Bangumi 官方 API。

针对 Emby 4.9.x / .NET 8 编写，编译目标 ABI `4.9.1.90`。

## 为什么要用它

TMDB 和 TheTVDB 把一部番的所有季塞进同一个条目的 `Season 1..N`。番剧的后续季在
这两个源上常年缺失、错位，或者干脆没有中文数据。**Bangumi 是按「一次放送」建条目的**，
每季独立，粒度天然对得上国内番剧的实际发行方式。

实测下面这些在 TMDB 刮不全或刮错的番，用 Bangumi 都能拿到正确数据：

- 异世界四重奏 第三季
- 异兽魔都 第二季
- 相反的你和我 第二季
- 超超超超超喜欢你的 100 个女朋友 第三季
- Re:Zero 第四季 夺还篇
- Clevatess II

代价是 Bangumi 的条目模型和 Emby 的 `Series / Season / Episode` 模型并不同构，
插件的主要复杂度都花在这个映射上（见下文「季度解析」与「集号匹配」）。

## 功能

- **剧集 / 季度 / 分集 / 剧场版**四类刮削器，外加封面图片刮削器。
- **续集链季度解析**：Emby 的 `Season 3` 会沿 Bangumi 的「续集」关系找到真正对应的
  条目，而不是简单数跳数。
- **分割播出（split cour）支持**：Bangumi 把一季拆成两条时（如 Re:Zero 第二季 =
  13 集 + 12 集），一个 25 集的 Emby 季度会正确跨两个条目匹配。
- **双编号匹配**：同时理解 Bangumi 的 `ep`（季内集号）与 `sort`（全系列连续集号），
  并支持手动偏移。
- **目录名兜底搜索**：条目已经被 TMDB 刮成错误的季度时，磁盘上的目录名往往还保留着
  真实季度（`2026年7月 Clevatess II-…`）。搜索会同时用 Emby 条目名和目录末段。
- **人员导入**：导演/脚本/音乐/原作等制作人员，以及声优（含所配角色名）。
- **三个 External ID**：条目、分集、人物，可在 Emby「识别」界面手填 Bangumi ID 强制覆盖。
- **中文优先**：`name_cn` 作标题、原文名写入「原始标题」，可反转。
- 内置**自我限速 + 指数退避 + TTL 缓存**，一次全库扫描不会打爆 Bangumi 配额。
- **代理支持**，因为 `api.bgm.tv` 在不少网络环境下不可直连。

## 安装

### 从 Release 安装

1. 下载 `Emby.Plugins.Bangumi.dll`。
2. 放进 Emby 的插件目录：
   - Windows 便携版：`<Emby 根目录>\programdata\plugins\`
   - Windows 安装版：`%AppData%\Emby-Server\programdata\plugins\`
   - Linux：`/var/lib/emby/plugins/`
   - Docker：`/config/plugins/`
3. **重启 Emby Server**（Emby 只在启动时加载插件程序集）。
4. 控制台 → 插件 → 「Bangumi 番组计划」，按需填写代理地址。
5. 媒体库 → 编辑 → 「元数据下载器」里勾选 Bangumi，并调整顺序。

### 从源码构建

需要 .NET SDK 8.0。

```powershell
# 推荐：针对你自己服务器的程序集编译，ABI 绝对一致
.\scripts\build.ps1 -EmbySystemDir 'E:\Emby-Server\system'

# 或者走 NuGet 上的 MediaBrowser.* 4.9.1.90（CI 用的就是这条路径）
.\scripts\build.ps1
```

一键构建 + 安装 + 重启：

```powershell
.\scripts\install-local.ps1 -EmbySystemDir 'E:\Emby-Server\system' -StopEmby -StartEmby
```

`install-local.ps1` 会在覆盖前把已安装的 DLL 备份成 `.bak-<时间戳>`。
Emby 运行时会锁住插件文件，所以不带 `-StopEmby` 时脚本只会警告并退出，不会破坏现场。

Linux / macOS 直接用 dotnet CLI：

```bash
dotnet build src/Emby.Plugins.Bangumi/Emby.Plugins.Bangumi.csproj -c Release \
  -p:EmbySystemDir=/opt/emby-server/system
```

## 配置

| 选项 | 默认值 | 说明 |
|---|---|---|
| Access Token | 空 | 可选。[在此生成](https://next.bgm.tv/demo/access-token)。留空也能刮公开条目；填写后可访问 NSFW 条目、配额更宽松 |
| 包含 NSFW 条目 | 关 | 需要有效 Token，否则 Bangumi 根本不返回这些条目 |
| 优先使用中文标题 | 开 | 用 `name_cn` 作标题，原文名进「原始标题」 |
| 同时写入原文标题 | 开 | |
| 集号匹配方式 | Auto | Auto = 先 `ep` 再 `sort`。见「集号匹配」 |
| 集号偏移 | 0 | 文件是 E01–E12 但 Bangumi 记作 13–24 时填 12 |
| 自动解析续集季度 | 开 | 关闭后所有季共用系列主条目 |
| 写入分集时长 | 关 | 媒体文件本身的时长通常更准 |
| 导入标签 / 上限 | 开 / 15 | 按热度截断 |
| 标签同时写入类型 | 关 | 把用户标签也塞进 Genres；开启会让类型列表变很杂 |
| meta_tags 写入类型 | 开 | 用 Bangumi 官方 `meta_tags` 当 Genres，见「字段映射」 |
| Genres 黑名单 | 见下 | 逗号分隔。过滤掉 `meta_tags` 里的平台 / 国家 / 原作类型 |
| 导入制作人员 / 声优 | 开 / 开 | |
| 人员数量上限 | 40 | 一个条目可能有 300+ 人员记录 |
| 代理地址 | 空 | 例 `http://127.0.0.1:7890`、`socks5://127.0.0.1:1080` |
| User-Agent | 内置 | **不要改成通用 UA，Bangumi 会拒绝** |
| API 地址 | `https://api.bgm.tv` | 可指向自建反代 |
| 请求最小间隔 | 340 ms | 自我限速 |
| 请求超时 / 重试 | 30 s / 2 次 | 仅对超时、网络错误、5xx、429 重试 |
| 响应缓存时长 | 30 分钟 | 0 = 不缓存 |
| 搜索结果数量 | 20 | |
| 刮削器优先级 | 0 | 设 `-1` 可排在 TheTVDB / TMDb 之前 |
| 详细日志 | 关 | 排查匹配错误时打开，会记录每次请求、标题清洗结果和候选打分 |

## 工作原理

### 标题清洗

文件名和目录名在搜索前会经过 `TitleNormalizer`：剥离发布组前缀 `[ANi]`、
`【喵萌奶茶屋】`、装饰符 `★☆♪`、画质/编码/字幕标记、集号、年份括号，
同时把全角字符转半角、把中文数字（`十二`、`二十三`）转阿拉伯数字。

季度标记（`第N季`/`第N期`/`第Ⅱ期`/`Season N`/`2nd Season`/`S2`/罗马数字 `II`）会被
单独提取，生成两个关键词：**带季度的**和**不带季度的**。搜索时两者都用，因为 Bangumi
有时把季度写在标题里，有时不写。像 `Vivy -Fluorite Eye's Song-` 这种标题里本身带连字符
和短横的，不会被误伤。

还处理这几类实际遇到的脏数据：

| 输入 | 关键词 | 季度 | 规则 |
|---|---|---|---|
| `2026年7月 穹庐下的魔女` | `穹庐下的魔女` | – | 剥 ani-rss / AutoBangumi 的**放送季前缀** `YYYY年M月[新番]` |
| `000 仙逆` / `010 Fate/Zero` | `仙逆` / `Fate/Zero` | – | 剥**排序前缀**（`0` 开头的纯数字）。`20 Century Boys`、`5 Centimeters per Second` 不受影响 |
| `Clevatess II-魔兽之王与虚假的勇者传承-` | `Clevatess-魔兽之王与虚假的勇者传承-` | 2 | **标题中段罗马数字**：只认多字母的 `II`–`IX`，且后面必须紧跟副标题分隔符或 CJK |
| `空之境界 II 杀人考察(前)` | `空之境界 杀人考察(前)` | 2 | 同上 |
| `乱马½ 第二季` | `乱马1/2` | 2 | **分数字符**归一化 `½ ⅓ ⅔ ¼ ¾ ⅕ ⅙ ⅛`，对上 Bangumi 的 `乱马1/2` |
| `X` / `V 外星人访地球` / `Steins;Gate 0` | 原样 | – | 单字母 `I`/`V`/`X` **故意不当罗马数字**，它们是真番名 |

### 搜索与打分

一次搜索会依次拿 **Emby 条目名**和**路径末段目录名**两个来源做候选关键词
（目录名只做字符串处理，不碰文件系统），去重后每个来源生成「带季度」和「不带季度」
两个查询词。这是唯一能救「条目已经被 TMDB 刮成 S1、但目录名还写着 II / 第四季」
这种局面的信息。

候选结果按下面几项加权排序：

- **标题相似度**：完全相等 1000；前缀包含 `300 + 400×覆盖率`；子串包含
  `200 + 300×覆盖率`；否则按字符 bigram 的 Sørensen–Dice 系数给 `600×dice`
  （`< 0.34` 直接记 0）。查询带季度 `> 1` 时，候选标题会**额外生成一份剥掉季度标记的
  形式**进对比池，抹平 `第3季` / `第三季` 的写法差异。
- **季度一致性**：查询季度为 N 时，候选 `marker == N` **+220**，`marker != N` **−260**；
  候选没有 marker（通常是系列主条目）时，只有在**结果集里确实存在 `marker == N` 的条目**
  才重罚 **−580**，否则只扣 −160。查询本身没有季度而候选 `marker > 1` 时 −140。
- **年份**：完全一致 +120，差 1 年 +40，差更多 −60。
- **人气**：`min(40, log10(收藏数+1) × 14)`，只做微调，不让热门番压过精确匹配。
- **平台**：TV +10、WEB +6；剧场版刮削器里翻转成剧场版 +15 / OVA +5 / TV −15。

### 字段映射

| Emby 字段 | Bangumi 来源 |
|---|---|
| `Name` / `OriginalTitle` | `name_cn` / `name`（可反转） |
| `Overview` | `summary` |
| `PremiereDate` / `ProductionYear` | `date` |
| `CommunityRating` | `rating.score` |
| `Genres` | `meta_tags` 减去黑名单 |
| `Tags` | `meta_tags` + 用户 `tags`（按热度，受上限约束） |
| `Studios` | infobox `动画制作` / `アニメーション制作`；为空时才退回 `制作` / `製作` |
| `People` | `/persons`（制作人员）+ `/characters`（声优 + 角色名） |
| `ProviderIds` | `Bangumi` / `BangumiEpisode` / `BangumiPerson` |

两处刻意的取舍：

- **`meta_tags` 一半不是类型。** 它混了播出平台（`TV`/`WEB`/`OVA`）、制作国家（`日本`）
  和原作类型（`漫画改`），直接当 Genres 会让「类型」筛选变得毫无意义。默认黑名单把这些
  过滤掉——《穹庐下的魔女》因此从 `TV, 日本, 漫画改, 历史` 收敛成 `历史`。
  被过滤的条目仍然进 `Tags`，信息不丢。
- **`製作` 是产出垃圾的重灾区。** 同一个 infobox 值里常常塞着製作委員会 + 括号里的
  出资方 + 分号后面的一串製作人姓名，例如
  `天幕のジャードゥーガル製作委員会(テレビ朝日、CyberAgent…)；柳井寛史、崎田康平…`。
  所以 `动画制作` / `アニメーション制作` 优先，只有它们全空时才退回 `製作`，
  并且按 `、，,/×；;()` 拆分、上限 8 个。

### 季度解析

Emby 请求 `Season N` 的元数据时，如果这个季度自身没有 Bangumi ID，按三级策略解析：

1. **续集链 + 季度标记**（`chain marker`）：从系列主条目沿 `续集` 关系构建条目链
   （深度上限 16，`HashSet` 防环），解析每个条目标题里的季度标记，找 `marker == N`，
   然后向后吸收 `marker` 相同的、或「无 marker 但标题含分割线索」的条目。
2. **定向搜索**（`search`）：搜 `"<系列名> 第N季"`，要求命中结果落在链内（或链上只有
   根条目），且命中条目的 `marker == N`。
3. **链上序号**（`chain ordinal`）：取链上第 N 个条目。**这是错的**——Re:Zero 链上第 3 个
   是「第二季 后半部分」而不是第三季——但作为最后兜底保留，日志会标明。

三级全失败就**返回 `null`，不写任何元数据**。宁可留空也不写错。
解析成功且结果不等于系列主条目时才写 `Name` / `OriginalTitle`，并且**绝不写
`IndexNumber`**，避免把 Emby 的季度编号搞乱。

`N ≤ 1` 或关闭「自动解析续集季度」时直接用系列主条目。

### 集号匹配

Bangumi 每个分集有两个编号，含义完全不同：

- `ep` —— **条目内**集号，每个条目从 1 重新开始；
- `sort` —— **全系列**连续编号，跨条目单调递增。

实测：278826（第二季）的 ep 1–13 对应 sort **26–38**；316247（第二季后半）的
ep 1–12 对应 sort **39–50**。所以 Emby 里那个 25 集季度的第 14 集，**既不是
`ep == 14` 也不是 `sort == 14`**，而是 316247 的 `ep == 1`。

匹配顺序：

1. 候选条目按顺序确定：分集自身的 Bangumi ID → 季度的 ID（并沿续集链扩展）→
   系列的 ID（按季号解析；链长 ≤ 1 时回退主条目，兼容《名侦探柯南》这类单条目长番）。
2. 在当前候选里找 `ep == N`，失败再找 `sort == N`。
3. 仍失败且前序候选已累计 `preceding` 集时，用 `N - preceding` 重试（日志记作 `via ep -13`）。
4. 全部分集都没有编号时，退化为按顺序取第 N 个。

特典（`ParentIndexNumber == 0`）不做续集链扩展。匹配成功后命中的条目 ID 会写回
分集的 `ProviderIds`，下次直接命中。

## Bangumi API 的坑

以下都是踩过的，详细版本见 [`docs/api-notes.md`](docs/api-notes.md)。

- **必须用正经 User-Agent。** 通用 UA 会被拒。
- **常常需要代理。** `api.bgm.tv` 在不少网络下不可直连。
- **`infobox` 的 `value` 是多态的**：可能是字符串，也可能是 `[{k, v}]` 数组。
  必须按 `JsonElement` 反序列化，否则直接抛 `JsonException`。
- **`ep` / `sort` 是小数**（存在 `5.5` 这种半集），不能用 `int`。
- **`sort` 是全系列编号，`ep` 每季重置。**
- **一季可能被拆成两个条目**（分割播出）。
- **`/v0/episodes` 不带 `type` 会返回 SP / OP / ED**，只要本篇得传 `type=0`。
- **`/v0/subjects/{id}/characters` 不返回 `name_cn`**，角色名只有原文。
- **`/v0/users/-/collections/{sid}` 用 `-` 代指当前用户会 404**，必须显式传用户名。
- **`meta_tags` 不是 Genre 集合**，见上文「字段映射」。
- **`/v0/subjects/{id}/persons` 只给原文名**（`関根明良` 而不是「关根明良」），
  拿中文名要对每个人再打一次 `/v0/persons/{id}`，一个条目 300+ 人的量级不值得。

## 已知限制

- **Bangumi 侧没有对应条目时，搜索必然跑偏。** 例如 `罪恶之渊`：Bangumi 上既搜不到
  这个中文名也搜不到 `罪の深淵`，插件只能返回 `罪恶王冠`、`断裁分离的罪恶之剪` 这类
  字面相近的结果。这不是打分问题，是数据源问题——**在 Emby 的「识别」界面手填
  Bangumi ID** 即可，插件的三个 External ID 就是为这种情况准备的。
- 同类情况还有 `乱马½`：Bangumi 记作 `乱马1/2`，分数归一化能对上，但同名重制版
  与旧版条目并存，相关性一般，建议也手填 ID。
- 系列主条目本身就叫「系列名」而各季标题里不带季度标记时（少数长番），
  季度解析会落到第三级「链上序号」，日志里会标明 `chain ordinal`，这一档不保证正确。

## 与 anime-auto-stack 的关系

这个插件是 [`anime-auto-stack`](https://github.com/Whereis-Alice/anime-auto-stack)
（ani-rss + qBittorrent + Emby + embyToLocalPlayer 的自动化番剧流水线）里
「元数据」这一环的替代方案。两者相互独立，可以单独使用：

- `anime-auto-stack` 里的 `bridge.py` 负责**把 Emby 的观看记录同步回 Bangumi**（写方向）。
- 这个插件负责**把 Bangumi 的元数据刮进 Emby**（读方向）。

两边共用同一套 Bangumi API 认知，`docs/api-notes.md` 对两个项目都适用。

## 开发

```
src/Emby.Plugins.Bangumi/
├── Plugin.cs                  BasePluginSimpleUI 入口，持有 API 客户端
├── PluginOptions.cs           GenericEdit 配置模型
├── BangumiConstants.cs        GUID、ProviderId key、URL 模板
├── Api/
│   ├── BangumiApiClient.cs    HttpClient 封装：限速、重试、缓存、404→null
│   ├── BangumiModels.cs       DTO（注意 infobox 的多态处理）
│   └── TtlCache.cs            带惰性过期扫描的内存缓存
├── Utils/
│   └── TitleNormalizer.cs     文件名清洗与季度标记提取
└── Providers/
    ├── BangumiProviderBase.cs      搜索、打分、字段映射的共享逻辑
    ├── BangumiSeriesProvider.cs
    ├── BangumiSeasonProvider.cs
    ├── BangumiSeasonResolver.cs    续集链构建与季度归组
    ├── BangumiEpisodeProvider.cs   候选条目解析与双编号匹配
    ├── BangumiMovieProvider.cs
    ├── BangumiImageProvider.cs
    └── BangumiExternalId.cs
```

插件不带任何 NuGet 运行时依赖：Emby 自包含的 .NET 8 运行时已经提供
`System.Net.Http` 和 `System.Text.Json`，`csproj` 里 `CopyLocalLockFileAssemblies`
和 `GenerateDependencyFile` 都是关的，产物就是单个 DLL。

引用解析是三级的：`-p:EmbySystemDir=<路径>` → 环境变量 `EMBY_SYSTEM_DIR` →
NuGet 上的 `MediaBrowser.Server.Core` / `MediaBrowser.Common` 4.9.1.90。
构建时会打印实际用的是哪条路径。CI 故意不设 `EmbySystemDir`，这样 NuGet 回退路径
一旦坏掉就会立刻暴露。

## 致谢

- [Bangumi 番组计划](https://bgm.tv) 与其[开放 API](https://bangumi.github.io/api/)
- [kookxiang/jellyfin-plugin-bangumi](https://github.com/kookxiang/jellyfin-plugin-bangumi) ——
  Jellyfin 侧的同类实现，`ProviderId` 的 key 沿用了它的 `"Bangumi"` 以便迁移

## License

[MIT](LICENSE)