# 实现状态

更新时间：2026-08-01。

## 已完成

- 项目主体改为 `Meta Companion`。
- DLL 改为 `MetaCompanion.dll`。
- 命名空间改为 `MetaCompanion`。
- 源码目录改为 `MetaCompanion/`。
- 测试目录改为 `MetaCompanionTests/`。
- 运行数据目录改为 `%AppData%\HearthstoneDeckTracker\MetaCompanion`。
- HDT 插件目录改为 `%AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion`。
- 安装脚本会删除旧 `Plugins\DeckPredictor`、旧 `%AppData%\HearthstoneDeckTracker\DeckPredictor`，并移除旧 `plugins.xml` 条目，避免双插件加载和旧目录残留。
- 后期未见牌统一使用原始牌计数器扣减，避免已见牌继续残留或复制数超过构筑上限。
- 后期面板和赛后浮窗位置会保存到配置，并提供重置按钮。
- 赛后浮窗默认常驻，展示最近一局、近期对手分布和推荐形态。
- HDT 历史导出、本地环境统计和插件 match history 都追加了 replay 字段。
- 标准和竞技场的我方可操作回合会采集版本化 `AdvisorGameState`，状态指纹变化时立即取消旧搜索。
- 插件会启动带随机 token 的隐藏 localhost Rust worker；生产配置固定为 Rust-only，旧 Python 实时后端已删除。Python 只隔离保留为显式离线训练、评测和审计工具；Rust worker 不可用时只降级实战建议，不影响原有 HDT/预测功能。
- 建议面板先显示临时 Top 3 完整行动线，再在约 10 秒总预算内更新最终排序；普通路线展示未校准的战术局面值及对手最坏可见回应，模型内存在反杀时明确警告，模型内斩杀展示 proof scope，并附风险和近似规则覆盖。
- 首批默认使用 6000 节点/深度 8，最终使用 20000 节点/深度 12 和总预算剩余时间；完整组合证明或直接斩杀证明会跳过重复终搜。Rust 的 exact、scoped lethal 与 visible fallback 共享请求级节点和端到端单调时钟截止，截断 proof 失败关闭，visible 仍保留逐首步基线并报告时间/节点/深度限制。
- 客户端超时会只按精确旧 request ID 发送协作取消；Rust 处理取消先于注册的竞态、限制一个 CPU 求解并让 health/cancel 保持并发。`allow_approximate_effects=false` 会禁止 visible 近似结果。
- 地点会正确占用棋盘格，战斗死亡清理不会误删地点；地点效果仍未进入求解器模拟，因此严格轨迹继续安全省略回放，但本方精确 Power 输入会独立记录为 `location_activate` 行为语料。
- 所有普通界面都通过中文消息边界：worker 自由文本、异常、HTTP、路径和规则码不会直接显示；重复限制最多聚合为两条中文提示，完整诊断仅写本地日志，顾问日志和复制诊断中的凭据值会脱敏。
- 玩家出牌、攻击、英雄技能和胜负结果可写入本机 `training-v2.jsonl`（`advisor-training-log-v2`）脱敏记录；设置中可以完全关闭训练记录。旧 `training.jsonl` 原地保留且默认启动不再追加。同一局使用固定私有 alias，落盘前再匿名化，并移除精确墙钟时间、身份/凭据字段和隐藏对手牌身份。
- `trajectory-readiness-v1` 会给初搜/终搜/单阶段标记稳定 decision 与 stage，并提供只读审计：canonical 去重、solve/result join、动作序号、按本方回合分段的 exact pre/post 链、生产模拟器独立回放、逐局 deterministic split、隐私扫描和生产样本阈值。结构契约与训练就绪分开报告；终局只有显式声明紧邻时才要求等于最后本方 post-state。
- HDT GameEvents 公开回调本身仍缺少完整 target/choice 证明。插件把动作前快照与后续两个相同稳定快照关联成自包含的 `partial_hdt_transition_candidate_v1`，重复 CardID 只标 ambiguous，不取第一个；前后状态由 worker 再次脱敏并重算 canonical SHA-256。本方 PowerLog 会额外联结 Options、SendOption、根动作和当前 `DebugPrintEntityChoices/DebugPrintEntitiesChosen`，但生产模拟器尚不能回放选择分支，因此严格候选仍为 `post_state_candidate_unverified`、`training_eligible=false`，不会因行为身份更精确而冒充可回放 RL 轨迹。
- `advisor-behavior-v1` 的本方 Power 动作已保存 `sub_option`、`board_position`、`choice_status` 与完整 `choices` 提供/最终选择集。只有 choice ID/Player、具体根动作来源、连续且唯一的实体索引、`EntitiesCount` 数量和“所选属于提供集”全部满足时才使用 `exact_hdt_power_choice_v1`；旧 `SendChoices`、缺提供集、错误来源、数量/索引截断和越界选择均降级且不具备行为训练资格。无根动作的换牌/选择不会串绑或污染后续出牌；Player 只保留内存指纹，旧行为行缺少可选字段时仍保持原哈希。
- `board_position` 已从行为旁证升级为正式动作维度：Python/Rust 对随从和地点枚举全部 1-based 合法落位，动作 ID 追加 `:position=N`，回放按实际顺序插入；C# 严格核验该身份并显示中文落位提示。非棋盘牌伪造位置、越界位置和缺位置的旧随从行为都不会被当成精确新候选；行为先验的模板也能区分同卡同目标的不同落位，但这仍只是行为排序数据，不是最优或 RL 标签。
- 审计报告额外统计 candidate 边界/状态/哈希/序号质量，并只为 exact 且独立回放成功的动作生成逐动作 `verified_transitions` allowlist。solve 质量按全部有效记录使用互斥桶报告 ok/partial/cancelled/unsupported/non-ok（仅 error 与未知状态），另以 `unsuccessful_solve_rate` 报告所有非 ok 的总体占比；`unsupported_solve_rate` 只统计显式 unsupported。问题样例仍限 100 条，但完整 `reason_counts` 不截断。`promote-trajectories` 会写入新的脱敏语料和 `trajectory-verification-manifest-v1`，不修改生产 `training-v2.jsonl`；训练器对同一不可变 byte snapshot 重新审计并验证语料、模拟器哈希与 allowlist，消除了活文件二次读取的 TOCTOU。
- 新增 `behavior-learning-readiness-v1` 联审：双方行为和终局日志各只读一次、分别保存内容寻址快照，报告绑定双输入/策略哈希并检查双方覆盖、动作与证据质量、序号/时间顺序、唯一终局关联和 game-level split，同时汇总选择状态、落位、选择项、提供实体和最终选择实体。Release Gate 合成夹具固定带一条完整选择并对选择覆盖做阻断校验。达到生产门槛后，`promote-behavior-imitation` 只把合格且已关联的行为另写为去时间戳、内容寻址的模仿语料与 manifest；它明确禁止 RL/最优标签用途，原始与输出记录都不会被自动晋升为 RL。
- HDT 历史 Power 解析已对陈旧 descriptor 失败关闭：`TAG_CHANGE` 和根 `BLOCK_START` 不再回滚已确认的 zone/controller/position，地标使用不会被旧 `zone=HAND` 再次误标成出牌。每条公开 pre/post state 都通过生产 `GameState` 合同，超手牌/棋盘容量的边界按稳定原因跳过并重新连续编号；历史行为联审额外阻断出牌来源仍留手、实际攻击缺少至少一次准备证据和结束回合未换人。实际 ATTACK 证据只标记该来源，不补猜其他合法动作，产物继续固定 `solver_evaluation_ready=false`。
- 新增 `behavior-imitation-prior-v2` 离线行为排序基线：训练器只读取 game-level `train` 分割，分别学习动作类型和卡牌/目标模板的 global/actor/mode/patch/hero-pair/public-state 分层计数；胜负不参与训练，validation/test 只评估。来源 manifest 必须明确通过新版回放转移合同，v1 旧模型由 Python/Rust 拒绝。它只能排序外部已确认合法的候选，未见模式/补丁返回均匀排序，并永久固定 live/RL/optimality/candidate-generation 资格为 false。默认门槛为 `30/10/10` 局与 `250/50/50` 条 train/validation/test 行为；Release Gate 用 hash-bound 三分割合成 fixture 真正执行训练、严格解析产物并做篡改负控，但不把 fixture PASS 当成真实数据 READY。
- 新增 `behavior-candidate-alignment-report-v1` 候选完整性联审：先重新验证模仿 dataset/manifest 字节绑定，再按双方、动作、模式和 split 汇总观察动作的 exact/target-mismatch/not-generated；报告不落任何 game/state/entity ID。只有本方动作精确命中、至少两个候选，并且可行动卡、英雄技能、棋盘、地点、落位和选择规则全部可证明时才计入排序训练资格；规则包只允许在 CardDefs build 与局面 patch 完全一致时应用。Release Gate 固定要求旧 behavior-prior 合成 fixture 被此门禁拒绝，证明“观察动作能复现”不等于“候选集合完整”。
- Rust 在线 worker 已接入严格分流的双模型二次解析与热加载：本方动作只使用 `decision-ranker-v1`，对手动作只使用 `behavior-prior-v1`，本方缺 ranker 时绝不回退到旧行为先验。`Update-AdvisorBehaviorPrior.ps1` 支持实时双日志快照晋级，也支持优先复用历史导入目录的 SHA 绑定行为、决策帧、模仿语料与 manifest；它分别训练两份候选，要求联合评估 READY 和两个 Rust loader 通过，再 staging、按内容哈希归档并成对替换模型及联合清单，故障注入自检证明失败时恢复三份旧文件。模型只重排已生成的合法动作，不改变候选集合、战术评分或完整穷举答案；缺失、损坏、跨补丁或运行时评分失败时对应一侧恢复基础顺序，且继续固定 live/RL/optimality/candidate-generation 资格为 false。
- 新增 `advisor-decision-ranker-v1`：直接读取严格恢复的 HDT `Options + SendOption + Power` 决策帧，用 game-level train/validation/test 隔离的稀疏 listwise logistic 基线学习本方候选排序。训练只使用公开前局面和 HDT 已给候选，validation 选择轮次与温度，test 只做最终 Top-1/Top-3/MRR/log-loss 报告；产物不保存 game/state/entity ID，并固定禁止候选生成、RL 和最优性声明。`observed-policy-evaluation-v1` 再把本方完整候选排序与对手公开行为模型分开联审；对手没有本机 Options，因此绝不伪造其候选集。
- 新增 `advisor-decision-solver-evaluation-v1`：按内容哈希确定性抽样真实 HDT 决策帧，启动同一 Release Rust worker，并把 HDT 完整合法候选作为首步组合输入。报告分别聚合 Rust 独立生成的召回率/精确率/全集匹配，以及 HDT supplied 组合的合同有效数、评估数、跳过数和覆盖率；两套指标绝不混算。费用修正等无法从公开状态安全重放的合法动作保留在 HDT 集合中并明确 omitted，不猜费用、不让整帧失败。报告另含 exact/partial/unsupported、false-exact、玩家选择 Top-1/Top-3 一致率、可验证多备选及公开 CardID/动作缺口，不写 game/state/entity/request ID、时间、URL、token 或绝对路径。只有 exact、独立集与 HDT 全集一致、HDT 全集均已评估、根覆盖/搜索完整且组合最优性已证明的帧才计入求解器范围内反事实证据；报告不生成语料，并固定 `live_policy_eligible=false`、`rl_training_eligible=false`、`global_optimality_verified=false`。离线 worker token 改由进程环境传入，不再出现在命令行参数。
- Rust 的 `partial` 响应可附带独立的 `hdt_complete_candidate_behavior_reference_v1`：本方 decision ranker 只排序本次 HDT 完整合法首步，所以尚未建模的地点激活、武器和复杂法术也可列为“你过去的打法参考”。C# 会绑定原始候选帧并严格复核模型 SHA、计数、规范动作、排序与全部禁止自动操作/候选生成/战术覆盖/live/RL/optimality 字段；任一篡改整块隐藏。该区域与战术建议分开，以中文固定声明“不代表最优、不是胜率、不是强化学习结论、不会自动出牌”；对手模型仍只用于公开回应搜索顺序。
- ArenaSmith 本地草稿缓存可转换为当前补丁内的匿名 card/hero/package 先验，并以版本化目录原子提升。
- 暴雪公开 Card Library API 的 Standard/Arena 可收集卡池可分别同步、映射本机 CardDefs、记录 ETag/SHA-256，并在双池验证完成后原子提升。Python 与 Rust worker 都严格复核发布标记、双池大小/哈希/计数/身份、分页新鲜度及当前 CardDefs 版本绑定；health 与每次 solve 只报告公开成员关系，固定不宣称规则覆盖、衍生物覆盖或动作合法性。发布二进制还要通过 Python/Rust 互操作与篡改负控门禁。
- `hdt-visible-point-effects-v1` 已接入首批真实 HDT 文本：当前为 47 条人工审核的可见效果规则，共 205 个显式 CardID。新增私运的铁铲、奥术绊索与直面托维尔；生产模拟器会维护墓地、已知剩余牌库与生成来源，逐点展开奥术绊索的随机伤害，并重放公开可见的 1 费历史。环形山的尼利现在按结构化引擎效果保留组件价值，裸硬币跳费不会压过保留，而同回合接 1 费牌时由真实翻倍收益自然胜出。只有 `card_id + normalized EnglishText SHA256 + card_type + required intrinsic mechanics + declared context guards` 完全一致才附加结构化效果；搜索中手牌从 4 张降到 3 张时会立刻把英雄技能重算为 0 费。Casts When Drawn 自动施放和不完整历史仍明确标为近似，不会伪装成 exact。
- 新增 `hdt-intrinsic-keywords-v1`：只在整段文本全部是已实现关键词且当前 HDT 标志/标签逐项吻合时，消除 `card_text_not_parsed` 的假未覆盖；Rush、Elusive 及多关键词白板不再拖累整帧，任何复合文本或证据缺口仍失败关闭。Rust/Python 均按来源类型执行扰魔目标限制，法术和英雄技能不可指定、战吼可指定，随机与群体效果保持原规则。
- `card-generation-pools-v1` 已登记官方 Standard/Arena 1,811 张去重卡中的全部 417 张随机/发现相关卡。每条规则保存 CardID、类型、规范化英文文本 SHA-256、触发器、选择方式、目的区域、来源域以及费用/类型/职业/派系/种族/系列/稀有度/关键词约束；32 张审核规则会在 Rust 中生成、发现或召唤真实候选并进入 Chance 转移，385 张则带明确 blocker 保持 unsupported。多费用、多稀有度和多种族“各一张”已拆成按文本顺序的多个池效果，联合分支用确定性分层限制在约 96 个结果；牌库、手牌、墓地、历史和专属衍生池缺少对应运行时来源时绝不回退到当前环境。Discover 仍使用候选质量启发式选择，并非完整 Chance→Choice 后续搜索，所以不声称全局最优。
- HDT 的 `Entity.IsPlayableCard` 对真实英雄技能恒为 false；适配器按 `HAS_ACTIVATE_POWER`、`EXHAUSTED`、`HERO_POWER_DISABLED` 保存技能是否未使用/禁用，合法动作枚举器再独立检查实际费用和当前法力。这样连续费用光环在同一搜索路线中变化后不会丢失后续技能动作；激活证据缺失时保持不可用。
- 当前可操作牌依赖上述未知机制时，求解器 fail-closed 并 abstain，不再把它当白板排序；唯一例外是完全不使用未知动作的 clean direct lethal，此时只输出已证明斩杀线。
- 结构化 SPELL 伤害会叠加当前可见法术伤害；随从战吼与英雄技能伤害不会误加法强。
- `visible-combat-v2` 已接入剩余攻击次数、风怒、武器耐久、休眠、突袭、冲锋、重生、免疫等公开战斗状态，并用确定性有界规划器优先寻找受支持规则内的斩杀。
- `oracle-turn-v1` 使用独立穷举 oracle 检查已证明斩杀 Top 1/Top 3、错误斩杀、行动线合法性和延迟；门槛失败时命令返回非零，且不会把同分无回退冒充质量提升。
- `counterplay-turnpair-v1` 会对我方完整回合候选搜索对手下一完整回合的最坏可见回应；先冻结全部合法首步并按预算稳定播种，覆盖统计使用完整根动作集合作分母。多个备选按首步去重，已知反杀线不会在仍有安全线时用于凑数；限时 PUCT 只声明 `best_found`，完整枚举且无深度截断才可声明 `co_optimal`。
- `oracle-turnpair-v1` 不导入生产搜索器、模拟器或估值器，独立检查双方逐动作合法性、Top 1/Top 3、minimax regret、false-safe、斩杀与响应契约；`evaluate-turnpair` 失败返回退出码 3，并作为 Release Gate 阻断项。
- `oracle-hdt-cardrules-v1` 用独立 point-effect oracle 检查原始 HDT 快照、双方逐动作合法性、Top 1/Top 3、minimax regret、false-safe/false-exact、规则 provenance 和 abstain 负控；`evaluate-hdt-rules` 失败返回退出码 3，并作为 Release Gate 阻断项。
- Release Gate 将合成能力门禁和真实数据状态明确拆开：trajectory/behavior audit fixtures 与行为先验三分割 fixture 的任何漂移都会阻断发布；另有非阻断 runtime 项通过 content-addressed 快照报告真实 `training-v2.jsonl`、`behavior-v1.jsonl` 的 `READY/NOT_READY/NO_DATA`。fixture PASS 永远不能替代 runtime READY；没有用户日志只得到 `NO_DATA`，不会阻断插件二进制发布，却会阻断任何真实训练/模型晋升。
- Rust 晋升还必须用 3 帧/9 候选合成 HDT 决策夹具通过 decision-solver coverage honesty gate：来源帧、双方行为和候选二进制 SHA-256 全绑定，Rust 独立生成召回/精确率均为 100%，HDT supplied 评估为 9/9、omitted 为 0，false-exact、协议错误和隐私错误均为 0，3 帧只能获得求解器局部范围证明而不能获得 RL/线上策略/全局最优资格；该报告与 parity、visible-response、卡池门禁必须绑定同一二进制。
- 官网单卡/牌组/职业排名只用于搜索先验，官方 Standard/Arena 卡池只用于版本边界和覆盖清单；两者都不是可执行规则、最优策略标签或训练结果。
- 求解器具备离线 replay/benchmark/frequency/PyTorch 骨架及有界 self-play 轨迹生成，但当前不是完整炉石规则引擎或已完成的强化学习策略。

