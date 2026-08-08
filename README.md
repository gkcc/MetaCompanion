# Meta Companion for Hearthstone Deck Tracker

Meta Companion 是一个个人向 HDT 插件，目标是把标准模式环境识别、对手形态预测、剩余卡牌预测、实战行动线建议、赛后总结和个人牌组形态推荐整合到一个本地工具里。

## 主要能力

- 从 HSReplay / HSReplay Premium 同步标准模式牌组库、形态热度和对阵矩阵。
- 从暴雪公开 Card Library API 分别同步版本化的 Standard 与 Arena 官方可收集卡池。
- 根据 HDT 对局事件和本地历史识别对手可能的形态。
- 前期把预测结果嵌入 HDT 原生对手牌组区域。
- 当原生列表会遮挡时切换到独立“剩余卡牌预测”面板，只显示最接近构筑里仍未出现的原始牌。
- 赛后显示常驻浮窗，汇总最近一局、近期对手分布和当前推荐形态。
- 结合网页端环境数据和本地最近对局，生成个人加权推荐。
- 在标准或竞技场的我方回合调用本机求解器，先给出临时排序，再在约 10 秒总预算内显示最多三条不同首步的路线；受支持的精确局面可附对手最坏可见回应，其余结果明确标为近似备选。
- 局面变化会立即取消旧搜索；近似路线只显示未校准的可见局面排序，不冒充已验证回应、安全性或最优性；确定性斩杀单独标明证明范围，并同时展示风险和规则覆盖度；不会替玩家操作游戏。
- 顾问、设置和数据面板只显示简洁中文状态与下一步；普通 Warning/Error 也只保留中文安全摘要，不输出英文堆栈、绝对路径、URL 或凭据值，重复机制警告最多聚合为两条中文提示。
- 开启“保存脱敏训练记录”后，即使关闭实时建议，也会继续采集双方公开行为；此模式不会请求求解，也不会显示建议面板。

## 运行架构

HDT 的插件加载器要求入口是托管 .NET 程序集，并由其中的类型实现 `Hearthstone_Deck_Tracker.Plugins.IPlugin`；因此 `MetaCompanion.dll` 必须继续使用 C#/.NET Framework 4.7.2，原生 Rust 可执行文件不能直接替代 HDT 插件入口。C# 层只负责 HDT 生命周期、公开局面采集、中文界面和协议校验，计算密集的模拟与搜索放在独立 Rust worker 中，避免把原生求解器加载进 HDT 进程。

实时建议只启动随包且通过门禁的 `metacompanion-solver.exe`，不再安装或回退到 Python 实时后端。Python 代码只放在独立的 `AdvisorOfflineTools` 目录，供用户显式执行离线训练、评测和数据审计；HDT 的 worker 搜索路径不包含该目录。Rust 的速度优势不等于规则完整或策略全局最优。

## 实战建议的当前边界

当前 Rust 实时后端使用三层决策路径：先尝试受支持规则内的 exact turn-pair，再尝试不依赖未知动作的 scoped lethal；两者都不适用时，才对已建模的公开基础战斗返回 `visible-response-v1` 近似排序。近似层按不同首步去重，会保留未知抽牌的未知性，并对可作为白板处理的随从逐实体标注近似依赖；它不返回已验证回应、minimax、安全性、regret 或最优性证明。

首批与最终搜索不是同参数重算：首批默认使用约 3 秒、6000 节点和深度 8，只有结果仍可深化时才用 10 秒总预算的剩余时间运行 20000 节点、深度 12 的最终阶段。Rust 在 exact、scoped lethal 和 visible fallback 之间共享同一请求的节点与单调时钟预算；截断的 exact 结果不会获得证明字段，visible 到期仍保留各已建模首步的基线候选。客户端超时会按精确旧请求 ID 协作取消，避免旧取消误伤同局面的新搜索。

