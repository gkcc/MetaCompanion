using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MetaCompanion;

namespace MetaCompanionTests
{
	[TestClass]
	public class AdvisorBehaviorCollectorTest
	{
		private const string GameId = "g1-0123456789abcdef0123456789abcdef";

		[TestMethod]
		public void BehaviorModeUsesFormatEvidenceInsteadOfAssumingRankedMeansStandard()
		{
			var state = BuildState(true, "mode-state", GameId);
			state.GameMode = "Ranked";
			state.Format = "Wild";
			state.FormatType = "FT_WILD";
			Assert.AreEqual("wild", AdvisorBehaviorCollector.NormalizeBehaviorMode(state));

			state.Format = "Standard";
			state.FormatType = "FT_STANDARD";
			Assert.AreEqual("standard", AdvisorBehaviorCollector.NormalizeBehaviorMode(state));

			state.Arena.IsArenaMatch = true;
			Assert.AreEqual("arena", AdvisorBehaviorCollector.NormalizeBehaviorMode(state));

			state.Arena.IsArenaMatch = false;
			state.Format = "";
			state.FormatType = "";
			Assert.AreEqual("unknown", AdvisorBehaviorCollector.NormalizeBehaviorMode(state));
		}

		[TestMethod]
		public void CollectsBothSidesAcrossBaseActionsAndLocalLocationActivation()
		{
			var collector = new AdvisorBehaviorCollector();
			Assert.IsTrue(collector.BeginGame(GameId));
			var cases = new[]
			{
				new BehaviorCase("local", "play_card"),
				new BehaviorCase("local", "attack"),
				new BehaviorCase("local", "hero_power"),
				new BehaviorCase("local", "end_turn"),
				new BehaviorCase("opponent", "play_card"),
				new BehaviorCase("opponent", "attack"),
				new BehaviorCase("opponent", "hero_power"),
				new BehaviorCase("opponent", "end_turn"),
				new BehaviorCase("local", "location_activate")
			};

			for (var index = 0; index < cases.Length; index++)
			{
				var capture = BuildCapture(cases[index].Side, cases[index].Kind, index + 1);
				AdvisorBehaviorRecord record;
				string rejection;
				Assert.IsTrue(
					collector.TryCollect(capture, out record, out rejection),
					cases[index].Side + "/" + cases[index].Kind + ": " + rejection);
				Assert.AreEqual(GameId, record.GameId);
				Assert.AreEqual(index + 1, record.BehaviorSequence);
				Assert.AreEqual(cases[index].Side, record.ActorSide);
				Assert.AreEqual(cases[index].Kind, record.Action.Kind);
				Assert.IsTrue(record.BehaviorEligible);
				Assert.IsFalse(record.RlTrainingEligible);
			}
		}

		[TestMethod]
		public void RejectsImpossiblePublicHandAndBoardCapacity()
		{
			var handCapture = BuildCapture("local", "end_turn", 1);
			for (var index = handCapture.PreState.Player.Hand.Count; index < 11; index++)
			{
				handCapture.PreState.Player.Hand.Add(Entity(
					100 + index, "HAND_" + index, "MINION", 1, "hand", 1, 1));
			}
			AssertRejected(
				StartedCollector(), handCapture, "public_hand_capacity_exceeded");

			var boardCapture = BuildCapture("local", "end_turn", 2);
			for (var index = boardCapture.PreState.Player.Board.Count; index < 8; index++)
			{
				boardCapture.PreState.Player.Board.Add(Entity(
					200 + index, "BOARD_" + index, "MINION", 1, "board", 1, 1));
			}
			AssertRejected(
				StartedCollector(), boardCapture, "public_board_capacity_exceeded");
		}

