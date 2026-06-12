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
			Assert.IsTrue(config.EnableLateGamePanel);
			Assert.IsFalse(config.EnableNativeHdtOpponentPredictions);
			Assert.AreEqual(10, config.LateGameEvidenceThreshold);
			Assert.AreEqual(15, config.LateGameRemainingDeckThreshold);
			Assert.AreEqual(18, config.LateGamePredictionThreshold);
			Assert.IsFalse(config.LateGamePanelRightSide);
			Assert.AreEqual(12, config.LateGamePanelCardLimit);
			Assert.IsTrue(config.EnableMatchHistory);
			Assert.IsTrue(config.EnablePredictionTimeline);
			Assert.AreEqual(3, config.LocalRecommendationHistoryDays);
			Assert.AreEqual(0.35, config.LocalRecommendationWeight);
			Assert.AreEqual(20, config.LocalRecommendationTop);
			Assert.AreEqual(35, config.LocalMetaMinConfidence);
			Assert.IsTrue(config.EnablePostGameMetaRefresh);
			Assert.AreEqual(8, config.PostGameMetaRefreshDelaySeconds);
			Assert.AreEqual(2, config.PostGameMetaRefreshCooldownMinutes);
			Assert.IsTrue(config.EnablePostGameDataRefresh);
			Assert.AreEqual(24, config.PostGameDataRefreshCooldownHours);
			Assert.AreEqual("CURRENT_PATCH", config.PostGamePrimaryTimeRange);
			Assert.AreEqual("LAST_3_DAYS", config.PostGameMetaFallbackTimeRange);
			Assert.AreEqual("LAST_7_DAYS", config.PostGamePremiumFallbackTimeRange);
			Assert.AreEqual(500, config.PostGameDataRefreshMaxDecks);
			Assert.AreEqual(30, config.PostGamePremiumRefreshMaxDecks);
			Assert.AreEqual(4, config.PostGameDataRefreshParallelism);
			Assert.IsTrue(config.PostGameDashboardPersistent);
			Assert.AreEqual(0, config.PostGameDashboardAutoHideSeconds);
			Assert.IsFalse(config.HasLateGamePanelPosition);
			Assert.IsFalse(config.HasDashboardPanelPosition);
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
		public void OverlayPositions_SaveLoad()
		{
			var config1 = new PluginConfig
			{
				HasLateGamePanelPosition = true,
				LateGamePanelPositionX = 0.33,
				LateGamePanelPositionY = 0.44,
				HasDashboardPanelPosition = true,
				DashboardPanelPositionX = 0.55,
				DashboardPanelPositionY = 0.66
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
		}
	}
}
