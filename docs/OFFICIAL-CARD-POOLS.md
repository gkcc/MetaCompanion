# 暴雪官方 Standard / Arena 卡池快照

`tools/Sync-BlizzardCardPools.ps1` 从公开的 Hearthstone Card Library API 分别同步当前 Standard 与 Arena 可收集卡池。它不读取 Chrome、Edge、浏览器登录态、Cookie 或密码，也不需要 Blizzard/HSReplay 账号授权。

默认接口为：

```text
https://hearthstone.blizzard.com/en-us/api/cards?set=standard&pageSize=450&page={page}&locale=en_US
https://hearthstone.blizzard.com/en-us/api/cards?gameMode=arena&pageSize=450&page={page}&locale=en_US
```

同步器只允许 HTTPS、`hearthstone.blizzard.com`、Card Library 固定路径和固定查询参数集合；禁用 Cookie 与自动重定向。单页响应默认限制为 8 MiB，并同时校验响应头大小与流式读取后的实际大小。

## 运行与目录

正常同步：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\Sync-BlizzardCardPools.ps1
```

默认使用 HDT 已有的卡牌定义：

```text
%AppData%\HearthstoneDeckTracker\CardDefs\CardDefs.base.xml
```

默认输出布局：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorData\OfficialCardPools\
  runs\<run-id>\
    standard.json
    arena.json
    manifest.json
  latest\
    standard.json
    arena.json
    manifest.json
    publish-complete.json
```

Standard 与 Arena 是两个独立的 API 查询和数据文件，不能用 Standard 卡池近似 Arena。数量会随暴雪轮换和接口数据变化，消费者应读取当次 manifest，不能把某个固定数量写进程序。

## 文件契约

`standard.json` 与 `arena.json` 的 `schema_version` 当前为 `1`。每个 `cards[]` 条目包含：

```text
card_id, dbf_id, slug, name, collectible,
card_set_id, class_id, multi_class_ids, card_type_id, rarity_id,
mana_cost, attack, health, durability, text,
spell_school_id, minion_type_id, multi_type_ids, keyword_ids
```

卡池 `coverage.generation_pool_metadata=true` 表示这些生成池筛选字段已经随快照保存；字段没有值时为 `null` 或空数组，不能猜默认派系、种族或关键词。

其中 `dbf_id` 来自 Card Library API，`card_id` 必须由本机 `CardDefs.base.xml` 成功映射。可选数值字段会保留为 JSON `null`，不会因缺值而改变对象结构。

`manifest.json` 记录：

- `run_id`、UTC 抓取时间、数据集状态和 schema 版本；
- 暴雪来源、无需认证/浏览器、Cookie/重定向/响应大小策略；
- CardDefs build、实体数、文件字节数和 SHA-256；
- 每个卡池的 API 声明总数、唯一 dbfId/CardID 数、文件大小和 SHA-256；
- 每一页的 URL、网络或 fixture 来源、HTTP 状态、ETag、Last-Modified、行数和 API 声明总数；
- `rules_coverage = false` 与 `generated_entities_coverage = false`。

`publish-complete.json` 只存在于完成提升的 `latest`，并用 `run_id` 和 `manifest_sha256` 绑定当前 manifest。消费者应先验证该 marker，再验证 manifest 中两个卡池文件的 SHA-256。`generated_at_utc` 与两个卡池记录的 `fetched_at_utc` 也是消费契约的一部分，必须是带时区的 ISO 8601 时间。

本机 Python 与 Rust worker 启动时都会按上述顺序验证快照，并额外执行两组运行时绑定；Rust 主路径从 `--advisor-data`，或运行数据目录同级的 `AdvisorData`，定位同一份 `OfficialCardPools`：

- manifest、卡池记录以及 page 元数据中存在的快照时间都不得超过最大年龄。默认最大年龄为 72 小时，可用环境变量 `METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS` 调小或调大；只接受大于 0 且不超过 720 小时的有限数。为容纳同步机与运行机的微小时钟偏差，时间最多可以比本机当前 UTC 超前 5 分钟；超过 5 分钟才以 `snapshot_timestamp_in_future` 拒绝，容差固定且会在 health 中报告为 `future_clock_skew_seconds = 300`。
- manifest 的 `card_defs.file_name/build/bytes/sha256` 必须与当前 HDT 的 `%AppData%\HearthstoneDeckTracker\CardDefs\CardDefs.base.xml` 完全一致。消费者不会使用 manifest 提供的任意路径；离线测试可显式注入 fixture 路径。
- manifest 必须恰好包含一条 Standard 和一条 Arena 记录；记录中的文件字节数、声明数量、唯一 CardID/DBF 数、SHA-256 以及不重复的正整数页码都会与实际卡池文件逐项核对。重复格式、额外格式、重复页或任一计数漂移都按无效快照降级，不能以“文件仍可解析”为由继续消费。

