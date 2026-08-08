# Rust 求解器迁移契约

Meta Companion 采用“C#/.NET HDT 薄适配层 + 独立 Rust 求解器进程”。HDT 的插件加载器只会加载
托管程序集，并要求插件类型实现 `Hearthstone_Deck_Tracker.Plugins.IPlugin`；当前工程还需要直接
使用 HDT/WPF 的 .NET Framework 4.7.2 类型。因此 `MetaCompanion.dll` 必须保留为 .NET 入口，
原生 Rust exe 不能直接替代它。

C# 层负责插件生命周期、公开局面快照、中文 UI、进程管理和响应合同复核；模拟、枚举与搜索等
性能热路径由独立 Rust worker 承担。生产配置固定为 `RustOnly`，HDT 不再安装或回退到 Python
实时后端。Python 只保留在隔离目录中，供显式离线训练、评测和审计使用。
HDT 插件入口因此只能继续使用 C#；选择 Rust 是把高性能工作移到独立进程，不是用 Rust exe
直接实现 `IPlugin`。

运行开关是两个正交维度。`live=true` 控制 `/v1/solve` 和建议 UI，`training=true` 控制局面采集、
`/v1/observe`、`/v1/behavior` 与本地日志。因而 `live=false, training=true` 仍会启动 worker 并记录
双方行为，但绝不发起 solve，也不显示建议；两项都关闭才停止该运行链路。

## 进程契约

生产可执行文件名固定为 `metacompanion-solver.exe`，必须支持：

```text
metacompanion-solver.exe serve --port <1..65535> --data-dir <path> [--no-training-log]
```

- 只允许绑定 `127.0.0.1`。
- 会话令牌从 `METACOMPANION_SOLVER_TOKEN` 读取，不写入命令行、标准输出或日志。
- 同时接受 `Authorization: Bearer <token>` 与 `X-Advisor-Token`；比较必须避免明显的时序泄漏。
- 请求正文上限为 2 MiB，响应必须小于 C# 客户端的 4 MiB 字符上限。
- `/v1/cancel` 必须能在另一请求线程中设置协作取消标志；客户端断开连接本身不算可靠取消。
- CPU 密集的 solve 同时只运行一个；health/cancel 仍可并发处理，排队中的旧请求可先被取消。
- 未捕获错误只返回稳定错误码和通用消息，不返回路径、堆栈、令牌或请求正文。C# 对普通
  Warning/Error 再统一生成中文安全摘要，不把英文异常、绝对路径、URL 或凭据值写入常规日志。
- Rust worker 已按 `advisor-training-log-v2` 接通 `/v1/solve` 与 `/v1/observe`。路径优先级为：
  `--no-training-log` 完全关闭、显式 `--training-log`、`<data-dir>/training-v2.jsonl`、配置文件的
  `training_log_path`；都未提供时保持关闭。配置中的相对路径以配置文件目录为基准。
- 行为语料路径由有效训练日志路径派生为同目录 `behavior-v1.jsonl`；`--no-training-log` 会同时
  关闭这两类落盘，不允许把行为语料写进 `training-v2.jsonl`。
- `/v1/health` 动态报告 `training_log_enabled`、`training_log_healthy`、
  `behavior_log_enabled` 与 `behavior_log_healthy`。严格轨迹与行为语料分别报告健康状态，且错误
  信息不泄露本地路径。
- 严格训练轨迹写盘前再次移除身份、凭据、牌组名/ID、精确墙钟时间和隐藏对手牌身份；私有 game ID 匿名化，
  同局固定分配 train/validation/test。并发写入共用单一 append 锁，每行必须是完整 JSON。
- `partial_hdt_transition_candidate_v1` 必须同时通过 pre/post state ID、原始 SHA-256、快照序号和
  边界字段校验；派生状态哈希只允许 logger 计算，最终强制为
  `partial_hdt_gameevents_v1`、`training_eligible=false`。因此现有 HDT GameEvents 记录可审计，
  但仍不是 exact/replayable 训练动作；只有离线 trajectory auditor 可以晋升独立回放通过的样本。

HTTP API 版本保持 `1.0`：

- `GET /v1/health`
- `POST /v1/solve`
- `POST /v1/cancel`
- `POST /v1/observe`
- `POST /v1/behavior`

