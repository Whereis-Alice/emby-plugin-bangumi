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
  13 集 + 12 集），一个 25 集的 Emby 季度会正确跨两个条目匹配。目录名对上的是第二个
  cour 时（Re:Zero 第四季的「夺还篇」），还会沿「前传」反向补齐更早的那个 cour。
- **双编号匹配**：同时理解 Bangumi 的 `ep`（季内集号）与 `sort`（全系列连续集号），
  并支持手动偏移；两种读法撞号时按播出日期消歧，不会把还没播的大结局盖到首集上。
- **文件名集号兜底**：`[GM-Team][国漫][诛仙 第4季][2026][01][HEVC]` 这种纯方括号命名 Emby
  自己解析不出集号，整集会掉出季度、还会被统一命名成同一个「第 01 集」。插件在 provider
  里再解析一次并回写，OP / ED / 特典 / 半集（`03.5`）一律跳过，方括号里的年份（`2026`）
  和分辨率（`1080`）也不会被当成集号。
- **跨季连续集号**：发布组按全系列连续号命名时（`仙逆 … [147]`），Bangumi 的当季条目里
  根本没有这个集号——但它正好等于 `sort`。常规匹配全部失败后会沿「前传 / 续集」走完整条
  franchise 链，用 `sort` 精确命中。实测 `仙逆 147` → 条目 `630676`（年番3）的 `ep 19`。
- **编号不会被刷新清空**：`ReplaceAllMetadata` 下原样回写 `IndexNumber` /
  `ParentIndexNumber`，不会像常见实现那样把整季打散成一堆「第 1 季」。
- **四路关键词 + 双接口搜索**：条目名、磁盘目录末段、Emby 已有的「原始标题」（原文名）、
  以及目录里媒体文件名的众数，四个来源合成关键词全部**逐个搜完再统一排序**，
  而不是第一个有返回就收工。v0 搜索接口索引有真实缺口（`ギルティホール`、
  `ハーレムきゃんぷっ！` 都查不到），所以第一轮分数不够时还会再走一轮旧版
  `/search/subject` 接口。这四条信息合起来能救「TMDB 已经刮错、条目名是错的译名、
  但目录名或原文名还对」的局面。
- **自动匹配闸门**：自动刮削只接受足够可信的结果——标题分 ≥ 550 直接放行，
  260–550 之间必须年份完全一致，否则**宁可不写**并在日志里写明拒绝原因，
  留给「识别」界面手填。手动识别不受闸门限制，永远列全候选。
- **人员导入尽可能全面**：制作人员（监督 / 系列构成 / 脚本 / 音乐 / 作词 / 各类监督 /
  制片）与声优一起导入，角色名与职位名默认中文化。Emby 只有 8 种 `PersonType`，
  装不下的职位（总作画监督、摄影监督、色彩设计…）按可读性排序落到「制作人」，
  职位原文保留在角色名里。同一个人身兼多职合并成一条（`篠原正寛 :: 分镜 / OP・ED 演出 / 导演`），
  一人分饰多角也合并（`河西健吾 :: 莱伊·巴登凯托斯 / 罗伊·爱尔法德`）。
  Bangumi 把 KADOKAWA 这类**公司也记成「人物」**，插件按职位关系把它们挡在人员列表外，
  只留在 `Studios`。
- **人物页面元数据**：演员 / 制作人员的人物页会填上简介（性别 / 生日 / 出生地 / 身高 /
  所属 / 职业 / 别名）、出生日期、出生地与头像，而不是一个空壳页。
