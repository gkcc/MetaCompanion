using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MetaCompanion;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PluginConfigTest
	{
		[TestMethod]
		public void CurrentMetaFileVersion_DefaultIs1()
		{
			var config = new PluginConfig();
			Assert.AreEqual("1", config.CurrentMetaFileVersion);
		}

		[TestMethod]
		public void LateGamePanel_DefaultsAreEnabledAndConservative()
		{
			var config = new PluginConfig();
			Assert.IsTrue(config.EnableMetaDashboard);
			Assert.IsTrue(config.EnableLateGamePanel);
			Assert.IsTrue(config.EnableNativeHdtOpponentPredictions);
			Assert.IsTrue(config.EnableLiveAdvisor);
			Assert.IsTrue(config.EnableAdvisorTrainingLog);
			Assert.AreEqual(
				AdvisorWorkerBackendMode.RustOnly, config.AdvisorWorkerBackendMode);
			Assert.AreEqual(3, config.AdvisorInitialResultSeconds);
			Assert.AreEqual(10, config.AdvisorSearchSeconds);
			Assert.AreEqual(8, config.LateGameEvidenceThreshold);
			Assert.AreEqual(15, config.LateGameRemainingDeckThreshold);
			Assert.AreEqual(18, config.LateGamePredictionThreshold);
			Assert.IsFalse(config.LateGamePanelRightSide);
			Assert.AreEqual(10, config.LateGamePanelCardLimit);
			Assert.IsTrue(config.EnableMatchHistory);
			Assert.IsTrue(config.EnablePredictionTimeline);
			Assert.AreEqual(3, config.LocalRecommendationHistoryDays);
			Assert.AreEqual(0, config.LocalRecommendationHistoryMatches);
			Assert.AreEqual(0.35, config.LocalRecommendationWeight);
			Assert.AreEqual(20, config.LocalRecommendationTop);
			Assert.AreEqual(35, config.LocalMetaMinConfidence);
			Assert.AreEqual(30.0, config.RecommendationRemotePriorGames);
			Assert.AreEqual(50.0, config.RecommendationMatchupPriorGames);
			Assert.AreEqual(2000, config.RecommendationPosteriorDraws);
			Assert.IsFalse(config.EnablePostGameMetaRefresh);
			Assert.AreEqual(8, config.PostGameMetaRefreshDelaySeconds);
			Assert.AreEqual(2, config.PostGameMetaRefreshCooldownMinutes);
			Assert.IsFalse(config.EnablePostGameDataRefresh);
			Assert.AreEqual(24, config.PostGameDataRefreshCooldownHours);
			Assert.AreEqual("LAST_7_DAYS", config.PostGamePrimaryTimeRange);
			Assert.AreEqual("DIAMOND_THROUGH_LEGEND", config.PostGameRankRange);
			Assert.AreEqual("LAST_1_DAY", config.PostGameMetaFallbackTimeRange);
			Assert.AreEqual("LAST_7_DAYS", config.PostGamePremiumFallbackTimeRange);
			Assert.AreEqual(500, config.PostGameDataRefreshMaxDecks);
			Assert.AreEqual(30, config.PostGamePremiumRefreshMaxDecks);
			Assert.AreEqual(4, config.PostGameDataRefreshParallelism);
			Assert.IsTrue(config.PostGameDashboardPersistent);
			Assert.AreEqual(0, config.PostGameDashboardAutoHideSeconds);
			Assert.IsFalse(config.HasLateGamePanelPosition);
			Assert.IsFalse(config.HasDashboardPanelPosition);
			Assert.IsFalse(config.HasAdvisorPanelPosition);
		}

		[TestMethod]
		public void CurrentMetaFileVersion_SaveLoad()
		{
			var config1 = new PluginConfig();
			config1.CurrentMetaFileVersion = "2";
			var memoryStream = new MemoryStream();
			config1.Save(new StreamWriter(memoryStream));
			memoryStream.Seek(0, SeekOrigin.Begin);

			var config2 = PluginConfig.Load(new StreamReader(memoryStream));
			Assert.AreEqual("2", config2.CurrentMetaFileVersion);
		}

		[TestMethod]
		public void RecommendationScope_MigratesOldDefaultOnceAndPreservesLaterChoice()
		{
			var legacy = new PluginConfig
			{
				PostGamePrimaryTimeRange = "CURRENT_PATCH",
				PostGameRankRange = "",
				RecommendationScopeSettingsVersion = 0
			};
			var legacyStream = new MemoryStream();
			legacy.Save(new StreamWriter(legacyStream));
			legacyStream.Seek(0, SeekOrigin.Begin);
			var migrated = PluginConfig.Load(new StreamReader(legacyStream));

			Assert.AreEqual("LAST_7_DAYS", migrated.PostGamePrimaryTimeRange);
			Assert.AreEqual("DIAMOND_THROUGH_LEGEND", migrated.PostGameRankRange);
			Assert.AreEqual(1, migrated.RecommendationScopeSettingsVersion);

			migrated.PostGamePrimaryTimeRange = "CURRENT_PATCH";
			var currentStream = new MemoryStream();
			migrated.Save(new StreamWriter(currentStream));
			currentStream.Seek(0, SeekOrigin.Begin);
			var reloaded = PluginConfig.Load(new StreamReader(currentStream));
			Assert.AreEqual("CURRENT_PATCH", reloaded.PostGamePrimaryTimeRange);
		}

		[TestMethod]
		public void RecommendationScope_FileLoadPersistsFirstMigrationForBackgroundRefresh()
		{
			var testDirectory = Path.Combine(
				Path.GetTempPath(),
				"MetaCompanionPluginConfigTest-" + Guid.NewGuid().ToString("N"));
			var configPath = Path.Combine(testDirectory, "config.xml");
			try
			{
				var legacy = new PluginConfig
				{
					PostGamePrimaryTimeRange = "CURRENT_PATCH",
					PostGameRankRange = "",
					RecommendationScopeSettingsVersion = 0
				};
				legacy.Save(configPath);

				var loaded = PluginConfig.Load(configPath);
				var persisted = File.ReadAllText(configPath);

				Assert.AreEqual("LAST_7_DAYS", loaded.PostGamePrimaryTimeRange);
				StringAssert.Contains(persisted,
					"<PostGamePrimaryTimeRange>LAST_7_DAYS</PostGamePrimaryTimeRange>");
				StringAssert.Contains(persisted,
					"<PostGameRankRange>DIAMOND_THROUGH_LEGEND</PostGameRankRange>");
				StringAssert.Contains(persisted,
					"<RecommendationScopeSettingsVersion>1</RecommendationScopeSettingsVersion>");
			}
			finally
			{
				if (Directory.Exists(testDirectory))
				{
					Directory.Delete(testDirectory, true);
				}
			}
		}

		[TestMethod]
		public void AdvisorWorkerBackendMode_SaveLoadAndRejectsInvalidValues()
		{
			var config1 = new PluginConfig
			{
				AdvisorWorkerBackendMode = AdvisorWorkerBackendMode.RustOnly
			};
			var memoryStream = new MemoryStream();
			config1.Save(new StreamWriter(memoryStream));
			memoryStream.Seek(0, SeekOrigin.Begin);

			var config2 = PluginConfig.Load(new StreamReader(memoryStream));
			Assert.IsNotNull(config2);
			Assert.AreEqual(
				AdvisorWorkerBackendMode.RustOnly, config2.AdvisorWorkerBackendMode);

			const string invalidXml =
				"<?xml version=\"1.0\"?><PluginConfig>" +
				"<AdvisorWorkerBackendMode>not-a-backend</AdvisorWorkerBackendMode>" +
				"</PluginConfig>";
			using (var invalidStream = new MemoryStream(
				System.Text.Encoding.UTF8.GetBytes(invalidXml)))
			{
				var invalidConfig = PluginConfig.Load(new StreamReader(invalidStream));
				Assert.IsNotNull(invalidConfig);
				Assert.AreEqual(
					AdvisorWorkerBackendMode.RustOnly,
					invalidConfig.AdvisorWorkerBackendMode);
			}

			var directConfig = new PluginConfig
			{
				AdvisorWorkerBackendMode = (AdvisorWorkerBackendMode)999
			};
			Assert.AreEqual(
				AdvisorWorkerBackendMode.RustOnly, directConfig.AdvisorWorkerBackendMode);
		}

		[TestMethod]
		public void OverlayPositions_SaveLoad()
		{
			var config1 = new PluginConfig
			{
				HasLateGamePanelPosition = true,
				LateGamePanelPositionX = 0.33,
				LateGamePanelPositionY = 0.44,
				HasDashboardPanelPosition = true,
				DashboardPanelPositionX = 0.55,
				DashboardPanelPositionY = 0.66,
				HasAdvisorPanelPosition = true,
				AdvisorPanelPositionX = 0.77,
				AdvisorPanelPositionY = 0.22
			};
			var memoryStream = new MemoryStream();
			config1.Save(new StreamWriter(memoryStream));
			memoryStream.Seek(0, SeekOrigin.Begin);

			var config2 = PluginConfig.Load(new StreamReader(memoryStream));

			Assert.IsTrue(config2.HasLateGamePanelPosition);
			Assert.AreEqual(0.33, config2.LateGamePanelPositionX);
			Assert.AreEqual(0.44, config2.LateGamePanelPositionY);
			Assert.IsTrue(config2.HasDashboardPanelPosition);
			Assert.AreEqual(0.55, config2.DashboardPanelPositionX);
			Assert.AreEqual(0.66, config2.DashboardPanelPositionY);
			Assert.IsTrue(config2.HasAdvisorPanelPosition);
			Assert.AreEqual(0.77, config2.AdvisorPanelPositionX);
			Assert.AreEqual(0.22, config2.AdvisorPanelPositionY);
		}
	}
}