旧 Python 兼容求解器已退出 HDT 实时运行链；其实现仅作为离线对照和评测代码保留。即使 Rust exact 路径覆盖了全部合法首步，也只说明每个首步至少有一条已验证路线，不等于穷尽该首步下的所有后续；只有无深度截断的完整枚举才允许标记“完整搜索范围内共同最优”。

第一批真实 HDT 卡牌文本桥接为 `hdt-visible-point-effects-v1`：当前有 47 条人工审核的可见规则，共登记 205 个显式 CardID。除定点伤害、治疗、自伤和有公开标签证明的吸血外，还覆盖 Backstab 的“仅未受伤随从”目标、58 个 The Coin 变体的本回合法力、Rock、Moonfire、Armor Up!、Demon Claws、Static Shock、Molten Gold 的法术与随从形态、Windpeak Wyrm 的伤害加甲、寒冰箭、灼热裂隙、赤红深渊、Wound Prey/Gorishi Stinger、奎尔多雷造箭师的持续英雄技能费用光环、环形山的尼利对 1 费牌的翻倍、下水道管网的可见召唤、私运的铁铲亡语、奥术绊索的逐点随机伤害与洗入，以及直面托维尔的可见 1 费历史重放。新增规则把 Earthen Roar (`CATA_554`) 的公开无龙分支、奉献、烈焰风暴、地狱烈焰、冰川裂片、Beaming Sidekick (`CORE_ULD_191`)、平等、暴风雪、神圣新星和激寒急流 (`CATA_485`) 接入真实状态转移；随机分支在显式 Chance 节点上保留概率并在结果发生后重算。规则仅在 `card_id + normalized EnglishText SHA256 + card_type + required intrinsic mechanics + declared context guards` 全部匹配时生效，不从自由文本猜效果。造箭师还必须有公开 AURA 标签和英雄技能 `TAG_LAST_KNOWN_COST_IN_HAND` 基础费用证据；搜索中手牌从 4 张降到 3 张后会立刻把技能重算为 0 费并继续枚举，来源离场则恢复基础费用。历史重放和 Casts When Drawn 尚未完整建模的路线会明确标为近似。HDT 对英雄技能恒为 false 的 `IsPlayableCard` 不再作为依据：未使用/禁用状态由公开激活标签判断，实际费用和当前法力由合法动作枚举器独立检查。

仅含内建关键词的卡牌文本现在走独立的结构化桥接：只有整段规范化文本全部由嘲讽、圣盾、潜行、吸血、剧毒、风怒、突袭、冲锋、复生、免疫或扰魔等已实现关键词组成，并且 HDT 同时给出对应实时标签时，才移除 `card_text_not_parsed`；任何多余词、缺失标签或复合效果仍失败关闭。扰魔还进入了动作合法性层：法术和英雄技能不能把它作为玩家指定目标，战吼仍可指定，随机与群体效果不受影响。

独立的 `card-generation-pools-v1` 会扫描当前 Standard/Arena 的 1,811 张去重卡，并把其中 417 张随机/发现相关卡全部登记为结构化库存。当前审核产物有 32 张可直接执行：费用、卡牌类型、职业、法术派系、随从种族、系列、稀有度和关键词会组合成当前模式卡池查询；多费用、多个稀有度或多个种族“各生成一张”会拆成按文本顺序结算的多个 Chance 效果。规则只有在 CardID、类型和规范化英文文本 SHA-256 全匹配时才挂载，随后由当前官方 Standard 或 Arena 快照解析候选。其余 385 张保留触发器、动态费用、专属衍生池、牌库/手牌/墓地/历史来源、嵌套施法或随机目标等明确阻塞标签，绝不拿当前环境卡池冒充这些来源。大池使用确定性分层代表并保留原池权重，联合随机结果约束在 96 个分支内；真实随机出现后由 HDT 立即重算。Discover 目前对三张候选使用牌面质量启发式折叠，还不是由完整后续搜索求出的真正 Choice 节点，因此不能据此宣称全局最优。Choose One、未知抽牌、未登记文本及证据不足的效果仍失败关闭；下水道管网召唤物的亡语尚未建模，因此只进入明确标注的近似候选，不能产生伪精确证明。

