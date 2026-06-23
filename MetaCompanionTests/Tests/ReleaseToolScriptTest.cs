using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class ReleaseToolScriptTest
	{
		[TestMethod]
		public void InvokeReleaseGate_SelfTestPasses()
		{
			var repoRoot = FindRepoRoot();
			var result = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
				"-SelfTest");

			Assert.AreEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "SELFTEST OK");
		}

		[TestMethod]
		public void InvokeHdtClientSmoke_ContainsManualCheckpointsAndDoesNotQueue()
		{
			var repoRoot = FindRepoRoot();
			var script = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Invoke-HdtClientSmoke.ps1"));

			StringAssert.Contains(script, "hdt-startup");
			StringAssert.Contains(script, "meta-deck-loading-state");
			StringAssert.Contains(script, "meta-deck-ready-state");
			StringAssert.Contains(script, "meta-deck-empty-state");
			StringAssert.Contains(script, "meta-deck-failed-state");
			StringAssert.Contains(script, "standard-game-start");
			StringAssert.Contains(script, "non-standard-not-enabled");
			StringAssert.Contains(script, "settings-data-health");
			StringAssert.Contains(script, "auto-refresh-entry");
			StringAssert.Contains(script, "copy-diagnostics");
			StringAssert.Contains(script, "recent-game-explanation");
			StringAssert.Contains(script, "correct-current-game");
			StringAssert.Contains(script, "correction-refresh");
			StringAssert.Contains(script, "meta_deck_load_status.tsv");
			StringAssert.Contains(script, "config-xml-unchanged");
			StringAssert.Contains(script, "Protect-SmokeText");
			StringAssert.Contains(script, "Test-SmokeSensitiveText");
			StringAssert.Contains(script, "Read-SmokeCheckpoint");
			StringAssert.Contains(script, "RequireManualPass");
			StringAssert.Contains(script, "Resolve-SmokeOverallResult");
			StringAssert.Contains(script, "Convert-SmokeCheckpointAnswer");
			StringAssert.Contains(script, "MANUAL_PENDING");
			Assert.IsFalse(script.Contains("Start Ranked"), script);
			Assert.IsFalse(script.Contains("Click Play"), script);
		}

		[TestMethod]
		public void InvokeHdtClientSmoke_SelfTestCoversOverallResultSemantics()
		{
			var repoRoot = FindRepoRoot();
			var result = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Invoke-HdtClientSmoke.ps1"),
				"-SelfTest");

			Assert.AreEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "all pass => PASS");
			StringAssert.Contains(result.Output, "manual pending => MANUAL_PENDING");
			StringAssert.Contains(result.Output, "fail => FAIL");
			StringAssert.Contains(result.Output, "RequireManualPass + manual => exit 1");
			StringAssert.Contains(result.Output, "manual y => PASS and n => FAIL");
		}

		[TestMethod]
		public void RefreshTask_IsExternalDailyDetector()
		{
			var repoRoot = FindRepoRoot();
			var refreshScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Run-MetaCompanionRefresh.ps1"));
			var installScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Install-MetaCompanionRefreshTask.ps1"));

			StringAssert.Contains(refreshScript, "Test-RemoteCacheRefreshedToday");
			StringAssert.Contains(refreshScript, "selected_time_range");
			StringAssert.Contains(refreshScript, "CURRENT_PATCH");
			Assert.IsFalse(refreshScript.Contains("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS"));
			StringAssert.Contains(refreshScript, "Remote cache already refreshed today");
			StringAssert.Contains(installScript, "Meta Companion Remote Cache Refresh");
			StringAssert.Contains(installScript, "Run-MetaCompanionRefresh.ps1");
			StringAssert.Contains(installScript, "New-ScheduledTaskTrigger -AtLogOn");
			StringAssert.Contains(installScript, "LogonDelayMinutes");
			StringAssert.Contains(installScript, "StartWhenAvailable");
			StringAssert.Contains(installScript, "Meta Companion Daily Refresh");
		}

		[TestMethod]
		public void OneClickRefreshScripts_PinCurrentPatchTimeRange()
		{
			var repoRoot = FindRepoRoot();
			var oneClickDir = Path.Combine(repoRoot, "\u4e00\u952e\u811a\u672c");
			var smartRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "04 *.cmd")[0]);
			var forceRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "05 *.cmd")[0]);
			var localRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "07 *.cmd")[0]);

			StringAssert.Contains(smartRefresh, "-PrimaryTimeRange CURRENT_PATCH");
			StringAssert.Contains(smartRefresh, "-MetaFallbackTimeRange CURRENT_PATCH");
			StringAssert.Contains(forceRefresh, "-PrimaryTimeRange CURRENT_PATCH");
			StringAssert.Contains(forceRefresh, "-MetaFallbackTimeRange CURRENT_PATCH");
			StringAssert.Contains(localRefresh, "-MetaTimeRange CURRENT_PATCH");

			foreach (var scriptPath in Directory.GetFiles(oneClickDir, "*.cmd"))
			{
				var script = File.ReadAllText(scriptPath);
				Assert.IsFalse(script.Contains("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS"), scriptPath);
				Assert.IsFalse(script.Contains("LAST_3_DAYS"), scriptPath);
			}
		}

		[TestMethod]
		public void LocalMetaScripts_UseFullCurrentPatchHdtHistory()
		{
			var repoRoot = FindRepoRoot();
			var exportScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Export-HdtOpponentHistory.ps1"));
			var updateScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Update-MetaCompanionData.ps1"));

			StringAssert.Contains(exportScript, "DefaultDeckStats.xml");
			StringAssert.Contains(exportScript, "Select-Object -Unique");
			StringAssert.Contains(updateScript, "$historyExportArgs.Since = $effectivePatchTime");
			StringAssert.Contains(updateScript, "$MetaTimeRange -eq \"CURRENT_PATCH\"");
		}

		[TestMethod]
		public void TestRunner_SandboxesHdtAppDataAndGuardsRealConfig()
		{
			var repoRoot = FindRepoRoot();
			var runner = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Run-Tests.ps1"));

			StringAssert.Contains(runner, "Set-HdtTestAppDataPath");
			StringAssert.Contains(runner, "Assert-RealHdtConfigUnchanged");
			StringAssert.Contains(runner, "AppDataPath");
			StringAssert.Contains(runner, "Get-FileHash");
			StringAssert.Contains(runner, "MetaCompanionTests-");
		}

		[TestMethod]
		public void ReleaseChecklist_DocumentsCommunityPackageAndNoPremiumRequirement()
		{
			var repoRoot = FindRepoRoot();
			var checklist = File.ReadAllText(
				Path.Combine(repoRoot, "docs", "RELEASE-CHECKLIST.md"));

			StringAssert.Contains(checklist, "tools\\*.ps1");
			StringAssert.Contains(checklist, "hsreplay_cookie.txt");
			StringAssert.Contains(checklist, "普通社区用户只有 DLL");
			StringAssert.Contains(checklist, "没有会员或历史");
		}

		private static ProcessResult RunPowerShell(string workingDirectory, string scriptPath, string arguments)
		{
			var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			var powerShell = Path.Combine(
				windowsDirectory,
				"System32",
				"WindowsPowerShell",
				"v1.0",
				"powershell.exe");
			if (!File.Exists(powerShell))
			{
				powerShell = "powershell.exe";
			}

			var startInfo = new ProcessStartInfo
			{
				FileName = powerShell,
				Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" " + arguments,
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};

			using (var process = Process.Start(startInfo))
			{
				var output = process.StandardOutput.ReadToEnd() +
					Environment.NewLine +
					process.StandardError.ReadToEnd();
				process.WaitForExit();
				return new ProcessResult(process.ExitCode, output);
			}
		}

		private static string FindRepoRoot()
		{
			var candidates = new[]
			{
				Path.GetDirectoryName(typeof(ReleaseToolScriptTest).Assembly.Location),
				AppDomain.CurrentDomain.BaseDirectory,
				Directory.GetCurrentDirectory()
			};
			foreach (var candidate in candidates)
			{
				var directory = new DirectoryInfo(candidate);
				while (directory != null)
				{
					if (File.Exists(Path.Combine(directory.FullName, "MetaCompanion.sln")) &&
						Directory.Exists(Path.Combine(directory.FullName, "tools")))
					{
						return directory.FullName;
					}
					directory = directory.Parent;
				}
			}
			throw new DirectoryNotFoundException("Could not find repository root.");
		}

		private class ProcessResult
		{
			public ProcessResult(int exitCode, string output)
			{
				ExitCode = exitCode;
				Output = output;
			}

			public int ExitCode { get; private set; }
			public string Output { get; private set; }
		}
	}
}
