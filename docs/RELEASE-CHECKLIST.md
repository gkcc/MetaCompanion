# 社区发布检查清单

这份清单用于 NGA/社区首发前的发布门禁。目标是确认插件稳定、不泄露个人数据、不在实际对局中打扰玩家。

## 自动门禁

1. 运行发布门禁：

   ```powershell
   .\tools\Invoke-ReleaseGate.ps1 `
     -RustSolverBinaryPath .\solver-rust\target\release\metacompanion-solver.exe
   ```

   Rust 候选必须显式通过 `-RustSolverBinaryPath` 交给门禁，不能先手工复制到插件目录，也不能与 `-SkipTests` 或外部 `-PackagePath` 同用。

2. 确认报告结果为 `PASS`：

   ```text
   artifacts\release-gate\<timestamp>\release-gate.md
   ```

3. 必须满足：
   - Release AnyCPU 构建通过。
   - `tools\Run-Tests.ps1` 全部通过。
   - 报告中 `Test result` 记录测试摘要，未跳过测试时必须为 `passed=... failed=0`。
   - `oracle-turnpair-v1` 使用正式求解器通过，报告中 `Counterplay turn-pair gate` 为 `PASS`，Top 1/Top 3、双方行动合法率、minimax regret、false-safe 和响应契约均满足固定阈值。
   - `oracle-hdt-cardrules-v1` 使用正式服务与独立 point-effect oracle 通过，报告中 `HDT visible point-effect gate` 为 `PASS`；Top 1/Top 3、双方行动合法率、minimax regret、false-safe/false-exact、规则 provenance、abstain 负控和 P95 延迟均满足固定阈值。
   - `trajectory-readiness-v1` 使用正式 `audit-trajectories --source-kind synthetic_fixture` 和版本化 fixture policy 通过；发布报告只能写作 `Trajectory auditor fixture self-test: PASS`。它验证 schema、输入/策略 SHA256、canonical decisions、join/exact/replay、solve 状态分布、candidate 证据、verified allowlist、动作链/切分/隐私和完整 reason 聚合，绝不能写成或暗示 `runtime training ready`。
   - `advisor-behavior-v1` 的 C#/Rust/Python 合同测试通过：双方四类基础动作、本方精确 `location_activate`、地点来源区域/类型校验、公开 pre/post 投影、缺失 post-state 时降级、行动方已知但身份未知的保留、`rl_training_eligible=false`、隐藏对手手牌移除、逐局连续 sequence、worker 内容寻址、`(game_id, sequence)` 幂等和 outbox 原样重试均不可跳过。本方 Power 动作还必须覆盖 `sub_option`、`board_position`、完整选择提供集/最终选择集、choice ID/Player 一致、连续唯一索引、`EntitiesCount` 数量核对、`exact_hdt_power_choice_v1` 晋级、缺提供集/错误来源/数量或索引截断/越界选择降级、无根换牌选择不串绑或污染后续出牌，以及旧行为行缺可选字段时原哈希不变。不得把当前缺少输入证据的对手地点激活伪造成已观察行为。
   - `behavior-learning-readiness-v1` 合成 fixture 使用正式联审器通过：必须同时绑定行为/终局输入 SHA-256、策略 SHA-256，覆盖 `local` 与 `opponent`，检查序号/时间顺序、隐私、终局关联、拆分与行为合格率；报告必须包含历史回放的出牌来源离手、攻击准备证据和结束回合换人指标及三个阻断合同。夹具还必须包含至少一个完整 selected choice、两个提供实体和一个最终选择实体，PowerShell summary 缺少对应审计指标时直接失败。报告只能称 `imitation-ready`，并强制 `rl_training_ready=false`。
   - `behavior-imitation-prior-v2` 三分割合成 fixture 必须使用正式 `train-behavior-prior` 通过：dataset 与 manifest SHA-256 相互绑定，来源审计明确通过回放转移合同，train/validation/test 各 2 局且验证/测试样本不得进入模型计数；PowerShell summary 必须重新核对源文件哈希、质量检查、模型总数和 `live_policy_eligible=false`、`rl_training_eligible=false`、`optimality_verified=false`、`candidate_generation_allowed=false`。v1 旧产物必须被 Python 与 Rust 拒绝。fixture 只证明训练器合同，不证明真实语料就绪或最优策略。
   - `behavior-candidate-alignment-report-v1` 固定负例必须用同一 behavior-prior dataset/manifest、版本化 candidate policy 和正式结构化规则包运行；命令必须以退出码 3 拒绝。旧夹具的 3 条随从出牌缺少正式 `board_position`，因此只允许 3/6 精确命中、另 3/6 为 `not_generated`，且 `candidate_set_eligible=0`；不得为兼容旧日志而偷偷补成最右落位。报告必须重新绑定 dataset/manifest/rules SHA-256，确认 fixture patch 与 CardDefs build 不同所以 0 条规则跨 build 应用，并保持候选生成、线上策略、RL 和最优性标志全部为 false。该报告验证规则重建覆盖，不能否定或覆盖已通过独立合同的 HDT 原生 Options 候选。
   - 同一行为先验产物必须继续通过生产 Rust `behavior-prior-check` 和 Rust/Python 概率互操作测试；篡改权限边界、质量检查、split 计数或模型总数必须拒绝。先验只能重排已生成的合法动作，完整穷举启用前后结果必须一致；原子替换后可热加载，损坏时停用并在恢复合法文件后重新加载。
   - `observed-policy-synthetic-fixture-v1` 必须真正生成 6 条双方行为、3 条本方完整决策帧和 train/validation/test 各 1 局的本方/对手独立评估样本；分别运行 `train-behavior-prior`、`train-decision-ranker`、`evaluate-observed-policy`，联合报告必须 SHA 绑定四份来源和两份模型并为 `READY`。本方 ranker 还必须通过 Rust `decision-ranker-check`，固定 `local_actions_only=true`；本方不得回退使用 behavior prior。两侧都必须保持候选生成、线上策略、RL 和最优性为 false。
   - `Update-AdvisorBehaviorPrior.ps1 -SelfTest` 必须覆盖双模型训练、联合评估、两个 Rust 加载门禁、首次成对更新、内容不变，以及第一份模型替换后故障注入的双模型/联合清单哈希回滚。生产安装清单必须绑定 behavior prior SHA、decision ranker SHA 和 observed-policy evaluation SHA；不得留下半更新状态。
   - HDT 历史回放导入测试必须覆盖最新 build 与模式隔离、双方动作、终局关联、公开轨迹去重、姓名/账号变化不影响匿名 game ID、隐藏对手手牌、原始文件名和真实时间不落盘，以及 `hdt_replay_power` 的 Rust/Python 双向互操作和证据/来源负控。实体更新必须同时覆盖带与不带 `Entity=` 的 `FULL_ENTITY/SHOW_ENTITY/CHANGE_ENTITY - Updating` 格式，并证明后续 implicit tags 绑定到被更新实体。历史本方随从/地点落位还必须用 Options、SendOption 与后续 PLAY 根动作严格绑定，并覆盖错来源、错目标、越界、新帧清空和对手不继承位置的负控。必须另有陈旧 `TAG_CHANGE` 与陈旧根 `BLOCK_START` 回归：已下场实体不能被放回手牌，后续地标使用必须保持 `location_activate`；输出状态逐条通过手牌/棋盘容量合同，出牌来源 100% 离手、实际攻击来源 100% 有至少一次准备证据、结束回合 100% 换人。社区包必须包含解析器源码，但必须阻止 `.hdtreplay`、`training-v2-results.jsonl` 和本地导入 manifest 进入发布包。
   - `advisor-decision-frame-v1` 必须通过独立 HDT 回放单元门禁：Options/target/subOption 的 ID 连续性、`error=NONE` 合法域、结束回合、全部棋盘落位、SendOption 与根动作精确绑定、选择分支/交易/重复语义/截断边界失败关闭、每次选择恰好结算一次、决策哈希和行为 ID/pre/post 联结均需覆盖。所有合格帧必须固定 `imitation_training_eligible=true`、`optimality_verified=false`、`rl_training_eligible=false`；社区包必须包含审计器源码，并阻止本地 `advisor-decision-frame-v1.jsonl` 与 `decision-frame-readiness.json` 进入发布包。
   - `advisor-decision-solver-evaluation-v1` 单元负控必须证明：exact 但独立生成漏掉任一 HDT 根动作、或 HDT 合法动作未被实际评估，都会计为 false-exact 并失败；诚实 partial/unsupported 仍可作为覆盖诊断但不会获得局部反事实资格；报告不含 game/state/entity/request ID、时间、URL、token 或绝对路径。Rust 晋升时还必须用同一 observed-policy 合成夹具执行 3 帧/9 候选的真实 worker 审计：Rust 独立生成召回和精确率均为 100%，HDT supplied 请求/响应合同有效 3/3、实际评估 9/9、omitted 0、全集匹配 3、协议错误 0、false-exact 0、求解器范围内 verified 3；玩家选择、胜负、线上策略、RL 与全局最优资格仍全部为 false。本地 `decision-solver-coverage*.json` 必须被社区包阻止。
   - behavior ACK 耐久性测试通过：Rust `write_all + flush + sync_data`、Python 完整 write + `flush + os.fsync` 均发生在 `logged=true` 前；同步失败不确认并使索引失效，销毁 worker 后的新实例在磁盘重建同步继续失败时仍不得返回 duplicate，恢复后只能确认原行且 corpus 保持一行。Rust/Python 双向重试必须得到相同 `behavior_id` 与 duplicate 结论。
   - behavior 尾部恢复测试覆盖：完整 JSON 仅缺换行时补换行且不归档；真半截按 SHA-256 生成只读 `.torn-tail.<sha256>.fragment` 后再截断并保留全部完整历史；中间坏行、重复和断序不修复，`behavior_log_healthy=false`。
   - 终局 result 可靠性测试通过：独立 `result-outbox-v1` 先落盘再标记、与 action 共用 FIFO、响应丢失和 worker 重启后内容寻址去重、同局冲突失败关闭、精确 ACK 后删除，以及 250ms 到 30 秒有界退避均不可跳过。Rust 的 `flush + sync_data` 与 Python 的 `flush + os.fsync` 必须发生在成功 ACK 前；同步失败不得确认，重试重扫后不得产生第二条 result。
   - 终局索引恢复测试必须同时覆盖三种情况：完整 JSON 仅缺换行时原位补换行且不归档；真正半截尾行先生成按 SHA-256 命名的只读 `.torn-tail.<sha256>.fragment`，再截断并保留此前全部完整行；中间坏行不得自动修复，`training_log_healthy` 必须为 `false`。Rust/Python 的行为与 `result_id`/`duplicate` 结果必须一致。
   - 独立运行 `audit-runtime-trajectories`：默认只读 `%APPDATA%\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker\training-v2.jsonl` 一次，创建 content-addressed 不可覆盖快照，并在报告绑定 `input_sha256`、`input_bytes`、`policy_sha256`。状态只能是 `READY`、`NOT_READY`、`NO_DATA`；`NO_DATA` 不代表 ready，也不因开发/发布机器没有用户历史而阻断插件发布。任何训练或模型晋升仍必须要求 `READY`。
   - 独立运行 `audit-runtime-behavior-learning`：分别只读 `behavior-v1.jsonl` 与 `training-v2.jsonl` 一次，创建两份 content-addressed 快照并绑定各自 SHA-256/字节数。状态只能为 `READY|NOT_READY|NO_DATA`，非 READY 不阻断二进制发布但禁止 `promote-behavior-imitation`；任何状态都必须保持 `rl_training_ready=false`。
   - `Rust combat parity gate` 为 `PASS`，fixture 不少于 7 条；`Rust full parity gate` 为 `PASS`，fixture 不少于 40 条；`Rust visible-response honesty gate` 为 `PASS`，fixture 不少于 3 条；`Rust official card-pool gate` 必须通过 Python/Rust 互操作和 4 类篡改负控；`Rust decision-solver coverage honesty gate` 必须通过上述 3 帧/9 候选双口径对账。五份报告必须绑定同一个候选二进制 SHA256。
   - 报告中 `Rust solver promoted: True`，同次运行的 `package-root\solver\metacompanion-solver.exe` 与被门禁验证的候选哈希一致。
   - 敏感信息扫描 0 命中。
   - 社区 zip 的 `tools\` 只允许本地匿名导出器 `Sync-HdtArenaAdvisorData.ps1`、公开卡池同步器 `Sync-BlizzardCardPools.ps1` 和失败关闭的 `Update-AdvisorBehaviorPrior.ps1`，不包含其他 `tools\*.ps1`、cookie、Premium 缓存、本地对局历史、`bin/obj`。
   - 社区 zip 包含运行时 `solver/`、`behavior.py`、`behavior_learning.py`、`behavior_candidate_alignment.py`、`behavior_prior.py`、`decision_frame.py`、`decision_ranker.py`、`observed_policy_evaluation.py`、`decision_solver_evaluation.py`、`rust_worker_client.py`、`observed_policy_fixture.py`、`card_rules.py`、`hdt_rule_evaluation.py`、`trajectory.py`、`verification.py`、规则包、oracle fixtures 与合成 fixture/policy；但不包含测试、cache、venv、真实 `training.jsonl`、`training-v2.jsonl`、`behavior-v1.jsonl`、真实决策帧/覆盖报告/模仿语料、本地两份模型或联合安装清单、behavior/result outbox、verified corpus/manifest 或 `AdvisorData`。
   - 社区 zip 与安装后的 `%AppData%\...\MetaCompanion\Tools` 都包含 `Update-AdvisorBehaviorPrior.ps1`；安装态脚本必须从相邻 `AdvisorOfflineTools` 找到 Python 离线工具、从 `AdvisorWorker` 找到 Rust worker，不能依赖源码目录。`AdvisorWorker` 不得包含 `launch_solver.py` 或 `metacompanion_solver`。
   - 报告中记录 DLL 和 zip 的 SHA256 与字节大小。

## 真实客户端烟测

1. 只允许从门禁结果为 `PASS` 的同一次 `package-root` 安装；不要从源码树的 `tools\` 或 `dist\` 目录部署本轮 Rust 候选：

   ```powershell
   .\artifacts\release-gate\<timestamp>\package-root\Install-MetaCompanion.ps1
   ```

2. 启动或重启 HDT 后运行只读后台验收，并把预期 DLL/Rust 路径固定到同一 `package-root`：

   ```powershell
   .\tools\Invoke-HdtAdvisorRuntimeSmoke.ps1 `
     -ExpectedPluginDll .\artifacts\release-gate\<timestamp>\package-root\MetaCompanion.dll `
     -ExpectedRustBinary .\artifacts\release-gate\<timestamp>\package-root\solver\metacompanion-solver.exe
   ```

3. 自动验收必须确认：

   - HDT 恰好一个进程；Rust worker 恰好一个，来自 `AdvisorWorker`，由当前 HDT 启动且处于 `serve` 模式。
   - 没有 MetaCompanion Python worker；实时后端固定为 Rust-only，Python 只允许在隔离目录中显式用于离线训练、评测和审计。
   - 生产 worker 按进程合同只监听 `127.0.0.1`，且不存在任何非回环监听；无令牌访问 `/v1/health` 返回 401。
   - 认证后的 `/v1/health` 同时报告 `training_log_enabled`、`training_log_healthy`、`behavior_log_enabled`、`behavior_log_healthy`；启用采集时四项均为 `true`，行为语料健康状态不得冒充严格轨迹健康状态。
   - `/v1/health.behavior_prior` 在没有真实模型时必须为中文友好的 `status=not_found` 且 `available=false`，基础求解仍可用；有模型时必须为 `ready`，并保持 `search_ordering_only=true`、`candidate_generation_allowed=false`、`live_policy_eligible=false`、`rl_training_eligible=false`、`optimality_verified=false`。单次 solve coverage 只可报告 `applied|available_not_applicable|disabled|runtime_rejected`，不得宣称策略或最优性。
   - `/v1/health.official_card_pools` 必须验证当前 Standard/Arena 双池与本机 CardDefs；可用时报告 run/build/hash/count，不可用时只给稳定 reason 并保持求解服务可用。单次 solve 的 `coverage.official_card_pool` 只统计当前可见已知卡的模式成员关系，固定 `rules_coverage=false`、`generated_entities_coverage=false`、`enforces_action_legality=false`。
   - 已安装 DLL 和 Rust worker 的 SHA256 分别匹配传入的门禁产物。
   - 当前 HDT 会话的 MetaCompanion 日志没有新增错误；警告会使结果降为 `warning`。普通 Warning/Error 即使被故障注入触发，也只能出现中文安全摘要，不能包含英文堆栈、绝对路径、URL、Cookie、Token、密码或 API Key 值。
   - 若《实战策略建议》面板可见，只读 UI Automation 未发现 HTTP、异常、路径、请求标识或令牌等技术文本。脚本不会移动真实鼠标，也不读取浏览器密码、Cookie 或登录态。
   - 面板未实际出现时 UI 项必须是 `not_exercised`，总结果为 `partial`，不能把未覆盖冒充通过。脚本退出码约定为：通过 0、失败 1、部分覆盖或警告 2。

4. 旧 `Invoke-HdtClientSmoke.ps1` 会调用源码目录的 `Install-MetaCompanion.ps1`，可能用源码 `solver/` 覆盖刚通过门禁的 Rust worker；本轮 Rust 灰度不要运行它。以下产品行为改为另行人工确认：

   - 单独做一次“实时策略建议关闭、保存脱敏训练记录开启”的标准或竞技场对局：HDT 仍只有一个 Rust worker、没有 MetaCompanion Python worker，且建议面板始终不出现。
   - 上述“仅采集”对局中，日志或请求计数必须证明 `/v1/observe` 与 `/v1/behavior` 被调用、`/v1/solve` 调用数为 0；对局前后 `training-v2.jsonl` 与 `behavior-v1.jsonl` 的字节数和完整 JSON 行数均增加。
   - `behavior-v1.jsonl` 必须同时出现 `actor_side=local` 和 `actor_side=opponent`，并覆盖本局实际发生的出牌/攻击/英雄技能/结束回合；若本方实际激活地点，还必须出现来源为本方公开棋盘 `LOCATION` 的 `location_activate`。若本局发生 Discover、Choose One 或其他本方选择，完整 HDT 提供/选择日志必须生成 `choice_status=selected`、非空 `choices`、完整 `option_entity_ids` 与属于提供集的 `selected_entity_ids`；证据不完整时只能是 `unresolved` 且 `behavior_eligible=false`。换牌阶段或无根选择不得出现在后续出牌的 `choices` 中。所有行的 `rl_training_eligible=false`。无法唯一解析的对手隐藏出牌只能记录为 `identity_status=unknown`、`behavior_eligible=false`；隐藏手牌占位可保留本局实体编号用于前后差分，但不得泄露 CardID、牌名、文本、费用或战斗属性。当前不要求也不允许凭差分猜测对手地点激活。
   - 对局结束后，胜负只出现在 `training-v2.jsonl` 的 `/v1/observe` result 记录中，`behavior-v1.jsonl` 不得新增 result/outcome 字段；两者由同一匿名 `game_id` 关联，关联后行为行仍必须保持 `rl_training_eligible=false`。
   - 模拟 worker 暂不可用、ACK 丢失或终局耐久同步失败后重启：behavior outbox 必须按原序、原始 JSON 重试；同一 `(game_id, behavior_sequence)` 只在 `behavior-v1.jsonl` 保留一行，精确 ACK 后队首文件才删除。result outbox 同样必须保留原始终局 JSON；完全相同的重试只在 `training-v2.jsonl` 保留一条 result，同局冲突不得覆盖，耐久同步完成且精确 ACK 后才删除。该局即使完成 behavior/result 关联，所有 behavior 仍须保持 `rl_training_eligible=false`，并与严格训练轨迹物理隔离。
   - 标准对战选套牌界面显示卡组流派推荐。
   - 面板可以关闭、拖动，鼠标提示可见。
   - 进入实际对局后，标准环境面板自动隐藏。
   - 标准和竞技场的我方可操作回合显示实战建议；约 3 秒出现临时三路线，约 10 秒内更新最终排序。
   - exact 路线在合同满足时显示对手最坏可见回应，scoped lethal 只显示其斩杀证明范围；`visible-response-v1` 近似路线不得显示已验证回应、minimax、安全性或最优性。模型内反杀有醒目警告，未知抽牌或未覆盖规则不能显示为已验证安全。
   - worker 健康信息显示 `hdt_visible_point_effects_v1 = true`，结构化规则目录为 47 条规则、205 个显式 CardID、5 条上下文门禁规则和 5 条内在机制证据门禁规则；若缺失或计数不符，不能发布。
   - 原始 HDT Fireball/Fireblast/Steady Shot/Armor Up!/Demon Claws/Static Shock/Molten Gold Elemental/Queldorei Fletcher/Sleet Storm 请求能返回规则 provenance；真实形态 `IsPlayableCard=false` 的英雄技能在 `HAS_ACTIVATE_POWER=1`、未疲劳/禁用且费用可支付时仍能枚举。SPELL 伤害正确叠加法强，Steady Shot 只能命中敌方英雄且不叠加法强；造箭师必须在手牌降至 3 张时把英雄技能重算为 0 费，并在光环来源离场时恢复公开基础费用；激寒急流必须分开玩家指定目标和随机敌方随从目标，并输出精确概率。AURA 或 `TAG_LAST_KNOWN_COST_IN_HAND` 证据缺失时失败关闭。临时英雄攻击力必须有显式 `NUM_ATTACKS_THIS_TURN` 才能恢复攻击机会，英雄攻击后标签计数同步递增。EnglishText 哈希漂移、类型错误、目标/伤害/英雄技能翻倍标签、玩家标签或攻击历史证据缺失、未登记文本，以及未审核的随机、Discover 或 Choose One 请求必须 abstain，不能返回伪精确路线。
   - 出牌、攻击或切换回合后旧 `state_id` 的建议立即消失，worker 不可用时 HDT 其他功能保持正常。
   - 建议浮窗可以关闭、拖动，重启 HDT 后位置保留；它不产生游戏点击或键盘输入。
   - 前几张牌后，HDT 原生内嵌预测稳定显示。
   - “剩余卡牌预测”只在牌库剩余阈值附近触发，不因预测牌数量过早弹出。
   - 剩余卡牌面板可拖动，重启 HDT 后位置保留。
   - 赛后本地历史刷新，更新时间和样本数变化合理。

5. 保存统一门禁报告、后台验收输出和人工检查记录；后台脚本输出 schema 为 `metacompanion-hdt-advisor-runtime-smoke-v1` 的 JSON。旧脚本的下列报告路径不作为本轮 Rust 灰度证据：

   ```text
   artifacts\client-smoke\<timestamp>\hdt-client-smoke.md
   ```

## 账号兼容性

发布前至少覆盖以下组合：

- 开发账号：有 HSReplay cookie，有本地历史。
- 普通账号模拟：无 HSReplay cookie，有本地历史。
- 新用户模拟：无 HSReplay cookie、无旧 config、无本地历史。
- 网络失败：HSReplay 不可用时，插件不影响 HDT 启动和实际对局。
- 显示环境：至少覆盖 1920x1080 100% 缩放，以及自己常用缩放/多屏环境。

普通社区用户只有 DLL 时也应能正常加载，实战建议可以降级；使用 DLL 和随包 Rust worker 时不要求安装 Python。Python 仅用于显式离线训练、评测和审计；没有会员或历史时，推荐数据可以为空；Rust worker 不可用时也不能崩溃、不能卡住 HDT、不能要求用户提供 cookie。

## 社区发布包

社区包默认只包含：

- `MetaCompanion.dll`
- `README.md`
- `LICENSE`
- `NOTICE.md`
- 必要说明文档
- 运行时 `solver/`
- 通过三组 Rust 门禁的 `solver\metacompanion-solver.exe`
- `tools\Sync-HdtArenaAdvisorData.ps1`（本地匿名导出器）
- `tools\Sync-BlizzardCardPools.ps1`（公开 Standard/Arena 卡池同步器）

社区包默认不包含：

- 除 `Sync-HdtArenaAdvisorData.ps1` 和 `Sync-BlizzardCardPools.ps1` 外的 `tools\*.ps1`
- `hsreplay_cookie.txt`
- `Premium\`
- `local_meta_*.tsv`
- `match_history.tsv`
- `hdt_opponent_history.tsv`
- `prediction_timeline.tsv`
- `bin\` / `obj\`
- `training.jsonl`、`training-v2.jsonl`、`behavior-v1.jsonl`、`behavior-outbox-v1\`、`result-outbox-v1\`、`AdvisorData\`、`ArenaLastDrafts.xml`
- 证书、发布配置或任何个人账号数据

开发者或高级用户需要远程数据刷新脚本时，应从源码仓库查看说明，不通过普通社区包自动下发。随包 Arena 导出器只读取本机 HDT 缓存并发布匿名聚合先验；公开卡池同步器只访问暴雪公开接口。插件与这两种同步器都不读取 Chrome/Edge 的密码、Cookie 或登录态。

## 回滚步骤

1. 关闭 HDT。
2. 删除或替换：

   ```text
   %AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion\MetaCompanion.dll
   ```

3. 如需彻底清理，删除：

   ```text
   %AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion
   ```

4. 保留 `%AppData%\HearthstoneDeckTracker\MetaCompanion` 可保留用户配置和本地历史；只有在排查数据问题时才备份后删除。