- **人物页批量补全（计划任务）**：Emby 建了人物行之后不会自己去刮人物详情，所以刚刮完
  一个库，几百号人的页面全是空的。内置计划任务「Bangumi：补全人物元数据」把这一步批量做完，
  默认每天 04:00 跑一次，也可以在「计划任务」里手动点。
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
`-StopEmby` 优先走 `POST /emby/System/Shutdown`（`-ApiKey`，或环境变量 `EMBY_API_KEY`；
`-ServerUrl` 默认 `http://127.0.0.1:8096`），只在没有 token 时才回退到窗口/进程信号——
脚本自己用 `-WindowStyle Hidden` 起的服务端没有主窗口，`CloseMainWindow()` 从第二次起必然失效。
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
| 从文件名解析集号 | 开 | Emby 解析不出集号时再从文件名解析一次并回写。OP / ED / 特典 / 半集跳过 |
| 集号匹配方式 | Auto | Auto = 先 `ep` 再 `sort`。见「集号匹配」 |
| 集号偏移 | 0 | 文件是 E01–E12 但 Bangumi 记作 13–24 时填 12 |
| 自动解析续集季度 | 开 | 关闭后所有季共用系列主条目 |
| 跨季连续集号回退 | 开 | 常规匹配全失败、且集号大于本季集数时，走完整 franchise 链按 `sort` 精确匹配 |
| 写入分集时长 | 关 | 媒体文件本身的时长通常更准 |
| 导入标签 / 上限 | 开 / 15 | 按热度截断 |
| 标签同时写入类型 | 关 | 把用户标签也塞进 Genres；开启会让类型列表变很杂 |
| meta_tags 写入类型 | 开 | 用 Bangumi 官方 `meta_tags` 当 Genres，见「字段映射」 |
| Genres 黑名单 | 见下 | 逗号分隔。过滤掉 `meta_tags` 里的平台 / 国家 / 原作类型 |
| 导入制作人员 | 开 | 监督 / 系列构成 / 脚本 / 音乐 / 原作 / 人物设定 / 各类监督 / 制片人 |
| 导入声优 | 开 | 导入为「演员」，角色名填其配音角色 |
| 未识别职位的处理 | Producer | Emby 只有 8 种 `PersonType`。Producer = 装不下的职位导入为「制作人」、职位原文进角色名；另可选 GuestStar / Skip |
| 职位黑名单 | 空 | 逗号分隔的 Bangumi 职位名。热门番「原画」动辄上百人，填 `原画,第二原画,动画` 可让列表干净 |
| 声优数量上限 | 100 | 按 主角 → 配角 → 客串 排序后截断 |
| 制作人员数量上限 | 100 | 按 监督 → 编剧 → 音乐 → 制片 → 其他 排序后截断 |
| 人员数量总上限 | 200 | 声优 + 制作人员写入 Emby 的总条数兜底 |
| 合并同一声优的多个角色 | 开 | 一人分饰多角合并成「角色A / 角色B」一条 |
| 角色名使用中文译名 | 开 | 条目角色接口只给日文名，逐个请求 `/v0/characters/{id}` 取「简体中文名」 |
| 声优 / 制作人员名使用中文译名 | 关 | 同理请求 `/v0/persons/{id}`。日文人名多为汉字原文，默认关 |
| 中文译名查询上限 | 40 | 每个条目最多请求多少条详情用于取中文名，0 = 关闭译名查询 |
| 人物页面元数据 | 开 | 为人物页填简介 / 生日 / 出生地 / 头像（`/v0/persons/{id}`） |
| 补全人物元数据任务上限 | 0（不限） | 计划任务单次最多处理多少人。人物上千时可设 200 分几晚跑 |
| 重试没有头像的人物 | 关 | Bangumi 上大量制作人员根本没有照片，默认不再为他们重复请求 |
| 代理地址 | 空 | 例 `http://127.0.0.1:7890`、`socks5://127.0.0.1:1080` |
| User-Agent | 内置 | **不要改成通用 UA，Bangumi 会拒绝** |
| API 地址 | `https://api.bgm.tv` | 可指向自建反代 |
| 请求最小间隔 | 340 ms | 自我限速 |
| 请求超时 / 重试 | 30 s / 2 次 | 仅对超时、网络错误、5xx、429 重试 |
| 响应缓存时长 | 30 分钟 | 0 = 不缓存 |
| 搜索结果数量 | 20 | |
| 最低标题匹配分 | 260 | 自动匹配的闸门。标题分低于此值一律不写；`260`–`550` 之间还要求年份完全一致。设 0 = 关闭闸门（回到「取第一名」的老行为） |
| 旧版搜索接口兜底 | 开 | 第一轮 `POST /v0/search/subjects` 的最高标题分 < 550 时，再用旧版 `GET /search/subject/{kw}` 搜一轮。v0 的索引确实有缺口，见「Bangumi API 的坑」 |
| 使用原始标题做线索 | 开 | 把 Emby 条目上已有的「原始标题」（通常是 TMDB 写的日文原名）也当搜索关键词。需要能按路径反查条目 |
| 文件名线索数量 | 2 | 从目录里的媒体文件名提取出现次数最多的前 N 个标题当关键词，0 = 关闭。只扫本级目录和一层子目录、最多 200 个文件 |
| 刮削器优先级 | 0 | 设 `-1` 可排在 TheTVDB / TMDb 之前 |
| 详细日志 | 关 | 排查匹配错误时打开，会记录每次请求、标题清洗结果和候选打分 |

