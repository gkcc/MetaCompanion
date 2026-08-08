# Advisor 本地数据与行为语料

`tools/Sync-HdtArenaAdvisorData.ps1` 把 HDT 缓存的竞技场选牌记录转换成求解器可读取的、当前补丁内的匿名先验。输入是：

```text
%AppData%\HearthstoneDeckTracker\ArenaLastDrafts.xml
```

该文件记录本机见过的英雄选择、三选一候选、实际选择、ArenaSmith 分数和 `Packages` 关系。它不是 HSReplay 全局数据，也不是完整竞技场卡池；输出中的 `complete_global_statistics` 和 `coverage.is_complete` 因此固定为 `false`。

## 运行与目录

正常运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\Sync-HdtArenaAdvisorData.ps1
```

默认读取 `MetaCompanion\patch_marker.txt`。只有 `Draft.StartTime >= patch_marker` 的草稿会进入本次快照；补丁标记缺失或无法解析时脚本直接失败，不会混用旧补丁数据。测试或离线导入可显式指定全部路径：

```powershell
.\tools\Sync-HdtArenaAdvisorData.ps1 `
  -InputPath C:\fixtures\ArenaLastDrafts.xml `
  -PatchMarkerPath C:\fixtures\patch_marker.txt `
  -OutputRoot C:\fixtures\AdvisorData\Arena
```

输出布局：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorData\Arena\
  runs\<run-id>\
    manifest.json
    card_pool.json
    card_priors.json
    hero_priors.json
    package_relations.json
  latest\
    <同一组 JSON>
    publish-complete.json
```

每次先在隐藏 staging 目录写完整快照，校验 JSON、字段、行数及 SHA-256 后再固定为 `runs/<run-id>`。`latest` 也先完整 staging，再通过目录重命名提升；新快照验证失败时既有 `latest` 保持不变。`.sync.lock` 用于防止两个同步进程同时提升。

## 文件契约

- `manifest.json`：`schema_version = 1`、run/补丁 epoch、来源与非完整性声明、样本计数、告警计数、各数据文件大小和 SHA-256。不会写入输入或输出的绝对路径。
- `card_pool.json`：当前补丁的本地草稿中实际出现过的候选/已选卡集合和观测次数。它只是 observed pool，不能用于断言某张未出现的卡不在官方竞技场卡池。
- `card_priors.json`：每张观测卡的 `prior_weight`、出现场次、选择率及 ArenaSmith 分数的 count/mean/min/max。权重限制在 `[0.05, 3]`。
- `hero_priors.json`：英雄出现/选择次数，以及相对均匀随机选择经过加一收缩后的先验。
- `package_relations.json`：ArenaSmith `Package(KeyCard -> Card)` 的有向关系、观测次数、关键牌被选次数和关联牌当时已在牌组中的次数。
- `publish-complete.json`：只在已整体提升的 `latest` 中出现，绑定 `run_id` 与 `manifest.json` 哈希。消费者可用它识别完整发布。

卡牌权重 `arenasmith_score_plus_local_choice_v1` 以当前快照的 ArenaSmith 平均分归一化信号为 80%，以“实际选择相对该次均匀随机选择”的加一收缩信号为 20%；没有分数时只使用后者。它是搜索排序先验，不是胜率，也不是监督标签。英雄先验采用同样的本地选择相对均匀基线。Package 权重只表达缓存关系的重复出现程度。

## 求解器消费方式

消费者应先读取 `latest/publish-complete.json`，确认其 `run_id` 与 manifest 一致并校验 manifest 哈希，再校验 manifest 中四个数据文件的哈希。补丁 marker 与当前运行环境不一致时应拒绝或明确降级，不能静默跨补丁使用。

- `card_priors.json.cards[].prior_weight` 可乘入策略网络或 MCTS 的动作先验；合法动作仍必须由规则引擎产生。
- `package_relations.json` 可作为竞技场选牌、套牌协同或隐藏牌 belief 的弱特征，不能当作卡牌效果或必然共现。
- `hero_priors.json` 适合竞技场职业/英雄选择先验，不应用于标准模式动作合法性。
- 数据缺失、样本过少或分数缺失时，求解器应退回模型/中性先验并降低置信度，而不是把缺失解释成零强度。

官方卡池与本地 Arena 先验必须分别验证、分别降级。Python 与 Rust worker 都会强制检查 manifest、卡池记录及 page 自带的 `generated_at_utc`/`fetched_at_utc` 最大年龄与未来时间，并把 manifest 中的 CardDefs build、字节数和 SHA-256 与当前 HDT `CardDefs.base.xml` 绑定；默认最大年龄为 72 小时，可通过 `METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS` 配置。系统时钟只允许固定 5 分钟偏差，超过才拒绝；旧 schema-v1 page 没有独立时间时继承已经验证的卡池记录时间。验证后的 run/build/hash/count 会进入 health 和 `coverage.official_card_pool`。任一检查失败只会令官方卡池 `available=false` 并返回稳定、非敏感 reason，不会把本地 observed Arena pool 当成替代品。完整契约见 [OFFICIAL-CARD-POOLS.md](OFFICIAL-CARD-POOLS.md)。

## 隐私、安全与限制

同步器会读取草稿容器以定位记录，但不持久化玩家标识、牌组标识、账号信息、原始 XML、绝对源路径或由这些值生成的匿名哈希。版本快照只保留聚合卡牌/英雄/Package 信号。

XML 读取限制为 32 MiB，禁止 DTD 和实体解析；畸形或不安全输入在写 run/latest 前失败。ArenaSmith 缓存会受本机历史、所选英雄、当时的 ArenaSmith 版本和玩家自身选择偏好影响，因此不能冒充官网完整卡池、全服单卡胜率或全局英雄排名。官方当前 Standard/Arena 卡池由独立同步器提供，见 [OFFICIAL-CARD-POOLS.md](OFFICIAL-CARD-POOLS.md)；全局统计仍需另接带版本和授权的数据源，并与本快照分开标注 provenance。

## 自检

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\Sync-HdtArenaAdvisorData.ps1 -SelfTest
```