本轮已经具备 `trajectory-readiness-v1` 脱敏轨迹契约、只读审计器、离线训练门禁、有界 self-play 轨迹生成器，以及独立 `oracle-turn-v1`、`oracle-turnpair-v1` 和 `oracle-hdt-cardrules-v1` 战术评测/晋升门禁。同一局只使用固定私有别名；初搜/终搜按同一 decision 归并；审计器检查 solve/result 关联、精确动作比例、生产模拟器独立回放、逐局 train/validation/test 切分和隐私泄漏。当前插件会把 HDT 动作前快照与后续两个相同稳定快照关联为自包含 candidate，并对重复 CardID、重叠动作、哈希/序号不一致安全降级。GameEvents 公开回调本身仍缺完整目标与选择证明；本方 PowerLog 现可额外联结 Options、SendOption、根动作和当前 HDT 的提供集/最终选择日志，但生产模拟器还不能回放选择分支，所以严格 candidate 仍固定 `training_eligible=false`，不会因行为证据更精确就冒充可回放 RL 轨迹。只有离线回放成功的 exact 动作才进入逐动作 verified allowlist；`promote-trajectories` 另写 hash-bound 脱敏语料和 manifest，训练器再对同一不可变 snapshot 复审，绝不原地修改生产日志。真实日志目前仍未达到生产训练门槛。HDT 卡牌规则门禁会用独立 point-effect oracle 检查原始 HDT 快照、双方行动合法性、Top 1/Top 3、minimax regret、false-safe、规则 provenance 和应 abstain 的负控；但这些门禁和 self-play 都不代表已有强化学习模型。要达到“最优出牌”，仍需完整且版本化的卡牌规则、足量可回放实战轨迹、策略/价值网络训练、隐藏信息建模和对手池。详细契约见 [solver/README.md](solver/README.md)。

双方行为另写入独立的 `behavior-v1.jsonl`，记录本方和对手的出牌、攻击、英雄技能、结束回合，以及动作前和可稳定关联的动作后公开局面；本方地点激活还会由 Options/SendOption/Power 三段证据精确记录为 `location_activate`。本方 Power 行为还能保存 `sub_option`、`board_position`、`choice_status` 和逐次 `choices`（来源、完整候选实体集、最终所选实体集）。`board_position` 已正式进入动作身份：随从和地点按从左到右 1-based 枚举全部合法位置，动作 ID 追加 `:position=N`，回放也按该位置插入；法术及非出牌动作不能伪造落位，界面会用中文提示“放在从左数第 N 个位置”。只有当前 HDT 的 `DebugPrintEntityChoices` 与 `DebugPrintEntitiesChosen` 同 ID/Player、来源匹配具体根动作、候选与最终选择索引从 0 连续且不重复、实际选择数等于 `EntitiesCount`，并且最终选择全部属于提供集时，才标为 `exact_hdt_power_choice_v1`；缺提供集、来源错误、数量/索引不完整、越界选择或没有具体根动作的换牌/选择都会保留为 `unresolved` 或丢弃关联，绝不串到下一次出牌。旧行为行不含这些可选字段时仍按原内容读取，内容哈希不变，但缺落位的旧随从动作不能冒充新候选的精确命中。HDT 当前没有同等可靠的对手地点输入事件，因此不会靠局面差分猜测对手地点激活。行为语料与严格 `training-v2.jsonl` 完全分离，所有记录固定为 `rl_training_eligible=false`；已知行动方但无法唯一还原卡牌、实体或选择时，只以降级状态保存，不伪造训练合格样本。隐藏的对手手牌只保留安全公开投影。最终胜负仍由 `/v1/observe` 的 result 记录保存在 `training-v2.jsonl`，`behavior-v1` 不自带终局字段；两者只通过同一 worker 生成的匿名 game ID 做局级关联，这不会把行为记录晋升为 RL 轨迹。worker 统一负责规范化内容哈希和幂等；插件侧的行为与终局结果各有独立可靠 outbox，均先落盘、按 FIFO 原样重试，并只在收到精确确认后删除。终局重试按内容寻址去重，同局冲突失败关闭，不会因为响应丢失而重复写入胜负行。