## 计划任务

### Bangumi：补全人物元数据

Emby 的人物是**懒加载**的：条目刮完之后，每个声优 / 制作人员只有一行名字加一张从条目
带过来的缩略图；`/v0/persons/{id}` 里的简介、生日、出生地、大图头像，只有在**人物条目
本身**被刷新时才会写入，而 Emby 不会自己去做这件事。结果就是刚刮完一个库，人物页全是空壳，
除非手动一个个点「刷新元数据」。

这个计划任务把那一步批量做完：

- 扫描全部 `Person`，只挑**带 `BangumiPerson` id 且简介还是空**的（简介是「从没刮过」
  的可靠信号，因为只要 Bangumi 有这个人，插件至少会写出事实清单）。
- 逐个 `RefreshFullItem`，用 `FullRefresh` + `ReplaceAllMetadata = false`：
  该查的都查，但**不覆盖**任何已有字段，手动改过的人物页不会被推平。
- 请求间隔沿用「请求最小间隔」（默认 340 ms）自我限速，进度和结果都写日志：

  ```
  Bangumi person task: 扫描 944 个人物，12 个待补全（每次请求间隔 340 ms）。
  Bangumi person task: 完成，刷新 12 个，新增简介 12 个，新增头像 0 个，失败 0 个。
  ```

- 默认触发器每天 04:00（放在夜间库扫描之后，新的人物行已经建好）。也可以在
  **仪表板 → 计划任务 → Bangumi** 里手动运行、改时间或关掉。
- `人物页面元数据` 关闭时任务直接跳过，不用单独再关一次。

没有头像的人默认**不会**被反复重试：实测 615 个 Bangumi 人物里有 223 个在 Bangumi 上
就是没有照片（`images` 全空），每晚为他们重发几百个请求毫无意义。确实怀疑是下载失败时，
打开「重试没有头像的人物」再跑一次。

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

一次搜索按下面的顺序收集候选关键词，每个来源都生成「带季度」和「不带季度」两个查询词，
最后整体去重：

1. **Emby 条目名**。
2. **路径末段目录名**（只做字符串处理，不碰文件系统）。这是唯一能救
   「条目已经被 TMDB 刮成 S1、但目录名还写着 II / 第四季」的信息。
3. **Emby 条目上已有的「原始标题」**。TMDB 一般会把日文原名写在这里，
   而 Bangumi 对原文名的召回远好于中文译名。`ItemLookupInfo` 并不带这个字段，
   所以是用 `ILibraryManager.FindByPath` 按路径反查条目取的；解析不到就跳过，不报错。
4. **目录内媒体文件名的众数**（默认取前 2 个）。只扫本级目录和一层子目录、最多 200 个文件，
   剥掉结尾的集号标记，**并且刻意剥掉季度标记**——被扫的目录可能是含多季子目录的合集根目录，
   否则文件最多的那一季会赢，`排球少年` 就会被写成 `排球少年 第二季` 的 ID，
   继而让整条季度链错位。

关键词**全部搜完再统一排序**。早期版本是「第一个返回非空结果就 break」，
于是一个字面相近的垃圾结果就能让后面更准的关键词永远没机会。现在每搜完一个关键词就把
结果并进同一个池子重排一次，只有标题分已经 ≥ 550（基本等于精确命中）才提前收工。