自检只在系统临时目录创建 XML、patch marker 和输出，验证补丁过滤、身份移除、ArenaSmith 分数、Package 关系、版本化/原子提升、验证失败保留旧 `latest` 以及畸形 XML；它不会读取或写入真实 AppData。

## 双方行为语料

行为语料是独立于上述环境先验、也独立于严格强化学习轨迹的本地链路：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker\
  training-v2.jsonl   # 严格轨迹与求解观察
  behavior-v1.jsonl   # 双方公开行为语料

%AppData%\HearthstoneDeckTracker\MetaCompanion\behavior-outbox-v1\
  <game>\<sequence>.<transport-sha256>.json

%AppData%\HearthstoneDeckTracker\MetaCompanion\result-outbox-v1\
  <fifo-order>.<transport-sha256>.json
```

只要开启“保存脱敏训练记录”，C# 入口就继续启动 worker、采集公开局面并提交 `/v1/observe` 与 `/v1/behavior`；“实时策略建议”可以同时关闭。后者关闭时不会调用 `/v1/solve`，也不会显示建议面板。

`advisor-behavior-v1` 按对局内顺序记录双方以下事件：

- `play_card`：出牌；
- `attack`：攻击；
- `hero_power`：使用英雄技能；
- `location_activate`：激活地点；当前只接受本方 `DebugPrintOptions + SendOption + PLAY` 精确证据，不推测对手地点激活；
- `end_turn`：结束回合。

每条记录包含 `actor_side=local|opponent|unknown`、动作来源证据、身份/可见性/边界状态，以及动作前和可稳定关联的动作后公开局面投影。对局结束或边界不稳定时允许 `post_state=null`，同时降级为 `behavior_eligible=false`。已知是本方或对手行动、但无法从公开证据唯一解析来源实体或卡牌时，可以保留 `identity_status=unknown` 的降级记录；此时同样不能把猜测补成实体身份。隐藏的对手手牌只保留本局实体编号与 `visibility=hidden` 占位，以支持前后状态差分；不写 CardID、牌名、文本、费用或战斗属性。

本方 Power 行为可以额外带以下可选字段：

- `sub_option`：HDT `SendOption.selectedSubOption`；
- `board_position`：HDT `SendOption.selectedPosition`；
- `choice_status=none|selected|unresolved|not_observed`；
- `choices[]`：每次选择的 `choice_id`、`choice_type`、`source_entity_id`、`option_entity_ids`、`selected_entity_ids` 和单项 `status`。

`board_position` 现已是正式动作维度，而不只是旁证。HDT 的值按棋盘从左到右、从 1 开始；随从和地点出牌会枚举 `1..当前棋盘实体数+1` 的全部合法落位，并把动作 ID 写成 `play_card:<source>:<target>:position=<N>`。回放按 `N-1` 插入棋盘；法术、武器、攻击、英雄技能和结束回合不得携带正落位。旧语料缺少该字段时仍可读取且原哈希不变，但旧随从出牌不能因此冒充与新候选集合精确对齐。

当前 HDT 1.54.1 的提供集来自 `GameState.DebugPrintEntityChoices()`，最终选择来自 `GameState.DebugPrintEntitiesChosen()`。只有两段日志的 choice ID 与 Player 一致、来源实体与产生选择的具体 PLAY 根动作一致、候选和最终选择的 `Entities[index]` 从 0 连续且索引/实体均不重复、实际最终选择数等于头部 `EntitiesCount`，并且最终选择非空且全部属于候选集时，动作证据才升级为 `exact_hdt_power_choice_v1`。Player 只在内存中转换为不可逆指纹用于相等性核对，不进入语料。旧 `SendChoices` 只证明结果而不证明完整提供集，因此固定降级；缺少提供集、来源错误、数量/索引截断、选中未提供实体等情况写为 `choice_status=unresolved`，并强制 `behavior_eligible=false`。选择累加器在产生时绑定具体根动作；没有根动作的换牌或其他选择不会延迟挂到后续出牌，也不能用畸形尾行污染后续动作。旧语料没有这些可选字段时仍按原合同读取，其规范内容与哈希保持不变。

`behavior_eligible` 只表示该条记录是否足以用于行为统计；所有行为记录都强制 `rl_training_eligible=false`。它们不会进入 `training-v2.jsonl`，不会被行为语料自身自动晋升，也不能绕过严格轨迹的独立回放、终局关联和离线晋升门禁。

最终 `win|loss|tie` 仍由 `/v1/observe` 的 `kind=result` 记录写入 `training-v2.jsonl`；`advisor-behavior-v1` 没有 result/outcome 字段。终局 JSON 先写入独立 `result-outbox-v1`，再按观察事件 FIFO 提交，因此不会越过此前已排队的 action。行为动作与胜负只能在同一 worker 持久化后，通过相同的匿名 `game_id` 做局级关联。这种关联便于行为统计，但不会修改 `behavior_eligible`，更不会把固定为 `false` 的 `rl_training_eligible` 提升为真。

## HDT 历史回放行为导入

HDT 的 `.hdtreplay` 内含双方 Power 轨迹，可用于补足“本方做了什么、对手做了什么”的模仿语料。先执行只读审计；默认只选择回放中最新的客户端 build，并只包含 Standard 与 Arena，避免把旧补丁、狂野或其他模式混入当前策略：

```powershell
python .\solver\launch_solver.py audit-hdt-replays `
  --output .\artifacts\hdt-replay-audit.json
```

