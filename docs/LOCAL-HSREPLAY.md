# 本地 HSReplay 数据说明

Meta Companion 不在 HDT 进程里直接抓网页。数据同步放在 `tools/` 脚本里完成，插件启动时只读取本地快照，降低对局中卡顿和网络失败的风险。

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

最近 3 天、标准天梯、钻石到传说的形态热度和对阵矩阵：

```powershell
.\tools\Sync-HSReplayMetaData.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -TimeRange LAST_3_DAYS `
  -TopOverall 20 `
  -TopPerClass 5
```

输出目录：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\Premium\Meta\latest
```

## 代表分支兜底

```powershell
.\tools\Sync-HSReplayArchetypeDecks.ps1 `
  -CookiePath "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt" `
  -BranchesPerArchetype 5 `
  -MinGames 100
```

输出：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\archetype_deck_branches.tsv
```

默认识别不再依赖这个文件。插件优先使用 `hsreplay_deckcodes.txt`，只有牌组快照缺失时才把这些代表分支作为旧数据兜底。自动刷新默认也不生成分支；需要时可手动给刷新入口加 `-Branches` 或给计划任务加 `-IncludeBranches`。

## 推荐生成

远端环境推荐：

```powershell
.\tools\Get-MetaArchetypeRecommendations.ps1 -Top 20 -IncludeClassTop
```

个人加权推荐：

```powershell
.\tools\Update-MetaCompanionData.ps1 -LocalMeta -PersonalRecommendations
```

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
- 已安装 `MetaCompanion.dll` 哈希。
- 插件日志和 HDT 日志里的错误。
- 预测快照是否异常膨胀。
- 本地环境统计是否过期。

