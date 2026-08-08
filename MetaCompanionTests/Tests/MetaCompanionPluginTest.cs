using MetaCompanion;
using Hearthstone_Deck_Tracker.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using HsMode = Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class MetaCompanionPluginTest
	{
		[TestMethod]
		public void PluginMetadata_UsesChineseSettingsButton()
		{
			var plugin = new MetaCompanionPlugin();

			Assert.AreEqual("设置", plugin.ButtonText);
		}

		[TestMethod]
		public void AdvisorOutboxRetryDelay_IsExponentialAndBoundedWithoutSleeping()
		{
			Assert.AreEqual(
				TimeSpan.FromMilliseconds(250),
				MetaCompanionPlugin.GetAdvisorOutboxRetryDelay(1));
			Assert.AreEqual(
				TimeSpan.FromMilliseconds(500),
				MetaCompanionPlugin.GetAdvisorOutboxRetryDelay(2));
			Assert.AreEqual(
				TimeSpan.FromSeconds(30),
				MetaCompanionPlugin.GetAdvisorOutboxRetryDelay(20));
			Assert.AreEqual(
				TimeSpan.FromSeconds(30),
				MetaCompanionPlugin.GetAdvisorOutboxRetryDelay(Int32.MaxValue));
		}

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
		public void GetGameStartDecision_HidesDashboardForUnsupportedActualGame()
		{
			var unsupportedMode = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard, GameMode.Battlegrounds, false);

			Assert.IsFalse(unsupportedMode.ShouldTrack);
			Assert.AreEqual(GameStartDashboardAction.Hide, unsupportedMode.DashboardAction);
		}

		[TestMethod]
		public void GetGameStartDecision_DoesNotTouchDashboardForDuplicateGameStart()
		{
			var duplicateStart = MetaCompanionPlugin.GetGameStartDecision(
				Format.Standard, GameMode.Ranked, true);

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
			Assert.AreEqual(GameStartDashboardAction.Hide, decision.DashboardAction);
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
			StringAssert.Contains(decision.PredictionUnavailableReason, "刷新本地数据后重试");
			Assert.IsFalse(decision.PredictionUnavailableReason.Contains("broken snapshot"));
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
			StringAssert.Contains(decision.PredictionUnavailableReason, "牌组库暂不可用");
			StringAssert.Contains(decision.PredictionUnavailableReason, "刷新本地数据后重试");
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

		[TestMethod]
		public void HdtReplayGuard_SuppressesSameGamePrefixAndAcceptsLiveSuffix()
		{
			var guard = new HdtGameEventReplayGuard();
			var firstStart = guard.BeginGame();
			var events = new[]
			{
				new HdtGameEventStamp("turn_start", 1, 1, "Opponent"),
				new HdtGameEventStamp("turn_start", 1, 1, "Player"),
				new HdtGameEventStamp("turn_start", 2, 2, "Opponent")
			};
			var plays = new[]
			{
				new HdtGameEventStamp("opponent_play", 1, 1, "CARD_A"),
				new HdtGameEventStamp("opponent_play", 2, 2, "CARD_B")
			};

			foreach (var item in events)
				Assert.IsTrue(guard.ShouldProcess(firstStart.Generation, item));
			foreach (var item in plays)
				Assert.IsTrue(guard.ShouldProcess(firstStart.Generation, item));

			var replayStart = guard.BeginGame();
			Assert.IsTrue(replayStart.IsReplay);
			Assert.AreEqual(firstStart.Generation, replayStart.Generation);
			foreach (var item in events)
				Assert.IsFalse(guard.ShouldProcess(replayStart.Generation, item));
			foreach (var item in plays)
				Assert.IsFalse(guard.ShouldProcess(replayStart.Generation, item));

			var liveTurn = new HdtGameEventStamp("turn_start", 2, 2, "Player");
			var livePlay = new HdtGameEventStamp("opponent_play", 3, 3, "CARD_C");
			Assert.IsTrue(guard.ShouldProcess(
				replayStart.Generation,
				liveTurn));
			Assert.IsTrue(guard.ShouldProcess(
				replayStart.Generation,
				livePlay));

			var secondReplayStart = guard.BeginGame();
			foreach (var item in events)
				Assert.IsFalse(guard.ShouldProcess(secondReplayStart.Generation, item));
			Assert.IsFalse(guard.ShouldProcess(secondReplayStart.Generation, liveTurn));
			foreach (var item in plays)
				Assert.IsFalse(guard.ShouldProcess(secondReplayStart.Generation, item));
			Assert.IsFalse(guard.ShouldProcess(secondReplayStart.Generation, livePlay));
			Assert.IsTrue(guard.ShouldProcess(
				secondReplayStart.Generation,
				new HdtGameEventStamp("turn_start", 3, 3, "Opponent")));
		}

		[TestMethod]
		public void HdtReplayGuard_NormalContinuousTurnsAndRepeatedCardIdsAreNotDropped()
		{
			var guard = new HdtGameEventReplayGuard();
			var session = guard.BeginGame();

			Assert.IsTrue(guard.ShouldProcess(
				session.Generation,
				new HdtGameEventStamp("turn_start", 1, 1, "Opponent")));
			Assert.IsTrue(guard.ShouldProcess(
				session.Generation,
				new HdtGameEventStamp("turn_start", 1, 1, "Player")));
			Assert.IsTrue(guard.ShouldProcess(
				session.Generation,
				new HdtGameEventStamp("turn_start", 2, 2, "Opponent")));
			Assert.IsTrue(guard.ShouldProcess(
				session.Generation,
				new HdtGameEventStamp("opponent_play", 2, 1, "CARD_A")));
			Assert.IsTrue(guard.ShouldProcess(
				session.Generation,
				new HdtGameEventStamp("opponent_play", 2, 2, "CARD_A")));
		}

		[TestMethod]
		public void HdtReplayGuard_SuppressesPrefixWhenReplayTurnNumberDriftsToLiveTurn()
		{
			var guard = new HdtGameEventReplayGuard();
			var session = guard.BeginGame();
			var accepted = Enumerable.Range(1, 6)
				.Select(turn => new HdtGameEventStamp(
					"opponent_play", turn, turn, "CARD_" + turn))
				.ToArray();
			foreach (var stamp in accepted)
				Assert.IsTrue(guard.ShouldProcess(session.Generation, stamp));

			var replay = guard.BeginGame();
			Assert.IsTrue(replay.IsReplay);
			foreach (var stamp in accepted)
			{
				Assert.IsFalse(guard.ShouldProcess(
					replay.Generation,
					new HdtGameEventStamp(
						stamp.Kind, 7, stamp.MonotonicSequence, stamp.Detail)));
			}
			Assert.IsTrue(guard.ShouldProcess(
				replay.Generation,
				new HdtGameEventStamp("opponent_play", 7, 7, "CARD_7")));
		}

		[TestMethod]
		public void HdtReplayGuard_NewGameGenerationDoesNotReusePreviousGameHistory()
		{
			var guard = new HdtGameEventReplayGuard();
			var first = guard.BeginGame();
			var openingTurn = new HdtGameEventStamp("turn_start", 1, 1, "Opponent");
			Assert.IsTrue(guard.ShouldProcess(first.Generation, openingTurn));

			guard.EndGame(first.Generation);
			var second = guard.BeginGame();

			Assert.AreNotEqual(first.Generation, second.Generation);
			Assert.IsFalse(second.IsReplay);
			Assert.IsFalse(guard.ShouldProcess(first.Generation, openingTurn));
			Assert.IsTrue(guard.ShouldProcess(second.Generation, openingTurn));
		}

		[TestMethod]
		public void AdvisorWorkerExit_ImmediatelyFallsBackOnlyForUnexpectedRustFailure()
		{
			Assert.IsTrue(MetaCompanionPlugin.ShouldImmediatelyFallbackAdvisorWorker(
				new AdvisorWorkerExitedEventArgs(
					17, false, AdvisorWorkerBackendKind.Rust, true),
				true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldImmediatelyFallbackAdvisorWorker(
				new AdvisorWorkerExitedEventArgs(
					17, false, AdvisorWorkerBackendKind.Rust, false),
				true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldImmediatelyFallbackAdvisorWorker(
				new AdvisorWorkerExitedEventArgs(
					17, false, AdvisorWorkerBackendKind.Python, true),
				true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldImmediatelyFallbackAdvisorWorker(
				new AdvisorWorkerExitedEventArgs(
					0, true, AdvisorWorkerBackendKind.Rust, true),
				true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldImmediatelyFallbackAdvisorWorker(
				new AdvisorWorkerExitedEventArgs(
					17, false, AdvisorWorkerBackendKind.Rust, true),
				false));
		}

		[TestMethod]
		public void AdvisorSolveFailure_FallsBackOnlyForProtocolIncompatibility()
		{
			var unsupportedResponse = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Recommendations,
				Response = new AdvisorSolveResponse
				{
					Status = AdvisorProtocol.StatusUnsupported
				}
			};
			var http422 = new AdvisorWorkerException("explicitly unsupported")
			{
				StatusCode = (System.Net.HttpStatusCode)422
			};
			var unsupportedHttp = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
				Error = http422
			};
			var incompatibleProtocol = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
				Error = new AdvisorWorkerProtocolException("incompatible response")
			};
			var finalStageIncompatibleProtocol = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.Recommendations,
				Response = new AdvisorSolveResponse { Status = AdvisorProtocol.StatusPartial },
				Error = new AdvisorWorkerProtocolException("incompatible final response")
			};
			var timeout = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
				Error = new TimeoutException("ordinary timeout")
			};
			var ordinaryHttpFailure = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
				Error = new AdvisorWorkerException("server error")
				{
					StatusCode = System.Net.HttpStatusCode.InternalServerError
				}
			};
			var cancellation = new AdvisorRecommendationUpdateEventArgs
			{
				Kind = AdvisorRecommendationUpdateKind.WorkerUnavailable,
				Error = new OperationCanceledException("state changed")
			};

			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				unsupportedResponse,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				unsupportedHttp,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsTrue(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				incompatibleProtocol,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsTrue(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				finalStageIncompatibleProtocol,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				timeout,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				ordinaryHttpFailure,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				cancellation,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				unsupportedHttp,
				true,
				AdvisorWorkerBackendMode.RustOnly,
				AdvisorWorkerBackendKind.Rust));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				unsupportedHttp,
				true,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Python));
			Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
				unsupportedHttp,
				false,
				AdvisorWorkerBackendMode.Auto,
				AdvisorWorkerBackendKind.Rust));
		}
	}
}
