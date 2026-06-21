using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class SettingsWindowTest
	{
		[TestMethod]
		public void BuildDiagnosticText_IncludesStatusesAndSanitizesCookieValues()
		{
			var dataHealth = new MetaDataHealthSnapshot
			{
				UserMessage = "数据可用",
				DetailLines = new List<string>
				{
					"HSReplay 牌组库: 1 套",
					"Premium Cookie 已配置",
					"Cookie: should-not-copy"
				}
			};
			var refreshTask = new RefreshTaskSnapshot
			{
				ToolsStatus = "高级刷新脚本已安装",
				ScheduledTaskStatus = "自动刷新已安装",
				LatestLogStatus = "最近刷新日志: 2026-06-22 09:00",
				LatestLogPath = @"C:\MetaCompanion\Logs\refresh-20260622.log",
				LatestLogSummaryLines = new List<string>
				{
					"line 1",
					"Cookie: secret-cookie-value"
				}
			};

			var text = SettingsDiagnostics.BuildDiagnosticText(
				new DateTime(2026, 6, 22, 9, 5, 0),
				@"C:\MetaCompanion",
				@"C:\MetaCompanion\Logs",
				"数据源: 可用",
				"推荐结果: Top 3",
				"对阵矩阵: 已同步",
				dataHealth,
				refreshTask);

			StringAssert.Contains(text, "Meta Companion 诊断信息");
			StringAssert.Contains(text, "[数据健康]");
			StringAssert.Contains(text, "HSReplay 牌组库: 1 套");
			StringAssert.Contains(text, "[自动刷新]");
			StringAssert.Contains(text, "高级刷新脚本已安装");
			StringAssert.Contains(text, "[最近刷新日志摘要]");
			StringAssert.Contains(text, "line 1");
			StringAssert.Contains(text, "Cookie=[redacted]");
			Assert.IsFalse(text.Contains("secret-cookie-value"));
			Assert.IsFalse(text.Contains("should-not-copy"));
		}
	}
}
