# 本地 HSReplay 数据说明

Meta Companion 不在 HDT 进程里直接抓网页。数据同步放在 `tools/` 脚本里完成，插件启动时只读取本地快照，降低对局中卡顿和网络失败的风险。社区发布包默认不安装这些脚本；它们主要用于开发者维护快照，或高级用户手动同步。

## 公共牌组快照

```powershell
.\tools\Sync-HSReplayDeckCodes.ps1 `
  -RankRanges DIAMOND_THROUGH_LEGEND `
  -LimitPerRange 250 `
  -MaxDecks 500 `
  -Parallelism 6
```

日常刷新、计划任务和赛后自动补快照默认都只取 `DIAMOND_THROUGH_LEGEND`。这个段位范围属于远端 HSReplay 数据口径，不会过滤本机对战历史。本地样本只支持最近 X 天 / X 场窗口。如需临时扩大远端牌组库，可显式传入多个 `-RankRanges`，或使用 `Update-MetaCompanionData.ps1 -Full`。

流派推荐面板的“本地样本”折叠区使用纯鼠标预设：天数可选本补丁全部、1/3/7/14/30 天，场数可选不限、10/20/50/100 场；需要任意精确数字时仍可在插件设置页填写。两个非零限制同时生效，并且始终截断在当前补丁起点之后。“清空数据”只清空插件用于推荐加权的本地样本，不删除 HDT 原始历史；“恢复本补丁全部”会重新读取 HDT 历史、移除清空边界，并把天数和场数都重置为 `0`（不限），确保恢复的是当前补丁全部可用对局，而不是原筛选窗口内的一部分。

输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt
```

插件启动时由 `MetaRetriever` 读取这个文件，并用 HearthDb 解析 deckstring。

## Premium Cookie

Premium 数据需要你自己的 HSReplay 登录 Cookie：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt
```

这个文件只放本机，不提交。

## Premium 环境数据

默认最近 7 天、标准天梯、钻石到传说的形态热度和对阵矩阵：

```powershell
.\tools\Sync-HSReplayMetaData.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -TimeRange LAST_7_DAYS `
  -TopOverall 20 `
  -TopPerClass 5
```

脚本会优先从本机炉石安装目录的 `.product.db` 解析补丁号，并写入 `manifest.json`。面板会显示为类似 `35.6.2补丁后`；解析不到补丁号时显示 `当前补丁后`。

输出目录：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest
```

## 识别语料与同口径代表卡组

```powershell
.\tools\Sync-HSReplayArchetypeDecks.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -OutputPath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\archetype_model_branches.tsv" `
  -CandidateTimeRange CURRENT_PATCH `
  -BranchesPerArchetype 5 `
  -MinGames 100
```

输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\archetype_model_branches.tsv
```

HSReplay 的 `list_deck_inventory_v2` 不支持 `TimeRange`，所以 `hsreplay_deckcodes.txt` 只能作为公共库存快照，可能包含旧补丁仍被保留的 deck id。插件发现 `archetype_model_branches.tsv` 明确属于受支持的当前补丁窗口，且 `CandidateAsOf` 不早于本地 `patch_marker.txt` 时，会优先用它做本地流派识别，避免旧牌组污染。

`archetype_deck_branches.tsv` 是另一份用途严格受限的快照：只给推荐面板的“胜率最高 / 使用最多”复制按钮使用。它必须和远端环境的时间、段位完全一致。默认统一使用最近 7 天，因此环境、克制矩阵和代表卡组可以直接对齐；若用户主动选择了上游不支持的范围，推荐仍会生成，但代码字段留空，不会跨口径冒用卡组。

## 自动刷新

开发或高级用户可安装外部计划任务：

```powershell
.\tools\Install-MetaCompanionRefreshTask.ps1
```

计划任务默认每天 08:05 运行 `Run-MetaCompanionRefresh.ps1`，并启用 `StartWhenAvailable`，所以电脑在定时时间关机或睡眠时，Windows 会在下次可用时补跑。安装脚本还会给同一个任务加一个登录后延迟 5 分钟的补查触发器；这个触发器不会每天重复抓取，因为刷新入口会先检查远程缓存是否已经在当天更新完整。

