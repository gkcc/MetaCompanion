using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
			var script = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"));
			var result = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
				"-SelfTest");

			Assert.AreEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "SELFTEST OK");
			StringAssert.Contains(script, "Test-ReleaseGateRepoScanPath");
			StringAssert.Contains(script, "Package paths should be normalized.");
			StringAssert.Contains(script, "Tracked-file source:");
			StringAssert.Contains(script, "Git dirty files:");
			StringAssert.Contains(script, "Duration seconds:");
			StringAssert.Contains(script, "## Inputs");
			StringAssert.Contains(script, "Build log warnings should be flagged.");
			StringAssert.Contains(script, "Passing test names should not be log issues.");
			StringAssert.Contains(script, "Test result summary should parse.");
			StringAssert.Contains(script, "Missing test result summary should be unavailable.");
			StringAssert.Contains(script, "Test result summary was not found in test log.");
			StringAssert.Contains(script, "Repo scan fallback should skip packages.");
			StringAssert.Contains(script, "Repo scan fallback should skip IDE state.");
			StringAssert.Contains(script, "Repo scan fallback should skip artifacts.");
			StringAssert.Contains(script, "Repo scan fallback should include tracked dist installer scripts.");
			StringAssert.Contains(script, "Repo scan fallback should skip dist build outputs.");
		}

		[TestMethod]
		public void EnsureRoslynCompiler_UsesRepoPackageWhenUserProfileIsMissing()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionRoslynTest-" + Guid.NewGuid().ToString("N"));
			var fakeCsc = Path.Combine(
				tempRoot,
				"Microsoft.Net.Compilers.4.2.0",
				"tools",
				"csc.exe");
			Directory.CreateDirectory(Path.GetDirectoryName(fakeCsc));
			File.WriteAllText(fakeCsc, string.Empty);

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Ensure-RoslynCompiler.ps1"),
					"-PackagesDirectory \"" + tempRoot + "\" -Quiet",
					new Dictionary<string, string> { { "USERPROFILE", string.Empty } });

				Assert.AreEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, fakeCsc);
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void AnyCpuProjectConfigurations_SuppressKnownHdtArchitectureWarnings()
		{
			var repoRoot = FindRepoRoot();
			var projectPaths = new[]
			{
				Path.Combine(repoRoot, "MetaCompanion", "MetaCompanion.csproj"),
				Path.Combine(repoRoot, "MetaCompanionTests", "MetaCompanionTests.csproj")
			};

			foreach (var projectPath in projectPaths)
			{
				var project = File.ReadAllText(projectPath);
				AssertProjectConfigurationContains(
					project,
					"Debug|AnyCPU",
					"<ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch>None</ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch>");
				AssertProjectConfigurationContains(
					project,
					"Release|AnyCPU",
					"<ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch>None</ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch>");
			}
		}

		[TestMethod]
		public void BuildScript_MissingLocalAppDataReportsActionableHdtPathError()
		{
			var repoRoot = FindRepoRoot();
			var result = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Build-MetaCompanion.ps1"),
				string.Empty,
				new Dictionary<string, string> { { "LOCALAPPDATA", string.Empty } });

			Assert.AreNotEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "LOCALAPPDATA is not set");
			StringAssert.Contains(result.Output, "-HdtAppPath");
			Assert.IsFalse(result.Output.Contains("Cannot bind argument"), result.Output);
		}

		[TestMethod]
		public void ReleaseGate_SkipBuildWithPackagePathDoesNotRequireLocalAppData()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGatePackageAuditTest-" + Guid.NewGuid().ToString("N"));
			var packageRoot = Path.Combine(tempRoot, "package");
			var artifactsRoot = Path.Combine(tempRoot, "nested", "..", "artifacts");
			Directory.CreateDirectory(packageRoot);
			Directory.CreateDirectory(Path.Combine(tempRoot, "nested"));
			File.WriteAllText(Path.Combine(packageRoot, "README.md"), "package audit");

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipBuild -SkipTests -PackagePath \"" + packageRoot +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"",
					new Dictionary<string, string> { { "LOCALAPPDATA", string.Empty } });

				Assert.AreEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "RELEASE GATE PASS");
				Assert.IsFalse(result.Output.Contains("nested\\.."), result.Output);
				Assert.IsFalse(result.Output.Contains("LOCALAPPDATA is not set"), result.Output);
				Assert.IsFalse(result.Output.Contains("Cannot bind argument"), result.Output);

				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				Assert.IsFalse(report.Contains("nested\\.."), report);
				StringAssert.Contains(report, "- Started: ");
				StringAssert.Contains(report, "- Finished: ");
				StringAssert.Contains(report, "- Duration seconds: ");
				StringAssert.Contains(report, "- Git branch: ");
				StringAssert.Contains(report, "- Git commit: ");
				StringAssert.Contains(report, "- Git dirty files: ");
				StringAssert.Contains(report, "- Build: Skipped");
				StringAssert.Contains(report, "- Tests: Skipped");
				StringAssert.Contains(report, "- Failure count: 0");
				StringAssert.Contains(report, "- MSBuild: Not required");
				StringAssert.Contains(report, "- Test PowerShell: Not required");
				StringAssert.Contains(report, "- Roslyn: Not required");
				StringAssert.Contains(report, "- HDT app: Not required");
				StringAssert.Contains(report, "## Inputs");
				StringAssert.Contains(report, "- Solution: " + Path.Combine(repoRoot, "MetaCompanion.sln"));
				StringAssert.Contains(report, "- Artifacts directory: " + Path.GetFullPath(artifactsRoot));
				StringAssert.Contains(report, "- Requested package: " + packageRoot);
				StringAssert.Contains(report, "- Skip build: True");
				StringAssert.Contains(report, "- Skip tests: True");
				StringAssert.Contains(report, "## Logs");
				StringAssert.Contains(report, "- Build log: Not generated (build skipped)");
				StringAssert.Contains(report, "- Build log issues: 0");
				StringAssert.Contains(report, "- Test log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Test result: Skipped");
				StringAssert.Contains(report, "- Test log issues: 0");
				StringAssert.Contains(report, "- Build skipped");
				StringAssert.Contains(report, "- Type: Directory");
				StringAssert.Contains(report, "- Size bytes: Not applicable");
				StringAssert.Contains(report, "- SHA256: Not applicable");
				StringAssert.Contains(report, "- Entry count: 1");
				StringAssert.Contains(report, "- Blocked entries: 0");
				StringAssert.Contains(report, "- Tracked-file source: git ls-files");
				StringAssert.Contains(report, "- Tracked files scanned: ");
				StringAssert.Contains(report, "- Package files scanned: 1");
				StringAssert.Contains(report, "- Package matches: 0");
				StringAssert.Contains(report, "  - README.md");
				Assert.IsFalse(report.Contains("- DLL: "), report);
				Assert.IsFalse(report.Contains("- SHA256: " + Environment.NewLine), report);
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void ReleaseGate_DirectoryPackageReportsBlockedEntryCount()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGateBlockedPackageTest-" + Guid.NewGuid().ToString("N"));
			var packageRoot = Path.Combine(tempRoot, "package");
			var toolsRoot = Path.Combine(packageRoot, "tools");
			var artifactsRoot = Path.Combine(tempRoot, "artifacts");
			Directory.CreateDirectory(toolsRoot);

			try
			{
				File.WriteAllText(Path.Combine(toolsRoot, "Update-MetaCompanionData.ps1"), "Write-Host blocked");

				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipBuild -SkipTests -PackagePath \"" + packageRoot +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Blocked package entry: tools/Update-MetaCompanionData.ps1");

				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				StringAssert.Contains(report, "- Result: FAIL");
				StringAssert.Contains(report, "- Failure count: 1");
				StringAssert.Contains(report, "- Entry count: 1");
				StringAssert.Contains(report, "- Blocked entries: 1");
				StringAssert.Contains(report, "- Package files scanned: 1");
				StringAssert.Contains(report, "  - tools/Update-MetaCompanionData.ps1");
				StringAssert.Contains(report, "- Blocked package entry: tools/Update-MetaCompanionData.ps1");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void ReleaseGate_ZipPackageReportsEntryAndBlockedCounts()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGateZipPackageTest-" + Guid.NewGuid().ToString("N"));
			var sourceRoot = Path.Combine(tempRoot, "zip-source");
			var toolsRoot = Path.Combine(sourceRoot, "tools");
			var packagePath = Path.Combine(tempRoot, "MetaCompanion-community.zip");
			var artifactsRoot = Path.Combine(tempRoot, "artifacts");
			Directory.CreateDirectory(toolsRoot);

			try
			{
				File.WriteAllText(Path.Combine(sourceRoot, "z-last.txt"), "safe");
				File.WriteAllText(Path.Combine(toolsRoot, "Update-MetaCompanionData.ps1"), "Write-Host blocked");
				File.WriteAllText(Path.Combine(sourceRoot, "a-first.txt"), "safe");
				CreateZipPackage(repoRoot, sourceRoot, packagePath);

				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipBuild -SkipTests -PackagePath \"" + packagePath +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Blocked package entry: tools/Update-MetaCompanionData.ps1");

				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				StringAssert.Contains(report, "- Result: FAIL");
				StringAssert.Contains(report, "- Type: File");
				StringAssert.Contains(report, "- Size bytes: ");
				StringAssert.Contains(report, "- Entry count: 3");
				StringAssert.Contains(report, "- Blocked entries: 1");
				StringAssert.Contains(report, "- Package files scanned: 3");
				AssertContainsInOrder(
					report,
					"  - a-first.txt",
					"  - tools/Update-MetaCompanionData.ps1",
					"  - z-last.txt");
				StringAssert.Contains(report, "- Blocked package entry: tools/Update-MetaCompanionData.ps1");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void ReleaseGate_DirectoryPackageSecretScanContinuesAfterLargeFile()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGateLargeFileScanTest-" + Guid.NewGuid().ToString("N"));
			var packageRoot = Path.Combine(tempRoot, "package");
			var artifactsRoot = Path.Combine(tempRoot, "artifacts");
			Directory.CreateDirectory(packageRoot);

			try
			{
				File.WriteAllBytes(
					Path.Combine(packageRoot, "aaa-large.bin"),
					new byte[1048577]);
				File.WriteAllText(
					Path.Combine(packageRoot, "zzz-secret.txt"),
					"Bearer " + new string('b', 24));

				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipBuild -SkipTests -PackagePath \"" + packageRoot +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Sensitive value in package: zzz-secret.txt (Bearer token)");

				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				StringAssert.Contains(report, "- Result: FAIL");
				StringAssert.Contains(report, "- Entry count: 2");
				StringAssert.Contains(report, "- Package files scanned: 1");
				StringAssert.Contains(report, "  - aaa-large.bin");
				StringAssert.Contains(report, "  - zzz-secret.txt");
				StringAssert.Contains(report, "- Package matches: 1");
				StringAssert.Contains(report, "- Sensitive value in package: zzz-secret.txt (Bearer token)");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void ReleaseGate_BuildWithMissingLocalAppDataReportsActionableHdtPathError()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGateEnvTest-" + Guid.NewGuid().ToString("N"));
			var fakeCsc = Path.Combine(tempRoot, "tools", "csc.exe");
			var packageRoot = Path.Combine(tempRoot, "package");
			var artifactsRoot = Path.Combine(tempRoot, "artifacts");
			Directory.CreateDirectory(Path.GetDirectoryName(fakeCsc));
			Directory.CreateDirectory(packageRoot);
			File.WriteAllText(fakeCsc, string.Empty);

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipTests -CscToolPath \"" + fakeCsc +
					"\" -PackagePath \"" + packageRoot +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"",
					new Dictionary<string, string> { { "LOCALAPPDATA", string.Empty } });

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "LOCALAPPDATA is not set");
				StringAssert.Contains(result.Output, "Hearthstone Deck Tracker");
				Assert.IsFalse(result.Output.Contains("Cannot bind argument"), result.Output);

				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				StringAssert.Contains(report, "- Build: Release AnyCPU");
				StringAssert.Contains(report, "- Tests: Skipped");
				StringAssert.Contains(report, "- MSBuild: ");
				StringAssert.Contains(report, "- Test PowerShell: Not required");
				StringAssert.Contains(report, "- Roslyn: " + fakeCsc);
				StringAssert.Contains(report, "- HDT app: Not resolved");
				StringAssert.Contains(report, "- Build log: Not generated");
				StringAssert.Contains(report, "- Build log issues: 0");
				StringAssert.Contains(report, "- Test log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Test result: Skipped");
				StringAssert.Contains(report, "- Test log issues: 0");
				StringAssert.Contains(report, "LOCALAPPDATA is not set");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
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
			StringAssert.Contains(script, "Meta Companion HDT 客户端烟测报告");
			StringAssert.Contains(script, "总结果");
			StringAssert.Contains(script, "结果说明");
			StringAssert.Contains(script, "自动检查");
			StringAssert.Contains(script, "已安装 DLL");
			StringAssert.Contains(script, "插件状态");
			StringAssert.Contains(script, "HDT 配置保护");
			StringAssert.Contains(script, "牌组库加载状态");
			StringAssert.Contains(script, "关键数据文件");
			StringAssert.Contains(script, "人工检查项");
			StringAssert.Contains(script, "日志尾部");
			StringAssert.Contains(script, "失败项");
			StringAssert.Contains(script, "HDT 已启动");
			StringAssert.Contains(script, "确认 HDT 正常启动");
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
			StringAssert.Contains(refreshScript, "LAST_1_DAY");
			StringAssert.Contains(refreshScript, "summary.as_of");
			StringAssert.Contains(refreshScript, "Update-MetaCompanionPatchState.ps1");
			Assert.IsFalse(refreshScript.Contains("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS"));
			StringAssert.Contains(refreshScript, "Remote cache already refreshed today");
			StringAssert.Contains(refreshScript, "Premium/meta refresh failed; recalculating recommendations from existing cache.");
			StringAssert.Contains(installScript, "Meta Companion Remote Cache Refresh");
			StringAssert.Contains(installScript, "Run-MetaCompanionRefresh.ps1");
			StringAssert.Contains(installScript, "New-ScheduledTaskTrigger -AtLogOn");
			StringAssert.Contains(installScript, "LogonDelayMinutes");
			StringAssert.Contains(installScript, "StartWhenAvailable");
			StringAssert.Contains(installScript, "Meta Companion Daily Refresh");
		}

		[TestMethod]
		public void HealthMonitor_DoesNotPinBuildSpecificDllHash()
		{
			var repoRoot = FindRepoRoot();
			var scriptPath = Path.Combine(repoRoot, "tools", "Watch-MetaCompanionHealth.ps1");
			var script = File.ReadAllText(scriptPath);
			var docs = File.ReadAllText(Path.Combine(repoRoot, "docs", "LOCAL-HSREPLAY.md"));
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionHealthMonitorHashTest-" + Guid.NewGuid().ToString("N"));
			var dataRoot = Path.Combine(tempRoot, "Data");
			var hdtRoot = Path.Combine(tempRoot, "HDT");
			Directory.CreateDirectory(dataRoot);
			Directory.CreateDirectory(hdtRoot);

			try
			{
				StringAssert.Contains(script, "[string]$ExpectedDllHash = \"\"");
				Assert.IsFalse(
					System.Text.RegularExpressions.Regex.IsMatch(
						script,
						"ExpectedDllHash\\s*=\\s*\"[0-9A-Fa-f]{64}\""),
					"Health monitor should not pin a build-specific DLL hash.");
				StringAssert.Contains(docs, "默认不校验 DLL 哈希");
				StringAssert.Contains(docs, "-ExpectedDllHash");
				StringAssert.Contains(docs, "Build Artifact");

				var result = RunPowerShell(
					repoRoot,
					scriptPath,
					"-Once -DataRoot \"" + dataRoot + "\" -HdtRoot \"" + hdtRoot + "\"");

				Assert.AreEqual(0, result.ExitCode, result.Output);
				var anomaliesPath = Path.Combine(dataRoot, "anomalies.tsv");
				var anomalies = File.Exists(anomaliesPath)
					? File.ReadAllText(anomaliesPath)
					: string.Empty;
				Assert.IsFalse(anomalies.Contains("DLL_NOT_FOUND"), anomalies);
				Assert.IsFalse(anomalies.Contains("DLL_HASH_MISMATCH"), anomalies);
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void HsReplaySyncScripts_SummarizeHtmlErrorBodies()
		{
			var repoRoot = FindRepoRoot();
			var scriptNames = new[]
			{
				"Sync-HSReplayMetaData.ps1",
				"Sync-HSReplayArchetypeDecks.ps1",
				"Sync-HSReplayPremiumData.ps1"
			};

			foreach (var scriptName in scriptNames)
			{
				var script = File.ReadAllText(Path.Combine(repoRoot, "tools", scriptName));
				StringAssert.Contains(script, "Format-HSReplayResponseBody");
				StringAssert.Contains(script, "Cloudflare challenge page");
				Assert.IsFalse(script.Contains("Body: $body"), scriptName);
			}
		}

		[TestMethod]
		public void InstallScripts_KeepRefreshToolsAndScheduledTaskAligned()
		{
			var repoRoot = FindRepoRoot();
			var scriptPaths = new[]
			{
				Path.Combine(repoRoot, "tools", "Install-MetaCompanion.ps1"),
				Path.Combine(repoRoot, "dist", "Install-MetaCompanion.ps1")
			};

			foreach (var scriptPath in scriptPaths)
			{
				var script = File.ReadAllText(scriptPath);
				StringAssert.Contains(script, "Resolve-ToolSourceDirectory");
				StringAssert.Contains(script, "Remove-MetaCompanionRefreshTask");
				StringAssert.Contains(script, "Run-MetaCompanionRefresh.ps1");
				StringAssert.Contains(script, "Update-MetaCompanionPatchState.ps1");
				StringAssert.Contains(script, "Refresh tools and scheduled task were not installed");
				StringAssert.Contains(script, @"MetaCompanion\bin\Release\MetaCompanion.dll");
				StringAssert.Contains(script, "Build-MetaCompanion.ps1");
				StringAssert.Contains(script, "-BuildPath explicitly");
				Assert.IsFalse(script.Contains("Get-ChildItem -Path $PSScriptRoot -Filter \"*.ps1\""), scriptPath);
				Assert.IsFalse(script.Contains(@"MetaCompanion\bin\x86\Release\MetaCompanion.dll"), scriptPath);
			}
		}

		[TestMethod]
		public void OneClickRefreshScripts_PinCurrentPatchWithLatestDayFallback()
		{
			var repoRoot = FindRepoRoot();
			var oneClickDir = Path.Combine(repoRoot, "\u4e00\u952e\u811a\u672c");
			var smartRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "04 *.cmd")[0]);
			var forceRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "05 *.cmd")[0]);
			var localRefresh = File.ReadAllText(Directory.GetFiles(oneClickDir, "07 *.cmd")[0]);

			StringAssert.Contains(smartRefresh, "-PrimaryTimeRange CURRENT_PATCH");
			StringAssert.Contains(smartRefresh, "-MetaFallbackTimeRange LAST_1_DAY");
			StringAssert.Contains(forceRefresh, "-PrimaryTimeRange CURRENT_PATCH");
			StringAssert.Contains(forceRefresh, "-MetaFallbackTimeRange LAST_1_DAY");
			StringAssert.Contains(localRefresh, "-MetaTimeRange CURRENT_PATCH");

			foreach (var scriptPath in Directory.GetFiles(oneClickDir, "*.cmd"))
			{
				var script = File.ReadAllText(scriptPath);
				Assert.IsFalse(script.Contains("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS"), scriptPath);
				Assert.IsFalse(script.Contains("LAST_3_DAYS"), scriptPath);
			}
		}

		[TestMethod]
		public void OneClickTestScript_BuildsBeforeRunningTests()
		{
			var repoRoot = FindRepoRoot();
			var oneClickDir = Path.Combine(repoRoot, "\u4e00\u952e\u811a\u672c");
			var testScript = File.ReadAllText(Directory.GetFiles(oneClickDir, "08 *.cmd")[0]);
			var instructions = File.ReadAllText(Path.Combine(oneClickDir, "00 \u4f7f\u7528\u8bf4\u660e.md"));

			AssertContainsInOrder(
				testScript,
				"Build-MetaCompanion.ps1",
				"Run-Tests.ps1");
			Assert.IsFalse(testScript.Contains("-SkipFreshnessCheck"), testScript);
			StringAssert.Contains(instructions, "\u5148\u6784\u5efa Release AnyCPU");
			StringAssert.Contains(instructions, "\u8fd0\u884c\u6d4b\u8bd5");
		}

		[TestMethod]
		public void LocalMetaScripts_UseFullCurrentPatchHdtHistory()
		{
			var repoRoot = FindRepoRoot();
			var exportScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Export-HdtOpponentHistory.ps1"));
			var updateScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Update-MetaCompanionData.ps1"));
			var localMetaScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Measure-HdtLocalMeta.ps1"));

			StringAssert.Contains(exportScript, "DefaultDeckStats.xml");
			StringAssert.Contains(exportScript, "Select-Object -Unique");
			StringAssert.Contains(updateScript, "$historyExportArgs.Since = $effectivePatchTime");
			StringAssert.Contains(updateScript, "Update-MetaCompanionPatchState.ps1");
			StringAssert.Contains(updateScript, "BranchPath = (Join-Path $dataDirectory \"archetype_deck_branches.tsv\")");
			StringAssert.Contains(localMetaScript, "Test-CurrentPatchBranchSnapshot");
			StringAssert.Contains(localMetaScript, "current_patch_branch");
		}

		[TestMethod]
		public void TestRunner_SandboxesHdtAppDataAndGuardsRealConfig()
		{
			var repoRoot = FindRepoRoot();
			var runner = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Run-Tests.ps1"));

			StringAssert.Contains(runner, "Set-HdtTestAppDataPath");
			StringAssert.Contains(runner, "Assert-RealHdtConfigUnchanged");
			StringAssert.Contains(runner, "Assert-TestAssemblyFresh");
			StringAssert.Contains(runner, "Resolve-TestAssemblyPath");
			StringAssert.Contains(runner, "AppDataPath");
			StringAssert.Contains(runner, "Get-FileHash");
			StringAssert.Contains(runner, "\".png\"");
			StringAssert.Contains(runner, "-SkipFreshnessCheck");
			StringAssert.Contains(runner, "$generatedDirectoryNames = @(\"bin\", \"obj\")");
			StringAssert.Contains(runner, "Sort-Object FullName");
			StringAssert.Contains(runner, "Sort-Object Name");
			StringAssert.Contains(runner, "Get-TestFailureMessage");
			StringAssert.Contains(runner, "Constructor failed:");
			StringAssert.Contains(runner, "Cleanup failed:");
			StringAssert.Contains(runner, "failureMessages.Count -gt 0");
			AssertContainsInOrder(
				runner,
				"$failureMessages = New-Object System.Collections.Generic.List[string]",
				"$instance = $null",
				"$instance = [Activator]::CreateInstance($type)",
				"Constructor failed:",
				"if ($instance -ne $null)",
				"$cleanup.Invoke($instance, @())",
				"Write-Host \"FAIL $name :: $([string]::Join('; ', $failureMessages))\"",
				"Write-Host \"PASS $name\"");
			StringAssert.Contains(runner, "MetaCompanionTests-");
		}

		[TestMethod]
		public void TestRunner_ReportsConstructorAndCleanupFailuresWithSummary()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionRunnerFailureIsolationTest-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(tempRoot);

			try
			{
				var hdtStubSource = Path.Combine(tempRoot, "HdtStub.cs");
				var hdtStubAssembly = Path.Combine(tempRoot, "HearthstoneDeckTracker.exe");
				File.WriteAllText(
					hdtStubSource,
					@"namespace Hearthstone_Deck_Tracker
{
	public static class Config
	{
		public static string AppDataPath;
	}
}");
				CompileCSharp(
					repoRoot,
					"/nologo /target:library /out:" + QuoteCommandLineArgument(hdtStubAssembly) +
					" " + QuoteCommandLineArgument(hdtStubSource),
					tempRoot);

				var testFrameworkPath = Path.Combine(
					repoRoot,
					"packages",
					"MSTest.TestFramework.1.2.0",
					"lib",
					"net45",
					"Microsoft.VisualStudio.TestPlatform.TestFramework.dll");
				var systemRuntimePath = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.Windows),
					"Microsoft.NET",
					"Framework",
					"v4.0.30319",
					"System.Runtime.dll");
				Assert.IsTrue(File.Exists(systemRuntimePath), "System.Runtime.dll not found: " + systemRuntimePath);
				File.Copy(
					testFrameworkPath,
					Path.Combine(tempRoot, Path.GetFileName(testFrameworkPath)),
					true);

				var testSource = Path.Combine(tempRoot, "SyntheticRunnerFailures.cs");
				var testAssembly = Path.Combine(tempRoot, "SyntheticRunnerFailures.dll");
				File.WriteAllText(
					testSource,
					@"using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CleanupFailureTest
{
	[TestMethod]
	public void BodyPasses()
	{
	}

	[TestCleanup]
	public void Cleanup()
	{
		throw new InvalidOperationException(""cleanup boom"");
	}
}

[TestClass]
public class ConstructorFailureTest
{
	public ConstructorFailureTest()
	{
		throw new InvalidOperationException(""constructor boom"");
	}

	[TestMethod]
	public void TestStillReported()
	{
	}
}

[TestClass]
public class LaterPassingTest
{
	[TestMethod]
	public void RunsAfterFailures()
	{
	}
}");
				CompileCSharp(
					repoRoot,
					"/nologo /target:library /reference:" + QuoteCommandLineArgument(testFrameworkPath) +
					" /reference:" + QuoteCommandLineArgument(systemRuntimePath) +
					" /out:" + QuoteCommandLineArgument(testAssembly) +
					" " + QuoteCommandLineArgument(testSource),
					tempRoot);

				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Run-Tests.ps1"),
					"-AssemblyPath \"" + testAssembly + "\" -SkipFreshnessCheck");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "FAIL CleanupFailureTest.BodyPasses :: Cleanup failed: cleanup boom");
				StringAssert.Contains(result.Output, "FAIL ConstructorFailureTest.TestStillReported :: Constructor failed: constructor boom");
				StringAssert.Contains(result.Output, "PASS LaterPassingTest.RunsAfterFailures");
				StringAssert.Contains(result.Output, "RESULT passed=1 failed=2");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void TestRunner_MissingAssemblyReportsBuildStep()
		{
			var repoRoot = FindRepoRoot();
			var missingAssembly = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionMissingTestAssembly-" + Guid.NewGuid().ToString("N"),
				"MetaCompanionTests.dll");

			var result = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Run-Tests.ps1"),
				"-AssemblyPath \"" + missingAssembly + "\"");

			Assert.AreNotEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "Test assembly not found");
			StringAssert.Contains(result.Output, "Build-MetaCompanion.ps1");
			Assert.IsFalse(result.Output.Contains("Resolve-Path"), result.Output);
		}

		[TestMethod]
		public void TestRunner_RejectsStaleTestAssemblyBeforeLoadingIt()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionStaleTestAssemblyTest-" + Guid.NewGuid().ToString("N"));
			var staleAssembly = Path.Combine(tempRoot, "MetaCompanionTests.dll");
			Directory.CreateDirectory(tempRoot);
			File.WriteAllText(staleAssembly, "not a real assembly");
			File.SetLastWriteTimeUtc(
				staleAssembly,
				new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Run-Tests.ps1"),
					"-AssemblyPath \"" + staleAssembly + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Test assembly is older than source file");
				StringAssert.Contains(result.Output, "Build-MetaCompanion.ps1");
				StringAssert.Contains(result.Output, "-SkipFreshnessCheck");
				Assert.IsFalse(result.Output.Contains("Bad IL format"), result.Output);
			}
			finally
			{
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void TestRunner_RejectsStaleTestAssemblyWhenResourceIsNewer()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionStaleResourceTest-" + Guid.NewGuid().ToString("N"));
			var staleAssembly = Path.Combine(tempRoot, "MetaCompanionTests.dll");
			var resourceName = "freshness-" + Guid.NewGuid().ToString("N") + ".png";
			var resourcePath = Path.Combine(repoRoot, "MetaCompanion", "Resources", resourceName);
			Directory.CreateDirectory(tempRoot);
			File.WriteAllText(staleAssembly, "not a real assembly");
			File.WriteAllText(resourcePath, "resource changed");
			File.SetLastWriteTimeUtc(
				staleAssembly,
				new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
			File.SetLastWriteTimeUtc(resourcePath, DateTime.UtcNow.AddMinutes(5));

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Run-Tests.ps1"),
					"-AssemblyPath \"" + staleAssembly + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Test assembly is older than source file");
				StringAssert.Contains(result.Output, "MetaCompanion\\Resources\\" + resourceName);
				Assert.IsFalse(result.Output.Contains("Bad IL format"), result.Output);
			}
			finally
			{
				if (File.Exists(resourcePath))
				{
					File.Delete(resourcePath);
				}
				if (Directory.Exists(tempRoot))
				{
					Directory.Delete(tempRoot, true);
				}
			}
		}

		[TestMethod]
		public void ReleaseChecklist_DocumentsCommunityPackageAndNoPremiumRequirement()
		{
			var repoRoot = FindRepoRoot();
			var checklist = File.ReadAllText(
				Path.Combine(repoRoot, "docs", "RELEASE-CHECKLIST.md"));

			StringAssert.Contains(checklist, "tools\\*.ps1");
			StringAssert.Contains(checklist, "hsreplay_cookie.txt");
			StringAssert.Contains(checklist, "Test result");
			StringAssert.Contains(checklist, "failed=0");
			StringAssert.Contains(checklist, "SHA256 与字节大小");
			StringAssert.Contains(checklist, "普通社区用户只有 DLL");
			StringAssert.Contains(checklist, "没有会员或历史");
		}

		[TestMethod]
		public void Readme_DocumentsTestRunnerFreshnessGuard()
		{
			var repoRoot = FindRepoRoot();
			var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));

			StringAssert.Contains(readme, "Build-MetaCompanion.ps1");
			StringAssert.Contains(readme, "测试程序集存在且不早于源码");
			StringAssert.Contains(readme, "-SkipFreshnessCheck");
		}

		[TestMethod]
		public void ImplementationSummary_PointsToReleaseGateForChangingBuildHashes()
		{
			var repoRoot = FindRepoRoot();
			var summary = File.ReadAllText(
				Path.Combine(repoRoot, "docs", "IMPLEMENTATION-SUMMARY.md"));

			StringAssert.Contains(summary, "release-gate");
			StringAssert.Contains(summary, "Build Artifact");
			Assert.IsFalse(
				System.Text.RegularExpressions.Regex.IsMatch(
					summary,
					"`[0-9A-Fa-f]{64}`"),
				"Implementation summary should not pin build-specific hashes.");
		}

		private static ProcessResult RunPowerShell(
			string workingDirectory,
			string scriptPath,
			string arguments,
			IDictionary<string, string> environmentOverrides = null)
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
			if (environmentOverrides != null)
			{
				foreach (var pair in environmentOverrides)
				{
					startInfo.EnvironmentVariables[pair.Key] = pair.Value ?? string.Empty;
				}
			}

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

		private static string GetSingleReleaseGateReportPath(string artifactsRoot)
		{
			var reports = Directory.GetFiles(
				artifactsRoot,
				"release-gate.md",
				SearchOption.AllDirectories);
			Assert.AreEqual(1, reports.Length, "Expected exactly one release gate report.");
			return reports[0];
		}

		private static void AssertProjectConfigurationContains(
			string project,
			string configuration,
			string expected)
		{
			var condition = "Condition=\"'$(Configuration)|$(Platform)' == '" + configuration + "'\"";
			var start = project.IndexOf(condition, StringComparison.Ordinal);
			Assert.IsTrue(start >= 0, "Configuration not found: " + configuration);
			var end = project.IndexOf("</PropertyGroup>", start, StringComparison.Ordinal);
			Assert.IsTrue(end > start, "PropertyGroup end not found: " + configuration);
			var propertyGroup = project.Substring(start, end - start);
			StringAssert.Contains(propertyGroup, expected);
		}

		private static void CreateZipPackage(string repoRoot, string sourceRoot, string packagePath)
		{
			var scriptPath = Path.Combine(Path.GetDirectoryName(packagePath), "CreateZipPackage.ps1");
			File.WriteAllText(
				scriptPath,
				"$ErrorActionPreference = 'Stop'" + Environment.NewLine +
				"Get-ChildItem -LiteralPath " + QuotePowerShellString(sourceRoot) +
				" | Compress-Archive -DestinationPath " + QuotePowerShellString(packagePath) +
				" -Force" + Environment.NewLine);

			var result = RunPowerShell(repoRoot, scriptPath, string.Empty);
			Assert.AreEqual(0, result.ExitCode, result.Output);
		}

		private static void CompileCSharp(string repoRoot, string arguments, string workingDirectory)
		{
			var cscPath = Path.Combine(
				repoRoot,
				"packages",
				"Microsoft.Net.Compilers.4.2.0",
				"tools",
				"csc.exe");
			Assert.IsTrue(File.Exists(cscPath), "Roslyn compiler not found: " + cscPath);

			var result = RunProcess(workingDirectory, cscPath, arguments);
			Assert.AreEqual(0, result.ExitCode, result.Output);
		}

		private static ProcessResult RunProcess(
			string workingDirectory,
			string fileName,
			string arguments)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
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

		private static void AssertContainsInOrder(string text, params string[] expectedSnippets)
		{
			var position = 0;
			foreach (var snippet in expectedSnippets)
			{
				var next = text.IndexOf(snippet, position, StringComparison.Ordinal);
				Assert.IsTrue(next >= 0, "Expected snippet after position " + position + ": " + snippet);
				position = next + snippet.Length;
			}
		}

		private static string QuotePowerShellString(string value)
		{
			return "'" + value.Replace("'", "''") + "'";
		}

		private static string QuoteCommandLineArgument(string value)
		{
			return "\"" + value.Replace("\"", "\\\"") + "\"";
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

