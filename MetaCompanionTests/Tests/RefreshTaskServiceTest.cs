using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
			Assert.AreEqual("高级刷新脚本未安装", snapshot.ToolsStatus);
			Assert.AreEqual("自动刷新不可用: 缺少高级刷新脚本", snapshot.ScheduledTaskStatus);
			Assert.AreEqual("最近刷新日志: 未找到", snapshot.LatestLogStatus);
			Assert.IsFalse(snapshot.CanInstallTask);
			Assert.IsFalse(snapshot.CanRunRefresh);
			Assert.IsFalse(snapshot.CanOpenLatestLog);
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
		public void Inspect_SelectsNewestRefreshLogAndReadsSanitizedTail()
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
			Assert.AreEqual("最近刷新日志: 2026-06-22 09:00", snapshot.LatestLogStatus);
			Assert.AreEqual(8, snapshot.LatestLogSummaryLines.Count);
			StringAssert.Contains(summary, "line 3");
			StringAssert.Contains(summary, "Cookie=[redacted]");
			Assert.IsFalse(summary.Contains("secret-cookie-value"));
			Assert.IsTrue(snapshot.CanOpenLatestLog);
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
			StringAssert.Contains(_lastStartInfo.Arguments, "-ExecutionPolicy Bypass");
			StringAssert.Contains(_lastStartInfo.Arguments, "-File \"" + service.RefreshScriptPath + "\"");
			StringAssert.Contains(_lastStartInfo.Arguments, "-DataDirectory \"" + service.DataDirectory + "\"");
			Assert.AreEqual(service.ToolsDirectory, _lastStartInfo.WorkingDirectory);
			Assert.IsFalse(_lastStartInfo.UseShellExecute);
			Assert.IsTrue(_lastStartInfo.CreateNoWindow);
			Assert.IsFalse(_lastStartInfo.Arguments.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		[TestMethod]
		public void StartRefreshNow_MissingScript_DoesNotStartProcess()
		{
			var result = CreateService(false).StartRefreshNow();

			Assert.IsFalse(result.Started);
			Assert.AreEqual(0, _startCount);
			StringAssert.Contains(result.Message, "高级刷新脚本未安装");
		}

		[TestMethod]
		public void Inspect_ScheduledTaskCheckerFailure_ShowsFailureSummary()
		{
			WriteToolScripts();
			var service = new RefreshTaskService(
				_tempDirectory,
				name => { throw new InvalidOperationException("access denied to task scheduler"); },
				StartProcess);

			var snapshot = service.Inspect();

			StringAssert.Contains(snapshot.ScheduledTaskStatus, "自动刷新状态读取失败");
			StringAssert.Contains(snapshot.ScheduledTaskStatus, "access denied");
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