所有路由都需要认证。`cancel` 同时提供 `request_id` 和 `state_id` 时，以精确 `request_id`
为准，不能再用旧 `state_id` 误取消同局面的新请求；仅缺少请求 ID 时才允许按状态取消。
服务端短暂保留尚未注册请求的取消标记，以关闭“取消先到、solve 后注册”的竞态。

### 双方行为语料合同

`/v1/behavior` 接受 `advisor-behavior-v1` 的未哈希内容对象。C# 不得提交 `content_sha256` 或
`behavior_id`；worker 是 game ID 匿名化、公开字段白名单、规范化 JSON 和内容寻址的唯一权威。
成功响应包含 `logged`、`duplicate`、匿名 `game_id`、`behavior_sequence` 和
`behavior-<64 hex>`，供插件精确确认。

语料对双方覆盖 `play_card`、`attack`、`hero_power`、`end_turn`，并额外接受本方 HDT Power
三段证据确认的 `location_activate`。HDT 当前没有同等可靠的对手地点输入事件，因此生产链路不靠
局面差分猜测对手地点激活。每条记录同时保存 `local`/`opponent` 行动方证据、动作前公开状态及可稳定关联的动作后状态；未得到稳定边界时 `post_state` 可以为
`null`，且该条必须降级为 `behavior_eligible=false`。对手隐藏手牌只能保留本局实体编号和
`visibility=hidden` 占位，不得包含 CardID、名称、文本、费用或战斗属性。已知行动方、但来源实体或卡牌无法唯一解析时，允许以
`identity_status=unknown`、`behavior_eligible=false` 降级记录；不得猜测身份。无论
`behavior_eligible` 为何，`rl_training_eligible` 必须且只能为 `false`。

本方 Power 行为可选地携带 `sub_option`、`board_position`、`choice_status` 与 `choices[]`。
其中 `board_position` 对随从和地点是从左到右的 1-based 正式动作身份；Rust/Python 均枚举全部合法落位、按该位置回放，并在 `action_id` 追加 `:position=N`。非棋盘牌或非出牌动作携带正位置、位置为 0/负数/大于 7、或位置超出当时棋盘插入范围时均失败关闭。C# 只接受同一规范身份，并以中文显示“放在从左数第 N 个位置”。
C# 还必须在落盘前验证 choice ID/Player、连续且唯一的实体索引、最终实体数等于
`EntitiesCount`；Player 只转换为内存指纹，不进入协议。Rust 必须逐项验证 choice ID/type、来源实体、
非重复提供集、非重复最终选择集，以及每个最终选择都属于提供集；`choice_status=selected` 还只接受 `actor_side=local`、`source_event=hdt_power_log`
和完整 selected 项。缺提供集、来源错误或越界选择只能以 `unresolved` 写入并强制
`behavior_eligible=false`。当前 C# 只有在 HDT 的 `DebugPrintEntityChoices` 与
`DebugPrintEntitiesChosen` 能绑定到产生它们的具体根动作时才声明 `exact_hdt_power_choice_v1`；
无根动作的换牌选择不会延迟挂到下一次出牌。旧行缺少这些可选字段时仍按原内容和原哈希读取。

`behavior-v1.jsonl` 与严格 `training-v2.jsonl` 使用不同文件、schema 和审计路径。行为记录不能
自动进入 RL 训练，也不能绕过严格轨迹的 Power 完整性、终局关联、独立回放和离线晋升门禁。
最终 `win|loss|tie` 仍由 `/v1/observe` 的 `kind=result` 写入 `training-v2.jsonl`；behavior
schema 不含 result/outcome 字段。两类记录只能通过同一 worker 对同一会话局 ID 生成的匿名
`game_id` 做局级关联，该 join 不改变任何行为记录的 `rl_training_eligible=false`。
终局请求由独立 `result-outbox-v1` 先持久化，再与 action observation 共用发送 FIFO。worker
对 result 的安全内容生成 `result-<64 hex>`：相同请求在响应丢失或重启后返回 `duplicate` 而
不追加第二行；同一匿名局 ID 的不同终局内容返回 `409 result_observation_conflict`。普通 action
observation 仍保持原有 append-only 语义。首次写入 result 时，Rust 必须在 `write_all` 后执行
`flush + sync_data`，Python 兼容 worker 必须执行 `flush + os.fsync`；耐久屏障失败时不得返回成功
ACK，并必须使下一次重试从磁盘重建终局索引，避免“已经写完但同步报错”造成重复追加。

