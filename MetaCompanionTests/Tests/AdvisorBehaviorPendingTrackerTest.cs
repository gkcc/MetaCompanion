using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MetaCompanion.Tests
{
	[TestClass]
	public class AdvisorBehaviorPendingTrackerTest
	{
		private const string GameId = "g1-0123456789abcdef0123456789abcdef";

		[TestMethod]
		public void OpponentHiddenPlay_ResolvesSourceButMissingTargetProofStaysIneligibleWithoutLeak()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var pre = State(false, "state-opponent-pre", 1);
			pre.Opponent.Hand.Add(Entity(22, "", "UNKNOWN", 2));
			Assert.IsTrue(tracker.Register(new AdvisorBehaviorPendingEvidence
			{
				GameGeneration = 7,
				ObservedAtUtc = Utc(1),
				PreState = pre,
				ActorSide = "opponent",
				ActorPlayerId = "opponent",
				ActorEvidence = "hdt_opponent_event",
				IdentityStatus = "unknown",
				VisibilityStatus = "revealed_post_action",
				SourceEvent = "opponent_play",
				Action = new AdvisorBehaviorAction
				{
					Kind = "play_card",
					CardId = "OPPONENT_SPELL"
				}
			}));

			var firstPost = State(false, "state-opponent-post", 2);
			firstPost.Opponent.Graveyard.Add(Entity(22, "OPPONENT_SPELL", "SPELL", 2));
			var secondPost = State(false, "state-opponent-post", 3);
			secondPost.Opponent.Graveyard.Add(Entity(22, "OPPONENT_SPELL", "SPELL", 2));
			Assert.AreEqual(0, tracker.ObserveSnapshot(firstPost, 7, 9).Count);

			var captures = tracker.ObserveSnapshot(secondPost, 7, 9);

			Assert.AreEqual(1, captures.Count);
			Assert.AreEqual("unknown", captures[0].IdentityStatus);
			Assert.AreEqual("revealed_post_action", captures[0].VisibilityStatus);
			Assert.AreEqual(22, captures[0].Action.SourceEntityId);
			Assert.AreEqual("OPPONENT_SPELL", captures[0].Action.CardId);
			Assert.AreEqual("isolated", captures[0].BoundaryStatus);

			var collector = new AdvisorBehaviorCollector();
			collector.BeginGame(GameId);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(captures[0], out record, out rejection), rejection);
			Assert.IsFalse(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
			Assert.IsTrue(record.PreState.Opponent.Hand[0].Hidden);
			Assert.AreEqual("", record.PreState.Opponent.Hand[0].CardId);
		}

		[TestMethod]
		public void UnresolvedOpponentPlay_RetainsKnownActorButCannotPromoteEligibility()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var pre = State(false, "state-hidden-pre", 1);
			pre.Opponent.Hand.Add(Entity(22, "", "UNKNOWN", 2));
			tracker.Register(new AdvisorBehaviorPendingEvidence
			{
				GameGeneration = 4,
				ObservedAtUtc = Utc(2),
				PreState = pre,
				ActorSide = "opponent",
				ActorPlayerId = "opponent",
				ActorEvidence = "hdt_opponent_event",
				IdentityStatus = "unknown",
				VisibilityStatus = "revealed_post_action",
				SourceEvent = "opponent_play",
				Action = new AdvisorBehaviorAction
				{
					Kind = "play_card",
					CardId = "REVEALED_BUT_UNRESOLVED"
				}
			});
			var firstPost = State(false, "state-hidden-post", 2);
			var secondPost = State(false, "state-hidden-post", 3);
			tracker.ObserveSnapshot(firstPost, 4, 12);

			var capture = tracker.ObserveSnapshot(secondPost, 4, 12)[0];

			Assert.AreEqual("opponent", capture.ActorSide);
			Assert.AreEqual("unknown", capture.IdentityStatus);
			Assert.IsNull(capture.Action.SourceEntityId);
			var collector = new AdvisorBehaviorCollector();
			collector.BeginGame(GameId);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsFalse(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
		}

		[TestMethod]
		public void LocalPowerIdentity_MergesIntoCallbackAndPreservesOneChronologicalAction()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var pre = State(true, "state-local-pre", 10);
			pre.Player.Hand.Add(Entity(12, "LOCAL_SPELL", "SPELL", 1));
			Assert.IsTrue(tracker.Register(Pending(
				pre, 8, Utc(3), "local", "hdt_player_event", "player_play",
				"play_card", null, null, "LOCAL_SPELL", false)));
			Assert.IsFalse(tracker.Register(Pending(
				pre, 8, Utc(3).AddMilliseconds(50), "local", "hdt_power_log", "hdt_power_log",
				"play_card", 12, null, "LOCAL_SPELL", true)));
			Assert.AreEqual(1, tracker.PendingCount);

			var firstPost = State(true, "state-local-post", 11);
			var secondPost = State(true, "state-local-post", 12);
			tracker.ObserveSnapshot(firstPost, 8, 21);
			var capture = tracker.ObserveSnapshot(secondPost, 8, 21)[0];

			Assert.AreEqual("hdt_power_log", capture.ActorEvidence);
			Assert.AreEqual("hdt_power_log", capture.SourceEvent);
			Assert.AreEqual("exact_public_entity", capture.IdentityStatus);
			Assert.AreEqual(12, capture.Action.SourceEntityId);
			Assert.AreEqual("isolated", capture.BoundaryStatus);

			var collector = new AdvisorBehaviorCollector();
			collector.BeginGame(GameId);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsTrue(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
		}

		[TestMethod]
		public void LocalSelectedChoiceSurvivesPowerMergeAndStableBoundary()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var pre = State(true, "state-choice-pre", 20);
			pre.Player.Hand.Add(Entity(12, "LOCAL_DISCOVER", "SPELL", 1));
			Assert.IsTrue(tracker.Register(Pending(
				pre, 18, Utc(3), "local", "hdt_player_event", "player_play",
				"play_card", null, null, "LOCAL_DISCOVER", false)));
			var power = Pending(
				pre, 18, Utc(3).AddMilliseconds(50), "local", "hdt_power_log",
				"hdt_power_log", "play_card", 12, null, "LOCAL_DISCOVER", true);
			power.Action.SubOption = -1;
			power.Action.BoardPosition = 0;
			power.Action.ChoiceStatus = "selected";
			power.Action.Choices.Add(new AdvisorObservedChoice
			{
				ChoiceId = 17,
				ChoiceType = "GENERAL",
				SourceEntityId = 12,
				OptionEntityIds = new List<int> { 117, 118 },
				SelectedEntityIds = new List<int> { 118 },
				Status = "selected"
			});
			Assert.IsFalse(tracker.Register(power));

			tracker.ObserveSnapshot(State(true, "state-choice-post", 21), 18, 31);
			var capture = tracker.ObserveSnapshot(
				State(true, "state-choice-post", 22), 18, 31).Single();
			Assert.AreEqual("selected", capture.Action.ChoiceStatus);
			Assert.AreEqual(1, capture.Action.Choices.Count);
			CollectionAssert.AreEqual(
				new[] { 117, 118 }, capture.Action.Choices[0].OptionEntityIds);

			var collector = new AdvisorBehaviorCollector();
			collector.BeginGame(GameId);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsTrue(record.BehaviorEligible);
			Assert.AreEqual("selected", record.Action.ChoiceStatus);
		}

		[TestMethod]
		public void GameEventsPlayAndHeroPowerWithoutTargetProofStayIneligibleForBothPlayers()
		{
			var collector = new AdvisorBehaviorCollector();
			collector.BeginGame(GameId);
			var expectedSequence = 0L;
			foreach (var localActor in new[] { true, false })
			{
				foreach (var kind in new[] { "play_card", "hero_power" })
				{
					var side = localActor ? "local" : "opponent";
					var pre = State(
						localActor,
						"state-no-power-" + side + "-" + kind + "-pre",
						1);
					int? sourceEntityId;
					string cardId;
					if (kind == "play_card")
					{
						var handEntityId = localActor ? 12 : 22;
						cardId = localActor ? "LOCAL_SPELL" : "OPPONENT_SPELL";
						(localActor ? pre.Player.Hand : pre.Opponent.Hand).Add(Entity(
							handEntityId,
							localActor ? cardId : "",
							localActor ? "SPELL" : "UNKNOWN",
							localActor ? 1 : 2));
						sourceEntityId = localActor ? (int?)handEntityId : null;
					}
					else
					{
						sourceEntityId = localActor ? 11 : 21;
						cardId = localActor ? "LOCAL_POWER" : "OPPONENT_POWER";
					}

					Assert.IsFalse(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
						pre,
						localActor,
						kind,
						sourceEntityId,
						null,
						AdvisorBehaviorTargetBindingStatus.Unknown,
						cardId));

					var tracker = new AdvisorBehaviorPendingTracker();
					Assert.IsTrue(tracker.Register(new AdvisorBehaviorPendingEvidence
					{
						GameGeneration = 9,
						ObservedAtUtc = Utc((int)(++expectedSequence)),
						PreState = pre,
						ActorSide = side,
						ActorPlayerId = localActor ? "friendly" : "opponent",
						ActorEvidence = localActor ? "hdt_player_event" : "hdt_opponent_event",
						IdentityStatus = "unknown",
						VisibilityStatus = !localActor && kind == "play_card"
							? "revealed_post_action" : "public_pre_state",
						SourceEvent = localActor
							? (kind == "play_card" ? "player_play" : "player_hero_power")
							: (kind == "play_card" ? "opponent_play" : "opponent_hero_power"),
						Action = new AdvisorBehaviorAction
						{
							Kind = kind,
							SourceEntityId = sourceEntityId,
							CardId = cardId
						},
						TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.Unknown
					}));

					var postStateId = "state-no-power-" + side + "-" + kind + "-post";
					var firstPost = State(localActor, postStateId, 2);
					var secondPost = State(localActor, postStateId, 3);
					if (!localActor && kind == "play_card")
					{
						firstPost.Opponent.Graveyard.Add(Entity(22, cardId, "SPELL", 2));
						secondPost.Opponent.Graveyard.Add(Entity(22, cardId, "SPELL", 2));
					}
					Assert.AreEqual(0, tracker.ObserveSnapshot(firstPost, 9, 31).Count);
					var capture = tracker.ObserveSnapshot(secondPost, 9, 31).Single();

					Assert.AreEqual("unknown", capture.IdentityStatus, side + "/" + kind);
					if (!localActor && kind == "play_card")
						Assert.AreEqual(22, capture.Action.SourceEntityId);
					AdvisorBehaviorRecord record;
					string rejection;
					Assert.IsTrue(
						collector.TryCollect(capture, out record, out rejection),
						side + "/" + kind + ": " + rejection);
					Assert.AreEqual(expectedSequence, record.BehaviorSequence);
					Assert.IsFalse(record.BehaviorEligible, side + "/" + kind);
					Assert.IsFalse(record.RlTrainingEligible, side + "/" + kind);
					Assert.AreEqual(false, record.ToWireValue()["rl_training_eligible"]);
				}
			}
		}

		[TestMethod]
		public void BothPlayers_AreDrainedInEventOrderAndEarlierUnclosedActionIsOverlapped()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var local = State(true, "state-local-turn", 1);
			var opponent = State(false, "state-opponent-turn", 2);
			tracker.Register(Pending(
				local, 3, Utc(4), "local", "active_player", "turn_passed_to_opponent",
				"end_turn", null, null, "", false));
			tracker.Register(Pending(
				opponent, 3, Utc(5), "opponent", "hdt_opponent_event", "opponent_attack",
				"attack", 23, 10, "OPPONENT_MINION", false));

			var firstPost = State(false, "state-after-actions", 3);
			var secondPost = State(false, "state-after-actions", 4);
			tracker.ObserveSnapshot(firstPost, 3, 30);
			var captures = tracker.ObserveSnapshot(secondPost, 3, 30);

			Assert.AreEqual(2, captures.Count);
			Assert.AreEqual("local", captures[0].ActorSide);
			Assert.AreEqual("end_turn", captures[0].Action.Kind);
			Assert.AreEqual("overlapped", captures[0].BoundaryStatus);
			Assert.AreEqual("opponent", captures[1].ActorSide);
			Assert.AreEqual("attack", captures[1].Action.Kind);
			Assert.AreEqual("isolated", captures[1].BoundaryStatus);
		}

		[TestMethod]
		public void ExactPublicBinding_RequiresCorrectOwnerZoneAndEnemyTarget()
		{
			var state = State(false, "state-binding", 1);
			Assert.IsTrue(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
				state, false, "attack", 23, 10,
				AdvisorBehaviorTargetBindingStatus.ExactEntityId, "OPPONENT_MINION"));
			Assert.IsFalse(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
				state, false, "attack", 13, 10,
				AdvisorBehaviorTargetBindingStatus.ExactEntityId, "LOCAL_MINION"));
			Assert.IsFalse(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
				state, false, "attack", 23, 20,
				AdvisorBehaviorTargetBindingStatus.ExactEntityId, "OPPONENT_MINION"));
		}

		[TestMethod]
		public void UnchangedFingerprint_IsRetainedUntilTerminalAsUnverifiedEvidence()
		{
			var tracker = new AdvisorBehaviorPendingTracker();
			var pre = State(true, "state-unchanged", 1);
			tracker.Register(Pending(
				pre, 6, Utc(6), "local", "active_player", "turn_passed_to_opponent",
				"end_turn", null, null, "", false));
			var first = State(true, "state-unchanged", 2);
			var second = State(true, "state-unchanged", 3);

			Assert.AreEqual(0, tracker.ObserveSnapshot(first, 6, 40).Count);
			Assert.AreEqual(0, tracker.ObserveSnapshot(second, 6, 40).Count);
			Assert.AreEqual(1, tracker.PendingCount);

			var drained = tracker.DrainUnresolved(6);
			Assert.AreEqual(1, drained.Count);
			Assert.AreEqual("unverified", drained[0].BoundaryStatus);
			Assert.IsNotNull(drained[0].PostState);
		}

		private static AdvisorBehaviorPendingEvidence Pending(
			AdvisorGameState pre,
			long generation,
			DateTime observedAt,
			string side,
			string evidence,
			string sourceEvent,
			string kind,
			int? source,
			int? target,
			string cardId,
			bool power)
		{
			return new AdvisorBehaviorPendingEvidence
			{
				GameGeneration = generation,
				ObservedAtUtc = observedAt,
				PreState = pre,
				ActorSide = side,
				ActorPlayerId = side == "local" ? "friendly" : "opponent",
				ActorEvidence = evidence,
				IdentityStatus = kind == "end_turn"
					? "event_only" : (source.HasValue ? "exact_public_entity" : "unknown"),
				VisibilityStatus = "public_pre_state",
				SourceEvent = sourceEvent,
				Action = new AdvisorBehaviorAction
				{
					Kind = kind,
					SourceEntityId = source,
					TargetEntityId = target,
					CardId = cardId
				},
				TargetBindingStatus = target.HasValue
					? AdvisorBehaviorTargetBindingStatus.ExactEntityId
					: (power || kind == "end_turn"
						? AdvisorBehaviorTargetBindingStatus.ExplicitNone
						: AdvisorBehaviorTargetBindingStatus.Unknown),
				HasPowerIdentity = power,
				PowerIdentityKey = power ? "g8:100" : ""
			};
		}

		private static AdvisorGameState State(bool localTurn, string stateId, long sequence)
		{
			return new AdvisorGameState
			{
				GameId = GameId,
				StateId = stateId,
				StateHash = "hash-" + stateId,
				SnapshotSequence = sequence,
				TurnNumber = 4,
				ActivePlayer = localTurn ? "player" : "opponent",
				IsLocalPlayerTurn = localTurn,
				GameMode = "Ranked",
				HearthstoneBuild = 247416,
				Player = Player(true),
				Opponent = Player(false)
			};
		}

		private static AdvisorPlayerState Player(bool local)
		{
			var controller = local ? 1 : 2;
			return new AdvisorPlayerState
			{
				PlayerId = controller,
				IsLocalPlayer = local,
				MaxMana = 4,
				DeckCount = 20,
				Resources = new AdvisorResourceState { Available = 3 },
				Hero = Entity(
					local ? 10 : 20,
					local ? "LOCAL_HERO" : "OPPONENT_HERO",
					"HERO",
					controller),
				HeroPower = Entity(
					local ? 11 : 21,
					local ? "LOCAL_POWER" : "OPPONENT_POWER",
					"HERO_POWER",
					controller),
				Board = new List<AdvisorEntityState>
				{
					Entity(
						local ? 13 : 23,
						local ? "LOCAL_MINION" : "OPPONENT_MINION",
						"MINION",
						controller)
				}
			};
		}

		private static AdvisorEntityState Entity(
			int entityId,
			string cardId,
			string cardType,
			int controller)
		{
			return new AdvisorEntityState
			{
				EntityId = entityId,
				CardId = cardId,
				CardType = cardType,
				ControllerId = controller,
				Attack = cardType == "MINION" ? 3 : 0,
				Health = cardType == "HERO" ? 30 : 4,
				Cost = 2,
				IsPlayableCard = true
			};
		}

		private static DateTime Utc(int seconds)
		{
			return new DateTime(2026, 7, 31, 12, 0, seconds, DateTimeKind.Utc);
		}
	}
}