## 验证

- 构建命令：`MetaCompanion.sln` Release AnyCPU。
- 测试命令：`tools\Run-Tests.ps1`。
- 当前测试覆盖：预测、后期剩余牌、原始牌多来源扣减、配置保存、赛后数据读取、推荐读取。
- Advisor 测试覆盖：snake_case 协议、solve 顶层 trajectory metadata、差异化初搜/终搜预算、完整证明跳过重算、精确请求取消、私有 game alias、状态指纹、取消/过期拒绝、worker 降级、动态启停、observe 资格门禁与隐藏信息双重脱敏。
- Python 测试命令：`python -m unittest discover -s solver\tests -v`。
- 对手回应门禁：`python solver\launch_solver.py evaluate-turnpair --fixtures solver\fixtures\oracle-turnpair-v1.json`；报告必须通过全部阈值。
- HDT 卡牌规则门禁：`python solver\launch_solver.py evaluate-hdt-rules --fixtures solver\fixtures\oracle-hdt-cardrules-v1.json`；报告必须通过全部阈值。
- 当前 DLL SHA256 与字节大小：以 `artifacts\release-gate\<timestamp>\release-gate.md` 的 `Build Artifact` 为准，避免文档内固定哈希过期。

## 手动验收

- 重启 HDT，插件列表应显示 `Meta Companion`，不应再同时出现旧插件。
- 标准天梯开局后，前期预测仍嵌入 HDT 原生对手牌组区域。
- 长局进入后期面板后，已见原始牌不应再出现在未见牌列表里。
- 非传奇最多显示 2 张，传奇最多显示 1 张。
- 拖动后期面板和赛后浮窗后，刷新、回合切换、回菜单都不应回到左上角。
- 对局结束后赛后浮窗应保持可见，直到手动关闭或下一局开始。
- 标准/竞技场进入我方可操作回合后，建议面板应先显示临时路线，再更新为最终路线；对手回合不显示我方行动建议。
- 最终普通路线应显示“对手最坏可见回应”；存在模型内反杀时必须明确标红，近似或未知抽牌局面不能显示为“已验证安全”。
- 任意出牌或攻击后旧路线应立即清除，不能在新 `state_id` 下复用旧结果。
- worker 健康信息应显示 `capabilities.hdt_visible_point_effects_v1 = true`，并在 `structured_card_rules` 中显示 `hdt-visible-point-effects-v1`、47 条规则、205 个显式 CardID、5 条上下文门禁规则、5 条内在机制证据门禁规则和严格匹配契约。
- 原始 HDT Fireball/Fireblast/Steady Shot 快照应报告匹配的 rule provenance；即使 HDT 将英雄技能的 `IsPlayableCard` 报为 false，公开激活标签与费用/法力允许时仍应枚举。SPELL 伤害应正确计入法强，Steady Shot 只能命中敌方英雄且不叠加法强；修改 EnglishText/card_type、激活目标修改标签或缺少玩家标签证据后必须 unsupported/abstain。
- 未覆盖机制必须显示 abstain/风险提示，不能作为白板动作排序；worker 或 Python 不可用时 HDT 仍正常运行。
- 建议面板只能展示信息，不能产生任何游戏点击、键盘输入或鼠标控制。
- 有 HSReplay 上传链接时可打开网页；没有上传链接时可打开本地 `.hdtreplay`。