重建终局索引前可以恢复且只能恢复活动 JSONL 的未换行尾段。若尾段是完整 JSON 对象，只补换行
并同步，原记录继续参与内容寻址去重；若尾段确实是不完整 JSON，必须先按 SHA-256 写成同目录
只读 `training-v2.jsonl.torn-tail.<sha256>.fragment`，再截断到最后一条完整 JSONL 记录并同步。
已经换行分隔的中间坏行不得自动截断或跳过；索引加载失败时 `/v1/health` 必须报告
`training_log_healthy=false`，后续 result 失败关闭。任何恢复或终局关联都不得把
`behavior-v1.jsonl` 的固定 `rl_training_eligible=false` 改为真，也不得把它并入严格轨迹。

离线 Python 工具提供独立的 `audit-behavior-learning`、
`audit-runtime-behavior-learning` 和 `promote-behavior-imitation`。它们把 Rust 写入的
`behavior-v1.jsonl` 与 `training-v2.jsonl` 终局按匿名 `game_id` 联审，分别生成内容寻址快照，
并只把 `behavior_eligible=true` 且有唯一终局的行为另写为去时间戳的
`behavior-imitation-example-v1`。这条链路批准模仿学习、对手建模与搜索排序先验，但每条输出仍
固定 `rl_training_eligible=false`、`optimality_verified=false`；Rust 在线 worker 不读取原始
行为或模仿语料，只能读取通过后续双重门禁的聚合先验产物。

`train-behavior-prior` 会在 Python 离线工具中把 hash-bound 模仿语料训练为
`behavior-imitation-prior-v2` / `hierarchical-behavior-frequency-v1`。它只使用 game-level `train` 分割学习动作类型与公开动作模板，
`validation/test` 仅评估，终局胜负不作为特征；调用方必须先用规则引擎提供合法候选。未见模式或补丁
返回均匀排序，且产物固定 `live_policy_eligible=false`、`rl_training_eligible=false`、
`optimality_verified=false`、`candidate_generation_allowed=false`。因此这不是把 Python 放回性能热路径；
Python 只负责离线审计和训练，Rust 会严格重新验证产物合同、计数、策略哈希、质量检查和用途边界。
`Update-AdvisorBehaviorPrior.ps1` 现在分别训练并发布 `decision-ranker-v1.json` 与 `behavior-prior-v1.json`。本方行动只允许 decision ranker 参与，
对手行动只允许 behavior prior 参与；本方 ranker 缺失时绝不回退到旧行为频率先验。脚本要求联合评估 READY、两个 Rust 加载门禁通过，
再 staging、归档并成对替换两份模型和联合清单；第二份替换或清单写入失败会恢复旧文件并复核哈希。在线 Rust 在下一次健康检查或求解时
分别热加载，并只重排已经生成的合法动作。它不能改变候选集合或战术分数，完整穷举结果不变，任何加载/评分异常都恢复确定性基础顺序。
官网单卡/牌组/团队/职业排名只可作为先验，官方 Standard/Arena 卡池只可作为版本边界和覆盖
清单；二者都不能替代规则执行或 RL 标签。

worker 按 `(anonymous game_id, behavior_sequence)` 做重启安全的顺序校验与幂等：完全相同的
重试返回同一 `behavior_id` 且不追加第二行；同序号不同内容或序号缺口失败关闭。C# 侧可靠
outbox 每局 FIFO 保存原始请求，HTTP 响应丢失时原样重试，只有匿名 game ID、sequence 和合法
`behavior_id` 都匹配 ACK 后才删除文件。队列文件名中的哈希仅用于传输完整性，不是 corpus ID。
首次行为写入必须经过 `write_all + flush + sync_data`（Rust）或完整 write、`flush + os.fsync`
（Python）后才可返回 `logged=true`；失败后索引失效。任何磁盘索引重建都要再次同步活动 corpus，
才能为已有行返回 `duplicate`。最后一个完整 JSON 仅缺换行时补换行并同步；真正半截尾行先保存为
只读 `behavior-v1.jsonl.torn-tail.<sha256>.fragment` 再截断同步；中间坏行、重复或断序不修复，
并使 `behavior_log_healthy=false`。