确认审计通过后再导入独立目录：

```powershell
python .\solver\launch_solver.py import-hdt-replays `
  --output-dir .\artifacts\hdt-replay-import
```

导入产物固定为：

- `behavior-v1.jsonl`：双方公开动作；
- `advisor-decision-frame-v1.jsonl`：可严格恢复的本方完整主行动候选集与真实选择；
- `training-v2-results.jsonl`：只含可关联的脱敏终局；
- `hdt-replay-import-v1.json`：选择范围、质量计数、隐私声明和输出 SHA-256。

原始回放、回放文件名、玩家名、账号、原始日志哈希和真实时间均不写入产物；对手手牌仍只保留隐藏实体占位。`game_id` 由脱敏后的公开轨迹生成，时间只表达动作先后。行为与决策帧都固定 `rl_training_eligible=false`，决策帧另固定 `optimality_verified=false`；胜局中的动作也不能因此被称为最优动作。导入文件不追加到实时 worker 的活动 JSONL；它们必须先走独立联审、模仿晋级和三分割模型门禁。跨 build 数据可以分别审计和留档，但不能直接合并为当前补丁策略。

Power 事件里的 entity descriptor 可能停留在实体首次出现时的旧 `zone/controller/zonePos`。解析器只让完整实体更新覆盖这些可变字段；`TAG_CHANGE` 与根 `BLOCK_START` 只补稳定身份或缺失字段，不能把已经确认的 `HAND -> PLAY -> GRAVEYARD` 迁移倒回去。HDT 的完整实体更新同时存在 `Updating Entity=[...]` 和 `Updating [...]` 两种正式格式；两者都必须先切换 implicit entity，再接收后续 `CARDTYPE/ATK/HEALTH/ZONE` 等标签，不能把标签误挂到上一个实体。历史日志中的本方 `DebugPrintOptions + SendOption + PLAY` 会额外绑定随从/地点落位，但必须同时满足所选 option 的主实体、目标、子选项与后续根动作完全一致，行动方确为本机，且 `selectedPosition` 位于当时 `1..棋盘实体数+1` 的合法范围内。新 Options 帧、错来源、错目标、错子选项、越界位置、对手动作、法术或其他非棋盘动作都会清空或降级，绝不把陈旧位置串给下一动作。输出前，pre/post state 会逐条经过生产 `GameState` 合同；超过 10 张手牌、超过 7 个棋盘实体或其他无效边界按稳定 `solver_state_contract:*` 原因跳过，保留下来的逐局 sequence 重新连续编号。

完整决策帧只接受本方主行动。option ID 必须从 0 连续，合法 POWER 必须为 `error=NONE`；每个 option 下只有 `error=NONE` 的 target 才进入候选，结束回合由 option 0 与实际主行动帧共同证明。随从和地点按核心棋盘槽位规则展开全部位置，再要求 `SendOption` 的 option、目标、子选项、位置与后续 PLAY/ATTACK 根动作逐项一致。只要出现合法 subOption、同一动作对应多个语义不明的 option、交易/牌库/暂存区选择、隐藏目标、截断 block 或新的 Options 帧抢占，整帧就按稳定原因拒收。每条合格帧必须包含唯一结束回合、唯一真实选择、完整候选集、对应 `selected_behavior_id` 和一致的 pre/post state；内容与 ID 均按规范 JSON 做 SHA-256 绑定。该合同证明 HDT 当时提供的界面候选与玩家选择，允许做候选模仿和离线排序评估，但不声称独立重建了炉石规则，也不把玩家动作或终局当作最优标签。对手没有本机 Options，因此只能进入双方行为语料，不能伪造对手候选集。

导入报告和后续 `audit-behavior-learning` 会同时检查：行动方在 pre-state 为当前玩家、每次 `play_card` 的来源在行动方手牌且 post-state 不再留在该手牌、每次 `end_turn` 都切换当前玩家。孤立的实际 `ATTACK` 根事件还会给该动作的来源补入 `can_attack=true`、`attacks_remaining>=1`；相邻的相同 post/pre 边界同步携带这条直接证据，保持 state ID 连续。它只证明已经发生的攻击，不猜其他角色是否可攻击。manifest 还会量化 Options 帧、SendOption、合格决策帧、候选总数、本方行为覆盖率、逐原因拒收数，并强制 `合格帧 + 拒收选择 = SendOption`、合同错误为 0、RL/最优标记为 0。即使 `ready_for_candidate_imitation_audit=true`，完整卡牌效果仍可能缺失，所以 `solver_evaluation_ready` 固定为 false。

决策帧在进入任何候选排序训练前还要与同批行为文件做独立回读联审：

```powershell
python .\solver\launch_solver.py audit-decision-frames `
  --input .\artifacts\hdt-replay-import\advisor-decision-frame-v1.jsonl `
  --behavior .\artifacts\hdt-replay-import\behavior-v1.jsonl `
  --output .\artifacts\hdt-replay-import\decision-frame-readiness.json
```