离线工具现可把 `behavior-v1.jsonl` 与 `training-v2.jsonl` 各读取一次并保存为内容寻址快照，联审双方覆盖、动作/身份/边界质量、逐局序号与时间顺序、终局关联率和 game-level train/validation/test 拆分。只有达到版本化 `behavior-learning-readiness-v1` 策略时，`promote-behavior-imitation` 才会另写去时间戳、绑定源文件 SHA-256 的 `behavior-imitation-example-v1` 语料与 manifest；它只批准模仿学习、对手建模和搜索排序先验，所有样本继续固定 `rl_training_eligible=false`、`optimality_verified=false`。这让“看双方实际怎么打”成为可训练的独立数据产品，但不会把玩家行为误称为最优答案。

`audit-hdt-replays` 与 `import-hdt-replays` 还能只读解析本机历史 `.hdtreplay`，复原双方公开出牌、攻击、英雄技能、地点、结束回合和终局。历史日志中的本方 `Options + SendOption + PLAY` 若来源、目标、子选项和根动作完全一致，还会为随从/地点保存当时合法的 1-based 棋盘落位；新 Options 帧、错配、越界、对手动作及非棋盘卡牌都不会继承或伪造位置。解析器现在还会另写 `advisor-decision-frame-v1.jsonl`：对可严格识别的本方主行动，保存 HDT 当时给出的全部 `error=NONE` 动作/目标、结束回合、全部合法棋盘落位、真实选择及前后局面，并绑定对应行为 ID。抉择分支、交易、同一动作的歧义 option、隐藏候选和不完整边界一律拒收；它只批准候选模仿学习，固定 `optimality_verified=false`、`rl_training_eligible=false`。默认只取最新客户端 build 的 Standard/Arena，并输出独立的脱敏行为、决策帧、终局与哈希绑定 manifest；原始文件名、玩家名、账号和真实时间不会进入语料。历史导入不与实时活动日志混写，必须按 build 单独联审和晋级。

历史解析器不会让 `TAG_CHANGE` 或后续 `BLOCK_START` 中重复的旧 entity descriptor 覆盖已确认的区域、控制者和位置；这避免把已下场的牌重新放回手牌，也避免把地标使用误标成再次出牌。每条输出的 pre/post state 都重新通过生产 `GameState` 容量与身份合同；联审还阻断“出牌后来源仍在行动方手牌”、结束回合未切换行动方，以及实际攻击缺少最少一次攻击机会证据。决策帧证明的是“客户端当时向本方提供了这些选择，并且本方实际选了其中一个”；对手没有同等 Options 证据，仍只保存其实际公开动作。它既不能补齐卡牌效果，也不能证明玩家选择最优，因此历史语料仍只用于行为模仿、对手建模和搜索排序，不作为求解器精确评测或最优动作真值。

`audit-behavior-candidates` 会把观察动作与规则引擎保守重建的候选集合对齐，用来回答“仅靠当前规则覆盖能否复原当时选择”；它仍是重要的规则覆盖诊断，但不能跨 CardDefs build 使用，也不能取代 HDT 原生 `Options`。`train-behavior-prior` 把 hash-bound 模仿语料训练成分层频率基线，只读取 `train` 局学习动作类型与公开模板，`validation/test` 只评估。在线 Rust 仅把它用于对手公开行为排序；本方建议绝不再回退到这个频率先验。本方另由 `train-decision-ranker` 学习 HDT 完整候选中的真实选择。两个产物都不能生成动作、覆盖战术评分或改变完整穷举结果，并固定 `live_policy_eligible=false`、`rl_training_eligible=false`、`optimality_verified=false`。