### 搜索预算合同

C# 首批请求默认发送 `time_budget_ms=3000`、`max_iterations=6000`、`max_depth=8`；需要深化时，
最终请求使用 10 秒总预算的实际剩余时间，并发送 `max_iterations=20000`、`max_depth=12`。
首批已经完成组合最优性证明或直接斩杀证明时，不再重复启动最终搜索。

Rust 必须从 HTTP 请求开始时把 `time_budget_ms` 转成单调时钟的绝对截止时间；正文读取、适配、
规则匹配和单 solve 队列等待都计入该预算。exact turn-pair、scoped lethal
和 visible fallback 共享同一个节点预算。节点上限不再按每条对手回应或每个回退阶段重新发放。
exact/scoped 一旦被时间、节点或深度截断，只能失败关闭并进入诚实的 partial 路径，不能携带
minimax、回应已验证或组合最优声明。visible 到期后停止扩展，但仍为每个已建模首步保留立即
结束己方回合与对手不行动的安全基线；节点按剩余首步公平分配，避免第一个首步独占预算。
响应在 counterplay coverage 中分别报告 `time_limit_reached`、`node_limit_reached` 和
`depth_limit_reached`，普通界面只显示中文概括。
`allow_approximate_effects=false` 时仍可返回完整 exact 或独立 scoped-lethal 证明，但必须禁止
visible 近似候选并以稳定的中文错误合同失败关闭。

## 不确定性计算架构（卡池 Chance 已上线，隐藏信息仍是目标设计）

随机、发现和对手隐藏手牌不应继续扩展成大量单卡特判，也不能交给策略网络“猜规则”。目标架构把
确定性规则、概率和学习模型分成三层：规则引擎只负责合法动作、卡牌效果与状态转移；概率层负责
维护可审计的分布并产生 chance node；策略/价值模型只负责排序、剪枝和叶子估值。任何学习模型都
不得创造动作、修改效果、绕过 CardID/文本哈希/机制证据门禁，或把玩家实际操作当作最优标签。

隐藏信息使用信息集 belief，而不是给每个手牌槽独立猜一张牌。候选域必须同时受模式、职业、当前
官方卡池、已识别套牌/竞技场牌池、已公开牌、生成来源、复制上限、抽牌时点和剩余牌库约束；每次
对手公开行动后用似然更新整组可能世界，并保持总概率为 1。双方行为语料可以校准“这个对手更可能
怎样打”，但不能把未见卡直接写进对手手牌，也不能改变动作合法性。

搜索层采用信息集搜索与 expectimax/ISMCTS 的组合：

- 小型随机结果池精确枚举；大型池按分层或重要性采样，并报告覆盖概率质量、样本数、固定种子和
  置信区间。概率分支按概率求期望，不能像玩家动作一样由 UCB 任意选择。
- 发现分为“随机提供候选集”和“看到候选后的选择”两层。尚未出现提供集时，对合法组合做 chance
  node；HDT 已给出实际候选后，只在该候选集内比较，不再对已经揭示的结果抽样。
- 对手手牌按整套加权可能世界 determinize，同一个我方动作跨世界聚合。建议同时看期望收益、可信
  下分位数和反杀概率，避免纯最坏情况被极低概率牌绑架，也避免纯平均值忽略高影响风险。
- 等价终态可合并以节省节点；剪枝必须保留概率质量账本，不能把“没算到”当成概率为零。超时或
  样本不足时只能返回带不确定性说明的 partial，不能升级为 exact/co-optimal。

最终界面仍只显示“首选 / 备选一 / 备选二”的行动线，必要时补充简短风险理由；节点数、内部概率
表和模型诊断留在 Tooltip/日志。每条方案未来应区分规则精确度、概率覆盖、期望值、下分位值和关键
风险来源，使“高均值但怕某张牌”与“更稳的备选”可以被人直接理解。

