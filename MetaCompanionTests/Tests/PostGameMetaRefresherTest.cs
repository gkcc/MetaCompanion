using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PostGameMetaRefresherTest
	{
		private string _tempDirectory;

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
		public void BuildArguments_UsesLocalMetaAndConfigValues()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig
				{
					LocalRecommendationTop = 12,
					LocalRecommendationHistoryDays = 5,
					LocalRecommendationWeight = 0.42,
					LocalMetaMinConfidence = 40
				});

			StringAssert.Contains(args, "-LocalMeta");
			StringAssert.Contains(args, "-PersonalRecommendations");
			StringAssert.Contains(args, "-RecommendationTop 12");
			StringAssert.Contains(args, "-PersonalRecommendationHistoryDays 5");
			StringAssert.Contains(args, "-PersonalRecommendationLocalWeight 0.42");
			StringAssert.Contains(args, "-LocalMetaMinConfidence 40");
			StringAssert.Contains(args, "\"C:\\MetaCompanion Tools\\Update-MetaCompanionData.ps1\"");
		}

		[TestMethod]
		public void BuildArguments_IncludesFullDataRefreshWhenRequested()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig
				{
					PostGameDataRefreshMaxDecks = 111,
					PostGamePremiumRefreshMaxDecks = 22,
					PostGameDataRefreshParallelism = 3
				},
				true,
				"CURRENT_PATCH",
				"LAST_3_DAYS",
				"LAST_7_DAYS",
				true);

			StringAssert.Contains(args,
				"-RankRanges \"DIAMOND_THROUGH_LEGEND,DIAMOND_FOUR_THROUGH_DIAMOND_ONE,PLATINUM,GOLD,BRONZE_THROUGH_GOLD\"");
			StringAssert.Contains(args, "-LimitPerRange 250");
			StringAssert.Contains(args, "-MaxDecks 111");
			StringAssert.Contains(args, "-Parallelism 3");
			StringAssert.Contains(args, "-Premium");
			StringAssert.Contains(args, "-Meta");
			Assert.IsFalse(args.Contains("-Branches"));
			StringAssert.Contains(args, "-PremiumTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-MetaTimeRange \"LAST_3_DAYS\"");
			StringAssert.Contains(args, "-PremiumMaxDecks 22");
			StringAssert.Contains(args, "-PremiumStopOnUnsupported");
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsFullDataRefreshWhenTrackedFilesAreMissing()
		{
			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig(),
				_tempDirectory,
				new DateTime(2026, 6, 12, 12, 0, 0));

			Assert.IsTrue(plan.IncludeFullDataRefresh);
			Assert.AreEqual("CURRENT_PATCH", plan.PrimaryTimeRange);
			Assert.AreEqual("LAST_3_DAYS", plan.MetaFallbackTimeRange);
			Assert.AreEqual("LAST_7_DAYS", plan.PremiumFallbackTimeRange);
		}

		[TestMethod]
		public void BuildRefreshPlan_SkipsFullDataRefreshWhenAttemptMarkerIsFresh()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteFile(PostGameMetaRefresher.GetDataRefreshAttemptPath(_tempDirectory), "attempt", now);

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_SkipsFullDataRefreshWhenTrackedFilesAreFresh()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteTrackedFiles(now.AddHours(-2));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_IgnoresStaleBranchFallbackFile()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteTrackedFiles(now.AddHours(-2));
			File.SetLastWriteTime(
				PostGameMetaRefresher.GetBranchSnapshotPath(_tempDirectory),
				now.AddHours(-25));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsFullDataRefreshWhenAnyPrimaryTrackedFileIsStale()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteTrackedFiles(now.AddHours(-2));
			File.SetLastWriteTime(
				PostGameMetaRefresher.GetMetaMatrixPath(_tempDirectory),
				now.AddHours(-25));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig(),
				_tempDirectory,
				now);

			Assert.IsTrue(plan.IncludeFullDataRefresh);
		}

		private void WriteTrackedFiles(DateTime lastWriteTime)
		{
			WriteFile(PostGameMetaRefresher.GetDeckSnapshotPath(_tempDirectory), "# Count: 1", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetBranchSnapshotPath(_tempDirectory), "Deck\tAA==", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetMetaSummaryPath(_tempDirectory), "{}", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetMetaMatrixPath(_tempDirectory), "{}", lastWriteTime);
		}

		private static void WriteFile(string path, string contents, DateTime lastWriteTime)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents);
			File.SetLastWriteTime(path, lastWriteTime);
		}
	}
}