第一轮走 `POST /v0/search/subjects`；若这一轮最高标题分仍 < 550，再用旧版
`GET /search/subject/{kw}` 把同一批关键词重搜一遍，两轮共用同一个候选池。
旧版接口返回的是**残缺条目**（没有 `infobox` / `tags` / `meta_tags` / `platform`），
所以选定之后会用 `/v0/subjects/{id}` 重新取一次完整条目，
否则 studios / genres / tags 会静默丢光。

排序结束后还有一道**自动匹配闸门**（「最低标题匹配分」，默认 260）：标题分 ≥ 550 放行；
在 260 与 550 之间要求年份完全一致才放行；否则放弃自动写入，并在日志里记下
被拒的候选和原因。260 这个值是实测定的——已知正确的弱匹配 `穹庐下的魔女` vs
`天幕的魔女` 是 267，而错误匹配 `罪恶之渊` vs `断裁分离的罪恶之剪` 是 240、
`后宫露营` vs `后宫之乌` 是 0。闸门只作用于自动刮削，「识别」界面的手动搜索永远列全候选。

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
| `People` | `/persons`（制作人员，公司关系除外）+ `/characters`（声优 + 角色名） |
| 人物页 `Overview` / `PremiereDate` / `ProductionLocations` / 头像 | `/v0/persons/{id}` 的 `summary` 与 infobox |
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
   然后向后吸收 `marker` 相同的、或「无 marker 但标题含分割线索」的条目，
   再沿 `前传` 关系**向前**补齐同季的更早 cour（见下）。
2. **定向搜索**（`search`）：搜 `"<系列名> 第N季"`，要求命中结果落在链内（或链上只有
   根条目），且命中条目的 `marker == N`。
3. **链上序号**（`chain ordinal`）：取链上第 N 个条目。**这是错的**——Re:Zero 链上第 3 个
   是「第二季 后半部分」而不是第三季——但作为最后兜底保留，日志会标明。

三级全失败就**返回 `null`，不写任何元数据**。宁可留空也不写错。
解析成功且结果不等于系列主条目时才写 `Name` / `OriginalTitle`。
季号本身仍然由目录结构决定，但会被**原样回写**而不是留空——原因见下文「编号回写」。

`N ≤ 1` 或关闭「自动解析续集季度」时直接用系列主条目。

#### 前置 cour（`前传` 反向补齐）

一季被识别到的条目常常是它的**第二个** cour，因为那才是标题和目录名对得上的那个：
Re:Zero 第四季 = `547888`（丧失篇）+ `633836`（夺还篇），而目录名取的是「夺还篇」。
只沿 `续集` 往后走的话，丧失篇永远进不了候选集，属于它的那些集必然匹配失败。
所以链头还会沿 `前传` 反向走，把同季更早的 cour 前置到候选列表最前面，
偏移量（`preceding`）才能对齐。

接受一个前传条目的条件是**双向**同季判定：季度标记相等，或者其中一方带分割播出线索。
真正的「上一季」会重置集号，一旦误收进来所有偏移计算都会错位，所以判定必须严。
候选总数仍受 `MaxSubjectsPerSeason` 限制。

### 集号匹配

Bangumi 每个分集有两个编号，含义完全不同：

- `ep` —— **条目内**集号，每个条目从 1 重新开始；
- `sort` —— **全系列**连续编号，跨条目单调递增。

实测：278826（第二季）的 ep 1–13 对应 sort **26–38**；316247（第二季后半）的
ep 1–12 对应 sort **39–50**。所以 Emby 里那个 25 集季度的第 14 集，**既不是
`ep == 14` 也不是 `sort == 14`**，而是 316247 的 `ep == 1`。

匹配顺序：

0. 集号 `N` 先要存在。Emby 解析不出时从文件名再解析一次（见「文件名集号兜底」）。
1. 候选条目按顺序确定：分集自身的 Bangumi ID → 季度的 ID（并沿续集链扩展）→
   系列的 ID（按季号解析；链长 ≤ 1 时回退主条目，兼容《名侦探柯南》这类单条目长番）。