只有每行合同和内容哈希有效、逐局 sequence 连续、决策 ID 与行为 ID 均唯一、每个选择都能精确联回同局同动作同 pre/post state，报告才返回 `READY`。这里的 READY 仍只代表 `candidate_imitation_ready=true`；`rl_training_ready` 与 `optimality_verified` 永远为 false。

```powershell
python .\solver\launch_solver.py audit-behavior-learning `
  --behavior .\artifacts\hdt-replay-import\behavior-v1.jsonl `
  --trajectory .\artifacts\hdt-replay-import\training-v2-results.jsonl `
  --output .\artifacts\hdt-replay-import\behavior-readiness.json
```

## 离线联审与模仿语料晋升

`audit-behavior-learning` 把行为文件和终局文件各读取一次，在临时不可变快照上执行联审。报告同时绑定两份输入的 SHA-256/字节数和有效策略 SHA-256，并检查单文件合同、隐私、逐局连续且按文件顺序递增的 sequence、时间倒退、双方覆盖、动作类型分布、稳定 post-state、身份/边界质量、唯一终局、关联率和按匿名 game ID 固定的 train/validation/test 拆分；选择证据另统计 `choice_status_counts`、落位记录数、选择项数、提供实体数和最终选择实体数。对于 `hdt_replay_power`，出牌来源离手、实际攻击的最少准备证据和结束回合切换是额外的合同阻断项；旧的区域回滚语料不能再通过晋升。生产默认门槛要求至少 50 局、500 条行为、45 局终局关联、250 条 `behavior_eligible`，双方合格行为各至少 100 条，关联率和双方覆盖率至少 90%，合格率至少 50%；未知行动方最多 2%，未知身份最多 25%。Release Gate 的合成行为夹具固定包含一条完整选择，并要求至少一个选择项、两个提供实体和一个最终选择实体；合同退化会阻断发布。上述阈值只定义模仿学习语料就绪，不定义 RL 或最优策略就绪。

```powershell
python .\solver\launch_solver.py audit-runtime-behavior-learning `
  --output .\artifacts\runtime-behavior-learning-readiness.json `
  --snapshot-dir .\artifacts\runtime-behavior-learning-snapshots
