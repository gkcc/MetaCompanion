# 本地 HSReplay 数据说明

Meta Companion 不在 HDT 进程里直接抓网页。数据同步放在 `tools/` 脚本里完成，插件启动时只读取本地快照，降低对局中卡顿和网络失败的风险。社区发布包默认不安装这些脚本；它们主要用于开发者维护快照，或高级用户手动同步。

## 公共牌组快照

```powershell
.\tools\Sync-HSReplayDeckCodes.ps1 `
  -RankRanges DIAMOND_THROUGH_LEGEND,DIAMOND_FOUR_THROUGH_DIAMOND_ONE,PLATINUM,GOLD,BRONZE_THROUGH_GOLD `
  -LimitPerRange 250 `
  -MaxDecks 500 `
  -Parallelism 6
```

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

当前补丁、标准天梯、钻石到传说的形态热度和对阵矩阵：

```powershell
.\tools\Sync-HSReplayMetaData.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -TimeRange CURRENT_PATCH `
  -TopOverall 20 `
  -TopPerClass 5
```

脚本会优先从本机炉石安装目录的 `.product.db` 解析补丁号，并写入 `manifest.json`。面板会显示为类似 `35.6.2补丁后`；解析不到补丁号时显示 `当前补丁后`。

输出目录：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest
```

## 当前补丁代表分支

```powershell
.\tools\Sync-HSReplayArchetypeDecks.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -CandidateTimeRange CURRENT_PATCH `
  -BranchesPerArchetype 5 `
  -MinGames 100
```

输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv
```

HSReplay 的 `list_deck_inventory_v2` 不支持 `TimeRange`，所以 `hsreplay_deckcodes.txt` 只能作为公共库存快照，可能包含旧补丁仍被保留的 deck id。插件发现 `archetype_deck_branches.tsv` 明确标记为 `CandidateTimeRange: CURRENT_PATCH`，且 `CandidateAsOf` 不早于本地 `patch_marker.txt` 时，会优先使用这个当前补丁分支文件，避免旧牌组污染识别。自动刷新默认会生成这个分支文件；需要临时跳过时可给刷新入口加 `-SkipBranches`。

## 自动刷新

开发或高级用户可安装外部计划任务：

```powershell
.\tools\Install-MetaCompanionRefreshTask.ps1
```

计划任务默认每天 08:05 运行 `Run-MetaCompanionRefresh.ps1`，并启用 `StartWhenAvailable`，所以电脑在定时时间关机或睡眠时，Windows 会在下次可用时补跑。安装脚本还会给同一个任务加一个登录后延迟 5 分钟的补查触发器；这个触发器不会每天重复抓取，因为刷新入口会先检查远程缓存是否已经在当天更新完整。

`Run-MetaCompanionRefresh.ps1` 判断“远程缓存已新鲜”的条件是这些文件都存在、最后写入日期是今天，并且 manifest 的时间范围是 `CURRENT_PATCH` 或补丁日兜底用的 `LAST_1_DAY`。如果已经识别到新的本地补丁时间，远端 `summary.as_of` 还早于这个补丁时间，也会强制刷新：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_deckcodes.txt
%AppData%\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\summary.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\head_to_head_archetype_matchups_v2.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\manifest.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Branches\latest\manifest.json
```

如果这些条件满足，脚本会输出 `Remote cache already refreshed today; skipping.` 并退出；否则会刷新远程牌组快照、环境数据、对阵矩阵和推荐结果。HSReplay 在补丁刚上线时可能暂时不开放 `CURRENT_PATCH` 查询；此时脚本会用 `LAST_1_DAY` 作为远端 meta 兜底，本地环境仍从补丁时间开始计算。

## 推荐生成

远端环境推荐：

```powershell
.\tools\Get-MetaArchetypeRecommendations.ps1 -Top 20 -IncludeClassTop
```

个人加权推荐：

```powershell
.\tools\Update-MetaCompanionData.ps1 -LocalMeta -PersonalRecommendations
```

`local_meta_archetypes.tsv` 是逐局识别缓存：脚本先从 HDT 的 `DeckStats.xml` 和 `DefaultDeckStats.xml` 导出每局对手实际出现过的卡，再用当前牌组库识别这局对手最像哪个流派，最后把识别结果、置信度、补丁权重和时间衰减权重写成表。它不是独立数据源，只是避免插件反复实时扫描 HDT 历史和解析大量 deckstring。默认能识别补丁时间时会从补丁标记时间开始导出并统计当前补丁内全量样本；识别不到补丁时间时退回最近 N 天。检测到炉石补丁号或客户端补丁时间前进时，插件和刷新脚本会先把活跃的本地对局记录与本地环境缓存移到 `PatchArchives`，当前路径从新补丁重新开始写。

相关输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_archetypes.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_environment.tsv
%AppData%\HearthstoneDeckTracker\MetaCompanion\local_meta_summary.json
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest\personal_recommendations.tsv
```

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

