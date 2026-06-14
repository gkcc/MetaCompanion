using MetaCompanion;
using Hearthstone_Deck_Tracker.Hearthstone;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
				.ToList();
			Assert.AreEqual(2, localRows.Count);
			Assert.AreEqual(1, localRows.Count(row =>
				row.EndsWith("\tplugin_match_history", StringComparison.Ordinal)));
			Assert.AreEqual(1, localRows.Count(row =>
				row.EndsWith("\thdt_deckstats", StringComparison.Ordinal)));
			var summary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			StringAssert.Contains(summary, "\"plugin_match_history\":1");
			StringAssert.Contains(summary, "\"hdt_deckstats\":1");
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