		[TestMethod]
		public void LocationActivationRequiresAnActorsPublicBoardLocation()
		{
			var validCollector = StartedCollector();
			var valid = BuildCapture("local", "location_activate", 1);
			valid.Action.TargetEntityId = 13;
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(
				validCollector.TryCollect(valid, out record, out rejection),
				rejection);
			Assert.IsTrue(record.BehaviorEligible);
			Assert.AreEqual("location_activate", record.Action.Kind);
			Assert.AreEqual(14, record.Action.SourceEntityId);
			Assert.AreEqual(13, record.Action.TargetEntityId);
			Assert.IsFalse(record.RlTrainingEligible);

			var wrongZoneCollector = StartedCollector();
			var wrongZone = BuildCapture("local", "location_activate", 2);
			wrongZone.PreState.Player.Hand[0].CardType = "LOCATION";
			wrongZone.Action.SourceEntityId = 12;
			wrongZone.Action.CardId = "FRIENDLY_SPELL";
			AssertRejected(
				wrongZoneCollector,
				wrongZone,
				"location_source_not_on_board");

			var wrongTypeCollector = StartedCollector();
			var wrongType = BuildCapture("local", "location_activate", 3);
			wrongType.Action.SourceEntityId = 13;
			wrongType.Action.CardId = "FRIENDLY_MINION";
			AssertRejected(
				wrongTypeCollector,
				wrongType,
				"location_source_not_location");
		}

		[TestMethod]
		public void ExactLocalChoiceAndBoardPositionSurviveBehaviorProjection()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("local", "play_card", 1);
			capture.ActorEvidence = "hdt_power_log";
			capture.SourceEvent = "hdt_power_log";
			capture.Action.SubOption = 1;
			capture.Action.BoardPosition = 3;
			capture.Action.ChoiceStatus = "selected";
			capture.Action.Choices.Add(new AdvisorObservedChoice
			{
				ChoiceType = "SUB_OPTION",
				SourceEntityId = 12,
				OptionEntityIds = new List<int> { 117, 118 },
				SelectedEntityIds = new List<int> { 118 },
				Status = "selected"
			});

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsTrue(record.BehaviorEligible);
			Assert.AreEqual(1, record.Action.SubOption);
			Assert.AreEqual(3, record.Action.BoardPosition);
			Assert.AreEqual("selected", record.Action.ChoiceStatus);
			Assert.AreEqual(1, record.Action.Choices.Count);
			CollectionAssert.AreEqual(
				new[] { 117, 118 }, record.Action.Choices[0].OptionEntityIds);
			CollectionAssert.AreEqual(
				new[] { 118 }, record.Action.Choices[0].SelectedEntityIds);

			var wireAction = (IDictionary<string, object>)record.ToWireValue()["action"];
			Assert.AreEqual(1, wireAction["sub_option"]);
			Assert.AreEqual(3, wireAction["board_position"]);
			Assert.AreEqual("selected", wireAction["choice_status"]);
			var choices = (IList<object>)wireAction["choices"];
			Assert.AreEqual(1, choices.Count);
		}

		[TestMethod]
		public void IncompleteChoiceIsRetainedButCannotBecomeBehaviorEligible()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("local", "play_card", 1);
			capture.ActorEvidence = "hdt_power_log";
			capture.SourceEvent = "hdt_power_log";
			capture.Action.SubOption = -1;
			capture.Action.BoardPosition = 0;
			capture.Action.ChoiceStatus = "unresolved";
			capture.Action.Choices.Add(new AdvisorObservedChoice
			{
				ChoiceId = 17,
				ChoiceType = "GENERAL",
				SourceEntityId = 12,
				OptionEntityIds = new List<int> { 117 },
				SelectedEntityIds = new List<int>(),
				Status = "unresolved"
			});

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsFalse(record.BehaviorEligible);
			Assert.AreEqual("unresolved", record.Action.ChoiceStatus);