```

运行态审计状态只能是 `READY|NOT_READY|NO_DATA`。缺少行为数据得到 `NO_DATA`；已有行为但还没有终局文件得到 `NOT_READY`。两者都不阻断插件发布，也都不能启动模型晋升。

达到 `READY` 后，必须另写语料，绝不改写两个生产 JSONL：

```powershell
python .\solver\launch_solver.py promote-behavior-imitation `
  --behavior behavior-v1.jsonl `
  --trajectory training-v2.jsonl `
  --output behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json
```

输出只包含 `behavior_eligible=true` 且有唯一终局的行为，移除 `observed_at_utc`，为对手动作反转 actor 视角胜负，并保留公开 pre/post state、动作证据和源 `behavior_id`。每条样本使用内容寻址 `example_id`，固定 `imitation_training_eligible=true`、`rl_training_eligible=false`、`optimality_verified=false`。manifest 明确批准 `behavior_cloning`、`opponent_behavior_modeling`、`search_ordering_prior`，并禁止把它当作直接 RL 轨迹、最优动作真值或隐藏对手牌重建依据。

## 行为动作与合法候选集合联审

双方动作本身就是有价值的示范数据，但“某人当时这样打了”并不能证明当前求解器已经列全了所有合法备选。任何生产候选排序训练前必须再运行：

```powershell
python .\solver\launch_solver.py audit-behavior-candidates `
  --input behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --rules .\solver\metacompanion_solver\rules_data\hdt-visible-point-effects-v1.json `
  --output behavior-candidate-alignment-v1.json
```

`behavior-candidate-alignment-report-v1` 重新验证 dataset/manifest 的逐字节 SHA-256 绑定，逐条把观察动作与保守候选器对齐，并按总体、本方/对手、动作类型、模式和 split 汇总 `exact|target_mismatch|not_generated`。报告只保留聚合计数，不写匿名 game ID、state ID 或 entity ID。结构化规则只有在局面 `patch` 与规则包 `source.card_defs_build` 完全相等时才会应用；跨 build 固定失败关闭，不能拿新 CardDefs 规则冒充旧版本的精确合法性。

一条记录只有同时满足以下条件，才计入 `candidate_set_eligible`：行动方是本方；观察动作（包括随从/地点落位）被精确生成；至少有两个不同候选；当时所有可行动手牌和英雄技能规则均已验证；棋盘战斗规则证据完整；地点激活、全部合法落位和选择分支均已完整建模。对手动作仍进入行为观察和对手建模统计，但隐藏手牌使其不能冒充“候选集合完整”的本方排序样本。默认策略要求 train/validation/test 分别至少 `30/10/10` 局和 `250/50/50` 条合格记录，并要求本方精确命中率与候选集合合格率均为 100%。

无论状态是否 READY，报告都固定 `candidate_generation_allowed=false`、`live_policy_eligible=false`、`rl_training_eligible=false`、`optimality_verified=false`。这个门禁继续用于规则重建覆盖诊断，也可通过脚本的 `-CandidateAlignmentPolicyPath` / `-CardRulesPath` 显式追加为硬门禁。已经严格恢复的 HDT `Options` 决策帧不再被较弱的规则重建结果覆盖；其候选完整性由 decision-frame 合同、训练器与联合评估独立复核。

## 行为排序先验基线

晋升后的语料可以继续生成一个离线、可审计的行为频率基线：

```powershell
python .\solver\launch_solver.py train-behavior-prior `
  --input behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --output behavior-prior-v1.json
