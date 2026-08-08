using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class RefreshTaskServiceTest
	{
		private readonly DateTime _now = new DateTime(2026, 6, 22, 9, 0, 0);
		private string _tempDirectory;
		private ProcessStartInfo _lastStartInfo;
		private int _startCount;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionTests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(_tempDirectory);
		}

		[TestCleanup]
		public void Cleanup()
		{
			if (Directory.Exists(_tempDirectory))
			{
				Directory.Delete(_tempDirectory, true);
			}
		}

		[TestMethod]
		public void Paths_AreResolvedUnderDataDirectory()
		{
			var service = CreateService(false);

			Assert.AreEqual(Path.GetFullPath(_tempDirectory), service.DataDirectory);
			Assert.AreEqual(Path.Combine(_tempDirectory, "Tools"), service.ToolsDirectory);
			Assert.AreEqual(
				Path.Combine(_tempDirectory, "Tools", "Run-MetaCompanionRefresh.ps1"),
				service.RefreshScriptPath);
			Assert.AreEqual(
				Path.Combine(_tempDirectory, "Tools", "Install-MetaCompanionRefreshTask.ps1"),
				service.InstallScriptPath);
			Assert.AreEqual(Path.Combine(_tempDirectory, "Logs"), service.LogDirectory);
		}

		[TestMethod]
		public void Inspect_NoTools_DegradesSafely()
		{
			var snapshot = CreateService(false).Inspect();

			Assert.IsFalse(snapshot.ToolsDirectoryExists);
			Assert.IsFalse(snapshot.RefreshScriptExists);
			Assert.IsFalse(snapshot.InstallScriptExists);
			Assert.AreEqual(
				"可选自动刷新组件未安装；不影响现有数据、预测和实战建议。",
				snapshot.ToolsStatus);
			Assert.AreEqual(
				"可选自动刷新组件未安装；不影响现有数据、预测和实战建议。",
				snapshot.ScheduledTaskStatus);
			Assert.AreEqual(
				"最近刷新日志: 未找到（可选自动刷新组件未安装）",
				snapshot.LatestLogStatus);
			Assert.IsTrue(snapshot.OptionalRefreshComponentsNotInstalled);
			Assert.IsFalse(snapshot.CanInstallTask);
			Assert.IsFalse(snapshot.CanRunRefresh);
			Assert.IsFalse(snapshot.CanOpenLatestLog);
		}

		[TestMethod]
		public void Inspect_TaskInstalledButNoTools_ShowsSplitState()
		{
			var snapshot = CreateService(true).Inspect();

			Assert.AreEqual(
				"高级自动刷新组件不完整，请安装完整组件。",
				snapshot.ToolsStatus);
			Assert.AreEqual(
				"自动刷新计划仍存在，但高级刷新组件不完整。",
				snapshot.ScheduledTaskStatus);
			Assert.IsFalse(snapshot.OptionalRefreshComponentsNotInstalled);
			Assert.IsFalse(snapshot.CanInstallTask);
			Assert.IsFalse(snapshot.CanRunRefresh);
		}

		[TestMethod]
		public void Inspect_ToolsPresentButNoScheduledTask_ShowsNotInstalled()
		{
			WriteToolScripts();

			var snapshot = CreateService(false).Inspect();

			Assert.IsTrue(snapshot.ToolsDirectoryExists);
			Assert.IsTrue(snapshot.RefreshScriptExists);
			Assert.IsTrue(snapshot.InstallScriptExists);
			Assert.AreEqual("高级刷新脚本已安装", snapshot.ToolsStatus);
			Assert.AreEqual("自动刷新未安装", snapshot.ScheduledTaskStatus);
			Assert.IsTrue(snapshot.CanInstallTask);
			Assert.IsTrue(snapshot.CanRunRefresh);
		}

		[TestMethod]
		public void Inspect_CommunityToolsAndMissingTask_AreOptionalNoticeWithoutWarning()
		{
			WriteFile(Path.Combine("Tools", "Sync-BlizzardCardPools.ps1"), "");
			WriteFile(Path.Combine("Tools", "Sync-HdtArenaAdvisorData.ps1"), "");
			var service = new RefreshTaskService(
				_tempDirectory,
				(Func<string, RefreshScheduledTaskInfo>)(name =>
				{
					throw new FileNotFoundException("The system cannot find the file specified.");
				}),
				StartProcess);
			Log.Info("community refresh inspection sentinel");
			var logLineBeforeInspect = Log.PrevLine;

			var snapshot = service.Inspect();
			var toolsStatus = BuildSettingsRefreshStatus("BuildRefreshToolsStatus", snapshot);
			var taskStatus = BuildSettingsRefreshStatus(
				"BuildRefreshScheduledTaskStatus",
				snapshot);
			var latestStatus = BuildSettingsRefreshStatus("BuildRefreshLatestStatus", snapshot);

			Assert.IsTrue(snapshot.ToolsDirectoryExists);
			Assert.IsFalse(snapshot.RefreshScriptExists);
			Assert.IsFalse(snapshot.InstallScriptExists);
			Assert.IsFalse(snapshot.ScheduledTaskInstalled);
			Assert.AreEqual("", snapshot.ScheduledTaskError);
			Assert.IsTrue(snapshot.OptionalRefreshComponentsNotInstalled);
			Assert.AreEqual(logLineBeforeInspect, Log.PrevLine);
			foreach (var status in new[] { toolsStatus, taskStatus, latestStatus })
			{
				StringAssert.StartsWith(status, "提示：");
				StringAssert.Contains(status, "可选自动刷新组件未安装");
				StringAssert.Contains(status, "不影响现有数据、预测和实战建议");
				Assert.IsFalse(status.Contains("需处理"));
				Assert.IsFalse(status.Contains("失败"));
				Assert.IsFalse(status.Contains("重新安装"));
				Assert.IsFalse(status.Contains("立即刷新"));
			}
			Assert.IsFalse(snapshot.CanInstallTask);
			Assert.IsFalse(snapshot.CanRunRefresh);
		}

		[TestMethod]
		public void ScheduledTaskNotFound_UsesWindowsHResultAndRejectsOtherFailures()
		{
			Assert.IsTrue(RefreshTaskService.IsScheduledTaskNotFound(
				new FileNotFoundException("localized missing task text")));
			Assert.IsTrue(RefreshTaskService.IsScheduledTaskNotFound(
				new COMException("different localized text", unchecked((int)0x80070002))));
			Assert.IsFalse(RefreshTaskService.IsScheduledTaskNotFound(
				new UnauthorizedAccessException("access denied")));
			Assert.IsFalse(RefreshTaskService.IsScheduledTaskNotFound(null));
		}

		[TestMethod]
		public void Inspect_PartialRefreshScripts_RemainActionRequired()
		{
			WriteFile(Path.Combine("Tools", "Run-MetaCompanionRefresh.ps1"), "");

			var snapshot = CreateService(false).Inspect();

			Assert.IsFalse(snapshot.OptionalRefreshComponentsNotInstalled);
			Assert.IsFalse(snapshot.RefreshScriptsComplete);
			foreach (var status in new[]
			{
				BuildSettingsRefreshStatus("BuildRefreshToolsStatus", snapshot),
				BuildSettingsRefreshStatus("BuildRefreshScheduledTaskStatus", snapshot),
				BuildSettingsRefreshStatus("BuildRefreshLatestStatus", snapshot)
			})
			{
				StringAssert.StartsWith(status, "需处理：");
				StringAssert.Contains(status, "组件不完整");
			}
		}

		[TestMethod]
		public void Inspect_ScheduledTaskPointingElsewhere_AsksForReinstall()
		{
			WriteToolScripts();
			var service = new RefreshTaskService(
				_tempDirectory,
				name => new RefreshScheduledTaskInfo
				{
					Installed = true,
					Arguments = "-NoProfile -File \"C:\\Old\\Run-MetaCompanionRefresh.ps1\""
				},
				StartProcess);

			var snapshot = service.Inspect();

			Assert.IsTrue(snapshot.ScheduledTaskInstalled);
			Assert.IsTrue(snapshot.ScheduledTaskActionKnown);
			Assert.IsFalse(snapshot.ScheduledTaskUsesInstalledScript);
			Assert.AreEqual("自动刷新已安装，但指向旧脚本路径；请重新安装", snapshot.ScheduledTaskStatus);
		}

		[TestMethod]
		public void Inspect_SelectsNewestRefreshLogWithoutDisplayingRawTail()
		{
			WriteToolScripts();
			var oldLog = WriteLog(
				"refresh-20260621-080500.log",
				"old log",
				_now.AddDays(-1));
			var newLog = WriteLog(
				"refresh-20260622-080500.log",
				string.Join(Environment.NewLine, Enumerable.Range(1, 10)
					.Select(index => index == 8 ? "Cookie: secret-cookie-value" : "line " + index)),
				_now);

			var snapshot = CreateService(true).Inspect();
			var summary = string.Join("\n", snapshot.LatestLogSummaryLines.ToArray());

			Assert.AreEqual(newLog, snapshot.LatestLogPath);
			Assert.AreNotEqual(oldLog, snapshot.LatestLogPath);
			Assert.AreEqual(_now, snapshot.LatestLogTime);
			Assert.AreEqual("最近刷新日志: 2026-06-22 09:00（状态未知）", snapshot.LatestLogStatus);
			Assert.AreEqual(1, snapshot.LatestLogSummaryLines.Count);
			StringAssert.Contains(summary, "尚未确认刷新结果");
			Assert.IsFalse(summary.Contains("line 3"));
			Assert.IsFalse(summary.Contains("Cookie"));
			Assert.IsFalse(summary.Contains("secret-cookie-value"));
			Assert.IsTrue(snapshot.CanOpenLatestLog);
		}

		[TestMethod]
		public void Inspect_HtmlFailureDoesNotDisplayRawLogBody()
		{
			WriteToolScripts();
			var htmlError =
				"PS>TerminatingError(): \"HSReplay 返回 HTTP 403：list_decks_by_win_rate_v2。" +
				"响应：<!DOCTYPE html><html><head><title>Just a moment...</title></head>" +
				"<body><script src=\"https://challenges.cloudflare.com/test.js\"></script></body></html>\"";
			WriteLog("refresh-20260622-080500.log", htmlError, _now);

			var snapshot = CreateService(true).Inspect();
			var summary = string.Join("\n", snapshot.LatestLogSummaryLines.ToArray());

			StringAssert.Contains(summary, "最近一次数据刷新失败");
			StringAssert.Contains(summary, "打开刷新日志");
			StringAssert.Contains(summary, "可能含英文技术信息");
			Assert.AreEqual("最近刷新日志: 2026-06-22 09:00（失败）", snapshot.LatestLogStatus);
			Assert.IsTrue(snapshot.LatestLogFailed);
			Assert.IsFalse(snapshot.LatestLogSucceeded);
			Assert.IsFalse(summary.Contains("HSReplay"));
			Assert.IsFalse(summary.Contains("HTTP 403"));
			Assert.IsFalse(summary.Contains("Cloudflare"));
			Assert.IsFalse(summary.Contains("<!DOCTYPE html"));
			Assert.IsFalse(summary.Contains("<script"));
			Assert.IsFalse(summary.Contains("challenges.cloudflare.com"));
			var userStatus = BuildSettingsRefreshStatus("BuildRefreshLatestStatus", snapshot);
			StringAssert.StartsWith(userStatus, "需处理：");
			StringAssert.Contains(userStatus, "最近一次数据刷新失败");
			StringAssert.Contains(userStatus, "打开开发者刷新日志");
		}

		[TestMethod]
		public void Inspect_DeferredOutcomeTreatsCaughtProducerErrorsAsWaitingForUpstream()
		{
			WriteToolScripts();
			WriteLog(
				"refresh-20260805-210505.log",
				"PS>TerminatingError(): stale producer response" + Environment.NewLine +
				"META_COMPANION_REFRESH_OUTCOME=DEFERRED" + Environment.NewLine +
				"Windows PowerShell transcript end",
				_now);

			var snapshot = CreateService(true).Inspect();
			var summary = string.Join("\n", snapshot.LatestLogSummaryLines.ToArray());

			Assert.IsTrue(snapshot.LatestLogDeferred);
			Assert.IsFalse(snapshot.LatestLogFailed);
			Assert.IsFalse(snapshot.LatestLogSucceeded);
			StringAssert.Contains(snapshot.LatestLogStatus, "等待上游数据");
			StringAssert.Contains(summary, "HSReplay 当前补丁数据仍在生成");
			Assert.IsFalse(summary.Contains("TerminatingError"));
			Assert.IsFalse(summary.Contains("刷新失败"));
		}

		[TestMethod]
		public void Inspect_CleanLegacyDeferralOverridesCaughtProducerErrorsAfterTranscriptEnds()
		{
			WriteToolScripts();
			WriteLog(
				"refresh-20260805-210505.log",
				"PS>TerminatingError(): stale producer response" + Environment.NewLine +
				"WARNING: HSReplay 数据仍在生成；本次停止分支与推荐刷新，请稍后重试。" +
				Environment.NewLine +
				"Windows PowerShell transcript end",
				_now);

			var snapshot = CreateService(true).Inspect();

			Assert.IsTrue(snapshot.LatestLogDeferred);
			Assert.IsFalse(snapshot.LatestLogFailed);
			Assert.IsFalse(snapshot.LatestLogSucceeded);
			StringAssert.Contains(snapshot.LatestLogStatus, "等待上游数据");
		}

		[TestMethod]
		public void Inspect_LogStatusMentionsMissingScriptWhenOnlyOldLogExists()
		{
			WriteLog(
				"refresh-20260622-080500.log",
				"远端缓存今天已经刷新完成，已跳过。如需强制刷新，请使用 -Force。" +
				Environment.NewLine +
				"Windows PowerShell 脚本结束",
				_now);

			var snapshot = CreateService(false).Inspect();

			Assert.AreEqual(
				"最近刷新日志: 2026-06-22 09:00（完成；可选自动刷新组件未安装）",
				snapshot.LatestLogStatus);
			Assert.IsTrue(snapshot.LatestLogSucceeded);
			Assert.IsFalse(snapshot.LatestLogFailed);
			var userStatus = BuildSettingsRefreshStatus("BuildRefreshLatestStatus", snapshot);
			StringAssert.StartsWith(userStatus, "提示：");
			StringAssert.Contains(userStatus, "可选自动刷新组件未安装");
			StringAssert.Contains(userStatus, "不影响现有数据、预测和实战建议");
			Assert.IsFalse(userStatus.Contains("需处理"));
			Assert.IsFalse(userStatus.Contains("重试"));
		}

		[TestMethod]
		public void StartRefreshNow_WithScript_StartsExternalPowerShellAndReturns()
		{
			WriteToolScripts();
			var service = CreateService(false);

			var result = service.StartRefreshNow();

			Assert.IsTrue(result.Started);
			Assert.AreEqual(1234, result.ProcessId);
			Assert.AreEqual(1, _startCount);
			Assert.IsNotNull(_lastStartInfo);
			StringAssert.Contains(_lastStartInfo.FileName, "powershell.exe");
			StringAssert.Contains(_lastStartInfo.Arguments, "-NoProfile");
			StringAssert.Contains(_lastStartInfo.Arguments, "-NoExit");
			StringAssert.Contains(_lastStartInfo.Arguments, "-ExecutionPolicy Bypass");
			StringAssert.Contains(_lastStartInfo.Arguments, "-File \"" + service.RefreshScriptPath + "\"");
			StringAssert.Contains(_lastStartInfo.Arguments, "-DataDirectory \"" + service.DataDirectory + "\"");
			Assert.AreEqual(service.ToolsDirectory, _lastStartInfo.WorkingDirectory);
			Assert.IsTrue(_lastStartInfo.UseShellExecute);
			Assert.IsFalse(_lastStartInfo.CreateNoWindow);
			Assert.IsFalse(_lastStartInfo.Arguments.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		[TestMethod]
		public void Inspect_LockedRefreshLog_ShowsFriendlyInProgressStatus()
		{
			WriteToolScripts();
			var path = WriteLog(
				"refresh-20260622-080500.log",
				"refresh still running",
				_now);

			using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
			{
				var snapshot = CreateService(true).Inspect();
				var summary = string.Join("\n", snapshot.LatestLogSummaryLines.ToArray());

				Assert.IsTrue(snapshot.LatestLogBusy);
				Assert.AreEqual("最近刷新日志: 2026-06-22 09:00（刷新中）", snapshot.LatestLogStatus);
				StringAssert.Contains(summary, "数据刷新正在进行");
				Assert.IsFalse(summary.Contains("日志摘要读取失败"));
			}
		}

		[TestMethod]
		public void StartInstallTask_WithScripts_RequestsElevationForScheduledTaskRegistration()
		{
			WriteToolScripts();
			var service = CreateService(false);

			var result = service.StartInstallTask();

			Assert.IsTrue(result.Started);
			Assert.AreEqual(1234, result.ProcessId);
			Assert.AreEqual(1, _startCount);
			Assert.IsNotNull(_lastStartInfo);
			StringAssert.Contains(_lastStartInfo.Arguments, "-File \"" + service.InstallScriptPath + "\"");
			StringAssert.Contains(_lastStartInfo.Arguments, "-DataDirectory \"" + service.DataDirectory + "\"");
			Assert.AreEqual("runas", _lastStartInfo.Verb);
			Assert.IsTrue(_lastStartInfo.UseShellExecute);
			Assert.IsFalse(_lastStartInfo.CreateNoWindow);
		}

		[TestMethod]
		public void StartRefreshNow_MissingScript_DoesNotStartProcess()
		{
			var result = CreateService(false).StartRefreshNow();

			Assert.IsFalse(result.Started);
			Assert.AreEqual(0, _startCount);
			StringAssert.Contains(result.Message, "立即刷新不可用");
			StringAssert.Contains(result.Message, "重新安装插件");
		}

		[TestMethod]
		public void SanitizeDiagnosticText_RedactsCredentialVariants()
		{
			var sanitized = RefreshTaskService.SanitizeDiagnosticText(
				"Cookie: cookie-secret Authorization: Bearer auth-secret " +
				"access_token=<token-value> api-key=key-secret Bearer loose-secret");

			StringAssert.Contains(sanitized, "Cookie=[redacted]");
			StringAssert.Contains(sanitized, "Authorization=[redacted]");
			StringAssert.Contains(sanitized, "access_token=[redacted]");
			StringAssert.Contains(sanitized, "api-key=[redacted]");
			StringAssert.Contains(sanitized, "Bearer [redacted]");
			Assert.IsFalse(sanitized.Contains("cookie-secret"));
			Assert.IsFalse(sanitized.Contains("auth-secret"));
			Assert.IsFalse(sanitized.Contains("<token-value>"));
			Assert.IsFalse(sanitized.Contains("key-secret"));
			Assert.IsFalse(sanitized.Contains("loose-secret"));
		}

		[TestMethod]
		public void StartRefreshNow_ProcessFailure_HidesTechnicalException()
		{
			WriteToolScripts();
			var service = new RefreshTaskService(
				_tempDirectory,
				name => false,
				startInfo =>
				{
					throw new InvalidOperationException(
						"Access denied at C:\\Users\\Player\\secret-token.txt");
				});

			var result = service.StartRefreshNow();

			Assert.IsFalse(result.Started);
			StringAssert.Contains(result.Message, "立即刷新失败");
			StringAssert.Contains(result.Message, "请确认 Windows 脚本组件可用后重试");
			Assert.IsFalse(result.Message.Contains("PowerShell"));
			Assert.IsFalse(result.Message.Contains("Access denied"));
			Assert.IsFalse(result.Message.Contains("secret-token"));
		}

		[TestMethod]
		public void Inspect_ScheduledTaskCheckerFailure_HidesTechnicalException()
		{
			WriteToolScripts();
			var service = new RefreshTaskService(
				_tempDirectory,
				(Func<string, RefreshScheduledTaskInfo>)(name =>
				{
					throw new InvalidOperationException("access denied to task scheduler");
				}),
				StartProcess);

			var snapshot = service.Inspect();

			StringAssert.Contains(snapshot.ScheduledTaskStatus, "读取自动刷新状态失败");
			StringAssert.Contains(snapshot.ScheduledTaskStatus, "刷新状态");
			Assert.IsFalse(snapshot.ScheduledTaskStatus.Contains("access denied"));
			Assert.IsFalse(snapshot.ScheduledTaskStatus.Contains("task scheduler"));
			StringAssert.StartsWith(
				BuildSettingsRefreshStatus("BuildRefreshScheduledTaskStatus", snapshot),
				"需处理：");
		}

		private static string BuildSettingsRefreshStatus(
			string methodName,
			RefreshTaskSnapshot snapshot)
		{
			var settingsType = typeof(PluginConfig).Assembly.GetType(
				"MetaCompanion.SettingsWindow",
				true);
			var method = settingsType.GetMethod(
				methodName,
				BindingFlags.Static | BindingFlags.NonPublic);
			Assert.IsNotNull(method);
			return (string)method.Invoke(null, new object[] { snapshot });
		}

		private RefreshTaskService CreateService(bool scheduledTaskExists)
		{
			return new RefreshTaskService(
				_tempDirectory,
				name => scheduledTaskExists,
				StartProcess);
		}

		private int StartProcess(ProcessStartInfo startInfo)
		{
			_startCount++;
			_lastStartInfo = startInfo;
			return 1234;
		}

		private void WriteToolScripts()
		{
			WriteFile(Path.Combine("Tools", "Run-MetaCompanionRefresh.ps1"), "");
			WriteFile(Path.Combine("Tools", "Install-MetaCompanionRefreshTask.ps1"), "");
		}

		private string WriteLog(string fileName, string contents, DateTime lastWriteTime)
		{
			var path = Path.Combine(_tempDirectory, "Logs", fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents, Encoding.UTF8);
			File.SetLastWriteTime(path, lastWriteTime);
			return path;
		}

		private void WriteFile(string relativePath, string contents)
		{
			var path = Path.Combine(_tempDirectory, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents, Encoding.UTF8);
			File.SetLastWriteTime(path, _now);
		}
	}
}
