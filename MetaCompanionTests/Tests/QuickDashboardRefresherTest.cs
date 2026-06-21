using MetaCompanion;
using Hearthstone_Deck_Tracker.Hearthstone;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

		[TestMethod]
		public void Refresh_MergesHdtDeckStatsAndPluginHistoryWithoutDuplicateMatches()
		{
			HearthDb.Cards.LoadBaseData();
			var now = new DateTime(2026, 6, 13, 14, 45, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			var deckStatsPath = Path.Combine(_tempDirectory, "DeckStats.xml");
			File.WriteAllText(
				deckStatsPath,
				"<DeckStats>" +
				DeckStatsGame(
					"hdt-1",
					"2026-06-13 13:04:45",
					"2026-06-13 13:05:00",
					"Win",
					"Priest",
					"CORE_CS1_112:2;CORE_CFM_604:1") +
				DeckStatsGame(
					"hdt-2",
					"2026-06-13 13:30:00",
					"2026-06-13 13:30:00",
					"Loss",
					"Priest",
					"CORE_CS1_112:1;CORE_CFM_604:1") +
				"</DeckStats>",
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				string.Join("\t", new[]
				{
					"plugin-duplicate",
					"2026-06-13 13:00:00",
					"2026-06-13 13:05:00",
					"Standard",
					"Ranked",
					"win",
					"Priest",
					questPriest,
					"35",
					"low",
					"1",
					"1",
					"20",
					questPriest,
					questPriest + ":35%",
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
				now,
				deckStatsPath,
				new List<Deck>
				{
					BuildDeck("Priest", questPriest, "CORE_CS1_112", "CORE_CFM_604")
				});

			Assert.IsTrue(result.EnvironmentUpdated);
			Assert.AreEqual(2, result.LocalMatchCount);
			var localRows = File.ReadAllLines(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					Encoding.UTF8)
				.Skip(1)
				.Select(line => line.Split('\t'))
				.ToList();
			Assert.AreEqual(2, localRows.Count);
			Assert.AreEqual(0, localRows.Count(row =>
				row[26] == "plugin_match_history"));
			Assert.AreEqual(2, localRows.Count(row =>
				row[26] == "hdt_deckstats"));
			var summary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			Assert.IsFalse(summary.Contains("\"plugin_match_history\""));
			StringAssert.Contains(summary, "\"hdt_deckstats\":2");
		}

		[TestMethod]
		public void ResolveDeckStatsPaths_IncludesDefaultHdtHistoryFile()
		{
			var paths = QuickDashboardRefresher.ResolveDeckStatsPaths(null)
				.Select(Path.GetFileName)
				.ToList();

			CollectionAssert.Contains(paths, "DeckStats.xml");
			CollectionAssert.Contains(paths, "DefaultDeckStats.xml");

			var explicitPath = Path.Combine(_tempDirectory, "OnlyThis.xml");
			CollectionAssert.AreEqual(
				new[] { explicitPath },
				QuickDashboardRefresher.ResolveDeckStatsPaths(explicitPath).ToArray());
		}

		[TestMethod]
		public void Refresh_KeepsCurrentPatchMatchesOlderThanRecentWindowWithRecencyDecay()
		{
			var now = new DateTime(2026, 6, 16, 3, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				"2026-06-12 03:00:00",
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"old-priest",
					"2026-06-12 03:50:00",
					"2026-06-12 04:00:00",
					"Priest",
					questPriest,
					"100") + Environment.NewLine +
				HistoryRow(
					"recent-shaman",
					"2026-06-16 01:50:00",
					"2026-06-16 02:00:00",
					"Shaman",
					heraldShaman,
					"100") + Environment.NewLine,
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
			Assert.AreEqual(2, result.LocalMatchCount);
			var rows = File.ReadAllLines(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					Encoding.UTF8)
				.Skip(1)
				.Select(line => line.Split('\t'))
				.ToDictionary(values => values[0]);
			var oldWeight = double.Parse(rows["old-priest"][14], CultureInfo.InvariantCulture);
			var recentWeight = double.Parse(rows["recent-shaman"][14], CultureInfo.InvariantCulture);
			var oldPatchWeight = double.Parse(rows["old-priest"][15], CultureInfo.InvariantCulture);
			var oldRecencyWeight = double.Parse(rows["old-priest"][16], CultureInfo.InvariantCulture);
			var recentRecencyWeight = double.Parse(rows["recent-shaman"][16], CultureInfo.InvariantCulture);
			Assert.AreEqual(1.0, oldPatchWeight, 0.001);
			Assert.IsTrue(oldWeight < recentWeight);
			Assert.IsTrue(oldRecencyWeight < recentRecencyWeight);
			var summary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			StringAssert.Contains(summary, "\"sample_window\":\"current_patch\"");
			StringAssert.Contains(summary, "\"game_count\":2");
		}

		[TestMethod]
		public void Refresh_UsesLatestCorrectionOverOriginalPrediction()
		{
			var now = new DateTime(2026, 6, 13, 14, 45, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"m1",
					"2026-06-13 14:30:00",
					"2026-06-13 14:40:00",
					"Priest",
					questPriest,
					"35") + Environment.NewLine,
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetCorrectionsPath(_tempDirectory),
				MatchHistoryRecorder.CorrectionsHeader + Environment.NewLine +
				"m1\t" + heraldShaman + "\t\tfirst correction" + Environment.NewLine +
				"m1\t" + divineShieldPaladin + "\tloss\tlatest correction" + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 3,
					LocalRecommendationWeight = 0.35,
					LocalRecommendationTop = 5,
					LocalMetaMinConfidence = 90
				},
				_tempDirectory,
				now);

			Assert.IsTrue(result.EnvironmentUpdated);
			Assert.AreEqual(1, result.LocalMatchCount);
			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			Assert.AreEqual(divineShieldPaladin, snapshot.Environment[0].Title);
			Assert.AreEqual(divineShieldPaladin, snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u7f6e\u4fe1 100%");
			var row = File.ReadAllLines(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					Encoding.UTF8)
				.Skip(1)
				.Single()
				.Split('\t');
			Assert.AreEqual("m1", row[0]);
			Assert.AreEqual("loss", row[3]);
			Assert.AreEqual(divineShieldPaladin, row[12]);
			Assert.AreEqual("100", row[13]);
		}

		[TestMethod]
		public void Refresh_PrefersCorrectedPluginHistoryOverDuplicateHdtRow()
		{
			HearthDb.Cards.LoadBaseData();
			var now = new DateTime(2026, 6, 13, 14, 45, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			var deckStatsPath = Path.Combine(_tempDirectory, "DeckStats.xml");
			File.WriteAllText(
				deckStatsPath,
				"<DeckStats>" +
				DeckStatsGame(
					"hdt-duplicate",
					"2026-06-13 13:04:45",
					"2026-06-13 13:05:00",
					"Win",
					"Priest",
					"CORE_CS1_112:2;CORE_CFM_604:1") +
				"</DeckStats>",
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"plugin-duplicate",
					"2026-06-13 13:04:30",
					"2026-06-13 13:05:00",
					"Priest",
					questPriest,
					"35") + Environment.NewLine,
				Encoding.UTF8);
			MatchHistoryRecorder.AppendCorrection(
				_tempDirectory,
				"plugin-duplicate",
				divineShieldPaladin);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 3,
					LocalRecommendationWeight = 0.35,
					LocalRecommendationTop = 5,
					LocalMetaMinConfidence = 35
				},
				_tempDirectory,
				now,
				deckStatsPath,
				new List<Deck>
				{
					BuildDeck("Priest", questPriest, "CORE_CS1_112", "CORE_CFM_604")
				});

			Assert.IsTrue(result.EnvironmentUpdated);
			Assert.AreEqual(1, result.LocalMatchCount);
			var row = File.ReadAllLines(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					Encoding.UTF8)
				.Skip(1)
				.Single()
				.Split('\t');
			Assert.AreEqual("plugin-duplicate", row[0]);
			Assert.AreEqual(divineShieldPaladin, row[12]);
			Assert.AreEqual("100", row[13]);
			Assert.AreEqual("plugin_match_history", row[26]);
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

		private static string DeckStatsGame(
			string id,
			string startedAt,
			string endedAt,
			string result,
			string opponentHero,
			string cards)
		{
			return "<Game>" +
				"<GameId>" + id + "</GameId>" +
				"<StartTime>" + startedAt + "</StartTime>" +
				"<EndTime>" + endedAt + "</EndTime>" +
				"<Format>Standard</Format>" +
				"<GameMode>Ranked</GameMode>" +
				"<Result>" + result + "</Result>" +
				"<OpponentHero>" + opponentHero + "</OpponentHero>" +
				"<OpponentCards>" +
				string.Join("", cards.Split(';').Select(card =>
					{
						var parts = card.Split(':');
						return "<Card Id=\"" + parts[0] + "\" Count=\"" + parts[1] + "\" />";
					})) +
				"</OpponentCards>" +
				"</Game>";
		}

		private static string HistoryRow(
			string id,
			string startedAt,
			string endedAt,
			string opponentClass,
			string archetype,
			string confidence)
		{
			return string.Join("\t", new[]
			{
				id,
				startedAt,
				endedAt,
				"Standard",
				"Ranked",
				"win",
				opponentClass,
				archetype,
				confidence,
				"high",
				"1",
				"10",
				"20",
				archetype,
				archetype + ":" + confidence + "%",
				"game_end",
				"",
				"",
				"",
				""
			});
		}

		private static Deck BuildDeck(string playerClass, string name, params string[] cardIds)
		{
			var deck = new Deck { Class = playerClass, Name = name };
			foreach (var cardId in cardIds)
			{
				var card = Database.GetCardFromId(cardId);
				Assert.IsNotNull(card, cardId);
				card.Count = cardId == "CORE_CS1_112" ? 2 : 1;
				deck.Cards.Add(card);
			}
			return deck;
		}
	}
}