对于已经从历史 HDT `Options + SendOption + Power` 严格恢复出的 `advisor-decision-frame-v1`，使用 `train-decision-ranker` 直接训练同一局面候选集合上的 listwise 排序，不再跨 build 猜历史候选。`evaluate-observed-policy` 会联合报告本方候选 Top-1/Top-3/log loss 与对手公开动作模型：本方只重排 HDT 给出的完整合法候选；对手因没有本机 `Options` 只做行为策略建模，不伪造候选。运行时也严格分流：本方只用 decision ranker，对手只用 behavior prior；任一模型缺失、损坏或评分异常时恢复确定性基础顺序。两者仍然只是模仿数据，不能声称最优或 RL 真值。

当战术求解只能诚实返回 `partial` 时，Rust 可另外返回 `hdt_complete_candidate_behavior_reference_v1`：本方 decision ranker 对本次 HDT 已确认的完整合法首步集合排序，因此地点激活、武器、复杂法术等尚不能模拟效果的合法动作也可以作为“你过去的打法参考”出现。它与战术路线分区显示，并固定标明“不代表最优、不是胜率、不是强化学习结论、不会自动出牌”。C# 会把每个参考动作重新绑定到原始请求的完整候选帧，并核验候选数量、规范动作 ID、模型 SHA-256、排序和全部禁止晋升字段；任一处不一致就整块隐藏。对手 behavior prior 仍只排序公开可见回应的搜索顺序，不猜隐藏手牌，也不会进入这个本方参考区域。

`audit-decision-solver-coverage` 会再把真实历史决策帧逐个交给 Release Rust worker，并把 HDT 当时的候选全集作为首步合法性来源。报告把两套口径严格分开：`independent_generated_root_coverage` 只衡量 Rust 自己生成的候选召回率、精确率和全集一致率；`hdt_supplied_root_portfolio_coverage` 则衡量 HDT 合法候选中实际建模、评估和明确跳过了多少，绝不会用后者反向美化前者。费用修正等无法从公开状态安全重放的 HDT 动作仍保留为合法候选，但标为未建模并返回诚实 `partial`，不会让整帧报错。玩家选择只作为 observed-choice 指标。只有 Rust 明确 `exact=true`、独立生成集合与 HDT 全集相等、HDT 全集均已评估、根覆盖与搜索完整且组合最优性已证明的帧，才计入“求解器范围内可复核反事实证据”；报告仍固定禁止线上策略、RL 自动晋升和完整炉石全局最优声明，并且不落 game/state/entity/request ID、时间或绝对路径。

社区包会安装一个手动更新入口。实时来源会只读快照本机脱敏行为、终局和完整决策帧；历史来源优先复用已经 SHA-256 绑定的 `behavior-v1.jsonl`、`advisor-decision-frame-v1.jsonl`、模仿语料与 manifest。脚本分别训练本方 decision ranker 和对手 behavior prior，再执行联合评估、两个 Rust 加载门禁和成对事务安装；任一步未通过都只返回中文的 `no_data`/`not_ready` 摘要并保留两份旧模型。旧模型会先按内容哈希归档，第二份替换或联合清单写入失败时会校验回滚，避免留下半更新状态：

```powershell
& "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools\Update-AdvisorBehaviorPrior.ps1"

# 使用已经完成联审的历史回放导入目录
& "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools\Update-AdvisorBehaviorPrior.ps1" `
  -HistoricalSourceDirectory "D:\path\to\hdt-replay-import"
