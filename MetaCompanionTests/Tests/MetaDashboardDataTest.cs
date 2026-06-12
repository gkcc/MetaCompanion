using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
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
				"1\t兆示萨\tSHAMAN\t58.13\t99.03" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_environment.tsv"),
				"rank\tname\tplayer_class\tgames\tlocal_pct" + Environment.NewLine +
				"1\t任务牧\tPRIEST\t4\t52.2" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
				"game_id\tresult\topponent_hero\tpredicted_archetype\tconfidence_pct" +
				Environment.NewLine +
				"g1\tWin\tRogue\t灌注贼\t95" + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "hdt_opponent_history.tsv"),
				"game_id\tresult\topponent_hero\treplay_path\thsreplay_url" + Environment.NewLine +
				"g1\tWin\tRogue\tC:\\HDT\\Replays\\g1.hdtreplay\thttps://hsreplay.net/uploads/upload/g1/" +
				Environment.NewLine,
				Encoding.UTF8);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);

			Assert.IsTrue(snapshot.HasContent);
			Assert.AreEqual("兆示萨", snapshot.Recommendations[0].Title);
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "预期 58.13%");
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "覆盖 99.03%");
			StringAssert.Contains(snapshot.Recommendations[0].ToolTip, "HSReplay 对阵矩阵");
			StringAssert.Contains(snapshot.Recommendations[0].ToolTip, "本地近 3 天");
			Assert.AreEqual("任务牧", snapshot.Environment[0].Title);
			Assert.AreEqual("52.2% / 4 局", snapshot.Environment[0].Detail);
			StringAssert.Contains(snapshot.Environment[0].ToolTip, "近期对手分布");
			Assert.AreEqual("灌注贼", snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "置信 95%");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "形态置信度");
			Assert.AreEqual("https://hsreplay.net/uploads/upload/g1/", snapshot.LastGame.HsReplayUrl);
			Assert.AreEqual("C:\\HDT\\Replays\\g1.hdtreplay", snapshot.LastGame.ReplayPath);
		}
	}
}