上线门槛至少包括：随机池概率守恒、发现组合与官方池一致、固定种子可复现、对手牌 posterior 的
log loss/Brier/校准测试、保留牌数与复制约束、chance-node Python/Rust 互操作、不同采样预算下的
收敛曲线、反杀概率夹具，以及 `false_exact = 0`。当前 Rust 已能把审核过的随机生成、Discover 和
随机召唤规则解析到官方当前模式卡池：小池精确枚举，大池使用确定性分层代表并保留原池权重，多次
随机的联合候选约束在 96 个结果内，公开结果发生后要求 HDT 重算。`card-generation-pools-v1` 当前
完整登记 417 张随机相关卡，其中 32 张通过 CardID、类型和英文文本哈希门进入运行时，385 张因
触发器、动态费用、专属池、区域/历史来源或嵌套结算保持失败关闭。Discover 目前把三张候选按牌面
质量启发式折叠，还没有把玩家选择接成完整后续搜索的 Choice 节点；对手隐藏手牌的整世界 belief、
posterior 校准和 ISMCTS 也尚未实现。因此当前只对已审核的公开卡池 Chance 声明相应范围，其他
随机、未知抽牌和隐藏世界继续 abstain 或明确 partial。

## 正确性边界

Rust 只能声明它实际证明的范围。未支持的卡牌、公开标签或效果必须返回 `unsupported`
或带稳定覆盖代码的近似结果，不能静默忽略。

HDT 适配器不会再把所有非空文本一律当作未知效果。若且唯若整段文本可解析为已实现的内建关键词序列，并且每个关键词都有当前实体公开标签证明，卡牌会绑定 `hdt-intrinsic-keywords-v1` provenance；复合文本或证据缺失仍保留 `card_text_not_parsed`。扰魔的两个指定限制同时进入 Rust/Python 合法动作枚举：法术与英雄技能排除该目标，战吼仍允许，随机和群体结算不经过这条玩家指定过滤。

对手回应只有同时满足下列条件才能标为已验证：

- `is_response_verified=true`
- `response_scope=visible_generic_turnpair_v1`
- `response_kind=minimax_best_response`
- `response_search_complete=true`
- `minimax_value` 是有限数
- `is_safe_after_response` 与 `response_is_proven_lethal` 互为否定
- `opponent_response.tactical_value` 与 `minimax_value` 一致
- `opponent_response.actions`、`opponent_reply` 和 `counterplay.actions`（存在时）逐动作一致

C# 仍会独立校验这些联动字段；Rust 不满足时不得依靠客户端“猜测”成安全路线。

### 多备选首步合同

返回多个方案时，“备选”按不同的第一步定义，不能用同一第一步的较差后续路线凑满
Top-K。实时插件会把同一稳定 `DebugPrintOptions` 帧展开成
`hdt_complete_main_action_options_v1`，绑定 `state_id/frame_id/collector_epoch/watermark`，并包含
唯一结束回合、全部合法目标和随从/地点落位。Rust 把该集合用作且只用作首步合法性来源；后续动作仍
独立生成。状态、帧、epoch、watermark、实体、目标或所选 option 任一错绑都在求解或 observation
写盘前失败关闭。Python 兼容 worker 不具备这条能力，收到该字段时用中文要求切换 Rust。

响应同时给出 `independent_generated_root_coverage` 与
`hdt_supplied_root_portfolio_coverage`。前者只统计 Rust 独立生成集合相对 HDT 的召回、精确和全集
一致；后者统计 HDT 合法集合里实际评估与明确 omitted 的动作，禁止拿 supplied 集合美化独立生成。
地点激活、费用修正等合法但无法从公开状态安全重放的首步仍留在 legal 集合，明确 omitted 并返回
`partial`，不得猜规则、猜费用或让同帧其他候选一起失败。只有独立集合等于 HDT 全集、HDT 全集均
已评估且完整搜索证明组合最优时才允许 exact。

求解器在开始搜索时冻结这组完整合法首步，并在
`coverage.details.counterplay` 返回：

- `legal_first_action_count`
- `legal_first_action_ids`
- `generated_first_action_count`
- `generated_first_action_ids`
- `response_verified_first_action_count`
- `response_verified_first_action_ids`
- `missing_first_action_ids`
- `root_action_coverage_complete`
- `portfolio_optimality_proven`

三个 ID 数组必须有序且不重复；计数必须与数组长度相同，generated 是 legal 的子集，
response-verified 是 generated 的子集，missing 必须恰好等于 legal 减去 response-verified。