```

HSReplay 等官网的单卡、牌组和职业排名可以作为搜索先验，暴雪 Standard/Arena 官方卡池可以作为版本边界和规则覆盖清单；它们都不包含完整效果执行、合法动作或最优策略标签，不能代替规则引擎、独立评测和强化学习训练。

## 常用路径

- 插件运行数据：`%AppData%\HearthstoneDeckTracker\MetaCompanion`
- HDT 插件目录：`%AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion`
- 源码目录：`MetaCompanion/`
- 测试目录：`MetaCompanionTests/`
- 工具脚本：`tools/`
- 可复制脚本输出：`dist/`
- 本机求解器：`solver/`
- 本地严格轨迹与双方行为语料：`%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker`
- 竞技场本地先验：`%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorData\Arena\latest`
- 官方 Standard/Arena 卡池：`%AppData%\HearthstoneDeckTracker\MetaCompanion\AdvisorData\OfficialCardPools\latest`

目录说明见 [docs/PROJECT-STRUCTURE.md](docs/PROJECT-STRUCTURE.md)。

## 构建与测试

```powershell
.\tools\Build-MetaCompanion.ps1
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-Tests.ps1
python -m unittest discover -s .\solver\tests -v
python .\solver\launch_solver.py evaluate-turnpair --fixtures .\solver\fixtures\oracle-turnpair-v1.json
python .\solver\launch_solver.py evaluate-hdt-rules --fixtures .\solver\fixtures\oracle-hdt-cardrules-v1.json
python .\solver\launch_solver.py audit-decision-solver-coverage --decision-frames .\artifacts\hdt-replay-import\advisor-decision-frame-v1.jsonl --behavior .\artifacts\hdt-replay-import\behavior-v1.jsonl --binary .\solver-rust\target\release\metacompanion-solver.exe --output .\artifacts\hdt-replay-import\decision-solver-coverage.json
python .\solver\launch_solver.py train-behavior-prior --input .\solver\fixtures\behavior-prior-readiness-v1.jsonl --manifest .\solver\fixtures\behavior-prior-readiness-v1.manifest.json --policy .\solver\fixtures\behavior-prior-readiness-policy-v1.json --output .\artifacts\behavior-prior-fixture.json
cargo test --manifest-path .\solver-rust\Cargo.toml --locked --all-targets
```

`Build-MetaCompanion.ps1` 会自动查找最新 HDT 安装目录，并确保 `packages\Microsoft.Net.Compilers.4.2.0\tools\csc.exe` 可用。首次运行如果本地没有 Roslyn，会优先从用户 NuGet 缓存复制，仍没有时再下载 NuGet 包。

`Run-Tests.ps1` 会把 HDT 的 AppData 重定向到临时目录，并校验真实 `%AppData%\HearthstoneDeckTracker\config.xml` 在测试期间没有变化。脚本默认会确认测试程序集存在且不早于源码；如果刚改过代码或测试，先重新运行 `Build-MetaCompanion.ps1`，仅排查旧程序集时才使用 `-SkipFreshnessCheck`。

带 Rust worker 的部署必须先由统一发布门禁生成并验证，不能直接从源码目录复制候选 exe：

```powershell
cargo build --manifest-path .\solver-rust\Cargo.toml --locked --release
.\tools\Invoke-ReleaseGate.ps1 `
  -RustSolverBinaryPath .\solver-rust\target\release\metacompanion-solver.exe
```

只有 `artifacts\release-gate\<timestamp>\release-gate.md` 为 `PASS` 后，才从同一次门禁产生的 `package-root` 运行安装器：

```powershell
.\artifacts\release-gate\<timestamp>\package-root\Install-MetaCompanion.ps1
```

该安装器会安装同包的 C# DLL 与 Rust worker，并把 Python 离线工具隔离到 `AdvisorOfflineTools`；`AdvisorWorker` 中的旧 Python 启动入口和代码会被删除。需要等待 HDT 退出时，使用同一 `package-root` 中的等待式安装器；不要改用仓库 `tools\` 或 `dist\` 下的安装器替换已验证产物。

```powershell
.\artifacts\release-gate\<timestamp>\package-root\Wait-AndInstall-MetaCompanion.ps1
```

