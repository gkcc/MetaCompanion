# 实现状态

更新时间：2026-06-12。

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

## 验证

- 构建命令：`MetaCompanion.sln` Release x86。
- 测试命令：`tools\Run-Tests.ps1`。
- 当前测试覆盖：预测、后期剩余牌、原始牌多来源扣减、配置保存、赛后数据读取、推荐读取。
- 当前 DLL SHA256：`6C5FC6B0CACF150FC69CF1D7EFFD8CAF0A9E992136982345CDE30381509FE48F`。

## 手动验收

- 重启 HDT，插件列表应显示 `Meta Companion`，不应再同时出现旧插件。
- 标准天梯开局后，前期预测仍嵌入 HDT 原生对手牌组区域。
- 长局进入后期面板后，已见原始牌不应再出现在未见牌列表里。
- 非传奇最多显示 2 张，传奇最多显示 1 张。
- 拖动后期面板和赛后浮窗后，刷新、回合切换、回菜单都不应回到左上角。
- 对局结束后赛后浮窗应保持可见，直到手动关闭或下一局开始。
- 有 HSReplay 上传链接时可打开网页；没有上传链接时可打开本地 `.hdtreplay`。



