using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text.RegularExpressions;

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
					LocalRecommendationHistoryMatches = 9,
					LocalRecommendationWeight = 0.42,
					LocalMetaMinConfidence = 40
				});

			StringAssert.Contains(args, "-LocalMeta");
			StringAssert.Contains(args, "-PersonalRecommendations");
			StringAssert.Contains(args, "-RecommendationTop 12");
			StringAssert.Contains(args, "-PersonalRecommendationHistoryDays 5");
			StringAssert.Contains(args, "-PersonalRecommendationHistoryMatches 9");
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
					EnablePostGameDataRefresh = true,
					PostGameDataRefreshMaxDecks = 111,
					PostGamePremiumRefreshMaxDecks = 22,
					PostGameDataRefreshParallelism = 3
				},
				true,
				"CURRENT_PATCH",
				"CURRENT_PATCH",
				"CURRENT_PATCH",
				true);

			StringAssert.Contains(args, "-RankRanges \"DIAMOND_THROUGH_LEGEND\"");
			Assert.IsFalse(args.Contains("DIAMOND_FOUR_THROUGH_DIAMOND_ONE"), args);
			StringAssert.Contains(args, "-LimitPerRange 250");
			StringAssert.Contains(args, "-MaxDecks 111");
			StringAssert.Contains(args, "-Parallelism 3");
			StringAssert.Contains(args, "-Premium");
			StringAssert.Contains(args, "-Meta");
			StringAssert.Contains(args, "-Branches");
			StringAssert.Contains(args, "-PremiumTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-MetaTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-BranchCandidateTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-PremiumMaxDecks 22");
			StringAssert.Contains(args, "-PremiumStopOnUnsupported");
		}

		[TestMethod]
		public void PremiumTimeRangeContract_UsesStrictModeOnlyForFullRangesAndRetriesSemanticFallback()
		{
			Assert.IsFalse(PostGameMetaRefresher.PremiumTimeRangeSupportsAllEndpoints("LAST_7_DAYS"));
			Assert.IsTrue(PostGameMetaRefresher.PremiumTimeRangeSupportsAllEndpoints("LAST_30_DAYS"));
			Assert.IsTrue(PostGameMetaRefresher.PremiumTimeRangeSupportsAllEndpoints("CURRENT_PATCH"));

			var strictSameRangePlan = new PostGameRefreshPlan
			{
				IncludeFullDataRefresh = true,
				PrimaryTimeRange = "CURRENT_PATCH",
				MetaFallbackTimeRange = "CURRENT_PATCH",
				PremiumFallbackTimeRange = "CURRENT_PATCH"
			};
			var tolerantSameRangePlan = new PostGameRefreshPlan
			{
				IncludeFullDataRefresh = true,
				PrimaryTimeRange = "LAST_7_DAYS",
				MetaFallbackTimeRange = "LAST_7_DAYS",
				PremiumFallbackTimeRange = "LAST_7_DAYS"
			};
			var snapshotSameRangePlan = new PostGameRefreshPlan
			{
				IncludeFullDataRefresh = true,
				IncludeDeckSnapshotRefresh = true,
				PrimaryTimeRange = "LAST_7_DAYS",
				MetaFallbackTimeRange = "LAST_7_DAYS",
				PremiumFallbackTimeRange = "LAST_7_DAYS"
			};

			Assert.IsTrue(strictSameRangePlan.ShouldRetryWithFallback);
			Assert.IsFalse(tolerantSameRangePlan.ShouldRetryWithFallback);
			Assert.IsTrue(snapshotSameRangePlan.ShouldRetryWithFallback);
			var primaryArgs = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig { EnablePostGameDataRefresh = true },
				strictSameRangePlan);
			var fallbackArgs = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig { EnablePostGameDataRefresh = true },
				strictSameRangePlan,
				true);
			StringAssert.Contains(primaryArgs, "-PremiumStopOnUnsupported");
			Assert.IsFalse(fallbackArgs.Contains("-PremiumStopOnUnsupported"), fallbackArgs);
		}

		[TestMethod]
		public void BuildArguments_CanRefreshPublicDeckSnapshotWithoutPremium()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig(),
				new PostGameRefreshPlan
				{
					IncludeDeckSnapshotRefresh = true,
					IncludePersonalRecommendations = false
				});

			Assert.IsFalse(HasStandaloneSwitch(args, "LocalMeta"), args);
			StringAssert.Contains(args, "-RankRanges");
			Assert.IsFalse(HasStandaloneSwitch(args, "PersonalRecommendations"), args);
			StringAssert.Contains(args, "-SkipPersonalRecommendations");
			Assert.IsFalse(HasStandaloneSwitch(args, "Premium"), args);
			Assert.IsFalse(HasStandaloneSwitch(args, "PremiumTimeRange"), args);
			Assert.IsFalse(HasStandaloneSwitch(args, "MetaTimeRange"), args);
		}

		[TestMethod]
		public void BuildArguments_PostGameFullRefreshLeavesFinalOutputsToCanonicalCSharpRefresh()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig
				{
					EnablePostGameDataRefresh = true,
					PostGameDataRefreshMaxDecks = 111,
					PostGamePremiumRefreshMaxDecks = 22,
					PostGameDataRefreshParallelism = 3
				},
				new PostGameRefreshPlan
				{
					IncludeDeckSnapshotRefresh = true,
					IncludeFullDataRefresh = true,
					IncludePersonalRecommendations = true,
					PrimaryTimeRange = "CURRENT_PATCH",
					MetaFallbackTimeRange = "LAST_1_DAY",
					PremiumFallbackTimeRange = "LAST_7_DAYS"
				});

			Assert.IsFalse(HasStandaloneSwitch(args, "LocalMeta"), args);
			Assert.IsFalse(HasStandaloneSwitch(args, "PersonalRecommendations"), args);
			Assert.IsTrue(HasStandaloneSwitch(args, "SkipPersonalRecommendations"), args);
			Assert.IsTrue(HasStandaloneSwitch(args, "Premium"), args);
			Assert.IsTrue(HasStandaloneSwitch(args, "Meta"), args);
			Assert.IsTrue(HasStandaloneSwitch(args, "Branches"), args);
			StringAssert.Contains(args, "-PremiumTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-MetaTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-BranchCandidateTimeRange \"CURRENT_PATCH\"");
		}

		[TestMethod]
		public void BuildArguments_PostGameFallbackKeepsCurrentPatchMetaAndBranchesPinned()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig { EnablePostGameDataRefresh = true },
				new PostGameRefreshPlan
				{
					IncludeDeckSnapshotRefresh = true,
					IncludeFullDataRefresh = true,
					IncludePersonalRecommendations = true,
					PrimaryTimeRange = "CURRENT_PATCH",
					MetaFallbackTimeRange = "LAST_1_DAY",
					PremiumFallbackTimeRange = "LAST_7_DAYS"
				},
				true);

			Assert.IsFalse(HasStandaloneSwitch(args, "LocalMeta"), args);
			Assert.IsFalse(HasStandaloneSwitch(args, "PersonalRecommendations"), args);
			Assert.IsTrue(HasStandaloneSwitch(args, "SkipPersonalRecommendations"), args);
			StringAssert.Contains(args, "-PremiumTimeRange \"LAST_7_DAYS\"");
			StringAssert.Contains(args, "-MetaTimeRange \"CURRENT_PATCH\"");
			StringAssert.Contains(args, "-BranchCandidateTimeRange \"CURRENT_PATCH\"");
			Assert.IsFalse(args.Contains("-MetaTimeRange \"LAST_1_DAY\""), args);
		}

		[TestMethod]
		public void BuildArguments_RollingFallbackKeepsMetaAndBranchesOnSameScope()
		{
			var args = PostGameMetaRefresher.BuildArguments(
				@"C:\MetaCompanion Tools\Update-MetaCompanionData.ps1",
				new PluginConfig
				{
					EnablePostGameDataRefresh = true,
					PostGameRankRange = "LEGEND"
				},
				new PostGameRefreshPlan
				{
					IncludeDeckSnapshotRefresh = true,
					IncludeFullDataRefresh = true,
					PrimaryTimeRange = "LAST_3_DAYS",
					MetaFallbackTimeRange = "LAST_1_DAY",
					PremiumFallbackTimeRange = "LAST_7_DAYS"
				},
				true);

			StringAssert.Contains(args, "-RankRanges \"LEGEND\"");
			StringAssert.Contains(args, "-RemoteRankRange \"LEGEND\"");
			StringAssert.Contains(args, "-PremiumTimeRange \"LAST_7_DAYS\"");
			StringAssert.Contains(args, "-MetaTimeRange \"LAST_1_DAY\"");
			StringAssert.Contains(args, "-BranchCandidateTimeRange \"LAST_1_DAY\"");
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsPublicDeckSnapshotWhenTrackedFilesAreMissingWithoutCookie()
		{
			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				new DateTime(2026, 6, 12, 12, 0, 0));

			Assert.IsTrue(plan.IncludeDeckSnapshotRefresh);
			Assert.IsFalse(plan.IncludeFullDataRefresh);
			Assert.IsFalse(plan.IncludePersonalRecommendations);
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsFullDataRefreshWhenTrackedFilesAreMissingWithCookie()
		{
			WritePremiumCookie();

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				new DateTime(2026, 6, 12, 12, 0, 0));

			Assert.IsTrue(plan.IncludeDeckSnapshotRefresh);
			Assert.IsTrue(plan.IncludeFullDataRefresh);
			Assert.IsTrue(plan.IncludePersonalRecommendations);
			Assert.AreEqual("LAST_7_DAYS", plan.PrimaryTimeRange);
			Assert.AreEqual("LAST_1_DAY", plan.MetaFallbackTimeRange);
			Assert.AreEqual("LAST_7_DAYS", plan.PremiumFallbackTimeRange);
		}

		[TestMethod]
		public void BuildRefreshPlan_SkipsFullDataRefreshWhenAttemptMarkerIsFresh()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteFile(PostGameMetaRefresher.GetDataRefreshAttemptPath(_tempDirectory), "attempt", now);

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
			Assert.IsFalse(plan.IncludePersonalRecommendations);
		}

		[TestMethod]
		public void BuildRefreshPlan_SkipsFullDataRefreshWhenTrackedFilesAreFresh()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteTrackedFiles(now.AddHours(-2));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
			Assert.IsTrue(plan.IncludePersonalRecommendations);
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
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsFalse(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsFullDataRefreshWhenBranchSnapshotIsStaleWithCookie()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WritePremiumCookie();
			WriteTrackedFiles(now.AddHours(-2));
			File.SetLastWriteTime(
				PostGameMetaRefresher.GetBranchSnapshotPath(_tempDirectory),
				now.AddHours(-25));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsTrue(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsFullDataRefreshWhenAnyPrimaryTrackedFileIsStale()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WritePremiumCookie();
			WriteTrackedFiles(now.AddHours(-2));
			File.SetLastWriteTime(
				PostGameMetaRefresher.GetMetaMatrixPath(_tempDirectory),
				now.AddHours(-25));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsTrue(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_RequestsRefreshWhenRemoteRankScopeChanges()
		{
			var now = new DateTime(2026, 8, 4, 12, 0, 0);
			WritePremiumCookie();
			WriteFile(PostGameMetaRefresher.GetDeckSnapshotPath(_tempDirectory), "# Count: 1", now);
			WriteFile(
				PostGameMetaRefresher.GetBranchSnapshotPath(_tempDirectory),
				"# CandidateTimeRange: LAST_3_DAYS" + Environment.NewLine +
				"# RankRange: DIAMOND_THROUGH_LEGEND" + Environment.NewLine,
				now);
			WriteFile(
				PostGameMetaRefresher.GetMetaSummaryPath(_tempDirectory),
				"{\"time_range\":\"LAST_3_DAYS\",\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"}",
				now);
			WriteFile(PostGameMetaRefresher.GetMetaMatrixPath(_tempDirectory), "{}", now);
			var config = RefreshEnabledConfig();
			config.PostGamePrimaryTimeRange = "LAST_3_DAYS";
			config.PostGameRankRange = "LEGEND";

			var plan = PostGameMetaRefresher.BuildRefreshPlan(config, _tempDirectory, now);

			Assert.IsTrue(plan.IncludeFullDataRefresh);
		}

		[TestMethod]
		public void BuildRefreshPlan_ReusesPremiumCacheForPersonalRecommendationsWithoutCookie()
		{
			var now = new DateTime(2026, 6, 12, 12, 0, 0);
			WriteTrackedFiles(now.AddHours(-26));

			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				RefreshEnabledConfig(),
				_tempDirectory,
				now);

			Assert.IsTrue(plan.IncludeDeckSnapshotRefresh);
			Assert.IsFalse(plan.IncludeFullDataRefresh);
			Assert.IsTrue(plan.IncludePersonalRecommendations);
		}

		[TestMethod]
		public void BuildRefreshPlan_DoesNotRunPersonalRecommendationsWhenDataRefreshDisabledWithoutCache()
		{
			var plan = PostGameMetaRefresher.BuildRefreshPlan(
				new PluginConfig { EnablePostGameDataRefresh = false },
				_tempDirectory,
				new DateTime(2026, 6, 12, 12, 0, 0));

			Assert.IsFalse(plan.IncludeDeckSnapshotRefresh);
			Assert.IsFalse(plan.IncludeFullDataRefresh);
			Assert.IsFalse(plan.IncludePersonalRecommendations);
		}

		private void WriteTrackedFiles(DateTime lastWriteTime)
		{
			WriteFile(PostGameMetaRefresher.GetDeckSnapshotPath(_tempDirectory), "# Count: 1", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetBranchSnapshotPath(_tempDirectory), "Deck\tAA==", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetMetaSummaryPath(_tempDirectory), "{}", lastWriteTime);
			WriteFile(PostGameMetaRefresher.GetMetaMatrixPath(_tempDirectory), "{}", lastWriteTime);
		}

		private static PluginConfig RefreshEnabledConfig()
		{
			return new PluginConfig { EnablePostGameDataRefresh = true };
		}

		private void WritePremiumCookie()
		{
			WriteFile(
				PostGameMetaRefresher.GetPremiumCookiePath(_tempDirectory),
				"sessionid=test",
				DateTime.Now);
		}

		private static void WriteFile(string path, string contents, DateTime lastWriteTime)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, contents);
			File.SetLastWriteTime(path, lastWriteTime);
		}

		private static bool HasStandaloneSwitch(string args, string switchName)
		{
			return Regex.IsMatch(args, @"(^|\s)-" + Regex.Escape(switchName) + @"(\s|$)");
		}
	}
}
