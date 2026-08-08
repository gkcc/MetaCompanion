# 推荐模型设计

推荐层回答一个问题：结合当前环境、近期本地对手和对阵数据，现在更适合使用哪一种牌组形态。

当前模型版本是 `beta_dirichlet_soft_v2`。核心仍是“环境占比 × 对阵胜率”的加权期望，但环境估计、流派识别和小样本对阵都保留不确定性，不再使用固定 35% 本地权重或 200 局硬切换。

## 数据层

- 远端环境：HSReplay Premium 的流派热度和流派对阵胜率；默认最近 7 天、钻石到传说，时间范围和段位范围只作用于这一层。
- 本地环境：HDT `DeckStats.xml`、`DefaultDeckStats.xml` 与插件 `match_history.tsv` 中的当前补丁对局；只按最近 X 天 / X 场筛选，不按段位筛选。
- 人工修正：`match_corrections.tsv`。同一局存在修正时，修正结果覆盖自动识别并作为 100% 的 one-hot 证据。
- 识别牌组库：优先读取当前补丁的 `archetype_model_branches.tsv`；它不可用或早于补丁标记时，回退到 `hsreplay_deckcodes.txt`。
- 代表卡组：`archetype_deck_branches.tsv` 只给“胜率最高 / 使用最多”复制按钮使用，必须与远端推荐的时间范围和段位范围完全一致；不一致时推荐照常生成，卡组代码留空。

本地对局按时间半衰期衰减，默认半衰期为 3 天，同时以 `patch_marker.txt` 作为硬截止：早于当前补丁 epoch 的对局权重恒为 0，不进入本地环境或个人推荐。`PrePatchWeight` 参数仅为旧调用兼容保留，当前固定按 0 处理。

## 软流派识别与 Unknown

自动识别不会再把整局硬塞给第一名。机器可读分布保留同职业全部有效候选；面板只展示概率最高的三个：

```text
P(流派 | 已见原始牌)
```

候选概率之和不足 1 的部分记为 `Unknown`。例如：

```text
任务牧 60% / 兆示萨 30% / Unknown 10%
```

该局对本地环境的已知证据分别增加 0.6 和 0.3；剩余 0.1 只进入未知证据诊断，不会被强行分配给任务牧。人工修正则直接变成被修正流派 100%、Unknown 0%。

自动识别的 Known 总质量同时受三部分约束：证据牌数量、已见牌在当前牌组库中的覆盖度，以及最佳流派/最佳分支对这些牌的拟合度。即使证据很多，自动识别也最多分配 95% Known，至少保留 5% Unknown；人工修正不受这一上限影响。这样“牌很多但所有候选都解释不好”的对局会得到更高 Unknown，而不会因为观察到 6 张牌就被错误视为 100% 确定。

`local_meta_archetypes.tsv` 会记录：

- `recognition_model`
- `top_probability_pct`
- `unknown_probability_pct`
- `recognition_tier`
- `archetype_distribution_json`
- `evidence_weight`
- `soft_known_weight`
- `soft_unknown_weight`
- `format` / `mode`

`recognition_tier` 只用于解释识别结果，可能为 `corrected`、`confirmed`、`likely`、`mixed` 或 `unknown`。

## Dirichlet 环境融合

远端分布被视为 Dirichlet 先验，本地软识别结果作为新证据加入：

```text
alpha[a] = remote_prior_games * remote_pct[a]
         + sum(local_evidence[t] * P_t(a))

environment_pct[a] = alpha[a] / sum(alpha)
```

默认 `remote_prior_games = 30`。因此少量本地对局只会轻微修正远端环境，本地证据积累后影响才会平滑增加：

```text
effective_local_weight =
  local_known_evidence / (remote_prior_games + local_known_evidence)
```

只有 Standard Ranked 对局进入这条融合公式；Casual/Friendly 对局仍可保留在最近对局和本地统计里，但不会修正天梯推荐。旧配置 `LocalRecommendationWeight = 0.35` 仅保留为兼容诊断字段 `legacy_local_weight_setting`，不再作为固定混合比例。JSON 同时写出实际的 `local_weight`、`remote_weight`、已知/未知证据量和 Kish 有效样本量。