当前 schema-v1 的历史快照可能只在卡池记录上提供 `fetched_at_utc`，page 条目没有独立时间。此时每个 page 明确继承已经验证过的所属卡池抓取时间；若 page 自带 `fetched_at` 或 `fetched_at_utc`，则必须逐页通过同一 stale/future 门禁。

health 与每次 solve 的 `coverage.official_card_pool` 会返回已经验证的 `run_id`、`card_defs_build`、`card_defs_sha256`、`card_defs_bytes`、manifest SHA-256、生成/最早抓取时间、最大年龄、时钟容差及相应模式卡池数量。快照缺失、陈旧、来自容差之外的未来或 CardDefs 不匹配时，worker 会降级为 `available=false`，不会因此停止建议服务；同时只返回非敏感稳定 reason，例如 `snapshot_file_missing`、`snapshot_stale`、`snapshot_timestamp_in_future`、`card_defs_build_mismatch`、`card_defs_size_mismatch` 或 `card_defs_hash_mismatch`，不会暴露真实路径。卡池成员关系也不会被用来替代动作合法性或卡牌规则。

## 验证与原子发布

一次同步只有在以下条件全部满足后才会发布：

- 所有分页成功，实际总行数与 API `cardCount` 一致；
- 每个卡池的 dbfId 和映射后的 CardID 均唯一；
- 所有 API 行都标记为 collectible；
- 每个 dbfId 都能映射到本机 CardDefs；
- 两个 JSON 文件、manifest、大小和 SHA-256 全部通过复读校验。

脚本先写入隐藏 staging 目录，验证后固定为 `runs/<run-id>`，再通过目录重命名提升完整的 `latest`。Standard 或 Arena 任一抓取、解析、映射或验证失败时，旧 `latest` 保持不变；`.sync.lock` 防止并发同步同时提升。

## 离线 fixture 与自检

离线复现时应同时传入两份单页 Card Library API JSON fixture；fixture 仍会执行 CardDefs 映射和全部发布校验：

```powershell
.\tools\Sync-BlizzardCardPools.ps1 `
  -StandardFixturePath C:\fixtures\standard.json `
  -ArenaFixturePath C:\fixtures\arena.json `
  -CardDefsPath C:\fixtures\CardDefs.base.xml `
  -OutputRoot C:\fixtures\OfficialCardPools
```

内置自检：

```powershell
.\tools\Sync-BlizzardCardPools.ps1 -SelfTest
```

自检只在系统临时目录创建最小 CardDefs、API fixture 和版本快照，覆盖映射、可选字段、来源、哈希、版本化、原子提升、URL 限制，以及网络/验证失败时保留旧 `latest`；它不会读取真实 AppData 或访问网络。

## 能做什么，不能做什么

该快照只定义暴雪接口当前返回的“可构筑或可选取卡牌集合”。它可以为 Standard/Arena 状态校验、候选域和后续规则数据对齐提供官方来源，但不包含完整卡牌规则，也不生成衍生物定义。

它不提供卡牌效果执行、合法动作生成、触发顺序、发现/抉择、随机结果、隐藏信息、单卡胜率、职业排名或最优出牌标签。因此它本身不能让 MCTS 或强化学习得到最优策略。完整实战建议仍需要版本化规则引擎、策略/价值训练、隐藏信息模型和独立评测门槛。

Rust 的 `card-generation-pools-v1` 是独立的哈希绑定规则层：它可以用本快照的费用、类型、职业、派系、种族、系列、稀有度和关键词字段解析已审核的随机/Discover 候选，但不会把快照本身称作规则。牌库、手牌、墓地、历史牌和专属衍生池必须由各自运行时数据提供，解析器会对这些来源失败关闭，绝不回退到 Standard/Arena 全池。

HDT 本地 ArenaSmith 草稿先验是另一套带本机样本偏差的数据，契约见 [ADVISOR-DATA.md](ADVISOR-DATA.md)。官方卡池负责回答“当前候选集合”，本地先验只提供弱排序信号，两者不能互相冒充。