每条推荐同时返回 `verified_portfolio_regret` 和 `alternative_kind`。完整覆盖所有合法首步只
证明“每种第一步至少验证过一条完整路线”，不证明该第一步下面的所有后续行动已经穷尽。
只有 `portfolio_optimality_proven=true`、该路线回应已验证且差值为 0 时，才能标记
`co_optimal`；限时 PUCT 始终把零差值路线称为 `best_found`，只有 Rust/独立 oracle 的完整
枚举且没有深度截断时才可证明共同最优。未完成回应验证的路线只能标记 `fallback`。
C# 会再次检查这些联动关系，并把不成立的“共同最优”降级为中文的“当前已验证最佳”。

这里的“共同最优”仍严格限定在当前公开信息、已建模规则和本次有界搜索范围内，不等于
完整炉石、隐藏手牌与未来随机结果下的全局最优。

### 历史打法参考合同

只有战术响应为 `partial`，且请求中的 `hdt_complete_main_action_options_v1` 已通过完整性与局面绑定校验时，Rust 才可附带 `hdt_complete_candidate_behavior_reference_v1`。本方 decision ranker 必须对这组 HDT 合法候选逐项评分，不能自行生成或删改候选；因此地点激活、武器、复杂法术等当前无法安全模拟效果的动作仍可进入历史参考排序。对手 behavior prior 不得进入该区域，它仍只用于公开对手回应的搜索顺序。

响应必须同时绑定 decision-ranker 产物 SHA-256、完整候选数、已排序候选数、实际展示数、连续排名、规范 `legal_action_id` 与单步 action。每项 `observed_choice_probability` 只表示行为克隆模型估计的玩家选择倾向，必须伴随 `probability_calibrated_as_win_rate=false` 和 `optimality_verified=false`。顶层还必须固定：

- `candidate_generation_allowed=false`
- `tactical_score_override_allowed=false`
- `automatic_action_allowed=false`
- `live_policy_eligible=false`
- `rl_training_eligible=false`
- `optimality_verified=false`
- `outcome_used_as_action_optimality=false`

C# 会把每项重新绑定到实际发送的候选帧，并独立核验模型哈希、候选计数、概率范围与顺序、动作身份和上述安全字段。任一字段缺失、类型错误、哈希不一致、动作不属于完整合法集合，或响应不是 `partial`，都会隐藏整个参考区；不可用模型是普通降级，不显示英文技术原因。通过校验后也只能在“你过去的打法参考（不代表最优）”独立中文区域显示，明确说明它不是胜率或强化学习最优结论，并且永远不会自动操作游戏。

## 固定晋升门槛

能力不是由二进制自行声明后就被信任。统一发布门禁先用固定行为联审 fixture 验证双方覆盖和一条
完整的提供/最终选择合同，再用固定 train/validation/test 行为 fixture 实际执行
`train-behavior-prior`。另一组合成夹具会真正执行 `train-decision-ranker`、`evaluate-observed-policy`、两个 Rust loader 和事务回滚自检，
验证本方完整候选与对手公开行为严格分流；随后对在线 Rust 候选使用五组固定门槛：

1. `combat-v1`：至少 7 条 `oracle-turn-v1` exact fixtures，比较初始合法动作、动作后语义
   终态、斩杀结论和第一动作。
2. `full`：至少 40 条 fixtures；当前共 51 条，由 7 条 `oracle-turnpair-v1` exact、43 条
   `oracle-hdt-cardrules-v1` exact 和 1 条 scoped-lethal 组成，比较整数 minimax utility、
   最坏回应、双方英雄血甲和 Top1。
3. `visible-response-v1`：至少 3 条 raw HDT partial fixtures，检查可见反杀威胁优先、未知己方
   动作禁用、不同首步近似，以及不得伪造 exact/safe/minimax/optimal 声明。
4. `official-card-pool-v1`：由独立 Python 门禁临时发布 Standard/Arena 双池，要求发布标记、
   清单、分页时间、CardDefs 和身份唯一性同时通过；随后对清单绑定、过期 page、CardDefs
   哈希和重复身份做篡改负控，并逐字段比较 Python/Rust 健康合同。
5. `advisor-decision-solver-evaluation-v1`：使用 3 帧/9 候选合成 HDT 决策帧实际请求同一 Rust
   二进制；要求独立生成 9/9、HDT supplied 评估 9/9、omitted 0、false-exact/协议/隐私错误均为 0。

