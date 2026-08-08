# 项目目录说明

`Meta Companion` 以当前仓库为项目根，和 `F:\Workspace` 下的其他实验目录分开管理。

## 源码

- `MetaCompanion/`  
  C#/.NET Framework 4.7.2 的 HDT 插件入口。包含预测逻辑、实战局面提取、本机 worker 桥接、覆盖层 UI、设置窗口、日志、对局历史、赛后浮窗和本地推荐读取；`AdvisorBehaviorCollector.cs`、`AdvisorBehaviorPendingTracker.cs`、`AdvisorBehaviorOutbox.cs` 负责双方公开行为投影、稳定 post-state 关联和可靠 FIFO 传输。Rust exe 不能直接替代此 HDT 入口。

- `MetaCompanionTests/`  
  单元测试。覆盖预测过滤、后期剩余牌、advisor 状态指纹/协议/过期拒绝/降级、赛后数据读取、配置保存等行为。

- `solver/`
  Python 3.10+ 本机求解器。包含 snake_case API schema、通用规则模拟器、PUCT 搜索、`advisor-training-log-v2` 脱敏训练日志、离线回放/基准/训练和有界 self-play 轨迹工具。`metacompanion_solver/trajectory.py` 提供 `trajectory-readiness-v1` 只读审计器；`fixtures/trajectory-readiness-v1.jsonl` 与对应 policy 是发布门禁夹具，不是生产训练集。`metacompanion_solver/card_rules.py` 与 `metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json` 提供首批严格规则目录：47 条审核规则、205 个显式 CardID/皮肤别名，只接受 `card_id + normalized EnglishText SHA256 + card_type + required intrinsic mechanics + declared context guards` 完整匹配；目录含定点/群体伤害、治疗、自伤、标签证明的吸血、条件目标、法力、护甲、冻结、身材增益、设定生命、临时英雄攻击力、持续英雄技能费用光环、1 费牌翻倍、HDT 已确认的地点激活/召唤、伤害后召唤突袭随从、武器亡语抽生成法术、逐点随机分配与可见 1 费历史重放，当前仍不是完整炉石规则引擎或已训练 RL 模型。

- `solver/metacompanion_solver/hdt_rule_evaluation.py`、`solver/fixtures/oracle-hdt-cardrules-v1.json`
  独立 raw-HDT point-effect oracle 门禁及夹具。覆盖规则 provenance、法术伤害、双方合法性、最坏回应和未登记/漂移/随机/Discover/Choose One 的 abstain 负控，不复用生产搜索结果作为真值。

- `solver/metacompanion_solver/trajectory.py`、`solver/metacompanion_solver/verification.py`、`solver/fixtures/trajectory-readiness-*`
  真实轨迹的数据准备/晋升门禁。检查 stage canonicalization、终局关联、精确动作契约、pre/post 独立回放、本方回合分段、候选状态/哈希/边界证据、按局切分及隐私；只为 exact 且回放成功的动作生成 allowlist。`verification.py` 另写 hash-bound 脱敏语料与逐动作 manifest，并让训练消费同一不可变 snapshot。当前 HDT action candidate 仍为 partial，不会自动晋升。

- `solver/metacompanion_solver/behavior.py`、`solver-rust/src/behavior.rs`
  独立 `advisor-behavior-v1` 合同与 `behavior-v1.jsonl` 写入器。worker 负责 game ID 匿名化、规范化哈希、`behavior_id` 和 `(game_id, sequence)` 幂等；行为语料固定 `rl_training_eligible=false`，不与严格 `training-v2.jsonl` 混用。

- `solver/metacompanion_solver/behavior_learning.py`、`solver/metacompanion_solver/behavior_candidate_alignment.py`、`solver/metacompanion_solver/behavior_prior.py`、`solver-rust/src/behavior_prior.rs`、`solver/fixtures/behavior-*-readiness-*`
  双文件不可变快照联审、`behavior-learning-readiness-v1` 生产门槛、`behavior-imitation-example-v1` 独立晋升器、观察动作与完整合法候选集合的聚合联审、分层行为频率基线，以及 Rust 严格二次解析、只排序接入和原子热加载。候选联审不输出对局/状态/实体 ID，并严格禁止跨 CardDefs build 套用规则。行为先验只用 game-level `train` 样本学习，validation/test 只评估；只能给求解器已确认合法的动作调整搜索顺序，不能生成动作、覆盖战术评分或成为线上/RL/最优策略。固定三分割 fixture、manifest 和 policy 是发布阻断门禁，不代表真实运行数据已经就绪。

