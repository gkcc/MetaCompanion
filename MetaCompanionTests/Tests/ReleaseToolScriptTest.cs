using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

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
			StringAssert.Contains(script, "Ensure-NetFxReferenceAssemblies.ps1");
			StringAssert.Contains(script, "/p:FrameworkPathOverride=$frameworkPath");
			StringAssert.Contains(script, "Framework path override:");
			StringAssert.Contains(script, "HDT rule evaluation summary should parse all release metrics.");
			StringAssert.Contains(script, "HDT visible point-effect oracle gate result:");
			StringAssert.Contains(script, "Trajectory auditor fixture summary should strictly parse source, hashes, flags, metrics, and caveat.");
			StringAssert.Contains(script, "Trajectory auditor fixture self-test result:");
			StringAssert.Contains(script, "Behavior learning fixture summary should parse UTF-8 without a BOM.");
			StringAssert.Contains(script, "Observed-policy fixture summary should parse UTF-8 without a BOM.");
			StringAssert.Contains(script, "Dual-model updater summary should use an encoding-stable ASCII marker.");
			StringAssert.Contains(script, "Behavior learning fixture caveat drift should be invalid.");
			StringAssert.Contains(script, "Non-ready behavior learning fixture should fail the release summary.");
			StringAssert.Contains(script, "Behavior prior fixture summary should strictly parse split isolation, readiness, and safety flags.");
			StringAssert.Contains(script, "Behavior prior readiness tampering should fail closed.");
			StringAssert.Contains(script, "Behavior prior quality-check tampering should fail closed.");
			StringAssert.Contains(script, "Community installer must install the behavior-prior updater.");
			StringAssert.Contains(script, "Behavior-prior updater must resolve the installed AdvisorWorker.");
			StringAssert.Contains(script, "Runtime trajectory NO_DATA summary should be explicit and non-blocking.");
			StringAssert.Contains(script, "Runtime trajectory summary must reject a changed content-addressed snapshot.");
			StringAssert.Contains(script, "solve-status-semantics-v1");
			StringAssert.Contains(script, "$minimumRustFullFixtureCount = 40");
			StringAssert.Contains(script, "$minimumVisibleResponseFixtureCount = 3");
			StringAssert.Contains(script, "Rust full parity fixture floor did not reject a 39-case report.");
			StringAssert.Contains(script, "Rust full parity fixture floor rejected a 40-case report.");
			StringAssert.Contains(script, "Visible-response false-claim metric did not block promotion.");
			StringAssert.Contains(script, "Valid Rust official card-pool report was not accepted.");
			StringAssert.Contains(script, "Official card-pool gate did not reject a changed Rust binary.");
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
		public void EnsureNetFxReferenceAssemblies_UsesCachedPackageWithoutDownload()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionNetFxReferencesTest-" + Guid.NewGuid().ToString("N"));
			var referencePath = Path.Combine(
				tempRoot,
				"Microsoft.NETFramework.ReferenceAssemblies.net472.1.0.3",
				"build",
				".NETFramework",
				"v4.7.2");
			Directory.CreateDirectory(referencePath);
			File.WriteAllText(Path.Combine(referencePath, "mscorlib.dll"), string.Empty);

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Ensure-NetFxReferenceAssemblies.ps1"),
					"-PackagesDirectory \"" + tempRoot + "\" -Quiet");

				Assert.AreEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, referencePath);
				Assert.IsFalse(result.Output.Contains("Downloading"), result.Output);
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
		public void Projects_UseNet472FacadeReferencesInsteadOfRuntimeDirectory()
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
				StringAssert.Contains(project, "<NetFxFacadePath");
				StringAssert.Contains(project, "$(NetFxFacadePath)\\netstandard.dll");
				Assert.IsFalse(
					project.Contains(@"C:\Windows\Microsoft.NET\Framework\v4.0.30319"),
					projectPath);
			}

			var testProject = File.ReadAllText(projectPaths[1]);
			StringAssert.Contains(testProject, "$(NetFxFacadePath)\\System.Runtime.dll");
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
			var requiredEntryCount = PopulateRequiredReleasePackage(packageRoot);

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
				StringAssert.Contains(report, "- HDT visible point-effect gate: Skipped");
				StringAssert.Contains(report, "- Trajectory auditor fixture self-test: Skipped");
				StringAssert.Contains(report, "- Behavior prior synthetic-fixture gate: Skipped");
				StringAssert.Contains(report, "- Rust behavior-prior loader gate: Skipped");
				StringAssert.Contains(report, "- Rust official card-pool gate: Not requested");
				StringAssert.Contains(report, "- Runtime training readiness (non-blocking for plugin release): Skipped");
				StringAssert.Contains(report, "\u8bad\u7ec3\u6570\u636e\u95e8\u7981\u8bf4\u660e");
				StringAssert.Contains(report, "\u751f\u4ea7\u8bad\u7ec3\u5c31\u7eea\u8bf4\u660e");
				StringAssert.Contains(report, "\u6c42\u89e3\u72b6\u6001\u53e3\u5f84");
				StringAssert.Contains(report, "- Failure count: 0");
				StringAssert.Contains(report, "- MSBuild: Not required");
				StringAssert.Contains(report, "- Test PowerShell: Not required");
				StringAssert.Contains(report, "- Roslyn: Not required");
				StringAssert.Contains(report, "- HDT app: Not required");
				StringAssert.Contains(report, "- Framework path override: Not required");
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
				StringAssert.Contains(report, "- HDT rule evaluation log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- HDT rule evaluation report: Not generated (tests skipped)");
				StringAssert.Contains(report, "- HDT rule evaluation result: Skipped");
				StringAssert.Contains(report, "- Trajectory auditor fixture log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Trajectory auditor fixture report: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Trajectory auditor fixture result: Skipped");
				StringAssert.Contains(report, "- Behavior prior fixture log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Behavior prior fixture artifact: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Behavior prior fixture result: Skipped");
				StringAssert.Contains(report, "- Rust behavior-prior loader log: Not generated (Rust promotion not requested)");
				StringAssert.Contains(report, "- Rust behavior-prior loader result: Skipped");
				StringAssert.Contains(report, "- Rust official card-pool log: Not generated (Rust promotion not requested)");
				StringAssert.Contains(report, "- Rust official card-pool report: Not generated (Rust promotion not requested)");
				StringAssert.Contains(report, "- Rust official card-pool result: Not requested");
				StringAssert.Contains(report, "- Runtime trajectory audit log: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Runtime trajectory audit report: Not generated (tests skipped)");
				StringAssert.Contains(report, "- Runtime training readiness result: Skipped");
				StringAssert.Contains(report, "- Build skipped");
				StringAssert.Contains(report, "- Type: Directory");
				StringAssert.Contains(report, "- Size bytes: Not applicable");
				StringAssert.Contains(report, "- SHA256: Not applicable");
				StringAssert.Contains(report, "- Entry count: " + (requiredEntryCount + 1));
				StringAssert.Contains(report, "- Blocked entries: 0");
				StringAssert.Contains(report, "- Tracked-file source: git ls-files");
				StringAssert.Contains(report, "- Tracked files scanned: ");
				StringAssert.Contains(report, "- Package files scanned: " + requiredEntryCount);
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
		public void ReleaseGate_ExplicitPackageRejectsMissingAdvisorRuntime()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionReleaseGateMissingRuntimeTest-" + Guid.NewGuid().ToString("N"));
			var packageRoot = Path.Combine(tempRoot, "package");
			var artifactsRoot = Path.Combine(tempRoot, "artifacts");
			Directory.CreateDirectory(packageRoot);
			File.WriteAllText(Path.Combine(packageRoot, "MetaCompanion.dll"), "fixture");

			try
			{
				var result = RunPowerShell(
					repoRoot,
					Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"),
					"-SkipBuild -SkipTests -PackagePath \"" + packageRoot +
					"\" -ArtifactsDirectory \"" + artifactsRoot + "\"");

				Assert.AreNotEqual(0, result.ExitCode, result.Output);
				StringAssert.Contains(result.Output, "Community package is missing required entry:");
				var report = File.ReadAllText(GetSingleReleaseGateReportPath(artifactsRoot));
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/counterplay.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/oracle-turnpair-v1.json");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/card_rules.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/hdt_rule_evaluation.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/visible_response_evaluation.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/oracle-hdt-cardrules-v1.json");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/visible-response-v1.json");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/trajectory.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/verification.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/trajectory-readiness-policy-v1.json");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/trajectory-readiness-v1.jsonl");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/metacompanion_solver/behavior_prior.py");
				StringAssert.Contains(
					report,
					"Community package is missing required entry: solver/fixtures/behavior-prior-readiness-v1.manifest.json");
			}
			finally
			{
				if (Directory.Exists(tempRoot))
					Directory.Delete(tempRoot, true);
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
			var requiredEntryCount = PopulateRequiredReleasePackage(packageRoot);

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
				StringAssert.Contains(report, "- Entry count: " + (requiredEntryCount + 1));
				StringAssert.Contains(report, "- Blocked entries: 1");
				StringAssert.Contains(report, "- Package files scanned: " + requiredEntryCount);
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
			var requiredEntryCount = PopulateRequiredReleasePackage(sourceRoot);

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
				StringAssert.Contains(report, "- Entry count: " + (requiredEntryCount + 3));
				StringAssert.Contains(report, "- Blocked entries: 1");
				StringAssert.Contains(report, "- Package files scanned: " + (requiredEntryCount + 2));
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
			var requiredEntryCount = PopulateRequiredReleasePackage(packageRoot);

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
				StringAssert.Contains(report, "- Entry count: " + (requiredEntryCount + 2));
				StringAssert.Contains(report, "- Package files scanned: " + requiredEntryCount);
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
				StringAssert.Contains(report, "- Framework path override: Not resolved");
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
			var branchScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayArchetypeDecks.ps1"));
			var installScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Install-MetaCompanionRefreshTask.ps1"));

			StringAssert.Contains(refreshScript, "Test-RemoteCacheRefreshedToday");
			StringAssert.Contains(refreshScript, "selected_time_range");
			StringAssert.Contains(refreshScript, "CURRENT_PATCH");
			StringAssert.Contains(refreshScript, "LAST_1_DAY");
			StringAssert.Contains(refreshScript, "Test-MetaCacheMatchesEpoch");
			StringAssert.Contains(refreshScript, "META_COMPANION_REFRESH_OUTCOME=$refreshOutcome");
			StringAssert.Contains(refreshScript, "$refreshOutcome = \"DEFERRED\"");
			StringAssert.Contains(refreshScript, "Get-MetaCompanionPublicPatchVersion");
			StringAssert.Contains(refreshScript, "本次停止分支与推荐刷新，请稍后重试");
			StringAssert.Contains(refreshScript, "summary.as_of");
			StringAssert.Contains(refreshScript, "matrix.as_of");
			StringAssert.Contains(refreshScript, "Update-MetaCompanionPatchState.ps1");
			Assert.IsFalse(refreshScript.Contains("AUTO_CURRENT_PATCH_OR_LAST_3_DAYS"));
			StringAssert.Contains(refreshScript, "远端缓存今天已经刷新完成");
			StringAssert.Contains(refreshScript, "Premium / 环境数据刷新失败；将使用现有缓存重新计算推荐。");
			StringAssert.Contains(branchScript, "[string]$CandidateTimeRange = \"LAST_7_DAYS\"");
			StringAssert.Contains(refreshScript, "ExpectedRankRange");
			StringAssert.Contains(refreshScript, "modelBranchManifest.rank_range");
			StringAssert.Contains(refreshScript,
				"$modelBranchTimeRange = [string]$modelBranchManifest.candidate_time_range");
			StringAssert.Contains(refreshScript,
				"Test-MetaCompanionCurrentPatchRange $modelBranchTimeRange");
			StringAssert.Contains(refreshScript, "archetype_model_branches.tsv");
			StringAssert.Contains(installScript, "Meta Companion Remote Cache Refresh");
			StringAssert.Contains(installScript, "Run-MetaCompanionRefresh.ps1");
			StringAssert.Contains(installScript, "New-ScheduledTaskTrigger -AtLogOn");
			StringAssert.Contains(installScript, "LogonDelayMinutes");
			StringAssert.Contains(installScript, "StartWhenAvailable");
			StringAssert.Contains(installScript, "schtasks.exe");
			StringAssert.Contains(installScript, "\"/RL\", \"LIMITED\"");
			StringAssert.Contains(installScript, "当前用户限权任务（无需管理员权限）");
			StringAssert.Contains(installScript, "Meta Companion Daily Refresh");
		}

		[TestMethod]
		public void RefreshScripts_WithChineseOutput_UseWindowsPowerShellSafeEncoding()
		{
			var repoRoot = FindRepoRoot();
			var scriptNames = new[]
			{
				"Run-MetaCompanionRefresh.ps1",
				"Install-MetaCompanionRefreshTask.ps1",
				"Update-MetaCompanionData.ps1",
				"Sync-HSReplayDeckCodes.ps1",
				"Sync-HSReplayPremiumData.ps1",
				"Sync-HSReplayMetaData.ps1",
				"Sync-HSReplayArchetypeDecks.ps1",
				"Export-HdtOpponentHistory.ps1",
				"Measure-HdtLocalMeta.ps1",
				"Get-MetaArchetypeRecommendations.ps1",
				"Get-PersonalMetaRecommendations.ps1",
				"Verify-DeckCodeImport.ps1"
			};

			foreach (var scriptName in scriptNames)
			{
				var bytes = File.ReadAllBytes(Path.Combine(repoRoot, "tools", scriptName));
				Assert.IsTrue(
					bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf,
					scriptName + " 必须保留 UTF-8 BOM，确保 Windows PowerShell 5.1 正确读取中文。");
			}

			foreach (var commandName in new[]
			{
				"04 手动刷新远程数据库（智能跳过）.cmd",
				"05 强制刷新远程数据库.cmd",
				"06 安装自动刷新计划任务（管理员）.cmd"
			})
			{
				var command = File.ReadAllText(Path.Combine(repoRoot, "一键脚本", commandName));
				StringAssert.Contains(command, "chcp 65001 >nul");
			}
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
				StringAssert.Contains(script, "Cloudflare");
				Assert.IsFalse(script.Contains("Body: $body"), scriptName);
			}

			var deckCodeScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayDeckCodes.ps1"));
			StringAssert.Contains(deckCodeScript, "Get-CurlExitSummary");
			StringAssert.Contains(deckCodeScript, "请求超时");
			Assert.IsFalse(
				deckCodeScript.Contains("curl.exe -sS"),
				"Native curl stderr must not flood scheduled refresh logs with raw English errors.");
		}

		[TestMethod]
		public void HsReplayRefresh_PollsProcessingAndPromotesOnlyValidatedRuns()
		{
			var repoRoot = FindRepoRoot();
			var premiumScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayPremiumData.ps1"));
			var metaScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayMetaData.ps1"));
			var branchScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayArchetypeDecks.ps1"));
			var refreshScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Run-MetaCompanionRefresh.ps1"));
			var personalRecommendationScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Get-PersonalMetaRecommendations.ps1"));

			foreach (var script in new[] { premiumScript, metaScript, branchScript })
			{
				StringAssert.Contains(script, "ProcessingMaxPolls");
				StringAssert.Contains(script, "ProcessingPollDelaySeconds");
				StringAssert.Contains(script, "Test-HSReplayProcessingResponse");
				StringAssert.Contains(script, "ProcessingPollCount");
				StringAssert.Contains(script, "处理中轮询");
				Assert.IsFalse(
					script.Contains("Set-Content -Path $latestPath -Value $response.Body"),
					"A response must not be promoted to latest before the full run is validated.");
			}

			StringAssert.Contains(premiumScript, "Assert-HSReplayAnalyticsPayload");
			StringAssert.Contains(premiumScript, "Test-HSReplayEndpointSupportsTimeRange");
			StringAssert.Contains(premiumScript, "unsupported_time_range:$TimeRange");
			StringAssert.Contains(premiumScript, "LAST_30_DAYS\", \"CURRENT_PATCH\", \"CURRENT_EXPANSION\", \"CURRENT_SEASON");
			StringAssert.Contains(premiumScript, "成功=$SuccessCount");
			StringAssert.Contains(premiumScript, "空结果=$NoContentCount");
			StringAssert.Contains(premiumScript, "跳过=$SkippedCount");
			StringAssert.Contains(premiumScript, "Publish-PremiumLatestAtomically");
			StringAssert.Contains(premiumScript, "publish-complete.json");
			StringAssert.Contains(premiumScript, ".staging");
			StringAssert.Contains(premiumScript, "System.Security.Cryptography.SHA256");
			Assert.IsFalse(
				premiumScript.Contains("Get-FileHash"),
				"The scheduled refresh host does not guarantee the Get-FileHash cmdlet.");

			StringAssert.Contains(metaScript, "Assert-HSReplayMetaPayload");
			StringAssert.Contains(metaScript, "\\d+\\.\\d+\\.\\d+(?:\\.\\d+)?");
			StringAssert.Contains(metaScript, "不能提升为 Meta latest");
			StringAssert.Contains(metaScript, "Publish-MetaLatestAtomically");
			StringAssert.Contains(metaScript, "publish-complete.json");
			StringAssert.Contains(metaScript, ".staging");
			StringAssert.Contains(metaScript, "System.Security.Cryptography.SHA256");
			Assert.IsFalse(
				metaScript.Contains("Get-FileHash"),
				"The scheduled refresh host does not guarantee the Get-FileHash cmdlet.");

			StringAssert.Contains(branchScript, "Assert-HSReplayAnalyticsPayload");
			StringAssert.Contains(branchScript, "series[0].data");
			StringAssert.Contains(branchScript, "Publish-BranchesLatestAtomically");
			StringAssert.Contains(branchScript, "publish-complete.json");
			StringAssert.Contains(branchScript, "# RunId: $runId");
			StringAssert.Contains(branchScript, "output_sha256");
			StringAssert.Contains(branchScript, "cachedManifest.candidate_time_range");
			StringAssert.Contains(branchScript, "cachedCompletion.manifest_sha256");
			StringAssert.Contains(branchScript, "cachedManifest.candidate.sha256");
			StringAssert.Contains(branchScript, "拒绝复用和重新标记");
			StringAssert.Contains(branchScript,
				"$eligibleRows = @($eligible | ForEach-Object { $_ })");
			Assert.IsFalse(branchScript.Contains("$eligible = @($eligible)"));
			StringAssert.Contains(branchScript, ".staging");
			Assert.IsFalse(
				branchScript.Contains("Set-Content -Path $candidateLatestPath"),
				"A candidate response must not be written to Branches/latest before the run is validated.");
			Assert.IsFalse(
				branchScript.Contains("Move-Item -LiteralPath $tempOutputPath -Destination $OutputPath"),
				"The main branch TSV must only be replaced by the validated atomic publisher.");

			StringAssert.Contains(refreshScript, "Invoke-MetaCompanionPremiumStage");
			StringAssert.Contains(refreshScript, "Invoke-MetaCompanionMetaStage");
			StringAssert.Contains(refreshScript, "Invoke-MetaCompanionBranchStage");
			StringAssert.Contains(refreshScript, "仅以兼容模式重试 Premium 阶段");
			StringAssert.Contains(refreshScript, "仅重试 Meta 阶段");
			StringAssert.Contains(refreshScript, "-TimeRange $effectiveMetaTimeRange");
			StringAssert.Contains(refreshScript, "-OutputPath $branchOutputPath");
			StringAssert.Contains(refreshScript, "-TimeRange $ModelBranchTimeRange");
			StringAssert.Contains(refreshScript, "-OutputPath $modelBranchOutputPath");
			Assert.IsFalse(refreshScript.Contains(
				"Invoke-MetaCompanionBranchStage -TimeRange $PremiumFallbackTimeRange"),
				"Representative decks must not use a different time range from the selected Meta cache.");
			StringAssert.Contains(refreshScript,
				"同口径代表卡组未刷新；将保留旧文件但不会把不同范围的代码显示到复制按钮。");
			Assert.IsFalse(refreshScript.Contains("$strictCurrentPatch"));
			StringAssert.Contains(refreshScript, "$metaTimeRangeCandidates");
			StringAssert.Contains(refreshScript,
				"改用 HSReplay CURRENT_PATCH 远端环境");
			StringAssert.Contains(refreshScript, "生产 latest 未被覆盖");
			Assert.IsFalse(
				refreshScript.Contains("Invoke-MetaCompanionRefreshRun"),
				"A stage failure must not restart the entire refresh pipeline.");
			StringAssert.Contains(personalRecommendationScript, "Test-RepresentativeDeckScope");
			StringAssert.Contains(personalRecommendationScript,
				"当前口径暂无同范围卡组代码；推荐排序会继续生成");
			Assert.IsFalse(personalRecommendationScript.Contains(
				"throw \"代表卡组缓存口径"));
		}

		[TestMethod]
		public void HsReplayFreshnessGuards_SelfTestsPassInWindowsPowerShell()
		{
			var repoRoot = FindRepoRoot();
			var metaResult = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Sync-HSReplayMetaData.ps1"),
				"-SelfTest");
			var branchResult = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Sync-HSReplayArchetypeDecks.ps1"),
				"-SelfTest");

			Assert.AreEqual(0, metaResult.ExitCode, metaResult.Output);
			StringAssert.Contains(metaResult.Output, "Meta freshness self-test passed");
			Assert.AreEqual(0, branchResult.ExitCode, branchResult.Output);
			StringAssert.Contains(branchResult.Output, "Branch freshness self-test passed");
		}

		[TestMethod]
		public void PremiumSync_LastSevenDaysStrictRejectsAndTolerantPublishesSkippedManifest()
		{
			var repoRoot = FindRepoRoot();
			var tempRoot = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionPremiumContract-" + Guid.NewGuid().ToString("N"));
			var outputDirectory = Path.Combine(tempRoot, "Premium Cache");
			Directory.CreateDirectory(tempRoot);
			try
			{
				var scriptPath = Path.Combine(repoRoot, "tools", "Sync-HSReplayPremiumData.ps1");
				var arguments =
					"-Cookie \"fixture=1\" -DeckIds deck1 -MaxDecks 1 " +
					"-TimeRange LAST_7_DAYS " +
					"-Endpoints single_deck_base_winrate_by_opponent_class_v2 " +
					"-OutputDirectory \"" + outputDirectory + "\" " +
					"-RequestDelayMs 0 -ProgressEvery 1";

				var strictResult = RunPowerShell(
					repoRoot,
					scriptPath,
					arguments + " -StopOnUnsupported");
				Assert.AreNotEqual(0, strictResult.ExitCode, strictResult.Output);
				StringAssert.Contains(strictResult.Output,
					"single_deck_base_winrate_by_opponent_class_v2");

				var tolerantResult = RunPowerShell(repoRoot, scriptPath, arguments);
				Assert.AreEqual(0, tolerantResult.ExitCode, tolerantResult.Output);
				StringAssert.Contains(tolerantResult.Output, "TimeRange=LAST_7_DAYS");

				var latestDirectory = Path.Combine(outputDirectory, "latest");
				var manifestPath = Path.Combine(latestDirectory, "manifest.json");
				var completionPath = Path.Combine(latestDirectory, "publish-complete.json");
				Assert.IsTrue(File.Exists(manifestPath), tolerantResult.Output);
				Assert.IsTrue(File.Exists(completionPath), tolerantResult.Output);
				var manifest = File.ReadAllText(manifestPath);
				var completion = File.ReadAllText(completionPath);
				StringAssert.Contains(manifest, "\"skipped\":  true");
				StringAssert.Contains(manifest, "unsupported_time_range:LAST_7_DAYS");
				StringAssert.Contains(manifest, "\"skipped\":  1");
				Assert.IsFalse(File.Exists(Path.Combine(
					latestDirectory,
					"deck1.single_deck_base_winrate_by_opponent_class_v2.json")));

				var markerMatch = System.Text.RegularExpressions.Regex.Match(
					completion,
					"\\\"manifest_sha256\\\"\\s*:\\s*\\\"(?<hash>[A-Fa-f0-9]{64})\\\"");
				Assert.IsTrue(markerMatch.Success, completion);
				Assert.AreEqual(
					ComputeFileSha256(manifestPath),
					markerMatch.Groups["hash"].Value,
					true);
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
				StringAssert.Contains(script, "[switch]$RemoveRefreshTools");
				StringAssert.Contains(script, "elseif ($RemoveRefreshTools)");
				StringAssert.Contains(script, "Run-MetaCompanionRefresh.ps1");
				StringAssert.Contains(script, "Update-MetaCompanionPatchState.ps1");
				StringAssert.Contains(script, "已有远程刷新组件和计划任务保持不变");
				StringAssert.Contains(script, @"MetaCompanion\bin\Release\MetaCompanion.dll");
				StringAssert.Contains(script, "Build-MetaCompanion.ps1");
				StringAssert.Contains(script, "-BuildPath explicitly");
				Assert.IsFalse(script.Contains("Get-ChildItem -Path $PSScriptRoot -Filter \"*.ps1\""), scriptPath);
				Assert.IsFalse(script.Contains(@"MetaCompanion\bin\x86\Release\MetaCompanion.dll"), scriptPath);
			}
		}

		[TestMethod]
		public void AdvisorRuntimeSmoke_TrainingOnlyModeSelfTestsReadOnlyGates()
		{
			var repoRoot = FindRepoRoot();
			var scriptPath = Path.Combine(
				repoRoot,
				"tools",
				"Invoke-HdtAdvisorRuntimeSmoke.ps1");
			var script = File.ReadAllText(scriptPath);
			var result = RunPowerShell(repoRoot, scriptPath, "-SelfTest");

			Assert.AreEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "training_only_config_gate");
			StringAssert.Contains(result.Output, "training_only_solve_gate");
			StringAssert.Contains(result.Output, "training_only_behavior_gate");
			StringAssert.Contains(result.Output, "training_only_panel_absence_gate");
			StringAssert.Contains(script, "[switch]$ExpectTrainingOnly");
			StringAssert.Contains(script, "DtdProcessing = [System.Xml.DtdProcessing]::Prohibit");
			StringAssert.Contains(script, "rl_training_eligible_violation_count");
			StringAssert.Contains(script, "Get-TrainingOnlyAdvisorPanelCheck");
			StringAssert.Contains(script, "System.Windows.Automation");
			Assert.IsFalse(script.Contains("SetCursorPos"), scriptPath);
			Assert.IsFalse(script.Contains("mouse_event"), scriptPath);
			Assert.IsFalse(result.Output.Contains("actor_side"), result.Output);
			Assert.IsFalse(result.Output.Contains("config.xml"), result.Output);
			Assert.IsFalse(result.Output.Contains("training-v2.jsonl"), result.Output);
		}

		[TestMethod]
		public void AdvisorDataArtifacts_AreInstalledPackagedAndSecretScanned()
		{
			var repoRoot = FindRepoRoot();
			var installScriptPaths = new[]
			{
				Path.Combine(repoRoot, "tools", "Install-MetaCompanion.ps1"),
				Path.Combine(repoRoot, "dist", "Install-MetaCompanion.ps1")
			};
			foreach (var installScriptPath in installScriptPaths)
			{
				var installScript = File.ReadAllText(installScriptPath);
				StringAssert.Contains(installScript, "Sync-HdtArenaAdvisorData.ps1");
				StringAssert.Contains(installScript, "Sync-BlizzardCardPools.ps1");
				StringAssert.Contains(installScript, "Resolve-ArenaAdvisorDataToolSource");
				StringAssert.Contains(installScript, "Copy-ArenaAdvisorDataTool");
				StringAssert.Contains(installScript, "Resolve-OfficialCardPoolToolSource");
				StringAssert.Contains(installScript, "Copy-OfficialCardPoolTool");
				StringAssert.Contains(installScript, "Update-AdvisorBehaviorPrior.ps1");
				StringAssert.Contains(installScript, "Resolve-BehaviorPriorUpdateToolSource");
				StringAssert.Contains(installScript, "Copy-BehaviorPriorUpdateTool");
				StringAssert.Contains(installScript, "Copy-AdvisorWorker");
				StringAssert.Contains(installScript, "Copy-AdvisorOfflineTools");
				StringAssert.Contains(installScript, "Resolve-RustAdvisorWorkerPath");
				StringAssert.Contains(installScript, "AdvisorWorker");
				StringAssert.Contains(installScript, "AdvisorOfflineTools");
				StringAssert.Contains(installScript, "实时求解仅使用 Rust");
				StringAssert.Contains(installScript, "旧 Python 实时求解");
				StringAssert.Contains(installScript, "metacompanion-solver.exe");
				StringAssert.Contains(installScript, "\"tests\"");
				StringAssert.Contains(installScript, "\"__pycache__\"");
				StringAssert.Contains(installScript, "\"data\"");
			}

			var releaseGate = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Invoke-ReleaseGate.ps1"));
			StringAssert.Contains(releaseGate, "tools/Sync-HdtArenaAdvisorData.ps1");
			StringAssert.Contains(releaseGate, "tools/Sync-BlizzardCardPools.ps1");
			StringAssert.Contains(releaseGate, "tools/Update-AdvisorBehaviorPrior.ps1");
			StringAssert.Contains(releaseGate, "docs/ADVISOR-DATA.md");
			StringAssert.Contains(releaseGate, "docs/OFFICIAL-CARD-POOLS.md");
			StringAssert.Contains(releaseGate, "solver/launch_solver.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/__main__.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/card_pool.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/card_rules.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/behavior_candidate_alignment.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/behavior_learning.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/behavior_prior.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/decision_ranker.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/decision_solver_evaluation.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/observed_policy_evaluation.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/rust_worker_client.py");
			StringAssert.Contains(releaseGate, "solver/tools/observed_policy_fixture.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/counterplay.py");
			StringAssert.Contains(releaseGate, "RustSolverBinaryPath");
			StringAssert.Contains(releaseGate, "--profile\", \"combat-v1");
			StringAssert.Contains(releaseGate, "--profile\", \"full");
			StringAssert.Contains(releaseGate, "binary changed after parity verification");
			StringAssert.Contains(releaseGate, "solver/metacompanion-solver.exe");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/hdt_rule_evaluation.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/visible_response_evaluation.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/trajectory.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/verification.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/turnpair_evaluation.py");
			StringAssert.Contains(releaseGate, "solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/oracle-hdt-cardrules-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/visible-response-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/oracle-turnpair-v1.json");
			StringAssert.Contains(releaseGate, "evaluate-turnpair");
			StringAssert.Contains(releaseGate, "Counterplay turn-pair oracle gate");
			StringAssert.Contains(releaseGate, "evaluate-hdt-rules");
			StringAssert.Contains(releaseGate, "HDT visible point-effect oracle gate");
			StringAssert.Contains(releaseGate, "evaluate-visible-response");
			StringAssert.Contains(releaseGate, "Rust visible-response partial honesty gate");
			StringAssert.Contains(releaseGate, "audit-decision-solver-coverage");
			StringAssert.Contains(releaseGate, "Rust decision-frame solver-coverage honesty gate");
			StringAssert.Contains(releaseGate, "solver/fixtures/trajectory-readiness-policy-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/trajectory-readiness-v1.jsonl");
			StringAssert.Contains(releaseGate, "audit-trajectories");
			StringAssert.Contains(releaseGate, "Trajectory auditor synthetic-fixture self-test");
			StringAssert.Contains(releaseGate, "audit-runtime-trajectories");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-learning-readiness-policy-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-learning-readiness-v1.jsonl");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-candidate-alignment-policy-v1.json");
			StringAssert.Contains(releaseGate, "audit-behavior-candidates");
			StringAssert.Contains(releaseGate, "Behavior candidate-completeness negative-fixture gate");
			StringAssert.Contains(releaseGate, "audit-behavior-learning");
			StringAssert.Contains(releaseGate, "audit-runtime-behavior-learning");
			StringAssert.Contains(releaseGate, "Behavior learning auditor synthetic-fixture self-test");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-prior-readiness-policy-v1.json");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-prior-readiness-v1.jsonl");
			StringAssert.Contains(releaseGate, "solver/fixtures/behavior-prior-readiness-v1.manifest.json");
			StringAssert.Contains(releaseGate, "train-behavior-prior");
			StringAssert.Contains(releaseGate, "Behavior prior synthetic-fixture gate");
			StringAssert.Contains(releaseGate, "Rust behavior-prior loader gate");
			StringAssert.Contains(releaseGate, "behavior-prior-check");
			StringAssert.Contains(releaseGate, "Rust official card-pool gate");
			StringAssert.Contains(releaseGate, "solver\\tools\\rust_card_pool_gate.py");
			StringAssert.Contains(releaseGate, "metacompanion-rust-official-card-pool-gate-v1");
			StringAssert.Contains(releaseGate, "Rust parity, visible-response, card-pool, and decision-solver coverage gates verified different solver binaries.");
			StringAssert.Contains(releaseGate, "behavior-prior-v1(?:\\.install)?\\.json");
			StringAssert.Contains(releaseGate, "source=synthetic_fixture");
			StringAssert.Contains(releaseGate, "--cached --others --exclude-standard");
			StringAssert.Contains(releaseGate, "\".py\"");
			StringAssert.Contains(releaseGate, "\".toml\"");
			StringAssert.Contains(releaseGate, "\".jsonl\"");
			StringAssert.Contains(releaseGate, "ArenaLastDrafts\\.xml");
			StringAssert.Contains(releaseGate, "AdvisorData(/|$)");
			StringAssert.Contains(releaseGate, "training(?:-v2)?\\.jsonl");
			StringAssert.Contains(releaseGate, "AdvisorWorker/data/training-v2.jsonl");
			StringAssert.Contains(releaseGate, "excludedSolverDirectories");

			var selfTest = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Sync-HdtArenaAdvisorData.ps1"),
				"-SelfTest");
			Assert.AreEqual(0, selfTest.ExitCode, selfTest.Output);
			StringAssert.Contains(selfTest.Output, "Arena advisor data self-test passed");

			var officialPoolSelfTest = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Sync-BlizzardCardPools.ps1"),
				"-SelfTest");
			Assert.AreEqual(0, officialPoolSelfTest.ExitCode, officialPoolSelfTest.Output);
			StringAssert.Contains(
				officialPoolSelfTest.Output,
				"Official Blizzard card pool self-test passed");
			var officialPoolScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-BlizzardCardPools.ps1"));
			StringAssert.Contains(
				officialPoolScript, "Add-Type -AssemblyName System.Net.Http");

			var archiveSelfTest = RunPowerShell(
				repoRoot,
				Path.Combine(repoRoot, "tools", "Archive-AdvisorTrainingLog.ps1"),
				"-SelfTest");
			Assert.AreEqual(0, archiveSelfTest.ExitCode, archiveSelfTest.Output);
			StringAssert.Contains(
				archiveSelfTest.Output,
				"metacompanion-training-log-archive-v1");

			var behaviorPriorUpdater = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Update-AdvisorBehaviorPrior.ps1"));
			StringAssert.Contains(behaviorPriorUpdater, "audit-runtime-behavior-learning");
			StringAssert.Contains(behaviorPriorUpdater, "audit-behavior-candidates");
			StringAssert.Contains(behaviorPriorUpdater, "candidate_ranking_training_ready");
			StringAssert.Contains(behaviorPriorUpdater, "behavior-candidate-alignment-v1.json");
			StringAssert.Contains(behaviorPriorUpdater, "promote-behavior-imitation");
			StringAssert.Contains(behaviorPriorUpdater, "train-behavior-prior");
			StringAssert.Contains(behaviorPriorUpdater, "train-decision-ranker");
			StringAssert.Contains(behaviorPriorUpdater, "evaluate-observed-policy");
			StringAssert.Contains(behaviorPriorUpdater, "behavior-prior-check");
			StringAssert.Contains(behaviorPriorUpdater, "decision-ranker-check");
			StringAssert.Contains(behaviorPriorUpdater, "transactional_pair = $true");
			StringAssert.Contains(behaviorPriorUpdater, "HistoricalSourceDirectory");
			StringAssert.Contains(behaviorPriorUpdater, "..\\AdvisorWorker");
			StringAssert.Contains(behaviorPriorUpdater, "Sort-Object LastWriteTimeUtc -Descending");
			StringAssert.Contains(behaviorPriorUpdater, "hot_reload_supported = $true");
			StringAssert.Contains(behaviorPriorUpdater, "现有模型保持不变");
			Assert.IsFalse(behaviorPriorUpdater.Contains("Cookie"));
			Assert.IsFalse(behaviorPriorUpdater.Contains("Chrome"));
			Assert.IsFalse(behaviorPriorUpdater.Contains("Edge"));
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
		public void DeckSnapshotDefaults_UseDiamondThroughLegendOnly()
		{
			var repoRoot = FindRepoRoot();
			var syncScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Sync-HSReplayDeckCodes.ps1"));
			var updateScript = File.ReadAllText(
				Path.Combine(repoRoot, "tools", "Update-MetaCompanionData.ps1"));
			var defaultMatch = System.Text.RegularExpressions.Regex.Match(
				syncScript,
				@"\[string\[\]\]\$RankRanges\s*=\s*@\((?<body>.*?)\)\s*,",
				System.Text.RegularExpressions.RegexOptions.Singleline);

			Assert.IsTrue(defaultMatch.Success, syncScript);
			var defaultBody = defaultMatch.Groups["body"].Value;
			StringAssert.Contains(defaultBody, "DIAMOND_THROUGH_LEGEND");
			Assert.IsFalse(defaultBody.Contains("DIAMOND_FOUR_THROUGH_DIAMOND_ONE"), defaultBody);
			Assert.IsFalse(defaultBody.Contains("PLATINUM"), defaultBody);
			Assert.IsFalse(defaultBody.Contains("GOLD"), defaultBody);
			Assert.IsFalse(defaultBody.Contains("BRONZE_THROUGH_GOLD"), defaultBody);
			StringAssert.Contains(updateScript, "$defaultRankRanges = @($RemoteRankRange)");
			StringAssert.Contains(updateScript, "$rankRanges = $defaultRankRanges");
			StringAssert.Contains(updateScript, "$rankRanges = $fullRankRanges");
			var fullMatch = System.Text.RegularExpressions.Regex.Match(
				updateScript,
				@"\$fullRankRanges\s*=\s*@\((?<body>.*?)\)",
				System.Text.RegularExpressions.RegexOptions.Singleline);
			Assert.IsTrue(fullMatch.Success, updateScript);
			var fullBody = fullMatch.Groups["body"].Value;
			StringAssert.Contains(fullBody, "DIAMOND_THROUGH_LEGEND");
			StringAssert.Contains(fullBody, "DIAMOND_FOUR_THROUGH_DIAMOND_ONE");
			StringAssert.Contains(fullBody, "PLATINUM");
			StringAssert.Contains(fullBody, "GOLD");
			StringAssert.Contains(fullBody, "BRONZE_THROUGH_GOLD");
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
		public void LocalMetaScripts_ApplyDaysMatchesPatchAndClearBoundaries()
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
			StringAssert.Contains(updateScript, "$historyStart = Get-LatestDate");
			StringAssert.Contains(updateScript, "$historyExportArgs.Since = $historyStart");
			StringAssert.Contains(updateScript, "local_history_cleared_at.txt");
			StringAssert.Contains(updateScript, "Matches = $PersonalRecommendationHistoryMatches");
			StringAssert.Contains(localMetaScript, "[Alias(\"Matches\")]");
			StringAssert.Contains(localMetaScript, "Select-Object -First $HistoryMatches");
			StringAssert.Contains(updateScript, "Update-MetaCompanionPatchState.ps1");
			StringAssert.Contains(updateScript, "BranchPath = $recognitionBranchPath");
			StringAssert.Contains(updateScript, "archetype_model_branches.tsv");
			Assert.IsFalse(updateScript.Contains("Assert-BranchCacheScope"));
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

		private static string ComputeFileSha256(string path)
		{
			using (var algorithm = SHA256.Create())
			using (var stream = File.OpenRead(path))
			{
				return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
			}
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

		private static int PopulateRequiredReleasePackage(string packageRoot)
		{
			var entries = new[]
			{
				"MetaCompanion.dll",
				"Install-MetaCompanion.ps1",
				"Wait-AndInstall-MetaCompanion.ps1",
				"tools/Sync-HdtArenaAdvisorData.ps1",
				"tools/Sync-BlizzardCardPools.ps1",
				"tools/Update-AdvisorBehaviorPrior.ps1",
				"solver/launch_solver.py",
				"solver/metacompanion_solver/__main__.py",
				"solver/metacompanion_solver/card_pool.py",
				"solver/metacompanion_solver/card_rules.py",
				"solver/metacompanion_solver/behavior_candidate_alignment.py",
				"solver/metacompanion_solver/behavior.py",
				"solver/metacompanion_solver/behavior_learning.py",
				"solver/metacompanion_solver/behavior_prior.py",
				"solver/metacompanion_solver/cli.py",
				"solver/metacompanion_solver/counterplay.py",
				"solver/metacompanion_solver/decision_frame.py",
				"solver/metacompanion_solver/decision_ranker.py",
				"solver/metacompanion_solver/decision_solver_evaluation.py",
				"solver/metacompanion_solver/evaluation.py",
				"solver/metacompanion_solver/hdt_rule_evaluation.py",
				"solver/metacompanion_solver/hdt_replay_behavior.py",
				"solver/metacompanion_solver/observed_policy_evaluation.py",
				"solver/metacompanion_solver/rust_worker_client.py",
				"solver/metacompanion_solver/visible_response_evaluation.py",
				"solver/metacompanion_solver/search.py",
				"solver/metacompanion_solver/trajectory.py",
				"solver/metacompanion_solver/verification.py",
				"solver/metacompanion_solver/turnpair_evaluation.py",
				"solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json",
				"solver/fixtures/oracle-hdt-cardrules-v1.json",
				"solver/fixtures/oracle-turn-v1.json",
				"solver/fixtures/oracle-turnpair-v1.json",
				"solver/fixtures/visible-response-v1.json",
				"solver/fixtures/trajectory-readiness-policy-v1.json",
				"solver/fixtures/trajectory-readiness-v1.jsonl",
				"solver/fixtures/behavior-learning-readiness-policy-v1.json",
				"solver/fixtures/behavior-learning-readiness-v1.jsonl",
				"solver/fixtures/behavior-candidate-alignment-policy-v1.json",
				"solver/fixtures/behavior-prior-readiness-policy-v1.json",
				"solver/fixtures/behavior-prior-readiness-v1.jsonl",
				"solver/fixtures/behavior-prior-readiness-v1.manifest.json",
				"solver/tools/observed_policy_fixture.py",
				"docs/ADVISOR-DATA.md",
				"docs/OFFICIAL-CARD-POOLS.md"
			};
			foreach (var entry in entries)
			{
				var path = Path.Combine(packageRoot, entry.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, "release gate package fixture");
			}
			return entries.Length;
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