2. 在当前候选里找 `ep == N`，失败再找 `sort == N`；
   **两者同时命中且指向不同分集时按播出日期消歧**（见下）。
3. 仍失败且前序候选已累计 `preceding` 集时，用 `N - preceding` 重试（日志记作 `via ep -13`）。
4. 全部分集都没有编号时，退化为按顺序取第 N 个。
5. 所有候选都失败、且 `N` 大于候选累计集数时，走 franchise 链按 `sort` 兜底
   （见「跨季连续集号回退」）。

特典（`ParentIndexNumber == 0`）不做续集链扩展。匹配成功后命中的条目 ID 会写回
分集的 `ProviderIds`，下次直接命中。

#### 文件名集号兜底

集号是 Emby 的 resolver 从文件名解析的，provider 无法干预——但 resolver 解析不出来时，
provider 是**最后一个还能救回来的地方**。`[GM-Team][国漫][诛仙 第4季][Jade Dynasty Ⅳ][2026][01][HEVC][GB][4K].mp4`
的集号在方括号里，Emby 给不出 `IndexNumber`，后果有两层：所有查找立刻失效，而且这些文件
会掉出季度、被 Emby 统一命名成同一个「第 01 集」（实测诛仙 3 集全叫「第 01 集」）。

解析规则（`Utils/TitleNormalizer.ParseEpisodeNumber`）：

- 先去扩展名、全角转半角；
- 命中 `OP` / `ED` / `NCOP` / `NCED` / `SP` / `PV` / `CM` / `MENU` / `预告` / `特典` /
  `映像特典` / `番外篇` / `图集` / `画集` / `舞蹈` / `变身` / `技能集` / `花絮` / `幕后` /
  `ノンクレジット` 等标记直接返回 null；
- `03.5` 这类半集（Bangumi 记作 `sort 3.5`，Emby 无法表示）返回 null；
- 依次尝试 `S01E01` → `第N话/話/集/回` → `EP?N` → `- N` → 结尾裸数字 → 方括号里的数字；
- **排除年份**（4 位且落在 1900–2099）**和分辨率**（`480/576/720/1080/1440/2160/4320`）。

解析出的集号要写两次：写进 `result.Item.IndexNumber` 好让 Emby 持久化，再写回 `info`
好让后面的匹配代码能用。Bangumi 侧没匹配上时也要把 `HasMetadata` 置 `true`，否则 Emby
会整份丢弃、连集号一起丢。分集标题为空时按实际命中的号生成「第 01 集」/「第 01 話」，
因为 Bangumi 上大量国创和网络放送的分集 `name` / `name_cn` **两个都是空的**
（实测条目 `312298` 诛仙、`630676` 仙逆年番3 全季无标题）。

日志：`Bangumi: Emby left "...[01]..." unnumbered, recovered episode 1 from the file name`。

#### 跨季连续集号回退

长番的发布组用**全系列连续号**命名：`[GM-Team][国漫][仙逆][Renegade Immortal][2023][147][AVC][GB][1080P]`。
而 Bangumi 把《仙逆》按年拆条目，每条的 `ep` 都从 1 重新开始，所以「147」在当季条目里
压根不存在。但它精确等于 `sort`：

| 条目 | 名称 | 集数 | `ep` | `sort` |
|---|---|---|---|---|
| `345802` | 仙逆 | 24 | 1–24 | 1–24 |
| `481211` | 仙逆 年番 | 52 | 1–52 | 25–76 |
| `526970` | 仙逆 年番2 | 52 | 1–52 | 77–128 |
| `630676` | 仙逆 年番3 | 52 | 1–52 | 129–180 |

季度解析故意只在**同一季**内扩展候选（`仙逆 年番` 既没有季号也没有 split cour 标记，
不会被当成第一季的另一个 cour），所以候选只有 `345802`、24 集，147 匹配不上。

