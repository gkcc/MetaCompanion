using MetaCompanion;
using Hearthstone_Deck_Tracker.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using HsMode = Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaCompanionPluginTest
	{
		[TestMethod]
		public void ShouldStartTrackingGame_AllowsStandardRanked()
		{
			Assert.IsTrue(MetaCompanionPlugin.ShouldStartTrackingGame(
				Format.Standard, GameMode.Ranked, false));
		}

		[TestMethod]
		public void ShouldStartTrackingGame_RejectsDuplicateStartWhileTracking()
		{
			Assert.IsFalse(MetaCompanionPlugin.ShouldStartTrackingGame(
				Format.Standard, GameMode.Ranked, true));
		}

		[TestMethod]
		public void ShouldStartTrackingGame_RejectsUnsupportedModes()
		{
			Assert.IsFalse(MetaCompanionPlugin.ShouldStartTrackingGame(
				Format.Wild, GameMode.Ranked, false));
			Assert.IsFalse(MetaCompanionPlugin.ShouldStartTrackingGame(
				Format.Standard, GameMode.Battlegrounds, false));
		}

		[TestMethod]
		public void GetGameStartDecision_HidesDashboardForTrackedStandardGame()
		{
			var decision = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard, GameMode.Ranked, false);

			Assert.IsTrue(decision.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.Hide, decision.DashboardAction);
		}

		[TestMethod]
		public void GetGameStartDecision_DoesNotTouchDashboardForUntrackedGame()
		{
			var unsupportedMode = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard, GameMode.Battlegrounds, false);
			var duplicateStart = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard, GameMode.Ranked, true);

			Assert.IsFalse(unsupportedMode.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.None, unsupportedMode.DashboardAction);
			Assert.IsFalse(duplicateStart.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.None, duplicateStart.DashboardAction);
		}

		[TestMethod]
		public void GetGameStartDecision_LoadingMetaDecks_DoesNotEnablePrediction()
		{
			var decision = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard,
				GameMode.Ranked,
				false,
				MetaDeckLoadSnapshot.Loading(DateTime.Now));

			Assert.IsFalse(decision.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.None, decision.DashboardAction);
			StringAssert.Contains(decision.PredictionUnavailableReason, "牌组库加载中");
		}

		[TestMethod]
		public void GetGameStartDecision_LoadedMetaDecks_EnablesPrediction()
		{
			var now = DateTime.Now;
			var decision = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard,
				GameMode.Ranked,
				false,
				MetaDeckLoadSnapshot.Ready(12, now.AddSeconds(-1), now));

			Assert.IsTrue(decision.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.Hide, decision.DashboardAction);
			Assert.AreEqual("", decision.PredictionUnavailableReason);
		}

		[TestMethod]
		public void GetGameStartDecision_FailedMetaDeckLoad_DowngradesPrediction()
		{
			var now = DateTime.Now;
			var decision = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard,
				GameMode.Ranked,
				false,
				MetaDeckLoadSnapshot.Failed("InvalidDataException: broken snapshot", now.AddSeconds(-1), now));

			Assert.IsFalse(decision.ShouldTrack);
			StringAssert.Contains(decision.PredictionUnavailableReason, "牌组库加载失败");
			StringAssert.Contains(decision.PredictionUnavailableReason, "broken snapshot");
		}

		[TestMethod]
		public void GetGameStartDecision_EmptyMetaDeckLoad_DoesNotCrashOrEnablePrediction()
		{
			var now = DateTime.Now;
			var decision = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard,
				GameMode.Ranked,
				false,
				MetaDeckLoadSnapshot.Ready(0, now.AddSeconds(-1), now));

			Assert.IsFalse(decision.ShouldTrack);
			StringAssert.Contains(decision.PredictionUnavailableReason, "牌组库不可用");
		}

		[TestMethod]
		public void ShouldShowStandardRecommendations_AllowsTraditionalPlayScene()
		{
			Assert.IsTrue(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.Ranked, HsMode.TOURNAMENT, false, true));
			Assert.IsTrue(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.All, GameMode.None, HsMode.TOURNAMENT, false, true));
		}

		[TestMethod]
		public void ShouldShowStandardRecommendations_RejectsGameplayAndUnsupportedContexts()
		{
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.None, HsMode.HUB, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.None, HsMode.GAME_MODE, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.None, HsMode.COLLECTIONMANAGER, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.Ranked, HsMode.GAMEPLAY, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Wild, GameMode.Ranked, HsMode.TOURNAMENT, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.Battlegrounds, HsMode.TOURNAMENT, false, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.Ranked, HsMode.TOURNAMENT, true, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldShowStandardRecommendations(
				Format.Standard, GameMode.Ranked, HsMode.TOURNAMENT, false, false));
		}
	}
}
