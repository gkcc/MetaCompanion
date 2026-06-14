# 社区发布检查清单

这份清单用于 NGA/社区首发前的发布门禁。目标是确认插件稳定、不泄露个人数据、不在实际对局中打扰玩家。

## 自动门禁

1. 运行发布门禁：

   ```powershell
   .\tools\Invoke-ReleaseGate.ps1
   ```

2. 确认报告结果为 `PASS`：

   ```text
   artifacts\release-gate\<timestamp>\release-gate.md
   ```

3. 必须满足：
   - Release x86 构建通过。
   - `tools\Run-Tests.ps1` 全部通过。
   - 敏感信息扫描 0 命中。
   - 社区 zip 包不包含 `tools\*.ps1`、cookie、Premium 缓存、本地对局历史、`bin/obj`。
   - 报告中记录 DLL 和 zip 的 SHA256。

## 真实客户端烟测

1. 运行半自动烟测：

   ```powershell
   .\tools\Invoke-HdtClientSmoke.ps1 -LaunchHearthstone
   ```

2. 按脚本提示逐项确认：
   - 标准对战选套牌界面显示卡组流派推荐。
   - 面板可以关闭、拖动，鼠标提示可见。
   - 进入实际对局后，标准环境面板自动隐藏。
   - 前几张牌后，HDT 原生内嵌预测稳定显示。
   - “剩余卡牌预测”只在牌库剩余阈值附近触发，不因预测牌数量过早弹出。
   - 剩余卡牌面板可拖动，重启 HDT 后位置保留。
   - 赛后本地历史刷新，更新时间和样本数变化合理。

3. 保存烟测报告：

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

普通社区用户只有 DLL 时也应能正常加载。没有会员或历史时，推荐数据可以为空，但不能崩溃、不能卡住 HDT、不能要求用户提供 cookie。

## 社区发布包

社区包默认只包含：

- `MetaCompanion.dll`
- `README.md`
- `LICENSE`
- `NOTICE.md`
- 必要说明文档

社区包默认不包含：

- `tools\*.ps1`
- `hsreplay_cookie.txt`
- `Premium\`
- `local_meta_*.tsv`
- `match_history.tsv`
- `hdt_opponent_history.tsv`
- `prediction_timeline.tsv`
- `bin\` / `obj\`
- 证书、发布配置或任何个人账号数据

开发者或高级用户需要数据刷新脚本时，应从源码仓库查看说明，不通过普通社区包自动下发。

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
