# 项目目录说明

`Meta Companion` 以当前仓库为项目根，和 `F:\Workspace` 下的其他实验目录分开管理。

## 源码

- `MetaCompanion/`  
  HDT 插件源码。包含预测逻辑、覆盖层 UI、设置窗口、日志、对局历史、赛后浮窗和本地推荐读取。

- `MetaCompanionTests/`  
  单元测试。覆盖预测过滤、后期剩余牌、多来源已知牌扣减、赛后数据读取、配置保存等行为。

- `Images/`  
  文档图片。

## 工具

- `tools/`  
  PowerShell 工具源码。用于同步 HSReplay 数据、导出 HDT 历史、计算本地环境、生成推荐、安装插件和健康监控。

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
- `match_history.tsv`、`prediction_timeline.tsv`、`hdt_opponent_history.tsv`
- `local_meta_*.tsv`、`local_meta_*.json`
- `recommendations.tsv`、`personal_recommendations.tsv`
- `anomalies.tsv`、`patch_marker.txt`

## HDT 运行时路径

- `%AppData%\HearthstoneDeckTracker\MetaCompanion`：插件数据、日志、Premium 缓存、本地环境、推荐结果和运行时工具。
- `%AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion`：已安装插件 DLL。
- `%AppData%\HearthstoneDeckTracker\DeckStats.xml`、`%AppData%\HearthstoneDeckTracker\DefaultDeckStats.xml`：HDT 原生对局历史。
- `%AppData%\HearthstoneDeckTracker\Replays`：本地 `.hdtreplay` 录像。

