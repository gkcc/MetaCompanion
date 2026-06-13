using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class QuickDashboardRefresherTest
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
		public void Refresh_UpdatesEnvironmentAndRecommendationsFromLatestMatch()
		{
			var now = new DateTime(2026, 6, 13, 14, 45, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				string.Join("\t", new[]
				{
					"m1",
					"2026-06-13 14:30:00",
					"2026-06-13 14:40:00",
					"Standard",
					"Ranked",
					"win",
					"Priest",
					questPriest,
					"95",
					"high",
					"1",
					"26",
					"14",
					questPriest,
					questPriest + ":95%",
					"game_end",
					"",
					"",
					"",
					""
				}) + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 3,
					LocalRecommendationWeight = 0.35,
					LocalRecommendationTop = 5,
					LocalMetaMinConfidence = 35
				},
				_tempDirectory,
				now);

			Assert.IsTrue(result.EnvironmentUpdated);
			Assert.IsTrue(result.RecommendationsUpdated);
			Assert.AreEqual(1, result.LocalMatchCount);
			Assert.AreEqual(2, result.RecommendationCount);

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			Assert.AreEqual(heraldShaman, snapshot.Recommendations[0].Title);
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "70%");
			Assert.AreEqual(questPriest, snapshot.Environment[0].Title);
			Assert.AreEqual(questPriest, snapshot.LastGame.Title);
		}

		private void WritePremiumMeta(
			string questPriest,
			string heraldShaman,
			string divineShieldPaladin)
		{
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(_tempDirectory);
			Directory.CreateDirectory(metaDirectory);
			File.WriteAllText(
				Path.Combine(metaDirectory, "archetypes.zh-hans.json"),
				"[" +
				"{\"id\":56,\"name\":\"" + questPriest + "\",\"player_class_name\":\"PRIEST\"}," +
				"{\"id\":857,\"name\":\"" + heraldShaman + "\",\"player_class_name\":\"SHAMAN\"}," +
				"{\"id\":595,\"name\":\"" + divineShieldPaladin + "\",\"player_class_name\":\"PALADIN\"}" +
				"]",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "summary.json"),
				"{" +
				"\"top_overall\":[" +
				"{\"archetype_id\":56,\"pct_of_total\":100,\"win_rate\":50}" +
				"]" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "head_to_head_archetype_matchups_v2.json"),
				"{" +
				"\"series\":{" +
				"\"metadata\":{" +
				"\"857\":{\"total_games\":1000,\"win_rate\":55}," +
				"\"595\":{\"total_games\":1000,\"win_rate\":55}" +
				"}," +
				"\"data\":{" +
				"\"857\":{\"56\":{\"total_games\":300,\"win_rate\":70}}," +
				"\"595\":{\"56\":{\"total_games\":300,\"win_rate\":55}}" +
				"}" +
				"}" +
				"}",
				Encoding.UTF8);
		}
	}
}