`Run-MetaCompanionRefresh.ps1` 判断“远程缓存已新鲜”的条件是这些必需文件都存在、最后写入日期是今天，并且远端 Meta 属于用户选择的时间/段位，识别语料属于 `CURRENT_PATCH`。同口径代表卡组是可选增强项，不会因为上游接口不支持而阻断整次刷新。如果已经识别到新的本地补丁时间，远端 `summary.as_of`、对阵矩阵或识别语料早于补丁时间，也会强制刷新：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt
%AppData%\HearthstoneDeckTracker\MetaCompanion\archetype_model_branches.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\summary.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\head_to_head_archetype_matchups_v2.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\manifest.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\ModelBranches\latest\manifest.json
```

如果这些条件满足，脚本会输出“远端缓存今天已经刷新完成，已跳过”并退出；否则会刷新远程牌组快照、环境数据、对阵矩阵、识别语料和推荐结果。HSReplay 在补丁刚上线时可能以 HTTP 202 表示查询仍在生成，脚本会有界轮询；仍未完成时只保留通过补丁 epoch 校验的既有快照。Premium 独立阶段仍可用滚动窗口兜底，不支持该窗口的接口会在请求前一次性跳过。

计划任务和手动运行属于 HDT 进程外路径，PowerShell 可以独立生成推荐。插件内的赛后刷新采用不同的编排方式：PowerShell 只补外部快照，并传入 `-SkipPersonalRecommendations`，不会写 `local_meta_*` 或 `personal_recommendations.*`；外部刷新完成后，再由插件内同一个 C# 刷新器强制重算最终结果。若 C# 刷新当时仍在运行，强制请求会排队。这样可避免赛后快速结果被延迟脚本覆盖，并保留 `match_corrections.tsv` 中的人工修正。

## 推荐生成

远端环境推荐：

```powershell
.\tools\Get-MetaArchetypeRecommendations.ps1 -Top 20 -IncludeClassTop
```

个人加权推荐：

```powershell
.\tools\Update-MetaCompanionData.ps1 -LocalMeta -PersonalRecommendations
```

当前个人推荐模型版本为 `beta_dirichlet_soft_v2`：

- 本地流派识别保留候选概率，不再把整局硬计入第一名；未分配概率记录为 `Unknown`。
- 远端流派分布作为默认 30 局强度的 Dirichlet 先验，本地已知证据逐局加入，实际本地权重随证据量平滑增长。
- 每个对阵格用默认 50 局强度的 Beta 先验向候选总体胜率收缩，不再以 200 局为评分硬阈值。
- 输出近似 90% 区间、`probability_best_pct` 和 `tier`，用于判断排名是否稳定。

详细公式见 [推荐模型设计](RECOMMENDATION-DESIGN.md)。

`local_meta_archetypes.tsv` 是逐局识别缓存：脚本先从 HDT 的 `DeckStats.xml` 和 `DefaultDeckStats.xml` 导出每局对手实际出现过的卡，再用当前牌组库估计候选流派概率，最后把概率分布、Unknown、补丁权重和时间衰减权重写成表。它不是独立数据源，只是避免插件反复实时扫描 HDT 历史和解析大量 deckstring。人工修正由插件合并 `match_history.tsv` 时应用，并作为对应流派 100% 的证据。

新增的逐局诊断列包括：

```text
recognition_model
top_probability_pct
unknown_probability_pct
recognition_tier
archetype_distribution_json
evidence_weight
soft_known_weight
soft_unknown_weight
format
mode
```

机器可读 JSON 保存同职业全部有效候选，面板只展示概率最高的三个。只有 `Standard + Ranked` 对局会进入个人推荐的 Dirichlet 环境融合；其他模式仍可用于最近一局展示。

默认能识别补丁时间时会从补丁标记时间开始导出并统计当前补丁内全量样本；识别不到补丁时间时退回最近 N 天。检测到炉石补丁号或客户端补丁时间前进时，插件和刷新脚本会先把活跃的本地对局记录与本地环境缓存移到 `PatchArchives`，当前路径从新补丁重新开始写。

相关输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_archetypes.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_environment.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_summary.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\personal_recommendations.tsv
```

`personal_recommendations.tsv` 保留原来的九个基础列，并追加：

```text
expected_win_rate_low_90
expected_win_rate_high_90
probability_best_pct
tier
model_version
legacy_coverage_pct
legacy_matchups_used
```

对应 JSON 使用 `schema_version = 2`，并记录实际本地/远端权重、远端和对阵先验强度、已知/未知证据量、Kish 有效样本量、抽样数以及覆盖率口径。`min_matchup_games = 200` 仅保留为旧口径诊断，不再决定某个对阵格是否参与评分。

## HDT 历史与录像

HDT 原生对局历史来自：

```text
%AppData%\HearthstoneDeckTracker\DeckStats.xml
%AppData%\HearthstoneDeckTracker\DefaultDeckStats.xml
```

本地录像来自：

```text
%AppData%\HearthstoneDeckTracker\Replays
```

导出脚本会把 `replay_file`、`replay_path`、`hsreplay_upload_id`、`hsreplay_url` 一并写入历史表，赛后浮窗据此显示 HSReplay / 本地录像入口。

## 健康监控

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File `
  "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\Tools\Watch-MetaCompanionHealth.ps1"
```

监控内容包括：

- HDT / Hearthstone 进程状态。
- 可选校验已安装 `MetaCompanion.dll` 哈希；默认不校验 DLL 哈希。需要完整性校验时，从 release gate 报告的 `Build Artifact` 复制 SHA256，并传入 `-ExpectedDllHash`。
- 插件日志和 HDT 日志里的错误。
- 预测快照是否异常膨胀。
- 本地环境统计是否过期。

