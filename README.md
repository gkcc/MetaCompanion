# Meta Companion for Hearthstone Deck Tracker

Meta Companion 是一个个人向 HDT 插件，目标是把标准模式环境识别、对手形态预测、剩余卡牌预测、赛后总结和个人牌组形态推荐整合到一个本地工具里。

## 主要能力

- 从 HSReplay / HSReplay Premium 同步标准模式牌组库、形态热度和对阵矩阵。
- 根据 HDT 对局事件和本地历史识别对手可能的形态。
- 前期把预测结果嵌入 HDT 原生对手牌组区域。
- 当原生列表会遮挡时切换到独立“剩余卡牌预测”面板，只显示最接近构筑里仍未出现的原始牌。
- 赛后显示常驻浮窗，汇总最近一局、近期对手分布和当前推荐形态。
- 结合网页端环境数据和本地最近对局，生成个人加权推荐。

## 常用路径

- 插件运行数据：`%AppData%\HearthstoneDeckTracker\MetaCompanion`
- HDT 插件目录：`%AppData%\HearthstoneDeckTracker\Plugins\MetaCompanion`
- 源码目录：`MetaCompanion/`
- 测试目录：`MetaCompanionTests/`
- 工具脚本：`tools/`
- 可复制脚本输出：`dist/`

目录说明见 [docs/PROJECT-STRUCTURE.md](docs/PROJECT-STRUCTURE.md)。

## 构建与测试

```powershell
$cscDir="$env:USERPROFILE\.nuget\packages\microsoft.net.compilers\4.2.0\tools"
& "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" .\MetaCompanion.sln /p:Configuration=Release /p:Platform=x86 /p:CscToolPath="$cscDir" /p:CscToolExe=csc.exe /p:LangVersion=latest /m /v:minimal
& "$env:WINDIR\SysWOW64\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .\tools\Run-Tests.ps1
```

安装到 HDT。普通安装只复制插件 DLL，不会安装自动同步脚本：

```powershell
.\tools\Install-MetaCompanion.ps1 -BuildPath .\MetaCompanion\bin\x86\Release\MetaCompanion.dll
```

开发或高级手动同步场景才需要把 `tools/` 复制到 HDT 数据目录：

```powershell
.\tools\Install-MetaCompanion.ps1 -BuildPath .\MetaCompanion\bin\x86\Release\MetaCompanion.dll -IncludeTools
```

如果 HDT 正在运行，使用等待式安装：

```powershell
.\tools\Wait-AndInstall-MetaCompanion.ps1
```

## 开发者数据刷新

社区发布包默认不附带自动刷新脚本；下面的入口仅用于本地开发、维护数据快照或高级用户手动同步：

```powershell
.\tools\Update-MetaCompanionData.ps1 -PersonalRecommendations
```

Premium 数据需要把自己的 HSReplay 登录 Cookie 放到：

```text
%AppData%\HearthstoneDeckTracker\MetaCompanion\hsreplay_cookie.txt
```

不要提交 Cookie、Premium 缓存或本地对局历史。

## 来源与许可

Meta Companion 是独立插件项目，但保留原开源项目和参考实现的许可与致谢。详见 [NOTICE.md](NOTICE.md)。

本仓库保留 MIT License。使用插件需自行承担游戏规则和第三方服务条款风险。