```

训练器会重新验证 dataset/manifest 的 SHA-256 绑定、字段白名单和三分割合同，只用 `split=train` 的局面更新计数。`validation` 与 `test` 只用于报告行为预测指标，胜负字段完全不参与训练。模型分别学习动作类型，以及 `play_card|attack|hero_power|location_activate` 的卡牌/目标公开模板；分层上下文为 global、actor、mode、patch、hero pair 和公开局面桶。层级回退的父分布强度至少为 8，并按 `0.5 × 标签数` 增大，避免数百种稀疏卡牌/目标模板被单局上下文过拟合；该强度只由训练标签空间决定，不查看验证或测试结果。某种动作在 train 分割尚无样本时仍保留平滑空桶，不能因此伪造频次。它不能创造合法动作，只能为外部规则引擎已经确认合法的候选排序；模式或补丁未见时返回均匀概率，不跨版本擅自迁移。

默认生产策略至少要求 train/validation/test 分别有 `30/10/10` 局、`250/50/50` 条行为，验证集中至少 25 条已见模板，并限制动作类型与模板相对 global baseline 的 log-loss 劣化以及未知模板率。未达门槛仍可写出审计产物，但 `search_ordering_prior_ready=false`，加载和评分默认失败关闭。即使全部通过，`live_policy_eligible`、`rl_training_eligible`、`optimality_verified`、`candidate_generation_allowed` 仍全部为 `false`；这只是“真实玩家/对手通常怎么打”的排序先验，不是奖励优化结果，也不是最优出牌证明。

## HDT 原生候选的 listwise 决策排序

历史回放若已经形成 `advisor-decision-frame-v1`，就不再拿其他 CardDefs build 的规则包反推当时的候选。该帧逐条保存 HDT `Options` 给出的完整合法候选、`SendOption` 的真实选择，以及严格绑定的前后公开局面。训练命令为：

```powershell
python .\solver\launch_solver.py train-decision-ranker `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --output decision-ranker-v1.json
```

`advisor-decision-ranker-v1` 使用无第三方依赖的稀疏 listwise logistic 基线。它只用按匿名 game ID 固定划分的 `train` 局更新权重；训练轮次和温度只看 `validation`，`test` 始终封存到最终报告。指标同时给出 Top-1、Top-3、MRR、log loss、均匀排序基线和未见选择模板率。模型特征只来自公开前局面与 HDT 已给出的候选，不保存 game/state/entity ID，也不能创造候选。

双方证据通过下面的联合命令复核：

```powershell
python .\solver\launch_solver.py evaluate-observed-policy `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --imitation behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --prior behavior-imitation-prior-v2.json `
  --ranker decision-ranker-v1.json `
  --output observed-policy-evaluation-v1.json
```

本方用完整候选做排序评估；对手没有本机 `Options`，因此只做公开动作类型与模板的策略建模，绝不伪造其候选集。两条路径都固定 `live_policy_eligible=false`、`rl_training_eligible=false`、`optimality_verified=false`。它们回答的是“你或对手通常会怎么打”，不是“这一步已经证明最优”。

## 历史决策帧与 Rust 求解覆盖对账

双方实际动作继续分别服务本方行为排序和对手模型；要推进到“有证据的备选方案”，还需要独立检查 Rust 在这些真实局面里到底看见了多少合法首步：

```powershell
python .\solver\launch_solver.py audit-decision-solver-coverage `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --binary .\solver-rust\target\release\metacompanion-solver.exe `
  --output decision-solver-coverage.json `
  --max-frames 256 `
  --time-budget-ms 250 `
  --max-iterations 100000 `
  --max-depth 8 `
  --top-k 10
```

`advisor-decision-solver-evaluation-v1` 先重跑决策帧/行为联审，再按帧内容 SHA-256 排序做确定性抽样；`--max-frames 0` 表示审计全部帧。工具只启动一个经过指定文件哈希绑定的 Rust worker，随机会话 token 只通过环境变量传入，不进入进程命令行。每帧发送公开 pre-state，并把历史 `legal_candidates` 转成 `hdt_complete_main_action_options_v1` 请求字段；历史帧早于 collector epoch/watermark 合同，离线适配标识只用于本次请求绑定，报告明确固定 `adapter_identity_used_as_training_evidence=false`。结束回合会把动作 wire ID `end_turn::` 规范到根组合合同 `end_turn`，避免把同一语义误计为缺失。

报告聚合以下证据：

- exact、partial、unsupported、HTTP/协议错误和错误码分布；
- Rust 独立生成候选相对 HDT 全集的召回率、精确率、逐帧均值和完整集合一致数；
- HDT 候选请求/响应合同有效帧数、候选总数、实际评估数、明确跳过数、评估覆盖率和首步组合完全建模帧数；
- exact 声明数、false-exact 及其稳定原因；
- 根覆盖完整、组合最优性已证明、可验证多备选和共同最优备选帧数；
- 玩家实际选择是否落在 Rust 根动作中，以及 Top-1/Top-3 observed-choice 一致率；
- 最常见的缺失动作类型和最多 25 个公开 CardID，用于下一轮规则实现排期。