## Beta 连续收缩

对候选流派 `d` 和对手流派 `a`，不再使用“199 局回退、200 局直接采用原始胜率”的硬阈值。对阵胜率连续收缩到候选流派总体胜率：

```text
mu = 候选流派总体胜率
p  = 该对阵格观测胜率
n  = 该对阵格样本量
k  = matchup_prior_games

posterior_win_rate = (k * mu + n * p) / (k + n)
data_share         = n / (k + n)
```

默认 `matchup_prior_games = 50`。例如总体胜率 55%、某对阵 300 局胜率 70%，收缩后的胜率为 67.86%，数据贡献度为 85.71%。缺失或非法对阵格使用总体胜率，数据贡献度为 0。

200 局旧口径仍写入 `legacy_coverage_pct` 和 `legacy_matchups_used`，只用于迁移诊断，不再参与评分。

## 当前评分

候选流派的点估计为：

```text
expected_win_rate[d] =
  sum(environment_pct[a] * posterior_win_rate[d, a])
```

连续覆盖率为：

```text
coverage_pct[d] =
  100 * sum(environment_pct[a] * data_share[d, a])
```

覆盖率低于 50% 的候选仍会被过滤。其余候选按期望胜率、连续覆盖率和加权样本量排序。

## 区间、P(best) 与梯队

模型根据 Dirichlet 环境方差和 Beta 对阵方差计算后验矩，并用固定种子的正态近似抽样估计排名不确定性。输出包括：

- `expected_win_rate_low_90` / `expected_win_rate_high_90`：近似 90% 区间。
- `probability_best_pct`：候选在抽样中排名第一的比例。
- `tier`：根据抽样重叠划分的近似梯队。
- `posterior_draws` 和 `uncertainty_method`：诊断所用抽样数及方法。

这些字段用于表达“排名有多稳”，不改变默认按后验均值排序的行为。

## 输出与单一最终写入路径

HDT 插件内的最终派生输出统一由 C# `QuickDashboardRefresher` 生成：

1. 一局结束后立即用现有缓存刷新本地环境和推荐。
2. 需要补远端快照时，局后 PowerShell 只更新牌组、分支、环境和对阵矩阵；参数包含 `-SkipPersonalRecommendations`，不写 `local_meta_*` 或 `personal_recommendations.*`。
3. PowerShell 成功结束后调用强制 C# 刷新；若已有刷新运行，强制请求进入队列，不会丢失。
4. 没有外部数据刷新工作时，不启动局后 PowerShell。

这样可避免 C# 快速结果在约 8 秒后被另一套识别/推荐实现覆盖，也保证人工修正最终仍由同一条路径应用。

手动或计划任务在 HDT 进程外运行时，PowerShell 仍可独立生成推荐文件；两种实现必须用固定夹具做字段一致性测试。

推荐 TSV 保留原有九个基础列并在尾部追加区间、`P(best)`、梯队、模型版本和旧覆盖率诊断。JSON 使用 `schema_version = 2` 和 `model_version = beta_dirichlet_soft_v2`。

## 验证原则

- 参数必须按时间顺序走步回测，不能随机把未来补丁或未来环境拆进训练集。
- 流派概率用人工修正或完整标签检查 log loss、Brier score 和校准误差。
- 环境分布用下一局对手流派的 log loss 检查。
- 对阵收缩用未来对局的概率损失选择 `matchup_prior_games`。
- 发布前必须比较 C# 与独立 PowerShell 在同一输入夹具下的排名、期望、覆盖率和诊断字段。

## 后续方向

- 按玩家实际分段和地区选择远端环境。
- 对玩家自己的牌组熟练度做强收缩的个人效应，不直接相信少量个人胜率。
- 在浮窗中把 Unknown、区间、`P(best)` 和推荐变化原因转成更直观的解释。
