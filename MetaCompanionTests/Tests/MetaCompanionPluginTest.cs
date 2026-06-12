using MetaCompanion;
using Hearthstone_Deck_Tracker.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
	}
}