只有同时满足以下条件的帧才计入 `solver_scope_counterfactual_evidence_count`：响应为 `ok`；`coverage.exact=true` 且 exact scope 非空；根动作覆盖、搜索和组合最优性全部完成；recommendation/coverage 协议自洽；Rust 独立生成根动作与 HDT 全集完全相等；HDT 合法首步全集均已实际评估。任一 exact 声明漏掉、多出或未评估 HDT 根动作都计为 false-exact，报告转为 `REVIEW_REQUIRED`。诚实的 partial/unsupported 可以让审计本身通过，因为它们对规则缺口仍有价值，但不会产生局部反事实资格。HDT 确认合法、但公开状态里的费用高于可用法力时，说明费用修正证据不足；求解器保留该动作在合法集合中并明确跳过，不猜实际支付费用，也不让该动作拖垮同帧其他候选。

报告只保留输入字节数和 SHA-256、样本 SHA、聚合计数及公开 CardID；禁止写入 game/state/entity/request/decision/behavior ID、真实时间、URL、token 或绝对路径。无论通过与否，都固定 `counterfactual_dataset_written=false`、`observed_choice_used_as_optimality_label=false`、`outcome_used_as_action_optimality=false`、`candidate_generation_allowed=false`、`live_policy_eligible=false`、`rl_training_eligible=false`、`global_optimality_verified=false`。因此它能回答“当前求解器在哪些真实局面覆盖了哪些备选”，仍不能把玩家操作、单局胜负或求解器局部证明自动升级为完整炉石最优策略或 RL 标签。

随包的 `Update-AdvisorBehaviorPrior.ps1` 现在发布一对互相绑定但用途分离的模型：`AdvisorWorker\decision-ranker-v1.json` 只服务本方完整 HDT 候选，`AdvisorWorker\behavior-prior-v1.json` 只服务对手公开行为。实时日志来源先做内容寻址行为/终局快照和模仿语料晋级；`-HistoricalSourceDirectory` 则优先复制并复核历史导入目录中已绑定的行为、决策帧、模仿语料和 manifest。随后依次训练两个候选、执行 `evaluate-observed-policy` 联合评估以及 Rust `behavior-prior-check` / `decision-ranker-check`。只有联合报告为 `READY` 且两个 Rust 门禁都通过，才会 staging、归档旧模型并成对替换，最后写入绑定两份模型 SHA 和评估 SHA 的 `advisor-ordering-models-v1.install.json`；任一替换失败会恢复两份旧模型与旧清单并复核哈希。

安装后的 Rust worker 会分别热加载两个模型。本方不存在 ranker 时不会回退到旧 behavior prior；任一模型缺失、损坏、跨模式/补丁或单次评分异常时，对应一侧恢复确定性基础顺序。模型只重排已经生成的合法动作，不生成候选、不覆盖战术分数，完整穷举结果保持不变。

本方 ranker 还有一个明确隔离的在线兜底用途：只有战术响应为 `partial`，且请求携带 `hdt_complete_main_action_options_v1` 完整候选帧时，worker 才可返回 `hdt_complete_candidate_behavior_reference_v1`。这里的分数是模型对“玩家过去会选哪个动作”的估计，不是胜率、价值函数、RL 回报或最优性标签；胜负不会被用作动作最优标签。参考动作必须全部来自原候选帧，可以包含战术模拟尚未覆盖的地点、武器或复杂法术。C# 会重新验证候选全集大小、逐项规范身份、排序、decision-ranker SHA-256 和禁止候选生成/覆盖战术评分/自动操作/live policy/RL/optimality 的字段，任何篡改都会令整个参考区失败关闭。通过后也只在“你过去的打法参考（不代表最优）”独立区域展示，不与战术建议、对手行为先验或胜率混排。

```powershell
& "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools\Update-AdvisorBehaviorPrior.ps1"

& "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools\Update-AdvisorBehaviorPrior.ps1" `
  -HistoricalSourceDirectory "D:\path\to\hdt-replay-import"