- `solver/metacompanion_solver/decision_frame.py`、`decision_ranker.py`、`observed_policy_evaluation.py`、`decision_solver_evaluation.py`、`rust_worker_client.py`
  HDT 原生完整候选帧合同、本方 listwise 行为排序、双方行为联合评估，以及真实历史帧对 Release Rust 根动作组合的覆盖审计。覆盖报告绑定决策帧、双方行为、确定性样本和二进制 SHA-256，分开输出 Rust 独立生成召回/精确率与 HDT supplied 组合的评估/跳过覆盖，再聚合 exact/partial/unsupported、false-exact、可复核多备选、observed-choice 一致率与公开 CardID 缺口；不输出私有 ID、时间、URL、token 或绝对路径。通用 worker 客户端通过环境变量传随机 token，供可见回应和决策覆盖两类离线门禁复用。

- `solver/metacompanion_solver/card_pool.py`、`solver-rust/src/card_pool.rs`、`solver/tools/rust_card_pool_gate.py`
  官方 Standard/Arena 卡池的双实现消费合同和发布二进制门禁。两条 worker 路径都复核原子发布标记、manifest、双池身份/计数/哈希、分页新鲜度与当前 HDT CardDefs；门禁用临时快照逐字段比较 Python/Rust，并覆盖发布绑定、过期时间、CardDefs 与重复身份篡改。卡池只作来源和模式覆盖证据，不执行规则或动作合法性。

- `Images/`  
  文档图片。

## 工具

- `tools/`  
  PowerShell 工具源码。用于同步 HSReplay 数据、导出 HDT/ArenaSmith 本地数据、计算本地环境、生成推荐、安装插件和健康监控；`Update-AdvisorBehaviorPrior.ps1` 支持实时日志或已绑定的历史导入目录，分别训练本方 HDT 决策排序与对手公开行为模型，经联合评估和两个 Rust 门禁后成对事务安装，任何失败都保留或回滚现有双模型。

- `dist/`  
  可复制脚本输出和发布产物目录。`dist/*.ps1` 可复制到 HDT 数据目录；`dist/MetaCompanion.dll` 和 `dist/*.zip` 是生成物，不提交。

## 文档

- `README.md`  
  项目入口、构建、测试、安装和数据刷新说明。

- `NOTICE.md`  
  来源、许可和致谢说明。

- `docs/LOCAL-HSREPLAY.md`  
  HSReplay / Premium 数据源和刷新流程说明。

- `docs/RECOMMENDATION-DESIGN.md`  
  推荐模型和后续加权思路。

- `docs/IMPLEMENTATION-SUMMARY.md`  
  当前实现状态和手动验收清单。

- `docs/ADVISOR-DATA.md`
  竞技场本地先验的数据契约，以及独立双方行为语料、可靠 outbox、隐私与训练隔离边界。

## 生成物和本地私有数据

这些内容不作为源码维护：

- `packages/`
- `MetaCompanion/bin/`、`MetaCompanion/obj/`
- `MetaCompanionTests/bin/`、`MetaCompanionTests/obj/`
- `.tmp_hsreplay_js/`
- `dist/MetaCompanion.dll`、`dist/*.zip`
- `hsreplay_cookie.txt`
- `Premium/`
- `Logs/`
- `AdvisorWorker/training.jsonl`（旧版，只保留不再追加）
- `AdvisorWorker/training-v2.jsonl`（当前 `advisor-training-log-v2` 本地记录）
- `AdvisorWorker/behavior-v1.jsonl`（本方与对手的公开行为语料，不是 RL 训练集）
- `advisor-decision-frame-v1.jsonl`、`decision-frame-readiness.json`、`decision-solver-coverage*.json`（本机历史候选与审计报告，不进入社区包）
- `behavior-outbox-v1/`（等待 worker 精确确认的可靠传输队列）
- `result-outbox-v1/`（等待 worker 持久、幂等确认的终局结果队列）
- `AdvisorData/`
- `self-play-run/`
- `match_history.tsv`、`prediction_timeline.tsv`、`hdt_opponent_history.tsv`
- `local_meta_*.tsv`、`local_meta_*.json`
- `recommendations.tsv`、`personal_recommendations.tsv`
- `anomalies.tsv`、`patch_marker.txt`

## HDT 运行时路径

- `%AppData%\HearthstoneDeckTracker\MetaCompanion`：插件数据、日志、Premium 缓存、本地环境、推荐结果、advisor worker、严格轨迹、独立双方行为语料、行为/终局可靠 outbox 和竞技场先验。
- `%AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion`：已安装插件 DLL。
- `%AppData%\HearthstoneDeckTracker\DeckStats.xml`、`%AppData%\HearthstoneDeckTracker\DefaultDeckStats.xml`：HDT 原生对局历史。
- `%AppData%\HearthstoneDeckTracker\Replays`：本地 `.hdtreplay` 录像。