缺少 Rust 二进制、缺少 profile、fixture 数低于 `7 / 40 / 3`、任何不一致或异常退出都必须使
对应门禁失败。三份搜索报告、官方卡池报告和决策帧覆盖报告还必须绑定同一个二进制 SHA-256。fake binary 只测试门禁本身是否
fail-closed，不计入实现覆盖率。

只有三组搜索门禁、官方卡池门禁与决策帧覆盖门禁全部通过，统一门禁才允许把 Rust 二进制放进
`package-root/solver/metacompanion-solver.exe`。该包安装后，C# 会把它部署到
`AdvisorWorker/metacompanion-solver.exe` 并固定作为唯一实时后端；Python 源码仍随包提供离线
训练/评测能力，但安装到 `AdvisorOfflineTools`，不位于 worker 搜索路径。

发布时必须显式把候选二进制交给统一门禁；该参数不能与 `-SkipTests` 或外部
`-PackagePath` 同用：

```powershell
.\tools\Invoke-ReleaseGate.ps1 `
  -RustSolverBinaryPath .\solver-rust\target\release\metacompanion-solver.exe
```

门禁会对同一个 SHA-256 依次运行固定 `combat-v1`、`full`、`visible-response-v1`、
`official-card-pool-v1` 与 `advisor-decision-solver-evaluation-v1`，并在
复制前再次校验文件哈希。任一门槛失败、报告字段漂移、fixture 数减少或二进制在验证后变化，
最终包都不会晋升 Rust，且整次发布判定失败；这样的 `package-root` 不得部署。

## 性能基线

2026-07-30 对 Python `oracle-turnpair-v1` 的一次 cProfile（8 次 solve）记录：

- 总计约 917 万次调用 / 2.77 秒；
- `copy.deepcopy` 累计约 1.44 秒；
- JSON 序列化状态键累计约 0.60 秒；
- `apply_action` 累计约 0.49 秒。

Rust 热路径因此使用拥有型紧凑状态、便宜克隆与显式结构化状态键；serde 只用于进程边界，
不得在树搜索内通过序列化 JSON 生成置换表键。

## 打包约束

- 目标为 `x86_64-pc-windows-msvc`。
- Cargo `target/` 永不复制进插件包；只 stage 经过门禁的 release exe。
- 打包继续包含 Python 离线训练/评测工具，但安装到 `AdvisorOfflineTools`；`AdvisorWorker` 只允许
  Rust 可执行文件、模型和运行数据。
- Rust 崩溃、启动失败或契约不兼容时，实时建议明确降级为不可用并后台重试，不启动 Python。
- Rust 对当前局面返回 `unsupported`、HTTP 422、超时或取消时保持 Rust 后端，只按响应合同显示
  覆盖不足或中止状态，不隐式切换运行时。

## 灰度部署与后台验收

Rust 灰度只能运行同一次 `PASS` 门禁生成的 `package-root/Install-MetaCompanion.ps1`。不能从
源码树或 `dist/` 运行安装器后再手工覆盖 exe，因为那会破坏门禁报告、DLL、Rust worker 与
安装内容之间的哈希绑定。

安装并重启 HDT 后运行：

```powershell
.\tools\Invoke-HdtAdvisorRuntimeSmoke.ps1 `
  -ExpectedPluginDll .\artifacts\release-gate\<timestamp>\package-root\MetaCompanion.dll `
  -ExpectedRustBinary .\artifacts\release-gate\<timestamp>\package-root\solver\metacompanion-solver.exe
```

后台验收检查一个 Rust worker、没有 MetaCompanion Python worker、仅回环监听、无令牌健康
请求为 401、DLL/Rust 哈希、当前会话日志，以及可见建议面板中是否泄露英文技术文本。它只读
使用 UI Automation，不移动真实鼠标；面板未出现时必须报告 `not_exercised/partial`，不能算作
UI 通过。

旧 `Invoke-HdtClientSmoke.ps1` 会调用源码树的安装器，本轮 Rust 灰度不要使用，否则可能覆盖
已通过门禁的 worker。它仍可用于与 Rust 产物无关的旧版人工流程，但不能作为本轮哈希绑定的
部署或验收证据。

上述门禁和灰度只证明固定 fixture 与当前公开信息范围内的合同一致性，不证明完整炉石规则、
强化学习质量或隐藏信息下的全局最优策略。