```

脚本的生产数据门槛首先要求行为联审达到至少 50 局、500 条双方行为和 45 局唯一终局关联，再分别要求本方决策帧排序与对手行为模型的 train/validation/test 质量门槛，最后要求联合评估 READY。没有真实对局时返回 `status=no_data`；有行为但没有完整决策帧时返回 `status=not_ready`。生产入口永远不会用 Release Gate 的合成夹具生成或安装本地模型。

## 写入、幂等与恢复

C# 只提交未哈希的合同字段，不生成语料 ID。Rust 实时 worker 在接受 `/v1/behavior` 后统一完成；隔离的 Python 离线实现只用于合同互操作测试：

1. 将会话 game ID 转换为固定的 `anon-<16 hex>`；
2. 对安全字段白名单做规范化序列化并计算 `content_sha256`；
3. 生成 `behavior-<64 hex>`；
4. 以 `(anonymous game_id, behavior_sequence)` 校验连续顺序和幂等。

相同内容的重试返回同一确认且不重复追加；同一序号对应不同内容、序号跳跃或生产者自报哈希/ID 时失败关闭。`GET /v1/health` 分别报告 `behavior_log_enabled` 与 `behavior_log_healthy`，不把行为语料健康状态混入 `training_log_healthy`。

行为行只有在完整写入并完成 `flush + sync_data`（Rust）或 `flush + os.fsync`（Python）后，才可返回 `logged=true`。同步失败不会确认，并立即废弃内存索引；无论是否经历过同步失败，worker 每次从磁盘重建索引时都必须先同步活动 `behavior-v1.jsonl`，之后才允许把已有行确认为 `duplicate`。这样即使 worker 重启丢失了内存中的失败标记，也不会凭未验证的完整行删除 outbox。

行为语料只恢复活动文件最后一个未换行片段：完整 JSON 对象仅缺换行时原位补换行并同步；真正半截 JSON 先按 SHA-256 保存为同目录只读 `behavior-v1.jsonl.torn-tail.<sha256>.fragment`，再截断到最后一个完整换行并同步。已换行分隔的中间坏行、重复 ID/序号或对局内断序均不自动修复，索引加载失败并令 `behavior_log_healthy=false`。

插件侧 `behavior-outbox-v1` 是可靠传输队列，不是训练语料。它按局、按序号原子落盘并以本地 transport hash 校验文件；worker 暂不可用、请求超时或响应丢失时，原始 JSON 会保持不变并按 FIFO 重试。只有收到 game ID、sequence 和合法 `behavior_id` 均匹配的精确 ACK 后才删除队首文件；进程在 `.pending` 重命名前后中断时，下次启动会恢复该文件。

`result-outbox-v1` 与行为队列、两类 JSONL 物理隔离。Rust/Python 只对 `kind=result` 做持久内容寻址：完全相同的终局重试（包括 worker 重启后）返回 `duplicate` 且不追加第二行；同一匿名 game ID 对应不同 state、result 或元数据时返回冲突并保留队首文件。为保证两种 worker 计算出完全相同的 `result_id`，终局 `observation.metadata` 只允许 string、boolean、integer、null，浮点值会在写入前按 `request.metadata.<field>` 拒绝；该限制不扩展到普通 action、solve 或卡牌标签元数据。终局行写入 `training-v2.jsonl` 后，Rust 必须执行 `flush + sync_data`，Python 必须执行 `flush + os.fsync`；只有耐久屏障成功，才能返回可供插件删除队首的成功 ACK。若写入已经完成但同步失败，本次不确认，下一次重试先重新扫描磁盘；已经完整落盘的同一 result 会按 `result_id` 返回 `duplicate`，不会再追加一行。

重启或健康检查读取终局索引时，只允许对活动 JSONL 的尾部做可证明、安全的恢复：

- 最后一段本身是完整 JSON 对象、只是缺少换行符时，原位补上换行并执行耐久同步；该记录保留并参与终局去重，不归档、不丢弃。
- 最后一段确实是半截 JSON 时，先按内容 SHA-256 保存为同目录只读文件 `training-v2.jsonl.torn-tail.<sha256>.fragment`，确认归档成功后才把活动文件截断到最后一个完整换行并耐久同步；若此前没有完整行，则活动文件截为零字节。
- 已由换行分隔的中间坏行不属于可安全推断的尾部中断。worker 不改写、不截断这类历史，而是失败关闭并令 `training_log_healthy=false`，等待人工审计。

插件只有在 game ID、state ID、result 和合法 `result_id` 全部匹配时才删除终局文件。两个 outbox 遇到暂时失败都会以 250ms 起、30 秒封顶的退避在后台自行重试；关闭插件会取消等待，但不会删除未确认文件。上述终局恢复只保护严格 `training-v2.jsonl` 的一致性；独立 `behavior-v1.jsonl` 仍固定 `rl_training_eligible=false`，不得因能够与终局按匿名 game ID 关联而进入严格训练轨迹。

官网单卡、牌组、传说团队和职业排名可以作为搜索或模型先验，官方 Standard/Arena 卡池可以界定版本与覆盖范围；这些数据都不提供完整效果执行、合法动作或最优策略标签，不能替代规则引擎、严格轨迹或强化学习训练。上述链路也不会读取 Chrome/Edge 的密码、Cookie 或登录态。