回退逻辑：先沿 `前传` 关系一路走到 franchise 的最早条目，再沿 `续集` 走完整条链，
在每个条目里找 `sort == N` 的分集。只接受**精确相等**——`sort` 是全系列唯一的连续计数器，
精确命中就是「本系列第 N 集」的定义，不需要任何偏移猜测。未播出的分集出局
（存在的文件不可能装着还没播的集）。

触发条件卡得很死：常规匹配全部失败、不是特典、且 `N` 大于所有候选条目的累计集数。
普通的「这一集确实缺」不会为此付一次链遍历的代价。

实测 `仙逆 147–155` → 条目 `630676` 的 `ep 19–27`（分集 ID `1650102`–`1650110`），8/8 命中。
副作用收益：《水星领航员》那种把 ANIMATION / NATURAL / AVVENIRE 全塞进一个 Emby 系列的
目录，`ARIA The ANIMATION 04–13` 也靠这条从 AVVENIRE（3 集）回到了正确条目 `531`。
日志：`Bangumi: index 147 looks like franchise numbering, sort 147 is ep 19 of subject 630676`。

#### ep / sort 撞号消歧

只要一个续集条目的 sort 起点小于等于它自己的集数，两种读法就会同时成立且指向不同分集。
实测 `638497`（正反対な君と僕 第2期，13 集）的 ep 1–13 对应 sort 13–25，于是文件
`S02E13` 同时是「ep 13」（大结局 `1717851`，2026-09-27 未播）和「sort 13」
（首集 `1704756`「平安夜」，2026-07-05）。纯粹「先 ep 后 sort」的顺序会把大结局盖到
本季第一集上。

判据是**文件不可能装着还没播的集**：两个候选的播出状态不同时，未播的那个出局；
播出日期分不开时保持 ep 优先（对老番零回归）。日志会记成
`ep (unaired sort rejected)` / `sort (unaired ep rejected)`。
`airdate` 缺失或无法解析视为已播，因此完全没有日期的条目行为与加这条判据之前完全一致。

#### 过期 pin 会被丢弃

分集 `ProviderIds` 里的 `BangumiEpisode` 是插件自己上一轮写进去的派生数据，
而 ID 查找排在编号查找**之前**。所以旧版规则写坏的 ID 会被永久锁死，连
`ReplaceAllMetadata` 都冲不掉。现在 pin 指向的分集若尚未播出，直接视为过期并放弃，
退回正常编号匹配。

### 编号回写

`IndexNumber` / `ParentIndexNumber` / `IndexNumberEnd` 由 Emby 从文件名和目录结构解析，
插件不做任何判断——但必须**原样回写**：

Emby 的 `MergeBaseItemData` 在 `ReplaceAllMetadata=true` 时会**无条件**用 provider 的返回值
覆盖这三个字段。provider 把它们留在默认的 `null` 上不等于「尊重文件名」，而是**把编号清空**。
丢 `ParentIndexNumber` 的代价最大：每一集都掉出自己的季度，Emby 会为每个文件凭空造一个
「第 1 季」。实测一次 `ReplaceAllMetadata` 重刮，54 集里 44 集丢编号，多出 39 个垃圾 Season。

把入参照抄回结果里，这次合并对这三个字段就是 no-op，无论跑的是哪种刷新模式。
季度侧同理（`BangumiSeasonProvider` 回写 `IndexNumber`）。

> 已经被旧版本刮坏的库不需要手工修：装上修复版后对每部再跑一次
> `FullRefresh` + `ReplaceAllMetadata=true` 即可。Emby 的 `BeforeMetadataRefresh`
> 会重新从文件名解析编号，provider 原样回写并保存，多余的 Season 由 Emby 自动回收。

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
- **`/v0/subjects/{id}/persons` 与 `/characters` 都只给原文名**（`関根明良` 而不是
  「关根明良」），中文译名只存在于各自详情的 infobox「简体中文名」里。插件按
  「中文译名查询上限」的预算逐个补 `/v0/characters/{id}` / `/v0/persons/{id}`，
  超预算的保留原文，不会为了 300+ 人的条目把请求数打爆。
