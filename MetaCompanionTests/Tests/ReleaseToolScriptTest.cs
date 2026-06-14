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

			StringAssert.Contains(script, "standard-menu-dashboard");
			StringAssert.Contains(script, "gameplay-hide-dashboard");
			StringAssert.Contains(script, "remaining-cards-panel");
			StringAssert.Contains(script, "post-game-refresh");
			StringAssert.Contains(script, "Read-SmokeCheckpoint");
			Assert.IsFalse(script.Contains("Start Ranked"), script);
			Assert.IsFalse(script.Contains("Click Play"), script);
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