			var spoof = BuildCapture("local", "play_card", 2);
			spoof.ActorEvidence = "hdt_power_log";
			spoof.SourceEvent = "hdt_power_log";
			spoof.Action.ChoiceStatus = "selected";
			spoof.Action.Choices.Add(new AdvisorObservedChoice
			{
				ChoiceId = 18,
				ChoiceType = "GENERAL",
				SourceEntityId = 12,
				OptionEntityIds = new List<int> { 117 },
				SelectedEntityIds = new List<int> { 118 },
				Status = "selected"
			});
			AssertRejected(StartedCollector(), spoof, "choice_entity_ids_invalid");
		}

		[TestMethod]
		public void OpponentHiddenPlayCannotClaimPreStateExactIdentity()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("opponent", "play_card", 1);
			capture.IdentityStatus = "exact_public_entity";
			capture.VisibilityStatus = "public_pre_state";

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsFalse(collector.TryCollect(capture, out record, out rejection));
			Assert.IsNull(record);
			Assert.AreEqual("opponent_hidden_play_tier_mismatch", rejection);
			Assert.AreEqual(0L, collector.Sequence);
		}

		[TestMethod]
		public void UnknownActorIsRetainedOnlyAsIneligibleEvidence()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("local", "play_card", 1);
			capture.ActorSide = "unknown";
			capture.ActorPlayerId = "friendly";
			capture.ActorEvidence = "unknown";
			capture.IdentityStatus = "unknown";
			capture.VisibilityStatus = "hidden_source";
			capture.BoundaryStatus = "unverified";
			capture.SourceEvent = "unknown";

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.AreEqual("unknown", record.ActorSide);
			Assert.AreEqual("unknown", record.ActorEvidence);
			Assert.IsFalse(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
			Assert.AreEqual(false, record.ToWireValue()["rl_training_eligible"]);
		}

		[TestMethod]
		public void KnownActorWithIncompleteEntityBindingIsRetainedAsIneligibleEvidence()
		{
			foreach (var side in new[] { "local", "opponent" })
			{
				foreach (var kind in new[] { "play_card", "attack", "hero_power" })
				{
					var collector = StartedCollector();
					var capture = BuildCapture(side, kind, 1);
					capture.IdentityStatus = "unknown";
					capture.VisibilityStatus = side == "opponent" && kind == "play_card"
						? "revealed_post_action" : "public_pre_state";
					capture.Action.SourceEntityId = null;
					capture.Action.TargetEntityId = null;

					AdvisorBehaviorRecord record;
					string rejection;
					Assert.IsTrue(
						collector.TryCollect(capture, out record, out rejection),
						side + "/" + kind + ": " + rejection);
					Assert.AreEqual(side, record.ActorSide);
					Assert.AreEqual("unknown", record.IdentityStatus);
					Assert.AreNotEqual("unknown", record.ActorEvidence);
					Assert.AreNotEqual("unknown", record.SourceEvent);
					Assert.IsNull(record.Action.SourceEntityId);
					Assert.IsNull(record.Action.TargetEntityId);
					Assert.IsFalse(record.BehaviorEligible);
					Assert.IsFalse(record.RlTrainingEligible);
				}
			}

			var spoofCollector = StartedCollector();
			var spoof = BuildCapture("local", "attack", 2);
			spoof.IdentityStatus = "unknown";
			spoof.VisibilityStatus = "hidden_source";
			spoof.Action.SourceEntityId = 23;
			spoof.Action.TargetEntityId = null;
			spoof.Action.CardId = "";
			AssertRejected(spoofCollector, spoof, "source_owner_mismatch");
		}

		[TestMethod]
		public void MissingPostStateCannotBeBehaviorEligible()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("local", "end_turn", 1);
			capture.PostState = null;

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);
			Assert.IsNull(record.PostState);
			Assert.IsFalse(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
		}

		[TestMethod]
		public void RejectsActorAndSourceOwnershipSpoofing()
		{
			var actorCollector = StartedCollector();
			var actorSpoof = BuildCapture("local", "attack", 1);
			actorSpoof.ActorSide = "opponent";
			actorSpoof.ActorEvidence = "source_owner";
			actorSpoof.SourceEvent = "opponent_attack";
			AssertRejected(actorCollector, actorSpoof, "actor_side_mismatch");

			var sourceCollector = StartedCollector();
			var sourceSpoof = BuildCapture("local", "attack", 1);
			sourceSpoof.Action.SourceEntityId = 23;
			sourceSpoof.Action.TargetEntityId = 10;
			sourceSpoof.Action.CardId = "OPPONENT_MINION";
			AssertRejected(sourceCollector, sourceSpoof, "source_owner_mismatch");

			var controllerCollector = StartedCollector();
			var controllerSpoof = BuildCapture("local", "attack", 1);
			controllerSpoof.PreState.Player.Board[0].ControllerId = 2;
			AssertRejected(controllerCollector, controllerSpoof, "source_owner_mismatch");
		}

		[TestMethod]
		public void KeepsSessionGameIdAndConsumesSequenceOnlyForAcceptedRecords()
		{
			var collector = StartedCollector();
			AdvisorBehaviorRecord first;
			string rejection;
			Assert.IsTrue(collector.TryCollect(
				BuildCapture("local", "play_card", 1), out first, out rejection), rejection);
			Assert.AreEqual(GameId, first.GameId);
			Assert.IsFalse(first.GameId.StartsWith("anon-", StringComparison.Ordinal));
			Assert.AreEqual(1L, first.BehaviorSequence);

			var rejected = BuildCapture("opponent", "play_card", 2);
			rejected.IdentityStatus = "exact_public_entity";
			rejected.VisibilityStatus = "public_pre_state";
			AdvisorBehaviorRecord ignored;
			Assert.IsFalse(collector.TryCollect(rejected, out ignored, out rejection));
			Assert.AreEqual(1L, collector.Sequence);
			Assert.IsFalse(collector.BeginGame(GameId));

			AdvisorBehaviorRecord second;
			Assert.IsTrue(collector.TryCollect(
				BuildCapture("local", "end_turn", 3), out second, out rejection), rejection);
			Assert.AreEqual(2L, second.BehaviorSequence);

			const string nextGameId = "g1-fedcba9876543210fedcba9876543210";
			Assert.IsTrue(collector.BeginGame(nextGameId));
			var nextCapture = BuildCapture("opponent", "attack", 4, nextGameId);
			AdvisorBehaviorRecord next;
			Assert.IsTrue(collector.TryCollect(nextCapture, out next, out rejection), rejection);
			Assert.AreEqual(nextGameId, next.GameId);
			Assert.AreEqual(1L, next.BehaviorSequence);

			Assert.ThrowsException<ArgumentException>(() => collector.BeginGame("account name"));
			Assert.AreEqual(nextGameId, collector.GameId);
			Assert.AreEqual(1L, collector.Sequence);
		}

		[TestMethod]
		public void SuspendAndResumeSameGame_PreservesMonotonicSequence()
		{
			var collector = StartedCollector();
			AdvisorBehaviorRecord first;
			string rejection;
			Assert.IsTrue(collector.TryCollect(
				BuildCapture("local", "end_turn", 1), out first, out rejection), rejection);
			Assert.AreEqual(1L, first.BehaviorSequence);

			collector.SuspendGame();
			AdvisorBehaviorRecord paused;
			Assert.IsFalse(collector.TryCollect(
				BuildCapture("local", "end_turn", 2), out paused, out rejection));
			Assert.AreEqual(1L, collector.Sequence);
			Assert.IsTrue(collector.BeginGame(GameId));

			AdvisorBehaviorRecord resumed;
			Assert.IsTrue(collector.TryCollect(
				BuildCapture("local", "end_turn", 3), out resumed, out rejection), rejection);
			Assert.AreEqual(2L, resumed.BehaviorSequence);

			collector.EndGame();
			Assert.IsTrue(collector.BeginGame(GameId));
			AdvisorBehaviorRecord nextGame;
			Assert.IsTrue(collector.TryCollect(
				BuildCapture("local", "end_turn", 4), out nextGame, out rejection), rejection);
			Assert.AreEqual(1L, nextGame.BehaviorSequence);
		}

		[TestMethod]
		public void DurableCommitFailure_ReusesCandidateSequenceUntilCommitSucceeds()
		{
			var collector = StartedCollector();
			var failedSequence = 0L;
			AdvisorBehaviorRecord failedRecord = null;
			string rejection = null;

			Assert.ThrowsException<IOException>(() => collector.TryCollectAndCommit(
				BuildCapture("local", "end_turn", 1),
				candidate =>
				{
					failedSequence = candidate.BehaviorSequence;
					throw new IOException("simulated durable enqueue failure");
				},
				out failedRecord,
				out rejection));

			Assert.AreEqual(1L, failedSequence);
			Assert.IsNull(failedRecord);
			Assert.AreEqual(0L, collector.Sequence);

			AdvisorBehaviorRecord first;
			Assert.IsTrue(collector.TryCollectAndCommit(
				BuildCapture("local", "end_turn", 2),
				candidate => { },
				out first,
				out rejection), rejection);
			Assert.AreEqual(1L, first.BehaviorSequence);
			Assert.AreEqual(1L, collector.Sequence);

			AdvisorBehaviorRecord second;
			Assert.IsTrue(collector.TryCollectAndCommit(
				BuildCapture("local", "end_turn", 3),
				candidate => { },
				out second,
				out rejection), rejection);
			Assert.AreEqual(2L, second.BehaviorSequence);
			Assert.AreEqual(2L, collector.Sequence);
		}

		[TestMethod]
		public void DurablePendingRenameFailure_CommitsSequenceAndRecoversBeforeNextRecord()
		{
			using (var directory = new TemporaryDirectory())
			{
				var collector = StartedCollector();
				AdvisorBehaviorRecord first;
				string rejection;
				using (var interrupted = new AdvisorBehaviorOutbox(
					directory.Path,
					(source, target) =>
					{
						throw new IOException("simulated rename interruption");
					}))
				{
					Assert.IsTrue(collector.TryCollectAndCommit(
						BuildCapture("local", "end_turn", 1),
						candidate => { interrupted.Enqueue(candidate); },
						out first,
						out rejection), rejection);
					Assert.AreEqual(1L, first.BehaviorSequence);
					Assert.AreEqual(1L, collector.Sequence);
					Assert.AreEqual(1, Directory.GetFiles(
						directory.Path,
						"*.json.pending",
						SearchOption.AllDirectories).Length);
				}

				using (var recovered = new AdvisorBehaviorOutbox(directory.Path))
				{
					Assert.AreEqual(1, recovered.CountPending());
					Assert.AreEqual(0, Directory.GetFiles(
						directory.Path,
						"*.json.pending",
						SearchOption.AllDirectories).Length);

					AdvisorBehaviorRecord second;
					Assert.IsTrue(collector.TryCollectAndCommit(
						BuildCapture("local", "end_turn", 2),
						candidate => { recovered.Enqueue(candidate); },
						out second,
						out rejection), rejection);
					Assert.AreEqual(2L, second.BehaviorSequence);
					Assert.AreEqual(2L, collector.Sequence);
					Assert.AreEqual(2, recovered.CountPending());
				}
			}
		}

		[TestMethod]
		public void RejectedCapture_DoesNotInvokeDurableCommitOrConsumeSequence()
		{
			var collector = StartedCollector();
			var rejected = BuildCapture("opponent", "play_card", 1);
			rejected.IdentityStatus = "exact_public_entity";
			rejected.VisibilityStatus = "public_pre_state";
			var commitInvoked = false;

			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsFalse(collector.TryCollectAndCommit(
				rejected,
				candidate => { commitInvoked = true; },
				out record,
				out rejection));

			Assert.AreEqual("opponent_hidden_play_tier_mismatch", rejection);
			Assert.IsFalse(commitInvoked);
			Assert.IsNull(record);
			Assert.AreEqual(0L, collector.Sequence);
		}

		[TestMethod]
		public void ConcurrentDurableCommits_ProduceUniqueContiguousSequences()
		{
			const int captureCount = 64;
			var collector = StartedCollector();
			var committedSequences = new ConcurrentBag<long>();

			Parallel.For(0, captureCount, index =>
			{
				AdvisorBehaviorRecord record;
				string rejection;
				Assert.IsTrue(collector.TryCollectAndCommit(
					BuildCapture("local", "end_turn", index + 1),
					candidate => committedSequences.Add(candidate.BehaviorSequence),
					out record,
					out rejection), rejection);
			});

			Assert.AreEqual(captureCount, committedSequences.Count);
			CollectionAssert.AreEqual(
				Enumerable.Range(1, captureCount).Select(value => (long)value).ToArray(),
				committedSequences.OrderBy(value => value).ToArray());
			Assert.AreEqual((long)captureCount, collector.Sequence);
		}

		[TestMethod]
		public void ProjectsPreAndPostStatesOntoThePublicAllowlist()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("opponent", "play_card", 1);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);

			Assert.AreEqual(capture.PreState.StateId, record.PreState.StateId);
			Assert.AreEqual(capture.PostState.StateId, record.PostState.StateId);
			Assert.AreEqual("opponent", record.PreState.ActivePlayerId);
			Assert.AreEqual("friendly", record.PreState.PerspectivePlayerId);
			Assert.AreEqual(1, record.PreState.Opponent.Hand.Count);
			Assert.IsTrue(record.PreState.Opponent.Hand[0].Hidden);

			var wire = record.ToWireValue();
			var pre = (IDictionary<string, object>)wire["pre_state"];
			var post = (IDictionary<string, object>)wire["post_state"];
			Assert.AreEqual(capture.PreState.StateId, pre["state_id"]);
			Assert.AreEqual(capture.PostState.StateId, post["state_id"]);

			var opponent = (IDictionary<string, object>)pre["opponent"];
			var hand = (IList<object>)opponent["hand"];
			var hidden = (IDictionary<string, object>)hand[0];
			CollectionAssert.AreEquivalent(
				new[] { "entity_id", "visibility" }, hidden.Keys.ToArray());
			Assert.AreEqual("hidden", hidden["visibility"]);
		}

		[TestMethod]
		public void TrainingProjectionUsesPermanentResourceCrystalsInsteadOfHdtRulesCap()
		{
			var capture = BuildCapture("local", "end_turn", 1);
			capture.PreState.Player.MaxMana = 10;
			capture.PreState.Player.Resources.Total = 1;
			capture.PreState.Opponent.MaxMana = 10;
			capture.PreState.Opponent.Resources.Total = 0;
			AdvisorBehaviorRecord record;
			string rejection;

			Assert.IsTrue(
				StartedCollector().TryCollect(capture, out record, out rejection), rejection);
			Assert.AreEqual(1, record.PreState.Friendly.MaxMana);
			Assert.AreEqual(0, record.PreState.Opponent.MaxMana);
		}

		[TestMethod]
		public void WirePayloadMatchesPythonContentDtoAndExcludesPrivateSourceFields()
		{
			var collector = StartedCollector();
			var capture = BuildCapture("local", "play_card", 1);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(collector.TryCollect(capture, out record, out rejection), rejection);

			var wire = record.ToWireValue();
			CollectionAssert.AreEquivalent(
				new[]
				{
					"schema", "game_id", "behavior_sequence", "observed_at_utc",
					"actor_side", "actor_player_id", "actor_evidence",
					"identity_status", "visibility_status", "boundary_status",
					"source_event", "action", "pre_state", "post_state",
					"behavior_eligible", "rl_training_eligible"
				},
				wire.Keys.ToArray());
			Assert.IsFalse(wire.ContainsKey("behavior_id"));
			Assert.IsFalse(wire.ContainsKey("content_sha256"));
			Assert.AreEqual(false, wire["rl_training_eligible"]);

			var action = (IDictionary<string, object>)wire["action"];
			CollectionAssert.AreEquivalent(
				new[]
				{
					"kind", "source_entity_id", "target_entity_id", "card_id",
					"sub_option", "board_position", "choice_status", "choices"
				},
				action.Keys.ToArray());
			Assert.IsInstanceOfType(action["source_entity_id"], typeof(string));
			Assert.AreEqual("12", action["source_entity_id"]);

			var flattened = new List<string>();
			CollectWireText(wire, flattened);
			var json = string.Join("|", flattened).ToLowerInvariant();
			foreach (var forbidden in new[]
			{
				"local secret name", "opponent secret name", "password-value",
				"bearer credential-value", "raw powerlog payload", "controller_id",
				"metadata", "card_text", "englishtext", "capturewarnings"
			})
			{
				Assert.IsFalse(json.Contains(forbidden), forbidden + " leaked into behavior DTO");
			}
		}

		private static AdvisorBehaviorCollector StartedCollector()
		{
			var collector = new AdvisorBehaviorCollector();
			Assert.IsTrue(collector.BeginGame(GameId));
			return collector;
		}

		private static void CollectWireText(object value, ICollection<string> output)
		{
			if (value == null)
				return;
			var text = value as string;
			if (text != null)
			{
				output.Add(text);
				return;
			}
			var dictionary = value as IDictionary<string, object>;
			if (dictionary != null)
			{
				foreach (var pair in dictionary)
				{
					output.Add(pair.Key);
					CollectWireText(pair.Value, output);
				}
				return;
			}
			var sequence = value as IEnumerable;
			if (sequence == null)
				return;
			foreach (var item in sequence)
				CollectWireText(item, output);
		}

		private static void AssertRejected(
			AdvisorBehaviorCollector collector,
			AdvisorBehaviorCapture capture,
			string expectedReason)
		{
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsFalse(collector.TryCollect(capture, out record, out rejection));
			Assert.IsNull(record);
			Assert.AreEqual(expectedReason, rejection);
			Assert.AreEqual(0L, collector.Sequence);
		}

		private static AdvisorBehaviorCapture BuildCapture(
			string side,
			string kind,
			int sequence,
			string gameId = GameId)
		{
			var local = side == "local";
			var capture = new AdvisorBehaviorCapture
			{
				ObservedAtUtc = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc)
					.AddSeconds(sequence),
				ActorSide = side,
				ActorPlayerId = local ? "friendly" : "opponent",
				ActorEvidence = kind == "end_turn"
					? "active_player"
					: (kind == "location_activate"
						? "hdt_power_log"
						: (local ? "hdt_player_event" : "hdt_opponent_event")),
				IdentityStatus = kind == "end_turn"
					? "event_only"
					: (!local && kind == "play_card"
						? "revealed_after_action" : "exact_public_entity"),
				VisibilityStatus = !local && kind == "play_card"
					? "revealed_post_action" : "public_pre_state",
				BoundaryStatus = "isolated",
				SourceEvent = SourceEvent(side, kind),
				Action = BuildAction(side, kind),
				PreState = BuildState(local, "state-" + side + "-" + kind + "-pre", gameId),
				PostState = BuildState(local, "state-" + side + "-" + kind + "-post", gameId)
			};
			return capture;
		}

		private static string SourceEvent(string side, string kind)
		{
			if (kind == "end_turn")
				return side == "local" ? "turn_passed_to_opponent" : "turn_passed_to_player";
			if (kind == "location_activate")
				return "hdt_power_log";
			var prefix = side == "local" ? "player_" : "opponent_";
			return prefix + (kind == "play_card" ? "play" : kind);
		}

		private static AdvisorBehaviorAction BuildAction(string side, string kind)
		{
			var local = side == "local";
			if (kind == "end_turn")
				return new AdvisorBehaviorAction { Kind = kind };
			if (kind == "play_card")
			{
				return new AdvisorBehaviorAction
				{
					Kind = kind,
					SourceEntityId = local ? 12 : 22,
					CardId = local ? "FRIENDLY_SPELL" : "REVEALED_OPPONENT_CARD"
				};
			}
			if (kind == "hero_power")
			{
				return new AdvisorBehaviorAction
				{
					Kind = kind,
					SourceEntityId = local ? 11 : 21,
					CardId = local ? "FRIENDLY_POWER" : "OPPONENT_POWER"
				};
			}
			if (kind == "location_activate")
			{
				return new AdvisorBehaviorAction
				{
					Kind = kind,
					SourceEntityId = local ? 14 : 24,
					CardId = local ? "FRIENDLY_LOCATION" : "OPPONENT_LOCATION"
				};
			}
			return new AdvisorBehaviorAction
			{
				Kind = kind,
				SourceEntityId = local ? 13 : 23,
				TargetEntityId = local ? 20 : 10,
				CardId = local ? "FRIENDLY_MINION" : "OPPONENT_MINION"
			};
		}

		private static AdvisorGameState BuildState(
			bool localTurn,
			string stateId,
			string gameId)
		{
			return new AdvisorGameState
			{
				StateId = stateId,
				GameId = gameId,
				TurnNumber = 4,
				ActivePlayer = localTurn ? "player" : "opponent",
				IsLocalPlayerTurn = localTurn,
				Format = "Standard",
				FormatType = "FT_STANDARD",
				GameMode = "Ranked",
				HearthstoneBuild = 247416,
				EnvironmentVersion = "credential-value",
				Metadata = new Dictionary<string, string>
				{
					{ "Authorization", "Bearer credential-value" },
					{ "password", "password-value" },
					{ "PowerLog", "raw PowerLog payload" }
				},
				CaptureWarnings = new List<string> { "raw PowerLog payload" },
				Player = BuildPlayer(true),
				Opponent = BuildPlayer(false)
			};
		}

		private static AdvisorPlayerState BuildPlayer(bool local)
		{
			var controller = local ? 1 : 2;
			return new AdvisorPlayerState
			{
				PlayerId = controller,
				IsLocalPlayer = local,
				Class = local ? "ROGUE" : "MAGE",
				DeckCount = local ? 22 : 24,
				Fatigue = 0,
				MaxMana = 4,
				Resources = new AdvisorResourceState
				{
					Total = 4,
					Available = local ? 3 : 2,
					SpellPower = local ? 1 : 0
				},
				Hero = Entity(
					local ? 10 : 20,
					local ? "FRIENDLY_HERO" : "OPPONENT_HERO",
					"HERO",
					controller,
					local ? "Local Secret Name" : "Opponent Secret Name",
					0,
					30),
				HeroPower = Entity(
					local ? 11 : 21,
					local ? "FRIENDLY_POWER" : "OPPONENT_POWER",
					"HERO_POWER",
					controller,
					"Password-Value",
					0,
					0),
				Hand = new List<AdvisorEntityState>
				{
					Entity(
						local ? 12 : 22,
						local ? "FRIENDLY_SPELL" : "INTERNAL_OPPONENT_CARD",
						"SPELL",
						controller,
						"Bearer credential-value",
						0,
						0)
				},
				Board = new List<AdvisorEntityState>
				{
					Entity(
						local ? 13 : 23,
						local ? "FRIENDLY_MINION" : "OPPONENT_MINION",
						"MINION",
						controller,
						"raw PowerLog payload",
						3,
						4),
					Entity(
						local ? 14 : 24,
						local ? "FRIENDLY_LOCATION" : "OPPONENT_LOCATION",
						"LOCATION",
						controller,
						"private location name",
						0,
						2)
				}
			};
		}

		private static AdvisorEntityState Entity(
			int entityId,
			string cardId,
			string cardType,
			int controllerId,
			string name,
			int attack,
			int health)
		{
			return new AdvisorEntityState
			{
				EntityId = entityId,
				CardId = cardId,
				CardType = cardType,
				ControllerId = controllerId,
				Name = name,
				CardText = name + " card_text",
				EnglishText = name + " EnglishText",
				Cost = 2,
				Attack = attack,
				Health = health,
				IsPlayableCard = true
			};
		}

		private sealed class BehaviorCase
		{
			internal BehaviorCase(string side, string kind)
			{
				Side = side;
				Kind = kind;
			}

			internal string Side { get; private set; }
			internal string Kind { get; private set; }
		}

		private sealed class TemporaryDirectory : IDisposable
		{
			internal string Path { get; private set; }

			internal TemporaryDirectory()
			{
				Path = System.IO.Path.Combine(
					System.IO.Path.GetTempPath(),
					"metacompanion-behavior-sequence-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(Path);
			}

			public void Dispose()
			{
				if (Directory.Exists(Path))
					Directory.Delete(Path, true);
			}
		}
	}
}
