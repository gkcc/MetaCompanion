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
				"\"game_type\":\"RANKED_STANDARD\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"," +
				"\"region\":\"ALL\"" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(recommendationDirectory, "manifest.json"),
				"{" +
				"\"selected_time_range\":\"LAST_3_DAYS\"," +
				"\"auto_time_range_policy\":\"choose_smaller_sample_between_CURRENT_PATCH_and_LAST_3_DAYS\"," +
				"\"candidate_sample_games\":[" +
				"{\"time_range\":\"CURRENT_PATCH\",\"sample_games\":40986,\"summary_as_of\":\"2026-06-12T09:21:35Z\"}," +
				"{\"time_range\":\"LAST_3_DAYS\",\"sample_games\":18765,\"summary_as_of\":\"2026-06-12T09:30:03Z\"}" +
				"]" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_environment.tsv"),
				"rank\tarchetype_id\tname\tplayer_class\tgames\tweighted_games\tlocal_pct\tavg_confidence\twins\tlosses\twin_rate" + Environment.NewLine +
				"1\t56\t\u4efb\u52a1\u7267\tPRIEST\t4\t3.4\t52.2\t95\t3\t1\t75" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct" +
				Environment.NewLine +
				"g1\tWin\tRogue\t\u704c\u6ce8\u8d3c\t95" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "hdt_opponent_history.tsv"),
				"game_id\tresult\topponent_hero\treplay_path\thsreplay_url" + Environment.NewLine +
				"g1\tWin\tRogue\tC:\\HDT\\Replays\\g1.hdtreplay\thttps://hsreplay.net/uploads/upload/g1/" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsTrue(snapshot.HasContent);
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
			StringAssert.Contains(snapshot.LastGame.Detail, "\u7f6e\u4fe1 95%");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u5f62\u6001\u7f6e\u4fe1\u5ea6");
			Assert.AreEqual("https://hsreplay.net/uploads/upload/g1/", snapshot.LastGame.HsReplayUrl);
			Assert.AreEqual("C:\\HDT\\Replays\\g1.hdtreplay", snapshot.LastGame.ReplayPath);
			Assert.IsTrue(snapshot.RemoteSource.HasData);
			Assert.AreEqual("LAST_3_DAYS", snapshot.RemoteSource.EffectiveTimeRange);
			Assert.AreEqual(2, snapshot.RemoteSource.Candidates.Count);
			StringAssert.Contains(snapshot.RemoteSource.SettingsText, "\u6700\u8fd13\u5929");
			StringAssert.Contains(snapshot.RemoteSource.ToolTip, "\u5019\u9009\u6837\u672c");
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
			Assert.AreEqual(0, snapshot.Recommendations.Count);
			Assert.AreEqual(0, snapshot.Environment.Count);
			Assert.AreEqual(0, snapshot.EnvironmentClasses.Count);
			Assert.IsNull(snapshot.LastGame);
			Assert.IsFalse(snapshot.UpdatedAt.HasValue);
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
