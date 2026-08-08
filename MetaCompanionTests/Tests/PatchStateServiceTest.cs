using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PatchStateServiceTest
	{
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				"MetaCompanionPatchStateTests-" + Guid.NewGuid().ToString("N"));
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
		public void EnsureCurrentPatchState_ArchivesActiveLocalFilesWhenPatchChanges()
		{
			Write("patch_version.txt", "35.6.2");
			Write("patch_marker.txt", "2026-06-12T01:00:00+08:00");
			Write("match_history.tsv", "old match");
			Write("prediction_timeline.tsv", "old timeline");
			Write("local_meta_summary.json", "{}");
			Write(System.IO.Path.Combine("Premium", "Meta", "latest", "personal_recommendations.tsv"), "old recs");
			Write(System.IO.Path.Combine("Premium", "Meta", "latest", "summary.json"), "remote cache");

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.0.12345",
					PatchTime = new DateTime(2026, 7, 7, 19, 16, 0)
				},
				new DateTime(2026, 7, 8, 12, 0, 0));

			Assert.IsTrue(result.PatchChanged);
			Assert.AreEqual("36.0.0.12345", result.PatchVersion);
			Assert.AreEqual(4, result.ArchivedFileCount);
			Assert.IsFalse(File.Exists(ResolvePath("match_history.tsv")));
			Assert.IsFalse(File.Exists(ResolvePath("prediction_timeline.tsv")));
			Assert.IsFalse(File.Exists(ResolvePath("local_meta_summary.json")));
			Assert.IsFalse(File.Exists(ResolvePath(System.IO.Path.Combine("Premium", "Meta", "latest", "personal_recommendations.tsv"))));
			Assert.IsTrue(File.Exists(ResolvePath(System.IO.Path.Combine("Premium", "Meta", "latest", "summary.json"))));
			Assert.AreEqual("36.0.0.12345", File.ReadAllText(ResolvePath("patch_version.txt"), Encoding.UTF8).Trim());
			StringAssert.Contains(
				File.ReadAllText(ResolvePath("patch_epoch.txt"), Encoding.UTF8),
				"36.0.0.12345@2026-07-07T19:16:00");
			StringAssert.Contains(
				File.ReadAllText(ResolvePath("patch_marker.txt"), Encoding.UTF8),
				"2026-07-07T19:16:00");
			Assert.IsTrue(File.Exists(Path.Combine(result.ArchiveDirectory, "match_history.tsv")));
		}

		[TestMethod]
		public void EnsureCurrentPatchState_DoesNotArchiveWhenStateIsCurrent()
		{
			Write("patch_version.txt", "36.0.0");
			Write("patch_marker.txt", "2026-07-07T19:16:00");
			Write("match_history.tsv", "current match");

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.0",
					PatchTime = new DateTime(2026, 7, 7, 19, 16, 0)
				},
				new DateTime(2026, 7, 8, 12, 0, 0));

			Assert.IsFalse(result.PatchChanged);
			Assert.AreEqual(0, result.ArchivedFileCount);
			Assert.IsTrue(File.Exists(ResolvePath("match_history.tsv")));
		}

		[TestMethod]
		public void EnsureCurrentPatchState_AdvancesEpochWhenReliableTimestampIsNewer()
		{
			Write("patch_version.txt", "36.0.3.247416");
			Write("patch_marker.txt", "2026-07-07T19:16:55");
			Write("match_history.tsv", "stale local match");
			var reliableProductDbTime = new DateTime(2026, 7, 22, 2, 6, 1);

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.3.247416",
					PatchTime = reliableProductDbTime,
					Source = @"F:\Hearthstone\.product.db"
				},
				new DateTime(2026, 7, 22, 12, 0, 0));

			Assert.IsTrue(result.PatchChanged);
			Assert.AreEqual(1, result.ArchivedFileCount);
			Assert.AreEqual(reliableProductDbTime, result.PatchTime);
			Assert.AreEqual(
				"2026-07-22T02:06:01.0000000",
				File.ReadAllText(ResolvePath("patch_marker.txt"), Encoding.UTF8).Trim());
			Assert.AreEqual(
				"36.0.3.247416@2026-07-22T02:06:01.0000000",
				File.ReadAllText(ResolvePath("patch_epoch.txt"), Encoding.UTF8).Trim());
			Assert.IsFalse(File.Exists(ResolvePath("match_history.tsv")));
		}

		[TestMethod]
		public void EnsureCurrentPatchState_UsesDetectionTimeWhenVersionChangesAndTimestampIsStale()
		{
			Write("patch_version.txt", "36.0.0.12345");
			Write("patch_marker.txt", "2026-07-07T19:16:55");
			Write("match_history.tsv", "pre-patch local match");
			var detectionTime = new DateTime(2026, 7, 22, 18, 53, 59);

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.3.247416",
					PatchTime = new DateTime(2026, 7, 7, 19, 16, 55),
					Source = @"F:\Hearthstone\Hearthstone.exe"
				},
				detectionTime);

			Assert.IsTrue(result.PatchChanged);
			Assert.AreEqual(detectionTime, result.PatchTime);
			Assert.AreEqual(1, result.ArchivedFileCount);
			Assert.IsFalse(File.Exists(ResolvePath("match_history.tsv")));
			Assert.AreEqual(
				"2026-07-22T18:53:59.0000000",
				File.ReadAllText(ResolvePath("patch_marker.txt"), Encoding.UTF8).Trim());
			Assert.AreEqual(
				"36.0.3.247416@2026-07-22T18:53:59.0000000",
				File.ReadAllText(ResolvePath("patch_epoch.txt"), Encoding.UTF8).Trim());
		}

		[TestMethod]
		public void PowerShellPatchState_PrefersProductDbTimeAndUsesNowForStaleVersionBoundary()
		{
			var repoRoot = FindRepoRoot();
			var dataDirectory = ResolvePath("Data");
			var gameDirectory = ResolvePath("Game");
			var exePath = System.IO.Path.Combine(gameDirectory, "Hearthstone.exe");
			var productDbPath = System.IO.Path.Combine(gameDirectory, ".product.db");
			Directory.CreateDirectory(dataDirectory);
			Directory.CreateDirectory(gameDirectory);
			File.WriteAllText(exePath, string.Empty, Encoding.ASCII);
			File.WriteAllText(productDbPath, "36.0.3.247416", Encoding.ASCII);
			File.WriteAllText(System.IO.Path.Combine(dataDirectory, "patch_version.txt"), "36.0.0.12345", Encoding.UTF8);
			File.WriteAllText(System.IO.Path.Combine(dataDirectory, "patch_marker.txt"), "2026-07-07T19:16:55", Encoding.UTF8);
			File.WriteAllText(System.IO.Path.Combine(dataDirectory, "match_history.tsv"), "pre-patch local match", Encoding.UTF8);

			var wrapperPath = ResolvePath("verify-patch-state.ps1");
			var updateScriptPath = System.IO.Path.Combine(
				repoRoot, "tools", "Update-MetaCompanionPatchState.ps1");
			var script =
				"$ErrorActionPreference = 'Stop'\r\n" +
				". " + QuotePowerShellString(updateScriptPath) + "\r\n" +
				"$fakeExe = " + QuotePowerShellString(exePath) + "\r\n" +
				"function Resolve-MetaCompanionHearthstoneExePath { return $fakeExe }\r\n" +
				"$productDb = " + QuotePowerShellString(productDbPath) + "\r\n" +
				"$productTime = [datetime]'2026-07-22T02:06:01'\r\n" +
				"[System.IO.File]::SetLastWriteTime($productDb, $productTime)\r\n" +
				"$detected = Get-MetaCompanionDetectedPatch -PatchVersion '36.0.3.247416'\r\n" +
				"if ([datetime]$detected.PatchTime -ne $productTime) { throw 'Explicit version did not retain .product.db time.' }\r\n" +
				"$oldTime = [datetime]'2026-07-07T19:16:55'\r\n" +
				"$now = [datetime]'2026-07-22T18:53:59'\r\n" +
				"[System.IO.File]::SetLastWriteTime($productDb, $oldTime)\r\n" +
				"$result = Update-MetaCompanionPatchState -DataDirectory " + QuotePowerShellString(dataDirectory) +
					" -PatchVersion '36.0.3.247416' -Now $now\r\n" +
				"if ([datetime]$result.PatchTime -ne $now) { throw 'Stale version boundary did not advance to Now.' }\r\n" +
				"if ([int]$result.ArchivedFileCount -ne 1) { throw 'Pre-patch history was not archived.' }\r\n" +
				"if (Test-Path -LiteralPath " + QuotePowerShellString(System.IO.Path.Combine(dataDirectory, "match_history.tsv")) +
					") { throw 'Pre-patch history remains active.' }\r\n" +
				"$marker = [datetime](Get-Content -LiteralPath " +
					QuotePowerShellString(System.IO.Path.Combine(dataDirectory, "patch_marker.txt")) + " -Raw)\r\n" +
				"if ($marker -ne $now) { throw 'patch_marker.txt was not advanced to Now.' }\r\n" +
				"Write-Output 'PATCH STATE OK'\r\n";
			File.WriteAllText(wrapperPath, script, new UTF8Encoding(false));

			var result = RunPowerShell(wrapperPath);
			Assert.AreEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "PATCH STATE OK");
		}

		[TestMethod]
		public void EnsureCurrentPatchState_PreservesNewerManualMarkerInEpoch()
		{
			Write("patch_version.txt", "36.0.3.247416");
			Write("patch_marker.txt", "2026-07-22T08:00:00");
			Write("match_history.tsv", "current local match");

			var result = PatchStateService.EnsureCurrentPatchState(
				_tempDirectory,
				new HearthstonePatchInfo
				{
					Version = "36.0.3.247416",
					PatchTime = new DateTime(2026, 7, 22, 2, 6, 1)
				},
				new DateTime(2026, 7, 22, 12, 0, 0));

			Assert.IsFalse(result.PatchChanged);
			Assert.AreEqual(new DateTime(2026, 7, 22, 8, 0, 0), result.PatchTime);
			StringAssert.Contains(result.PatchEpoch, "@2026-07-22T08:00:00");
			Assert.IsTrue(File.Exists(ResolvePath("match_history.tsv")));
		}

		private void Write(string relativePath, string value)
		{
			var path = ResolvePath(relativePath);
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
			File.WriteAllText(path, value, Encoding.UTF8);
		}

		private string ResolvePath(string relativePath)
		{
			return System.IO.Path.Combine(_tempDirectory, relativePath);
		}

		private static string FindRepoRoot()
		{
			foreach (var startPath in new[]
			{
				Directory.GetCurrentDirectory(),
				AppDomain.CurrentDomain.BaseDirectory
			})
			{
				var current = new DirectoryInfo(startPath);
				while (current != null)
				{
					if (File.Exists(System.IO.Path.Combine(
						current.FullName, "tools", "Update-MetaCompanionPatchState.ps1")))
					{
						return current.FullName;
					}
					current = current.Parent;
				}
			}
			throw new DirectoryNotFoundException("Could not locate the MetaCompanion repo root.");
		}

		private static ProcessResult RunPowerShell(string scriptPath)
		{
			var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			var powerShell = System.IO.Path.Combine(
				windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
			if (!File.Exists(powerShell))
			{
				powerShell = "powershell.exe";
			}
			var startInfo = new ProcessStartInfo
			{
				FileName = powerShell,
				Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
				WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			using (var process = Process.Start(startInfo))
			{
				var output = process.StandardOutput.ReadToEnd() + Environment.NewLine +
					process.StandardError.ReadToEnd();
				process.WaitForExit();
				return new ProcessResult(process.ExitCode, output);
			}
		}

		private static string QuotePowerShellString(string value)
		{
			return "'" + value.Replace("'", "''") + "'";
		}

		private sealed class ProcessResult
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
