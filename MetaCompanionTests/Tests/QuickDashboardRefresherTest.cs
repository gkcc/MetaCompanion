using MetaCompanion;
using Hearthstone_Deck_Tracker.Hearthstone;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
			StringAssert.Contains(snapshot.Recommendations[0].Detail, "67.86%");
			Assert.AreEqual(questPriest, snapshot.Environment[0].Title);
			Assert.AreEqual(questPriest, snapshot.LastGame.Title);

			var recommendationPath = QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory);
			var recommendationRow = ReadTsvRows(recommendationPath)
				.Single(row => row["name"] == heraldShaman);
			Assert.AreEqual("67.86", recommendationRow["expected_win_rate"]);
			Assert.AreEqual("85.71", recommendationRow["coverage_pct"]);
			Assert.AreEqual("300", recommendationRow["weighted_sample_games"]);
			Assert.AreEqual("1", recommendationRow["matchups_used"]);
			Assert.AreEqual("beta_dirichlet_soft_v2", recommendationRow["model_version"]);
			Assert.AreEqual("100", recommendationRow["legacy_coverage_pct"]);
			Assert.AreEqual("1", recommendationRow["legacy_matchups_used"]);
			Assert.IsFalse(string.IsNullOrWhiteSpace(
				recommendationRow["expected_win_rate_low_90"]));
			Assert.IsFalse(string.IsNullOrWhiteSpace(
				recommendationRow["expected_win_rate_high_90"]));
			Assert.IsFalse(string.IsNullOrWhiteSpace(
				recommendationRow["probability_best_pct"]));
			Assert.IsFalse(string.IsNullOrWhiteSpace(recommendationRow["tier"]));

			var recommendationJson = File.ReadAllText(
				Path.ChangeExtension(recommendationPath, ".json"), Encoding.UTF8);
			StringAssert.Contains(recommendationJson, "\"schema_version\":2");
			StringAssert.Contains(recommendationJson,
				"\"model_version\":\"beta_dirichlet_soft_v2\"");
			StringAssert.Contains(recommendationJson, "\"matchup_prior_games\":50");
			StringAssert.Contains(recommendationJson, "\"remote_prior_games\":30");
			StringAssert.Contains(recommendationJson,
				"\"coverage_model\":\"posterior_data_share\"");
			StringAssert.Contains(recommendationJson,
				"\"uncertainty_method\":\"dirichlet_beta_moments_with_normal_rank_draws\"");
		}

		[TestMethod]
		public void Refresh_KeepsUnidentifiedPluginHistoryAsLastGameWithoutEnvironmentWeight()
		{
			var now = new DateTime(2026, 7, 1, 20, 30, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"known",
					"2026-07-01 20:00:00",
					"2026-07-01 20:05:00",
					"Priest",
					questPriest,
					"95") + Environment.NewLine +
				string.Join("\t", new[]
				{
					"unknown-mage",
					"2026-07-01 20:20:00",
					"2026-07-01 20:24:00",
					"Standard",
					"Ranked",
					"loss",
					"Mage",
					"",
					"0",
					"\u672a\u77e5",
					"0",
					"12",
					"21",
					"",
					"",
					"game_end",
					"",
					"",
					"",
					"",
					"Archmage Kalec, Arcane Barragex2"
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
			Assert.AreEqual(2, result.LocalMatchCount);

			var localMetaPath = Path.Combine(_tempDirectory, "local_meta_archetypes.tsv");
			var localRows = File.ReadAllLines(localMetaPath);
			Assert.AreEqual(3, localRows.Length);
			StringAssert.Contains(localRows[2], "unknown-mage");
			StringAssert.Contains(localRows[2], "\tMage\tMage\t");
			StringAssert.Contains(localRows[2], "\t0\t\t0");
			StringAssert.Contains(localRows[2], "plugin_match_history_unidentified");
			var unknownRow = ReadTsvRows(localMetaPath)
				.Single(row => row["game_id"] == "unknown-mage");
			Assert.AreEqual("prediction_softmax_v1", unknownRow["recognition_model"]);
			Assert.AreEqual("0", unknownRow["top_probability_pct"]);
			Assert.AreEqual("100", unknownRow["unknown_probability_pct"]);
			Assert.AreEqual("unknown", unknownRow["recognition_tier"]);
			Assert.AreEqual("0", unknownRow["soft_known_weight"]);
			Assert.IsTrue(double.Parse(
				unknownRow["soft_unknown_weight"], CultureInfo.InvariantCulture) > 0.0);
			StringAssert.Contains(unknownRow["archetype_distribution_json"],
				"\"name\":\"Unknown\"");
			StringAssert.Contains(unknownRow["archetype_distribution_json"],
				"\"probability\":1");
			var localSummary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			StringAssert.Contains(localSummary, "\"schema_version\":2");
			StringAssert.Contains(localSummary, "\"soft_known_evidence\"");
			StringAssert.Contains(localSummary, "\"soft_unknown_evidence\"");
			StringAssert.Contains(localSummary, "\"soft_unknown_pct\"");

			var environmentRows = File.ReadAllLines(Path.Combine(_tempDirectory, "local_meta_environment.tsv"));
			Assert.AreEqual(2, environmentRows.Length);
			Assert.IsFalse(environmentRows.Any(line => line.Contains("\tMage\t")));

			var snapshot = MetaDashboardSnapshot.Load(_tempDirectory);
			Assert.AreEqual("\u6cd5\u5e08 \u672a\u8bc6\u522b", snapshot.LastGame.Title);
			StringAssert.Contains(snapshot.LastGame.Detail, "\u5931\u8d25 / \u5bf9\u9635 \u6cd5\u5e08");
			StringAssert.Contains(snapshot.LastGame.ToolTip, "\u4ec5\u663e\u793a\u5bf9\u624b\u804c\u4e1a");
		}

		[TestMethod]
		public void Refresh_PreservesSoftArchetypeDistributionAndUnknownMass()
		{
			var now = new DateTime(2026, 7, 1, 20, 30, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				string.Join("\t", new[]
				{
					"mixed-priest",
					"2026-07-01 20:00:00",
					"2026-07-01 20:05:00",
					"Standard",
					"Ranked",
					"win",
					"Priest",
					questPriest,
					"60",
					"medium",
					"3",
					"10",
					"20",
					questPriest,
					questPriest + ":60% / " + heraldShaman + ":30%",
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
					LocalRecommendationTop = 5,
					LocalMetaMinConfidence = 35
				},
				_tempDirectory,
				now);

			Assert.IsTrue(result.EnvironmentUpdated);
			var row = ReadTsvRows(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"))
				.Single();
			Assert.AreEqual("prediction_softmax_v1", row["recognition_model"]);
			Assert.AreEqual("60", row["top_probability_pct"]);
			Assert.AreEqual("10", row["unknown_probability_pct"]);
			Assert.AreEqual("likely", row["recognition_tier"]);
			StringAssert.Contains(row["archetype_distribution_json"],
				"\"name\":\"" + questPriest + "\",\"probability\":0.6");
			StringAssert.Contains(row["archetype_distribution_json"],
				"\"name\":\"" + heraldShaman + "\",\"probability\":0.3");
			StringAssert.Contains(row["archetype_distribution_json"],
				"\"name\":\"Unknown\",\"probability\":0.1");
			var evidenceWeight = double.Parse(row["evidence_weight"], CultureInfo.InvariantCulture);
			var knownWeight = double.Parse(row["soft_known_weight"], CultureInfo.InvariantCulture);
			var unknownWeight = double.Parse(row["soft_unknown_weight"], CultureInfo.InvariantCulture);
			Assert.AreEqual(evidenceWeight * 0.9, knownWeight, 0.001);
			Assert.AreEqual(evidenceWeight * 0.1, unknownWeight, 0.001);
		}

		[TestMethod]
		public void Refresh_UsesOnlyRankedMatchesForRecommendationPersonalization()
		{
			var now = new DateTime(2026, 7, 1, 20, 30, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"ranked-priest", "2026-07-01 20:00:00", "2026-07-01 20:05:00",
					"Priest", questPriest, "100") + Environment.NewLine +
				HistoryRow(
					"casual-shaman", "2026-07-01 20:10:00", "2026-07-01 20:15:00",
					"Shaman", heraldShaman, "100", "Casual") + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 3,
					LocalRecommendationTop = 5
				},
				_tempDirectory,
				now);

			Assert.IsTrue(result.RecommendationsUpdated);
			Assert.AreEqual(2, result.LocalMatchCount);
			var rows = ReadTsvRows(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"));
			Assert.AreEqual("Ranked", rows.Single(row => row["game_id"] == "ranked-priest")["mode"]);
			Assert.AreEqual("Casual", rows.Single(row => row["game_id"] == "casual-shaman")["mode"]);
			var recommendationJson = File.ReadAllText(
				Path.ChangeExtension(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory), ".json"),
				Encoding.UTF8);
			StringAssert.Contains(recommendationJson, "\"recommendation_local_match_count\":1");
			StringAssert.Contains(recommendationJson, "\"recommendation_mode\":\"Standard Ranked\"");
		}

		[TestMethod]
		public void Refresh_MatchesStandalonePowerShellRecommendationOnSharedFixture()
		{
			var now = new DateTime(2026, 7, 1, 20, 30, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"parity-priest", "2026-07-01 20:00:00", "2026-07-01 20:05:00",
					"Priest", questPriest, "60") + Environment.NewLine,
				Encoding.UTF8);
			var config = new PluginConfig
			{
				LocalRecommendationHistoryDays = 3,
				LocalRecommendationTop = 5,
				RecommendationRemotePriorGames = 30,
				RecommendationMatchupPriorGames = 50,
				RecommendationPosteriorDraws = 200
			};

			var result = QuickDashboardRefresher.Refresh(config, _tempDirectory, now);
			Assert.IsTrue(result.RecommendationsUpdated);
			var csharpRows = ReadTsvRows(
				QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory));
			var outputPrefix = Path.Combine(_tempDirectory, "powershell-personal");
			var scriptPath = FindRepositoryFile(
				Path.Combine("tools", "Get-PersonalMetaRecommendations.ps1"));
			var arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
				" -MetaDirectory " + Quote(QuickDashboardRefresher.GetPremiumMetaDirectory(_tempDirectory)) +
				" -HistoryPath " + Quote(MatchHistoryRecorder.GetHistoryPath(_tempDirectory)) +
				" -LocalMetaPath " + Quote(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv")) +
				" -CorrectionsPath " + Quote(MatchHistoryRecorder.GetCorrectionsPath(_tempDirectory)) +
				" -OutputPrefix " + Quote(outputPrefix) +
				" -Top 5 -HistoryDays 3 -RemotePriorGames 30 -MatchupPriorGames 50" +
				" -PosteriorDraws 200 -IncludeClassTop";
			var startInfo = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = arguments,
				WorkingDirectory = Path.GetDirectoryName(scriptPath),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			string output;
			string error;
			int exitCode;
			using (var process = Process.Start(startInfo))
			{
				Assert.IsNotNull(process);
				output = process.StandardOutput.ReadToEnd();
				error = process.StandardError.ReadToEnd();
				process.WaitForExit();
				exitCode = process.ExitCode;
			}
			Assert.AreEqual(0, exitCode, output + Environment.NewLine + error);

			var powershellRows = ReadTsvRows(outputPrefix + ".tsv");
			Assert.AreEqual(csharpRows.Count, powershellRows.Count);
			for (var index = 0; index < csharpRows.Count; index++)
			{
				Assert.AreEqual(csharpRows[index]["rank"], powershellRows[index]["rank"]);
				Assert.AreEqual(csharpRows[index]["archetype_id"], powershellRows[index]["archetype_id"]);
				Assert.AreEqual(csharpRows[index]["name"], powershellRows[index]["name"]);
				Assert.AreEqual(
					double.Parse(csharpRows[index]["expected_win_rate"], CultureInfo.InvariantCulture),
					double.Parse(powershellRows[index]["expected_win_rate"], CultureInfo.InvariantCulture),
					0.01);
				Assert.AreEqual(
					double.Parse(csharpRows[index]["coverage_pct"], CultureInfo.InvariantCulture),
					double.Parse(powershellRows[index]["coverage_pct"], CultureInfo.InvariantCulture),
					0.01);
			}
		}

		[TestMethod]
		public void StandaloneRefresh_ZeroLocalEvidence_KeepsRemoteAndPersonalFullLeaderboardIdentical()
		{
			var appDataRoot = Path.Combine(_tempDirectory, "appdata");
			var dataDirectory = Path.Combine(
				appDataRoot, "HearthstoneDeckTracker", "MetaCompanion");
			WriteRecommendationParityMeta(dataDirectory);
			var scriptPath = FindRepositoryFile(
				Path.Combine("tools", "Update-MetaCompanionData.ps1"));
			var outputPath = Path.Combine(dataDirectory, "hsreplay_deckcodes.txt");
			var result = RunPowerShellScript(
				scriptPath,
				"-Recommendations -OutputPath " + Quote(outputPath) +
				" -RecommendationTop 20 -MetaTimeRange CURRENT_PATCH" +
				" -RemoteRankRange DIAMOND_THROUGH_LEGEND" +
				" -PatchTime \"2026-07-22T18:00:00\"",
				new Dictionary<string, string> { { "APPDATA", appDataRoot } });

			Assert.AreEqual(0, result.ExitCode, result.Output);
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(dataDirectory);
			var remoteRows = ReadTsvRows(Path.Combine(metaDirectory, "recommendations.tsv"));
			var personalRows = ReadTsvRows(
				Path.Combine(metaDirectory, "personal_recommendations.tsv"));

			AssertRecommendationRowsMatch(remoteRows, personalRows);
			CollectionAssert.AreEqual(
				new[] { "857", "595", "56" },
				remoteRows.Select(row => row["archetype_id"]).ToArray(),
				"The production remote path must score the same complete matrix candidate pool as personal recommendations.");
			var personalJson = File.ReadAllText(
				Path.Combine(metaDirectory, "personal_recommendations.json"), Encoding.UTF8);
			AssertJsonNumberIsZero(personalJson, "local_known_evidence");
			AssertJsonNumberIsZero(personalJson, "local_weight");
			var shaman = personalRows.Single(row => row["archetype_id"] == "857");
			Assert.AreEqual("AAEHighestWinShaman", shaman["highest_winrate_deck_code"]);
			Assert.AreEqual("AAEMostPopularShaman", shaman["most_popular_deck_code"]);
		}

		[TestMethod]
		public void StandaloneRefresh_RejectsRollingRemoteCacheForCurrentPatchRecommendation()
		{
			var appDataRoot = Path.Combine(_tempDirectory, "appdata");
			var dataDirectory = Path.Combine(
				appDataRoot, "HearthstoneDeckTracker", "MetaCompanion");
			WriteRecommendationParityMeta(dataDirectory);
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(dataDirectory);
			var summaryPath = Path.Combine(metaDirectory, "summary.json");
			File.WriteAllText(
				summaryPath,
				File.ReadAllText(summaryPath, Encoding.UTF8)
					.Replace("CURRENT_PATCH", "LAST_1_DAY"),
				Encoding.UTF8);
			var scriptPath = FindRepositoryFile(
				Path.Combine("tools", "Update-MetaCompanionData.ps1"));

			var result = RunPowerShellScript(
				scriptPath,
				"-Recommendations -OutputPath " + Quote(
					Path.Combine(dataDirectory, "hsreplay_deckcodes.txt")) +
				" -MetaTimeRange CURRENT_PATCH" +
				" -RemoteRankRange DIAMOND_THROUGH_LEGEND" +
				" -PatchTime \"2026-07-22T18:00:00\"",
				new Dictionary<string, string> { { "APPDATA", appDataRoot } });

			Assert.AreNotEqual(0, result.ExitCode, result.Output);
			StringAssert.Contains(result.Output, "LAST_1_DAY");
			StringAssert.Contains(result.Output, "CURRENT_PATCH");
			Assert.IsFalse(File.Exists(Path.Combine(metaDirectory, "recommendations.tsv")));
			Assert.IsFalse(File.Exists(Path.Combine(metaDirectory, "personal_recommendations.tsv")));
		}

		[TestMethod]
		public void StandaloneRefresh_AcceptsCurrentPatchCacheOlderThanLocalPatchMarker()
		{
			var appDataRoot = Path.Combine(_tempDirectory, "appdata");
			var dataDirectory = Path.Combine(
				appDataRoot, "HearthstoneDeckTracker", "MetaCompanion");
			WriteRecommendationParityMeta(dataDirectory);
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(dataDirectory);
			var scriptPath = FindRepositoryFile(
				Path.Combine("tools", "Update-MetaCompanionData.ps1"));

			var result = RunPowerShellScript(
				scriptPath,
				"-Recommendations -OutputPath " + Quote(
					Path.Combine(dataDirectory, "hsreplay_deckcodes.txt")) +
				" -MetaTimeRange CURRENT_PATCH" +
				" -RemoteRankRange DIAMOND_THROUGH_LEGEND" +
				" -PatchTime \"2026-07-22T19:00:00\"",
				new Dictionary<string, string> { { "APPDATA", appDataRoot } });

			Assert.AreEqual(0, result.ExitCode, result.Output);
			Assert.IsTrue(File.Exists(Path.Combine(metaDirectory, "recommendations.tsv")));
			Assert.IsTrue(File.Exists(Path.Combine(metaDirectory, "personal_recommendations.tsv")));
		}

		[TestMethod]
		public void Refresh_NewPatchPrePatchHistoryDoesNotInfluencePersonalRecommendation()
		{
			var now = new DateTime(2026, 7, 22, 20, 0, 0);
			var patchTime = new DateTime(2026, 7, 22, 18, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			WriteRecommendationParityMeta(_tempDirectory);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				patchTime.ToString("o", CultureInfo.InvariantCulture),
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"pre-patch-priest",
					"2026-07-22 17:00:00",
					"2026-07-22 17:10:00",
					"Priest",
					questPriest,
					"100") + Environment.NewLine,
				Encoding.UTF8);

			var refreshResult = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 3,
					PostGamePrimaryTimeRange = "CURRENT_PATCH",
					PostGameRankRange = "DIAMOND_THROUGH_LEGEND",
					LocalRecommendationTop = 20,
					LocalMetaMinConfidence = 35,
					RecommendationRemotePriorGames = 30,
					RecommendationMatchupPriorGames = 50,
					RecommendationPosteriorDraws = 200
				},
				_tempDirectory,
				now,
				Path.Combine(_tempDirectory, "missing-deck-stats.xml"),
				new List<Deck>());

			Assert.IsTrue(refreshResult.RecommendationsUpdated);
			var remoteOutputPrefix = Path.Combine(_tempDirectory, "remote-current-patch");
			var remoteScriptPath = FindRepositoryFile(
				Path.Combine("tools", "Get-MetaArchetypeRecommendations.ps1"));
			var scriptResult = RunPowerShellScript(
				remoteScriptPath,
				"-MetaDirectory " + Quote(
					QuickDashboardRefresher.GetPremiumMetaDirectory(_tempDirectory)) +
				" -OutputPrefix " + Quote(remoteOutputPrefix) +
				" -Top 20 -MatchupPriorGames 50 -MinCoveragePct 50 -UseAllCandidates");
			Assert.AreEqual(0, scriptResult.ExitCode, scriptResult.Output);

			var remoteRows = ReadTsvRows(remoteOutputPrefix + ".tsv");
			var personalRows = ReadTsvRows(
				QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory));
			AssertRecommendationRowsMatch(remoteRows, personalRows);

			var environmentPath = Path.Combine(_tempDirectory, "local_meta_environment.tsv");
			var environmentRows = File.Exists(environmentPath)
				? ReadTsvRows(environmentPath)
				: new List<Dictionary<string, string>>();
			Assert.AreEqual(0, environmentRows.Count,
				"A match that ended before the new patch boundary must not enter the local environment.");
			var personalJson = File.ReadAllText(
				Path.ChangeExtension(
					QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory), ".json"),
				Encoding.UTF8);
			AssertJsonNumberIsZero(personalJson, "local_known_evidence");
			AssertJsonNumberIsZero(personalJson, "local_weight");
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
		public void Refresh_AppliesRecentDayWindowInsideCurrentPatch()
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
			Assert.AreEqual(1, result.LocalMatchCount);
			var rows = File.ReadAllLines(
					Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"),
					Encoding.UTF8)
				.Skip(1)
				.Select(line => line.Split('\t'))
				.ToDictionary(values => values[0]);
			var recentWeight = double.Parse(rows["recent-shaman"][14], CultureInfo.InvariantCulture);
			var recentRecencyWeight = double.Parse(rows["recent-shaman"][16], CultureInfo.InvariantCulture);
			Assert.IsFalse(rows.ContainsKey("old-priest"));
			Assert.IsTrue(recentWeight > 0);
			Assert.IsTrue(recentRecencyWeight > 0);
			var summary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			StringAssert.Contains(summary, "\"sample_window\":\"last_3_days_within_current_patch\"");
			StringAssert.Contains(summary, "\"game_count\":1");
		}

		[TestMethod]
		public void Refresh_ExcludesPrePatchMatchesWhenPatchIsInsideRecentWindow()
		{
			var now = new DateTime(2026, 7, 2, 12, 0, 0);
			var patchTime = new DateTime(2026, 7, 2, 8, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				patchTime.ToString("o"),
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"pre-patch", "2026-07-02 07:30:00", "2026-07-02 07:45:00",
					"Priest", questPriest, "100") + Environment.NewLine +
				HistoryRow(
					"post-patch", "2026-07-02 09:30:00", "2026-07-02 09:45:00",
					"Shaman", heraldShaman, "100") + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig { LocalRecommendationHistoryDays = 3 },
				_tempDirectory,
				now);

			Assert.IsTrue(result.EnvironmentUpdated);
			Assert.AreEqual(1, result.LocalMatchCount);
			var rows = ReadTsvRows(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"));
			Assert.AreEqual(1, rows.Count);
			Assert.AreEqual("post-patch", rows[0]["game_id"]);
			Assert.AreEqual("1", rows[0]["patch_weight"]);
			var summary = File.ReadAllText(
				Path.Combine(_tempDirectory, "local_meta_summary.json"), Encoding.UTF8);
			StringAssert.Contains(summary, "\"sample_window\":\"current_patch\"");
			StringAssert.Contains(summary, "\"pre_patch_weight\":0");
		}

		[TestMethod]
		public void Refresh_UsesRemoteOnlyWhenNoPostPatchMatchesExist()
		{
			var now = new DateTime(2026, 7, 2, 12, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				"2026-07-02 08:00:00",
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"pre-patch", "2026-07-02 07:30:00", "2026-07-02 07:45:00",
					"Priest", questPriest, "100") + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig { LocalRecommendationHistoryDays = 3 },
				_tempDirectory,
				now);

			Assert.IsFalse(result.EnvironmentUpdated);
			Assert.IsTrue(result.RecommendationsUpdated);
			Assert.AreEqual(0, result.LocalMatchCount);
			var recommendationJson = File.ReadAllText(
				Path.ChangeExtension(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory), ".json"),
				Encoding.UTF8);
			StringAssert.Contains(recommendationJson, "\"local_weight\":0");
			StringAssert.Contains(recommendationJson, "\"recommendation_local_match_count\":0");
		}

		[TestMethod]
		public void Refresh_DoesNotWriteRecommendationsFromRemoteSnapshotBeforePatchEpoch()
		{
			var now = new DateTime(2026, 7, 2, 12, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			var divineShieldPaladin = "\u5723\u76fe\u9a91";
			WritePremiumMeta(questPriest, heraldShaman, divineShieldPaladin);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				"2026-07-02 08:00:00",
				Encoding.UTF8);
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(_tempDirectory);
			var summaryPath = Path.Combine(metaDirectory, "summary.json");
			var summary = File.ReadAllText(summaryPath, Encoding.UTF8)
				.Replace("2099-01-01T00:00:00", "2026-07-02T07:59:59");
			File.WriteAllText(summaryPath, summary, Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig(), _tempDirectory, now);

			Assert.IsFalse(result.RecommendationsUpdated);
			Assert.IsFalse(File.Exists(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory)));
		}

		[TestMethod]
		public void Refresh_CurrentPatchOlderThanLocalMarkerStillUsesRemoteWithZeroLocalGames()
		{
			WriteRecommendationParityMeta(_tempDirectory);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				"2026-07-22T19:00:00+08:00",
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					PostGamePrimaryTimeRange = "LAST_7_DAYS",
					PostGameMetaFallbackTimeRange = "LAST_3_DAYS",
					PostGameRankRange = "DIAMOND_THROUGH_LEGEND"
				},
				_tempDirectory,
				new DateTime(2026, 7, 22, 20, 0, 0));

			Assert.IsTrue(result.RecommendationsUpdated);
			Assert.AreEqual(0, result.LocalMatchCount);
			var recommendationJson = File.ReadAllText(
				Path.ChangeExtension(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory), ".json"),
				Encoding.UTF8);
			StringAssert.Contains(recommendationJson, "\"recommendation_local_match_count\":0");
			var shaman = ReadTsvRows(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory))
				.Single(row => row["archetype_id"] == "857");
			Assert.AreEqual("AAEHighestWinShaman", shaman["highest_winrate_deck_code"]);
		}

		[TestMethod]
		public void Refresh_CurrentPatchOlderThanLocalMarkerStillBlendsOneLocalGame()
		{
			WriteRecommendationParityMeta(_tempDirectory);
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				"2026-07-22T19:00:00+08:00",
				Encoding.UTF8);
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow(
					"post-patch-priest",
					"2026-07-22 19:10:00",
					"2026-07-22 19:20:00",
					"Priest",
					"\u4efb\u52a1\u7267",
					"100") + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					PostGamePrimaryTimeRange = "LAST_7_DAYS",
					PostGameMetaFallbackTimeRange = "LAST_3_DAYS",
					PostGameRankRange = "DIAMOND_THROUGH_LEGEND"
				},
				_tempDirectory,
				new DateTime(2026, 7, 22, 20, 0, 0));

			Assert.IsTrue(result.RecommendationsUpdated);
			Assert.AreEqual(1, result.LocalMatchCount);
			var recommendationJson = File.ReadAllText(
				Path.ChangeExtension(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory), ".json"),
				Encoding.UTF8);
			StringAssert.Contains(recommendationJson, "\"recommendation_local_match_count\":1");
		}

		[TestMethod]
		public void Refresh_CurrentPatchFromPreviousVersionDoesNotReplaceRecommendations()
		{
			WriteRecommendationParityMeta(_tempDirectory);
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(_tempDirectory);
			var summaryPath = Path.Combine(metaDirectory, "summary.json");
			File.WriteAllText(
				summaryPath,
				File.ReadAllText(summaryPath, Encoding.UTF8)
					.Replace("\"patch_version\":\"36.2.0\"", "\"patch_version\":\"36.0.0\""),
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					PostGamePrimaryTimeRange = "CURRENT_PATCH",
					PostGameRankRange = "DIAMOND_THROUGH_LEGEND"
				},
				_tempDirectory,
				new DateTime(2026, 7, 22, 20, 0, 0));

			Assert.IsFalse(result.RecommendationsUpdated);
			Assert.IsFalse(File.Exists(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory)));
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
			StringAssert.Contains(snapshot.LastGame.Detail, "\u6700\u9ad8 100%");
			StringAssert.Contains(snapshot.LastGame.Detail, "\u5df2\u4eba\u5de5\u4fee\u6b63");
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
			var correctedRow = ReadTsvRows(
				Path.Combine(_tempDirectory, "local_meta_archetypes.tsv")).Single();
			Assert.AreEqual("manual_correction", correctedRow["recognition_model"]);
			Assert.AreEqual("100", correctedRow["top_probability_pct"]);
			Assert.AreEqual("0", correctedRow["unknown_probability_pct"]);
			Assert.AreEqual("corrected", correctedRow["recognition_tier"]);
			StringAssert.Contains(correctedRow["archetype_distribution_json"],
				"\"name\":\"" + divineShieldPaladin + "\",\"probability\":1");
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

		[TestMethod]
		public void Refresh_AppliesRecentDaysAndRecentMatchesTogether()
		{
			var now = new DateTime(2026, 8, 4, 12, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			WritePremiumMeta(questPriest, heraldShaman, "\u5723\u76fe\u9a91");
			File.WriteAllText(
				MatchHistoryRecorder.GetHistoryPath(_tempDirectory),
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow("outside-days", "2026-08-02 10:00:00", "2026-08-02 10:05:00",
					"Priest", questPriest, "100") + Environment.NewLine +
				HistoryRow("inside-older", "2026-08-04 09:00:00", "2026-08-04 09:05:00",
					"Priest", questPriest, "100") + Environment.NewLine +
				HistoryRow("inside-latest", "2026-08-04 11:00:00", "2026-08-04 11:05:00",
					"Shaman", heraldShaman, "100") + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					LocalRecommendationHistoryDays = 1,
					LocalRecommendationHistoryMatches = 1
				},
				_tempDirectory,
				now);

			Assert.AreEqual(1, result.LocalMatchCount);
			var rows = ReadTsvRows(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv"));
			Assert.AreEqual(1, rows.Count);
			Assert.AreEqual("inside-latest", rows[0]["game_id"]);
		}

		[TestMethod]
		public void LocalMetaDataService_ClearPreservesHistoryAndRestoreReimportsCurrentPatch()
		{
			var now = new DateTime(2026, 8, 4, 12, 0, 0);
			var questPriest = "\u4efb\u52a1\u7267";
			var heraldShaman = "\u5146\u793a\u8428";
			WritePremiumMeta(questPriest, heraldShaman, "\u5723\u76fe\u9a91");
			File.WriteAllText(
				Path.Combine(_tempDirectory, "patch_marker.txt"),
				new DateTime(2026, 8, 3, 0, 0, 0).ToString("o"),
				Encoding.UTF8);
			var historyPath = MatchHistoryRecorder.GetHistoryPath(_tempDirectory);
			File.WriteAllText(
				historyPath,
				MatchHistoryRecorder.HistoryHeader + Environment.NewLine +
				HistoryRow("patch-one", "2026-08-03 10:00:00", "2026-08-03 10:05:00",
					"Priest", questPriest, "100") + Environment.NewLine +
				HistoryRow("patch-two", "2026-08-04 10:00:00", "2026-08-04 10:05:00",
					"Shaman", heraldShaman, "100") + Environment.NewLine,
				Encoding.UTF8);
			var config = new PluginConfig
			{
				LocalRecommendationHistoryDays = 1,
				LocalRecommendationHistoryMatches = 1
			};

			var cleared = LocalMetaDataService.ClearLocalSamples(
				config, _tempDirectory, now);
			Assert.AreEqual(0, cleared.LocalMatchCount);
			Assert.IsTrue(File.Exists(historyPath), "清空派生样本不能删除插件原始对战历史。");
			Assert.IsTrue(File.Exists(
				QuickDashboardRefresher.GetLocalHistoryClearMarkerPath(_tempDirectory)));

			var restored = LocalMetaDataService.RestoreCurrentPatchHistory(
				config, _tempDirectory, now);
			Assert.AreEqual(2, restored.LocalMatchCount);
			Assert.AreEqual(0, config.LocalRecommendationHistoryDays,
				"恢复当前补丁全部历史时不能继续沿用最近天数限制。");
			Assert.AreEqual(0, config.LocalRecommendationHistoryMatches,
				"恢复当前补丁全部历史时不能继续沿用最近场数限制。");
			StringAssert.Contains(restored.Message, "不限天数、不限场数");
			Assert.IsFalse(File.Exists(
				QuickDashboardRefresher.GetLocalHistoryClearMarkerPath(_tempDirectory)));
			Assert.AreEqual(2,
				ReadTsvRows(Path.Combine(_tempDirectory, "local_meta_archetypes.tsv")).Count);
		}

		[TestMethod]
		public void Refresh_RemoteRankSelectionRejectsDifferentRankCache()
		{
			WritePremiumMeta("\u4efb\u52a1\u7267", "\u5146\u793a\u8428", "\u5723\u76fe\u9a91");

			var mismatched = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					PostGamePrimaryTimeRange = "LAST_7_DAYS",
					PostGameRankRange = "LEGEND"
				},
				_tempDirectory,
				new DateTime(2026, 8, 4, 12, 0, 0));
			Assert.IsFalse(mismatched.RecommendationsUpdated);

			var matching = QuickDashboardRefresher.Refresh(
				new PluginConfig
				{
					PostGamePrimaryTimeRange = "LAST_7_DAYS",
					PostGameRankRange = "DIAMOND_THROUGH_LEGEND"
				},
				_tempDirectory,
				new DateTime(2026, 8, 4, 12, 0, 0));
			Assert.IsTrue(matching.RecommendationsUpdated);
		}

		[TestMethod]
		public void Refresh_RepresentativeDecksUseExactArchetypeIdAndDifferentSortOrders()
		{
			WritePremiumMeta("\u4efb\u52a1\u7267", "\u5146\u793a\u8428", "\u5723\u76fe\u9a91");
			File.WriteAllText(
				Path.Combine(_tempDirectory, "archetype_deck_branches.tsv"),
				"# CandidateTimeRange: LAST_7_DAYS" + Environment.NewLine +
				"# RankRange: DIAMOND_THROUGH_LEGEND" + Environment.NewLine +
				"名称不参与关联\tAAE-Shaman-Popular\tshaman-popular\t857\tSHAMAN\t1\t1000\t60" + Environment.NewLine +
				"任意名称\tAAE-Shaman-Winrate\tshaman-winrate\t857\tSHAMAN\t2\t100\t75" + Environment.NewLine +
				"兆示萨\tAAE-Paladin-WrongId\tpaladin\t595\tPALADIN\t1\t5000\t99" + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig(),
				_tempDirectory,
				new DateTime(2026, 8, 4, 12, 0, 0));

			Assert.IsTrue(result.RecommendationsUpdated);
			var row = ReadTsvRows(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory))
				.Single(value => value["archetype_id"] == "857");
			Assert.AreEqual("AAE-Shaman-Winrate", row["highest_winrate_deck_code"]);
			Assert.AreEqual("AAE-Shaman-Popular", row["most_popular_deck_code"]);
		}

		[TestMethod]
		public void Refresh_DifferentRepresentativeScopeKeepsRecommendationsAndLeavesCodesEmpty()
		{
			WritePremiumMeta("\u4efb\u52a1\u7267", "\u5146\u793a\u8428", "\u5723\u76fe\u9a91");
			File.WriteAllText(
				Path.Combine(_tempDirectory, "archetype_deck_branches.tsv"),
				"# CandidateTimeRange: LAST_3_DAYS" + Environment.NewLine +
				"# RankRange: DIAMOND_THROUGH_LEGEND" + Environment.NewLine +
				"\u5146\u793a\u8428\tAAE-WrongScope\twrong-scope\t857\tSHAMAN\t1\t1000\t75" + Environment.NewLine,
				Encoding.UTF8);

			var result = QuickDashboardRefresher.Refresh(
				new PluginConfig(),
				_tempDirectory,
				new DateTime(2026, 8, 4, 12, 0, 0));

			Assert.IsTrue(result.RecommendationsUpdated);
			var row = ReadTsvRows(QuickDashboardRefresher.GetRecommendationsPath(_tempDirectory))
				.Single(value => value["archetype_id"] == "857");
			Assert.AreEqual("", row["highest_winrate_deck_code"]);
			Assert.AreEqual("", row["most_popular_deck_code"]);
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
				"\"time_range\":\"LAST_7_DAYS\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"," +
				"\"as_of\":\"2099-01-01T00:00:00\"," +
				"\"top_overall\":[" +
				"{\"archetype_id\":56,\"pct_of_total\":100,\"win_rate\":50}" +
				"]" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "head_to_head_archetype_matchups_v2.json"),
				"{" +
				"\"as_of\":\"2099-01-01T00:00:00\"," +
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

		private static void WriteRecommendationParityMeta(string dataDirectory)
		{
			var metaDirectory = QuickDashboardRefresher.GetPremiumMetaDirectory(dataDirectory);
			Directory.CreateDirectory(metaDirectory);
			File.WriteAllText(
				Path.Combine(dataDirectory, "patch_version.txt"),
				"36.2.0.211835",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "archetypes.zh-hans.json"),
				"[" +
				"{\"id\":56,\"name\":\"\\u4efb\\u52a1\\u7267\",\"player_class_name\":\"PRIEST\"}," +
				"{\"id\":857,\"name\":\"\\u5146\\u793a\\u8428\",\"player_class_name\":\"SHAMAN\"}," +
				"{\"id\":595,\"name\":\"\\u5723\\u76fe\\u9a91\",\"player_class_name\":\"PALADIN\"}" +
				"]",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "summary.json"),
				"{" +
				"\"generated_at\":\"2026-07-22T18:05:00+08:00\"," +
				"\"as_of\":\"2026-07-22T10:00:00Z\"," +
				"\"time_range\":\"CURRENT_PATCH\"," +
				"\"patch_version\":\"36.2.0\"," +
				"\"game_type\":\"RANKED_STANDARD\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"," +
				"\"region\":\"ALL\"," +
				"\"all\":[" +
				"{\"archetype_id\":56,\"pct_of_total\":60,\"win_rate\":45}," +
				"{\"archetype_id\":595,\"pct_of_total\":40,\"win_rate\":55}" +
				"]" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(metaDirectory, "head_to_head_archetype_matchups_v2.json"),
				"{" +
				"\"as_of\":\"2026-07-22T10:00:00Z\"," +
				"\"series\":{" +
				"\"metadata\":{" +
				"\"56\":{\"total_games\":1000,\"win_rate\":45}," +
				"\"857\":{\"total_games\":1000,\"win_rate\":65}," +
				"\"595\":{\"total_games\":1000,\"win_rate\":55}" +
				"}," +
				"\"data\":{" +
				"\"56\":{" +
				"\"56\":{\"total_games\":300,\"win_rate\":40}," +
				"\"595\":{\"total_games\":300,\"win_rate\":50}" +
				"}," +
				"\"857\":{" +
				"\"56\":{\"total_games\":300,\"win_rate\":70}," +
				"\"595\":{\"total_games\":300,\"win_rate\":60}" +
				"}," +
				"\"595\":{" +
				"\"56\":{\"total_games\":300,\"win_rate\":55}," +
				"\"595\":{\"total_games\":300,\"win_rate\":55}" +
				"}" +
				"}" +
				"}" +
				"}",
				Encoding.UTF8);
			File.WriteAllText(
				Path.Combine(dataDirectory, "archetype_deck_branches.tsv"),
				"# CandidateTimeRange: CURRENT_PATCH" + Environment.NewLine +
				"# PatchVersion: 36.2.0" + Environment.NewLine +
				"# RankRange: DIAMOND_THROUGH_LEGEND" + Environment.NewLine +
				"任务牧\tAAECurrentPatchPriest\tdeck-priest\t56\tPRIEST\t1\t500\t55" + Environment.NewLine +
				"兆示萨\tAAEMostPopularShaman\tdeck-shaman-popular\t857\tSHAMAN\t1\t900\t60" + Environment.NewLine +
				"名称故意不同\tAAEHighestWinShaman\tdeck-shaman-winrate\t857\tSHAMAN\t2\t100\t70" + Environment.NewLine +
				"圣盾骑\tAAECurrentPatchPaladin\tdeck-paladin\t595\tPALADIN\t1\t300\t58" + Environment.NewLine,
				Encoding.UTF8);
			WriteMetaPublishMarker(metaDirectory, "fixture-current-patch");
		}

		private static void WriteMetaPublishMarker(string metaDirectory, string runId)
		{
			var manifestPath = Path.Combine(metaDirectory, "manifest.json");
			File.WriteAllText(
				manifestPath,
				"{" +
				"\"run_id\":\"" + runId + "\"," +
				"\"time_range\":\"CURRENT_PATCH\"," +
				"\"selected_time_range\":\"CURRENT_PATCH\"," +
				"\"patch_version\":\"36.2.0\"," +
				"\"rank_range\":\"DIAMOND_THROUGH_LEGEND\"" +
				"}",
				Encoding.UTF8);
			string manifestHash;
			using (var algorithm = SHA256.Create())
			using (var stream = File.OpenRead(manifestPath))
			{
				manifestHash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
			}
			File.WriteAllText(
				Path.Combine(metaDirectory, "publish-complete.json"),
				"{" +
				"\"run_id\":\"" + runId + "\"," +
				"\"manifest_sha256\":\"" + manifestHash + "\"" +
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
			string confidence,
			string mode = "Ranked")
		{
			return string.Join("\t", new[]
			{
				id,
				startedAt,
				endedAt,
				"Standard",
				mode,
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

		private static List<Dictionary<string, string>> ReadTsvRows(string path)
		{
			var lines = File.ReadAllLines(path, Encoding.UTF8)
				.Where(line => !string.IsNullOrWhiteSpace(line))
				.ToArray();
			var headers = lines[0].Split('\t');
			return lines.Skip(1)
				.Select(line =>
				{
					var values = line.Split('\t');
					return headers
						.Select((header, index) => new
						{
							Header = header,
							Value = index < values.Length ? values[index] : ""
						})
						.ToDictionary(item => item.Header, item => item.Value,
							StringComparer.OrdinalIgnoreCase);
				})
				.ToList();
		}

		private static void AssertRecommendationRowsMatch(
			IReadOnlyList<Dictionary<string, string>> expected,
			IReadOnlyList<Dictionary<string, string>> actual)
		{
			Assert.AreEqual(expected.Count, actual.Count, "Recommendation row count differs.");
			for (var index = 0; index < expected.Count; index++)
			{
				Assert.AreEqual(expected[index]["rank"], actual[index]["rank"],
					"Rank differs at row " + index.ToString(CultureInfo.InvariantCulture));
				Assert.AreEqual(expected[index]["archetype_id"], actual[index]["archetype_id"],
					"Candidate differs at row " + index.ToString(CultureInfo.InvariantCulture));
				Assert.AreEqual(
					double.Parse(expected[index]["expected_win_rate"], CultureInfo.InvariantCulture),
					double.Parse(actual[index]["expected_win_rate"], CultureInfo.InvariantCulture),
					0.000001);
			}
		}

		private static void AssertJsonNumberIsZero(string json, string propertyName)
		{
			var pattern = "\\\"" +
				System.Text.RegularExpressions.Regex.Escape(propertyName) +
				"\\\"\\s*:\\s*0(?:\\.0+)?(?=\\s*[,}])";
			Assert.IsTrue(
				System.Text.RegularExpressions.Regex.IsMatch(json ?? "", pattern),
				"JSON property was not zero: " + propertyName + Environment.NewLine + json);
		}

		private static PowerShellScriptResult RunPowerShellScript(
			string scriptPath,
			string arguments,
			IDictionary<string, string> environmentVariables = null)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
					" " + arguments,
				WorkingDirectory = Path.GetDirectoryName(scriptPath),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var pair in environmentVariables ??
				new Dictionary<string, string>())
			{
				startInfo.EnvironmentVariables[pair.Key] = pair.Value;
			}

			using (var process = Process.Start(startInfo))
			{
				Assert.IsNotNull(process);
				var output = process.StandardOutput.ReadToEnd();
				var error = process.StandardError.ReadToEnd();
				process.WaitForExit();
				return new PowerShellScriptResult
				{
					ExitCode = process.ExitCode,
					Output = output + Environment.NewLine + error
				};
			}
		}

		private static string FindRepositoryFile(string relativePath)
		{
			var candidates = new[]
			{
				Path.GetDirectoryName(typeof(QuickDashboardRefresherTest).Assembly.Location),
				AppDomain.CurrentDomain.BaseDirectory,
				Directory.GetCurrentDirectory()
			};
			foreach (var root in candidates)
			{
				var directory = new DirectoryInfo(root);
				while (directory != null)
				{
					var candidate = Path.Combine(directory.FullName, relativePath);
					if (File.Exists(candidate))
					{
						return candidate;
					}
					directory = directory.Parent;
				}
			}
			Assert.Fail("Repository file was not found: " + relativePath);
			return "";
		}

		private static string Quote(string value)
		{
			return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
		}

		private class PowerShellScriptResult
		{
			public int ExitCode { get; set; }
			public string Output { get; set; }
		}
	}
}
