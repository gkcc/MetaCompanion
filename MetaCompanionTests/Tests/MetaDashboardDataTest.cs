using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaDashboardDataTest
	{
		private string _tempDirectory;

		[TestInitialize]
		public void Initialize()
		{
			_tempDirectory = Path.Combine(
				Path.GetTempPath(), "MetaCompanionTests", Guid.NewGuid().ToString("N"));
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
		public void Load_ReadsRecommendationsEnvironmentLastGameAndReplayLinks()
		{
			var recommendationDirectory = Path.Combine(_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(recommendationDirectory);
			File.WriteAllText(
				Path.Combine(recommendationDirectory, "personal_recommendations.tsv"),
				"rank\tname\tplayer_class\texpected_win_rate\tcoverage_pct" + Environment.NewLine +
				"1\t\u5143\u7d20\u8428\tSHAMAN\t58.13\t99.03" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(recommendationDirectory, "summary.json"),
				"{" +
				"\"generated_at\":\"2026-06-13T00:42:46+08:00\"," +
				"\"as_of\":\"2026-06-12T09:21:35Z\"," +
				"\"time_range\":\"CURRENT_PATCH\"," +
				"\"patch_version\":\"35.6.2\"," +
				"\"game_type\":\"RANKED_STANDARD\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"," +
				"\"region\":\"ALL\"" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(recommendationDirectory, "manifest.json"),
				"{" +
				"\"selected_time_range\":\"CURRENT_PATCH\"," +
				"\"patch_version\":\"35.6.2\"" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_environment.tsv"),
				"rank\tarchetype_id\tname\tplayer_class\tgames\tweighted_games\tlocal_pct\tavg_confidence\twins\tlosses\twin_rate" + Environment.NewLine +
				"1\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t3.4\t52.2\t95\t3\t1\t75" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\tcandidate_archetypes\tkey_evidence_cards" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t95\t" +
				"\u704c\u6ce8\u8d3c:95% score=480 branchCount=4 / \u6d77\u76d7\u8d3c:38% score=180 branchCount=2 / \u5947\u8ff9\u8d3c:12% score=70 branchCount=1\t" +
				"\u8ff7\u4f60\u5305,\u9634\u5f71\u6b65" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "hdt_opponent_history.tsv"),
				"game_id\tresult\topponent_hero\treplay_path\thsreplay_url" + Environment.NewLine +
				"g1\tWin\tRogue\tC:\\HDT\\Replays\\g1.hdtreplay\thttps://hsreplay.net/uploads/upload/g1/" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsTrue(snapshot.HasContent);
			Assert.IsFalse(snapshot.HasReadIssue);
			StringAssert.StartsWith(snapshot.UserStatusMessage, "\u6b63\u5e38\uff1a");
			Assert.AreEqual("\u5143\u7d20\u8428", snapshot.Recommendations[0].Title);
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "\u9884\u671f 58.13%");
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "\u8986\u76d6 99.03%");
			StringAssert.Contains(snapshot.Recommendations[0].ToolTip, "HSReplay \u5bf9\u9635\u77e9\u9635");
			Assert.AreEqual("\u4efb\u52a1\u7267", snapshot.Environment[0].Title);
			Assert.AreEqual("4 \u5c40 / 100%", snapshot.Environment[0].Detail);
			StringAssert.Contains(snapshot.Environment[0].ToolTip, "\u5168\u6837\u672c\u5360\u6bd4");
			StringAssert.Contains(snapshot.Environment[0].ToolTip, "\u80dc\u8d1f 3-1");
			Assert.AreEqual(1, snapshot.EnvironmentClasses.Count);
			Assert.AreEqual("PRIEST", snapshot.EnvironmentClasses[0].PlayerClass);
			Assert.AreEqual("\u7267\u5e08", snapshot.EnvironmentClasses[0].ClassName);
			Assert.AreEqual(52.2, snapshot.EnvironmentClasses[0].GlobalPct, 0.001);
			StringAssert.Contains(snapshot.Environment[0].ToolTip, "\u7267\u5e08");
			Assert.AreEqual("\u704c\u6ce8\u8d3c", snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u80dc\u5229 / \u5bf9\u9635 \u6f5c\u884c\u8005");
			StringAssert.Contains(snapshot.LastGame.Detail, "\u7f6e\u4fe1 95%");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u5f62\u6001\u7f6e\u4fe1\u5ea6");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u5339\u914d\u5206 480");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u5206\u652f 4");
			Assert.IsFalse(snapshot.LastGame.ToolTip.Contains("score="));
			Assert.IsFalse(snapshot.LastGame.ToolTip.Contains("branchCount="));
			Assert.AreEqual("g1", snapshot.LastGame.MatchId);
			Assert.AreEqual(3, snapshot.LastGame.Candidates.Count);
			Assert.AreEqual("\u704c\u6ce8\u8d3c", snapshot.LastGame.Candidates[0].Name);
			Assert.AreEqual(95, snapshot.LastGame.Candidates[0].ConfidencePercent);
			Assert.AreEqual(480, snapshot.LastGame.Candidates[0].Score);
			Assert.AreEqual(4, snapshot.LastGame.Candidates[0].BranchCount);
			CollectionAssert.Contains(snapshot.LastGame.KeyEvidenceCards, "\u8ff7\u4f60\u5305");
			Assert.IsFalse(snapshot.LastGame.IsLowConfidence);
			Assert.AreEqual("https://hsreplay.net/uploads/upload/g1/", snapshot.LastGame.HsReplayUrl);
			Assert.AreEqual("C:\\HDT\\Replays\\g1.hdtreplay", snapshot.LastGame.ReplayPath);
			Assert.IsTrue(snapshot.RemoteSource.HasData);
			Assert.AreEqual("CURRENT_PATCH", snapshot.RemoteSource.EffectiveTimeRange);
			Assert.AreEqual("RANKED_STANDARD", snapshot.RemoteSource.GameType);
			Assert.AreEqual("DIAMOND_THROUGH_LEGEND", snapshot.RemoteSource.RankRange);
			Assert.AreEqual("ALL", snapshot.RemoteSource.Region);
			Assert.AreEqual("35.6.2", snapshot.RemoteSource.PatchVersion);
			StringAssert.Contains(snapshot.RemoteSource.SettingsText, "35.6.2\u8865\u4e01\u540e");
			Assert.IsFalse(snapshot.RemoteSource.SettingsText.Contains("CURRENT_PATCH"));
			StringAssert.Contains(snapshot.RemoteSource.ToolTip, "\u6a21\u5f0f\uff1a\u6807\u51c6\u6a21\u5f0f\uff08\u5929\u68af\uff09");
			StringAssert.Contains(snapshot.RemoteSource.ToolTip, "\u5730\u533a\uff1a\u5168\u90e8\u5730\u533a");
			Assert.IsFalse(snapshot.RemoteSource.ToolTip.Contains("CURRENT_PATCH"));
			Assert.IsFalse(snapshot.RemoteSource.ToolTip.Contains("RANKED_STANDARD"));
			Assert.IsFalse(snapshot.RemoteSource.ToolTip.Contains("DIAMOND_THROUGH_LEGEND"));
			Assert.IsFalse(snapshot.RemoteSource.ToolTip.Contains("ALL"));
		}

		[TestMethod]
		public void Load_RecommendationClassFallbackUsesChineseDisplayWithoutRewritingTsv()
		{
			var recommendationDirectory = Path.Combine(
				_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(recommendationDirectory);
			var recommendationPath = Path.Combine(
				recommendationDirectory, "personal_recommendations.tsv");
			File.WriteAllText(
				recommendationPath,
				"rank\tname\tplayer_class" + Environment.NewLine +
				"1\t\u6d4b\u8bd5\u6d41\u6d3e\tSHAMAN" + Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u8428\u6ee1", snapshot.Recommendations[0].Detail);
			StringAssert.Contains(File.ReadAllText(recommendationPath, Encoding.UTF8), "SHAMAN");
		}

		[TestMethod]
		public void Load_ExactUnknownArchetypePlaceholderIsLocalizedOnlyForDisplay()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\t" +
					"candidate_archetypes" + Environment.NewLine +
				"g1\tunknown\tMage\tUnknown\t25\tUnknown:25% score=12 branchCount=1" +
					Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u672a\u8bc6\u522b\u6d41\u6d3e", snapshot.LastGame.Title);
			Assert.AreEqual("Unknown", snapshot.LastGame.Candidates[0].Name,
				"\u89e3\u6790\u5c42\u5e94\u4fdd\u7559\u5386\u53f2\u534f\u8bae\u539f\u503c\u3002");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u672a\u8bc6\u522b\u6d41\u6d3e 25%");
			Assert.IsFalse(snapshot.LastGame.Title.Contains("Unknown"));
			Assert.IsFalse(snapshot.LastGame.Detail.Contains("unknown"));
			Assert.IsFalse(snapshot.LastGame.ToolTip.Contains("Unknown"));
		}

		[TestMethod]
		public void Load_BuildsClassDistributionFromAllEnvironmentRowsAndTopFiveList()
		{
			WriteEnvironmentRows(
				"1\t142\t\u4efb\u52a1\u6cd5\tMAGE\t1\t1\t20\t98\t1\t0\t100",
				"2\t594\t\u6253\u8138\u6cd5\tMAGE\t2\t2\t10\t94\t1\t1\t50",
				"3\t56\t\u4efb\u52a1\u7267\tPRIEST\t3\t3\t30\t95\t2\t1\t66.67",
				"4\t842\t\u704c\u6ce8\u8d3c\tROGUE\t1\t1\t15\t95\t1\t0\t100",
				"5\t856\t\u5146\u793a\u8d3c\tROGUE\t1\t1\t5\t95\t0\t1\t0",
				"6\t865\t\u4f19\u4f34\u730e\tHUNTER\t1\t1\t3\t90\t1\t0\t100");

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual(5, snapshot.Environment.Count);
			Assert.AreEqual("\u4efb\u52a1\u7267", snapshot.Environment[0].Title);
			Assert.AreEqual("3 \u5c40 / 33.3%", snapshot.Environment[0].Detail);
			Assert.AreEqual("\u6253\u8138\u6cd5", snapshot.Environment[1].Title);
			Assert.AreEqual("2 \u5c40 / 22.2%", snapshot.Environment[1].Detail);
			Assert.AreEqual(4, snapshot.EnvironmentClasses.Count);

			var mage = snapshot.EnvironmentClasses.Single(row => row.PlayerClass == "MAGE");
			Assert.AreEqual("\u6cd5\u5e08", mage.ClassName);
			Assert.AreEqual(33.33, mage.SamplePct, 0.01);
			Assert.AreEqual(3, mage.Games);
			Assert.AreEqual(2, mage.Segments.Count);
			Assert.AreEqual("\u6253\u8138\u6cd5", mage.Segments[0].Title);
			Assert.AreEqual(66.67, mage.Segments[0].ClassSamplePct, 0.01);
			Assert.AreEqual(22.22, mage.Segments[0].SamplePct, 0.01);
			Assert.AreEqual("\u4efb\u52a1\u6cd5", mage.Segments[1].Title);
			Assert.AreEqual(33.33, mage.Segments[1].ClassSamplePct, 0.01);
			Assert.AreEqual(11.11, mage.Segments[1].SamplePct, 0.01);
			StringAssert.Contains(mage.ToolTip, "\u4efb\u52a1\u6cd5");
			StringAssert.Contains(mage.ToolTip, "\u804c\u4e1a\u5185");
			Assert.IsFalse(mage.ToolTip.Contains("\u52a0\u6743"));
		}

		[TestMethod]
		public void Load_WithCleanInstallDirectory_ReturnsEmptySnapshot()
		{
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsFalse(snapshot.HasContent);
			Assert.IsFalse(snapshot.HasReadIssue);
			StringAssert.StartsWith(snapshot.UserStatusMessage, "\u63d0\u793a\uff1a");
			StringAssert.Contains(snapshot.UserStatusMessage, "\u8bf7\u5148\u8fd0\u884c\u4e00\u6b21\u6570\u636e\u66f4\u65b0");
			Assert.AreEqual(0, snapshot.Recommendations.Count);
			Assert.AreEqual(0, snapshot.Environment.Count);
			Assert.AreEqual(0, snapshot.EnvironmentClasses.Count);
			Assert.IsNull(snapshot.LastGame);
			Assert.IsFalse(snapshot.UpdatedAt.HasValue);
		}

		[TestMethod]
		public void Load_WhenRecommendationFileIsLocked_ReturnsPartialDataAndSafeChineseStatus()
		{
			var recommendationDirectory = Path.Combine(
				_tempDirectory, "Premium", "Meta", "latest");
			Directory.CreateDirectory(recommendationDirectory);
			var recommendationPath = Path.Combine(
				recommendationDirectory, "personal_recommendations.tsv");
			File.WriteAllText(
				recommendationPath,
				"rank\tname\tplayer_class" + Environment.NewLine +
				"1\t\u5143\u7d20\u8428\tSHAMAN" + Environment.NewLine,
				Encoding.UTF8);
			WriteEnvironmentRows(
				"1\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t4\t45\t95\t3\t1\t75");

			MetaDashboardSnapshot snapshot;
			using (new FileStream(
				recommendationPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.None))
			{
				snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			}

			Assert.IsTrue(snapshot.HasReadIssue);
			Assert.IsTrue(snapshot.HasContent, "\u5176\u4ed6\u53ef\u7528\u6570\u636e\u5e94\u7ee7\u7eed\u8fd4\u56de\u3002");
			Assert.AreEqual(0, snapshot.Recommendations.Count);
			Assert.AreEqual(1, snapshot.Environment.Count);
			StringAssert.StartsWith(snapshot.UserStatusMessage, "\u9700\u5904\u7406\uff1a");
			StringAssert.Contains(
				snapshot.UserStatusMessage,
				"\u8bf7\u5728\u8bbe\u7f6e\u9875\u5237\u65b0\u6216\u91cd\u65b0\u751f\u6210\u6570\u636e");
			Assert.IsFalse(snapshot.UserStatusMessage.Contains("IOException"));
			Assert.IsFalse(snapshot.UserStatusMessage.Contains("Error"));
			Assert.IsFalse(snapshot.UserStatusMessage.Contains("failed"));
			Assert.IsFalse(snapshot.UserStatusMessage.Contains(_tempDirectory));
		}

		[TestMethod]
		public void Load_DoesNotTreatLastGameAsDashboardContent()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t95" + Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsFalse(snapshot.HasContent);
			Assert.IsNotNull(snapshot.LastGame);
		}

		[TestMethod]
		public void Load_LastGameMarksLowConfidence()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\tcandidate_archetypes" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t39\t\u704c\u6ce8\u8d3c:39% score=120 branchCount=1" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsTrue(snapshot.LastGame.IsLowConfidence);
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u4f4e\u7f6e\u4fe1");
		}

		[TestMethod]
		public void Load_LastGamePrefersPreciseRecognitionDistributionAndUnknown()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\t" +
				"candidate_archetypes\trecognition_tier\tarchetype_distribution_json" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t95\t" +
				"\u704c\u6ce8\u8d3c:100% score=120 branchCount=2 / \u6d77\u76d7\u8d3c:100% score=119 branchCount=1\t" +
				"mixed\t" +
				"[{\"id\":842,\"name\":\"\u704c\u6ce8\u8d3c\",\"probability\":0.47}," +
				"{\"id\":843,\"name\":\"\u6d77\u76d7\u8d3c\",\"probability\":0.31}," +
				"{\"id\":0,\"name\":\"Unknown\",\"probability\":0.22}]" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u6f5c\u884c\u8005\u00b7\u6d41\u6d3e\u5f85\u5b9a", snapshot.LastGame.Title);
			Assert.AreEqual("mixed", snapshot.LastGame.RecognitionTier);
			Assert.AreEqual(22.0, snapshot.LastGame.UnknownProbabilityPercent, 0.001);
			Assert.AreEqual(47, snapshot.LastGame.ConfidencePercent);
			Assert.AreEqual(2, snapshot.LastGame.Candidates.Count);
			Assert.AreEqual(47, snapshot.LastGame.Candidates[0].ConfidencePercent);
			Assert.IsTrue(snapshot.LastGame.Candidates[0].IsProbability);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u672a\u77e5 22%");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u672a\u8bc6\u522b\u6982\u7387 22");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u591a\u79cd\u6d41\u6d3e\u5747\u6709\u53ef\u80fd");
			Assert.IsFalse(snapshot.LastGame.ToolTip.Contains("Unknown"));
			Assert.IsFalse(snapshot.LastGame.ToolTip.Contains("mixed"));
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u5019\u9009\u6982\u7387");
		}

		[TestMethod]
		public void Load_LastGameFallsBackWhenRecognitionJsonIsMalformed()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\t" +
				"candidate_archetypes\tarchetype_distribution_json" + Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t61\t" +
				"\u704c\u6ce8\u8d3c:61% score=120 branchCount=2\t{broken" + Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u704c\u6ce8\u8d3c", snapshot.LastGame.Title);
			Assert.AreEqual(61, snapshot.LastGame.ConfidencePercent);
			Assert.IsFalse(snapshot.LastGame.Candidates[0].IsProbability);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u7f6e\u4fe1 61%");
		}

		[TestMethod]
		public void Load_DoesNotCombineStaleArchetypeWithLatestHdtOpponent()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct" +
				Environment.NewLine +
				"old-shaman\tWin\tShaman\t\u6cd5\u672f\u8428\t91" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "hdt_opponent_history.tsv"),
				"game_id\tresult\topponent_hero\treplay_path\thsreplay_url" + Environment.NewLine +
				"new-rogue\tLoss\tRogue\tC:\\HDT\\Replays\\new.hdtreplay\thttps://hsreplay.net/new" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u6f5c\u884c\u8005 \u672a\u8bc6\u522b", snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u5931\u8d25 / \u5bf9\u9635 \u6f5c\u884c\u8005");
			Assert.AreEqual(0, snapshot.LastGame.ConfidencePercent);
			Assert.AreEqual(0, snapshot.LastGame.Candidates.Count);
			Assert.AreEqual("https://hsreplay.net/new", snapshot.LastGame.HsReplayUrl);
		}

		[TestMethod]
		public void Load_SelectsLatestGameByTimestampWhenMergedRowsAreOutOfOrder()
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tend_time\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct" +
				Environment.NewLine +
				"new-rogue\t2026-07-12T09:36:59+08:00\tLoss\tRogue\t\u6253\u4e8c\u8d3c\t94" +
				Environment.NewLine +
				"old-shaman\t2026-07-10T20:37:26+08:00\tWin\tShaman\t\u6cd5\u672f\u8428\t95" +
				Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "hdt_opponent_history.tsv"),
				"game_id\tend_time\tresult\topponent_hero" + Environment.NewLine +
				"new-rogue\t2026-07-12T09:36:59+08:00\tLoss\tRogue" + Environment.NewLine +
				"old-shaman\t2026-07-10T20:37:26+08:00\tWin\tShaman" + Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.AreEqual("\u6253\u4e8c\u8d3c", snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u5931\u8d25 / \u5bf9\u9635 \u6f5c\u884c\u8005");
			Assert.AreEqual(94, snapshot.LastGame.ConfidencePercent);
			Assert.AreEqual("new-rogue", snapshot.LastGame.MatchId);
		}

		[TestMethod]
		public void Load_LastGameLocalizesResultEnumsWithoutChangingSourceParsing()
		{
			var cases = new[]
			{
				new[] { "win", "\u80dc\u5229" },
				new[] { "loss", "\u5931\u8d25" },
				new[] { "tie", "\u5e73\u5c40" },
				new[] { "unknown", "\u7ed3\u679c\u672a\u77e5" }
			};
			foreach (var item in cases)
			{
				File.WriteAllText(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct\t" +
						"candidate_archetypes" + Environment.NewLine +
					"g1\t" + item[0] + "\tMage\t\u6d4b\u8bd5\u6d41\u6d3e\t66\t" +
						"\u6d4b\u8bd5\u6d41\u6d3e:66% score=321 branchCount=7" +
						Environment.NewLine,
					Encoding.UTF8);

				var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

				Assert.AreEqual(
					item[1] + " / \u5bf9\u9635 \u6cd5\u5e08 / \u7f6e\u4fe1 66%",
					snapshot.LastGame.Detail,
					item[0]);
				Assert.AreEqual(321, snapshot.LastGame.Candidates[0].Score, item[0]);
				Assert.AreEqual(7, snapshot.LastGame.Candidates[0].BranchCount, item[0]);
				Assert.IsFalse(snapshot.LastGame.Detail.Contains(item[0]), item[0]);
			}
		}

		[TestMethod]
		public void LocalizeEvidenceCardName_UsesChineseNameAndNeverLeaksEnglishFallback()
		{
			Assert.AreEqual(
				"\u8ffd\u8e2a\u672f ×2",
				MetaDashboardSnapshot.LocalizeEvidenceCardName(
					"Trackingx2",
					name => name == "Tracking" ? "\u8ffd\u8e2a\u672f" : ""));
			Assert.AreEqual(
				"\u7a33\u56fa\u5c04\u51fb ×2",
				MetaDashboardSnapshot.LocalizeEvidenceCardName(
					"\u7a33\u56fa\u5c04\u51fbx2",
					name => "should not be used"));
			Assert.AreEqual(
				"",
				MetaDashboardSnapshot.LocalizeEvidenceCardName(
					"Trackingx2",
					name => "Tracking"));
			Assert.AreEqual(
				"",
				MetaDashboardSnapshot.LocalizeEvidenceCardName(
					"Tracking",
					name => { throw new InvalidOperationException("database unavailable"); }));
		}

		private void WriteEnvironmentRows(params string[] rows)
		{
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_environment.tsv"),
				"rank\tarchetype_id\tname\tplayer_class\tgames\tweighted_games\tlocal_pct\tavg_confidence\twins\tlosses\twin_rate" +
				Environment.NewLine +
				string.Join(Environment.NewLine, rows) +
				Environment.NewLine,
				Encoding.UTF8);
		}
	}
}