- **`POST /v0/search/subjects` 的索引有缺口。** 不是打分问题，也和 NSFW 无关
  （加 `filter.nsfw` 一样查不到）：`ギルティホール` 和 `ハーレムきゃんぷっ！`
  在 v0 搜索里**完全查不到**，但旧版 `GET /search/subject/{kw}?responseGroup=large`
  两者都是第一条命中（`516604` / `395537`）。旧版接口不需要 Token。
  所以插件把旧版接口当第二轮 pass，而不是只在 v0 返回空数组时才用——
  v0 几乎总会返回一堆字面相近的垃圾，永远不为空，那样写等于死代码。
- **旧版搜索接口返回的是残缺条目**：`responseGroup=large` 也只有
  `id` / `name` / `name_cn` / `summary` / `air_date` / `images` / `rating` / `eps`，
  没有 `infobox` / `tags` / `meta_tags` / `platform`。直接拿去映射会静默丢掉
  studios / genres / tags，必须再 `/v0/subjects/{id}` 补全。
  v0 搜索返回的条目则与 `/v0/subjects/{id}` 同形，不需要补。
- **受限（NSFW）条目的子资源返回 404 而不是 401。** 无 Token 时
  `/v0/subjects/{id}/persons` 与 `/characters` 一律 404，条目本身却能正常取到，
  结果就是「刮削成功但演员表几乎是空的」，和「这个条目本来就没录人员」无法区分。
  插件为此会在日志里打一次明确提示（每次改配置后重新武装）。
- **公司会被记成「人物」**：`製作`、`动画制作`、`出品方` 这些关系下挂的是 KADOKAWA、
  WHITE FOX 这类法人，Bangumi 的 `type` 仍是 1（个人）。按 `type` 过滤不管用，只能按
  职位关系名挡掉，否则演员表里会混进一堆公司。

## 已知限制

- **中文译名对不上时，只能靠原文名或目录名把它救回来。** `罪恶之渊` 就是典型：
  这个中文名和 `罪の深淵` 在 Bangumi 上都搜不到，正确条目 `516604` 只在用原文名
  `ギルティホール` 搜旧版接口时才出现。插件现在会自动用「原始标题」和文件名线索
  去试，所以这一类大多已经能自动命中；但如果 TMDB 也没写原文名、目录名和文件名也都只有
  中文译名，就真的没有可用信息了——此时闸门会拒绝写入（日志里有记录），
  **在「识别」界面手填 Bangumi ID** 即可，插件的三个 External ID 就是为这种情况准备的。
- 同类情况还有 `乱马½`：Bangumi 记作 `乱马1/2`，分数归一化能对上，但同名重制版
  与旧版条目并存，相关性一般，建议也手填 ID。
- **受限（NSFW）条目不填 Token 就没有人员表。** 见「Bangumi API 的坑」，
  这不是匹配问题，条目 ID 是对的，只是 `/persons` 与 `/characters` 被 404 掉了。
- 系列主条目本身就叫「系列名」而各季标题里不带季度标记时（少数长番），
  季度解析会落到第三级「链上序号」，日志里会标明 `chain ordinal`，这一档不保证正确。
- **文件名里没有季号时，插件救不了。** 像
  `[ANi] Re：從零開始的異世界生活 第四季 - 12 [...].mp4` 这种只有 `- 12` 的命名，
  Emby 在调用任何 provider 之前就已经解析成 `S01E12`，并为它造出一个「第 1 季」。
  插件收到的 `SeasonInfo.IndexNumber` 就是 1，无从纠正。这类文件要在**下载器重命名**
  那一层修（见 `anime-auto-stack` 的 ani-rss 配置），或手工改名成 `S04E78` 形式。
- **未播判定的容差是 UTC 当天 +1 天**，粒度到日。跨时区的当日新番在这一天内两种读法
  都算已播，此时仍按 ep 优先。

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
│   ├── BangumiPeopleModels.cs  人物 / 角色详情 DTO 与 infobox 取值
│   └── TtlCache.cs            带惰性过期扫描的内存缓存
├── Tasks/
│   └── BangumiPersonMetadataTask.cs  批量补全人物页的计划任务
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
    ├── BangumiPersonProvider.cs     人物页元数据 + 头像
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