Rust 实时后端不要求 Python；只有显式运行离线训练、评测或审计工具时才需要 Python 3.10 或更新版本。插件会用每进程随机 token 启动隐藏的回环 worker。求解和训练数据不上传；关闭“保存脱敏训练记录”后，`/v1/observe` 和双方行为语料均不落盘。该开关开启而“实时策略建议”关闭时，worker 仍运行并接收 `/v1/observe` 与 `/v1/behavior`，但插件不调用 `/v1/solve`、不显示建议。落盘数据会移除账号字段、登录凭据样式和隐藏对手手牌身份；本功能不读取 Chrome 或 Edge 的密码、Cookie、登录态。

## 开发者数据刷新

社区发布包不附带需要网站登录态的 HSReplay 自动刷新脚本；只随包提供经过审计的本机 Arena 导出器、暴雪公开卡池同步器和上述行为先验更新器。其余入口仅用于本地开发、维护数据快照或高级用户手动同步：

```powershell
.\tools\Update-MetaCompanionData.ps1 -PersonalRecommendations
```

Premium 数据需要把自己的 HSReplay 登录 Cookie 放到：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt
```

不要提交 Cookie、Premium 缓存或本地对局历史。

Standard 与 Arena 官方卡池通过暴雪公开接口同步，不读取 Chrome、Edge、Cookie、登录态或密码：

```powershell
.\tools\Sync-BlizzardCardPools.ps1
```

接口限制、CardDefs 映射、版本化目录、哈希和原子发布契约见 [docs/OFFICIAL-CARD-POOLS.md](docs/OFFICIAL-CARD-POOLS.md)。

竞技场先验可以直接从 HDT 的本地 ArenaSmith 草稿缓存生成；它只代表本机当前补丁内见过的数据，不冒充官网完整卡池或全服排名：

```powershell
.\tools\Sync-HdtArenaAdvisorData.ps1
```

数据字段、补丁过滤、隐私与原子发布规则见 [docs/ADVISOR-DATA.md](docs/ADVISOR-DATA.md)。

## 社区发布前检查

发布前先跑自动门禁，脚本会完成 Release AnyCPU 构建、测试、行为先验三分割训练、行为动作与合法候选集合的固定负向门禁、官方卡池 Python/Rust 互操作及篡改负控、敏感信息扫描和社区包内容审计。Rust 发布候选必须显式提供 `-RustSolverBinaryPath`；固定 fixture 下限为 `combat-v1` 7 条、`full` 40 条、`visible-response-v1` 3 条，当前 `full` 套件实际为 51 条（7 条 turn-pair exact、43 条 HDT exact、1 条 scoped lethal），且官方卡池 5 项门禁必须全部通过，任何一组减少或失败都不能部署：

```powershell
.\tools\Invoke-ReleaseGate.ps1 `
  -RustSolverBinaryPath .\solver-rust\target\release\metacompanion-solver.exe
```

安装门禁通过的 `package-root` 后，使用只读后台验收脚本核对进程、监听、认证、产物哈希、当前会话日志和可见中文建议面板：

```powershell
.\tools\Invoke-HdtAdvisorRuntimeSmoke.ps1 `
  -ExpectedPluginDll .\artifacts\release-gate\<timestamp>\package-root\MetaCompanion.dll `
  -ExpectedRustBinary .\artifacts\release-gate\<timestamp>\package-root\solver\metacompanion-solver.exe
```

脚本使用后台 UI Automation，不移动真实鼠标；若建议面板当时没有实际出现，UI 项会记为 `not_exercised`，不能算作通过。旧 `Invoke-HdtClientSmoke.ps1` 会调用源码树中的安装器，因此本轮 Rust 灰度不要运行它，以免覆盖刚通过门禁的 worker；其余人工客户端检查按 [发布清单](docs/RELEASE-CHECKLIST.md) 执行。

完整清单见 [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md)。

## 来源与许可

Meta Companion 是独立插件项目，但保留原开源项目和参考实现的许可与致谢。详见 [NOTICE.md](NOTICE.md)。

本仓库保留 MIT License。使用插件需自行承担游戏规则和第三方服务条款风险。

