using Hearthstone_Deck_Tracker.Enums;
using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class AdvisorIntegrationTest
	{
		[TestMethod]
		public void ShouldStartAdvisorGame_AllowsStandardAndArenaIndependentlyOfDeckPredictions()
		{
			Assert.IsTrue(MetaCompanionPlugin.ShouldStartAdvisorGame(
				Format.Standard, GameMode.Ranked, true));
			Assert.IsTrue(MetaCompanionPlugin.ShouldStartAdvisorGame(
				null, GameMode.Arena, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldStartAdvisorGame(
				Format.Standard, GameMode.Battlegrounds, true));
			Assert.IsFalse(MetaCompanionPlugin.ShouldStartAdvisorGame(
				Format.Standard, GameMode.Ranked, false));
		}

		[TestMethod]
		public void AdvisorRuntimeMode_SeparatesAllFourLiveAndTrainingStates()
		{
			var off = MetaCompanionPlugin.GetAdvisorRuntimeMode(false, false);
			Assert.IsFalse(off.RuntimeNeeded);
			Assert.IsFalse(off.CaptureEnabled);
			Assert.IsFalse(off.ObserveEnabled);
			Assert.IsFalse(off.SolveEnabled);
			Assert.IsFalse(off.UiEnabled);

			var trainingOnly = MetaCompanionPlugin.GetAdvisorRuntimeMode(false, true);
			Assert.IsTrue(trainingOnly.RuntimeNeeded);
			Assert.IsTrue(trainingOnly.CaptureEnabled);
			Assert.IsTrue(trainingOnly.ObserveEnabled);
			Assert.IsFalse(trainingOnly.SolveEnabled,
				"Training-only mode must never call solve.");
			Assert.IsFalse(trainingOnly.UiEnabled,
				"Training-only mode must never display advisor UI.");

			var liveOnly = MetaCompanionPlugin.GetAdvisorRuntimeMode(true, false);
			Assert.IsTrue(liveOnly.RuntimeNeeded);
			Assert.IsTrue(liveOnly.CaptureEnabled);
			Assert.IsFalse(liveOnly.ObserveEnabled);
			Assert.IsTrue(liveOnly.SolveEnabled);
			Assert.IsTrue(liveOnly.UiEnabled);

			var both = MetaCompanionPlugin.GetAdvisorRuntimeMode(true, true);
			Assert.IsTrue(both.RuntimeNeeded);
			Assert.IsTrue(both.CaptureEnabled);
			Assert.IsTrue(both.ObserveEnabled);
			Assert.IsTrue(both.SolveEnabled);
			Assert.IsTrue(both.UiEnabled);
		}

		[TestMethod]
		public void AdvisorRuntimeAction_UsesRuntimeNeededAndRestartsForTrainingOrBackend()
		{
			Assert.AreEqual(AdvisorRuntimeAction.Enable,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(false, false, false, true));
			Assert.AreEqual(AdvisorRuntimeAction.Enable,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(false, false, true, false));
			Assert.AreEqual(AdvisorRuntimeAction.Disable,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(false, true, false, false));
			Assert.AreEqual(AdvisorRuntimeAction.Disable,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(true, false, false, false));
			Assert.AreEqual(AdvisorRuntimeAction.None,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(false, true, true, true));
			Assert.AreEqual(AdvisorRuntimeAction.None,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(true, true, false, true));
			Assert.AreEqual(AdvisorRuntimeAction.RestartWorker,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(true, false, true, true));
			Assert.AreEqual(AdvisorRuntimeAction.RestartWorker,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(true, true, true, false));
			Assert.AreEqual(
				AdvisorRuntimeAction.RestartWorker,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(
					true,
					true,
					true,
					true,
					AdvisorWorkerBackendMode.Auto,
					AdvisorWorkerBackendMode.PythonOnly));
			Assert.AreEqual(
				AdvisorRuntimeAction.RestartWorker,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(
					false,
					true,
					false,
					true,
					AdvisorWorkerBackendMode.Auto,
					AdvisorWorkerBackendMode.PythonOnly));
			Assert.AreEqual(
				AdvisorRuntimeAction.None,
				MetaCompanionPlugin.GetAdvisorRuntimeAction(
					false,
					false,
					false,
					false,
					AdvisorWorkerBackendMode.Auto,
					AdvisorWorkerBackendMode.PythonOnly));
		}

		[TestMethod]
		public void ShouldAttemptAdvisorWorkerStart_EnforcesBackoffAndSingleFlight()
		{
			var now = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
			Assert.IsTrue(MetaCompanionPlugin.ShouldAttemptAdvisorWorkerStart(
				true, false, false, now, now));
			Assert.IsFalse(MetaCompanionPlugin.ShouldAttemptAdvisorWorkerStart(
				false, false, false, now, DateTime.MinValue));
			Assert.IsFalse(MetaCompanionPlugin.ShouldAttemptAdvisorWorkerStart(
				true, true, false, now, DateTime.MinValue));
			Assert.IsFalse(MetaCompanionPlugin.ShouldAttemptAdvisorWorkerStart(
				true, false, true, now, DateTime.MinValue));
			Assert.IsFalse(MetaCompanionPlugin.ShouldAttemptAdvisorWorkerStart(
				true, false, false, now, now.AddSeconds(1)));
		}

		[TestMethod]
		public void AdvisorWarningLogSummary_CollapsesRealWarningFloodByStableCategory()
		{
			var cardNames = new[]
			{
				"Mug's Magic",
				"Steady Shot",
				"Ranger General Sylvanas",
				"Quel'dorei Fletcher",
				"Low Security Wing",
				"Wound Prey",
				"Underbelly Network",
				"Ranger Captain Alleria"
			};
			var warnings = Enumerable.Range(0, 80)
				.Select(index => cardNames[index % cardNames.Length] +
					" has a currently playable unsupported rule " +
					"(mechanic:card_text_not_parsed); ranked advice is withheld.")
				.Concat(Enumerable.Repeat(
					"No opponent response was attached because the bounded counterplay deadline was exhausted.",
					12))
				.Concat(Enumerable.Repeat(
					"The entity snapshot was truncated during capture.", 7))
				.Concat(Enumerable.Repeat(
					"Unknown hidden card may change this line.", 4))
				.Concat(new[]
				{
					"Unknown worker note; session_" + "token=" + "do-not-log-this-value"
				})
				.ToList();

			Log.Debug("警告聚合测试哨兵");
			var logBeforeUiNormalization = Log.PrevLine;
			var notices = AdvisorUserMessages.Notices(warnings);
			Assert.AreEqual(2, notices.Count);
			Assert.AreEqual(logBeforeUiNormalization, Log.PrevLine,
				"UI warning normalization must not write each raw warning to Debug.");

			var summary = MetaCompanionPlugin.BuildAdvisorWarningLogSummary(warnings);

			StringAssert.StartsWith(summary, "顾问模型覆盖限制：");
			StringAssert.Contains(summary, "可出牌规则未覆盖 80 条");
			StringAssert.Contains(summary, "对手回应搜索受限 12 条");
			StringAssert.Contains(summary, "局面采集不完整 7 条");
			StringAssert.Contains(summary, "共 104 条");
			Assert.IsFalse(summary.Contains("隐藏信息影响"),
				"Only the three largest stable categories should be logged.");
			Assert.IsFalse(summary.Contains("Mug's Magic"));
			Assert.IsFalse(summary.Contains("ranked advice"));
			Assert.IsFalse(summary.Contains("card_text_not_parsed"));
			Assert.IsFalse(summary.Contains("do-not-log-this-value"));
			Assert.IsFalse(summary.Contains("故障"));
			Assert.AreEqual(
				AdvisorUserMessages.WarningCode(warnings[0]),
				AdvisorUserMessages.WarningCode(warnings[7]),
				"Card-specific prose must map to one stable aggregation code.");
		}

		[TestMethod]
		public void ShouldSolveAdvisorSnapshot_RequiresAnActionableLocalTurn()
		{
			var state = NewState("state-1");
			Assert.IsTrue(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));

			state.IsLocalPlayerTurn = false;
			Assert.IsFalse(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));
			state.IsLocalPlayerTurn = true;
			state.Phase.HasPendingChoice = true;
			Assert.IsFalse(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));
			state.Phase.HasPendingChoice = false;
			state.IsSpectating = true;
			Assert.IsFalse(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));
		}

		[TestMethod]
		public void ShouldSolveAdvisorSnapshot_RejectsFinishedHeroButToleratesMissingHealth()
		{
			var state = NewState("state-finished");
			state.Player.Hero = new AdvisorEntityState { Health = 30, Damage = 30 };
			state.Opponent.Hero = new AdvisorEntityState { Health = 30, Damage = 12 };
			Assert.IsFalse(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));

			state.Player.Hero.Damage = 12;
			state.Opponent.Hero.Damage = 30;
			Assert.IsFalse(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state));

			state.Opponent.Hero.Health = 0;
			Assert.IsTrue(MetaCompanionPlugin.ShouldSolveAdvisorSnapshot(state),
				"A missing base-health tag must not be mistaken for a finished game.");
		}

		[TestMethod]
		public void Fingerprint_IgnoresCaptureMetadataButChangesWithGameState()
		{
			var first = NewState("state-a");
			first.SnapshotSequence = 1;
			first.CapturedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			var firstHash = AdvisorGameStateFingerprint.Compute(first);

			first.SnapshotSequence = 99;
			first.CapturedAtUtc = first.CapturedAtUtc.AddMinutes(1);
			Assert.AreEqual(firstHash, AdvisorGameStateFingerprint.Compute(first));

			first.Player.Resources.Available++;
			Assert.AreNotEqual(firstHash, AdvisorGameStateFingerprint.Compute(first));
		}

		[TestMethod]
		public void GameStateExtractor_SessionGameAliasIsStablePrivateAndRotatesAtGameBoundary()
		{
			var extractor = new AdvisorGameStateExtractor(() => DateTime.UtcNow);

			var initial = extractor.SessionGameAlias;
			Assert.IsFalse(string.IsNullOrWhiteSpace(initial));
			StringAssert.StartsWith(initial, "g1-");
			Assert.AreEqual(initial, extractor.SessionGameAlias,
				"Repeated reads within one game must keep the same private alias.");

			extractor.BeginGame();
			var rotated = extractor.SessionGameAlias;
			Assert.AreNotEqual(initial, rotated,
				"Starting a new game without an HDT key must rotate the private alias.");
			Assert.AreEqual(rotated, extractor.SessionGameAlias);

			extractor.BeginGame(" fixed ");
			var fixedAlias = extractor.SessionGameAlias;
			StringAssert.StartsWith(fixedAlias, "g1-");
			Assert.AreNotEqual("fixed", fixedAlias,
				"Raw HDT game identifiers must never be copied into advisor snapshots.");
			Assert.IsFalse(fixedAlias.Contains("fixed"));
			Assert.AreEqual(fixedAlias, extractor.SessionGameAlias);

			extractor.BeginGame("fixed");
			Assert.AreEqual(fixedAlias, extractor.SessionGameAlias,
				"The same normalized HDT key must produce a predictable private alias.");

			var secondExtractor = new AdvisorGameStateExtractor(() => DateTime.UtcNow);
			secondExtractor.BeginGame("fixed");
			Assert.AreEqual(fixedAlias, secondExtractor.SessionGameAlias);
		}

		[TestMethod]
		public void GameStateExtractor_HeroAttackHistoryPreservesExplicitZero()
		{
			var heroTags = new Dictionary<string, int>(StringComparer.Ordinal);
			AdvisorGameStateExtractor.RecordHeroAttackHistoryTag(
				heroTags,
				(int)HearthDb.Enums.CardType.HERO,
				0);

			Assert.IsTrue(heroTags.ContainsKey("NUM_ATTACKS_THIS_TURN"));
			Assert.AreEqual(0, heroTags["NUM_ATTACKS_THIS_TURN"]);

			var minionTags = new Dictionary<string, int>(StringComparer.Ordinal);
			AdvisorGameStateExtractor.RecordHeroAttackHistoryTag(
				minionTags,
				(int)HearthDb.Enums.CardType.MINION,
				0);
			Assert.IsFalse(minionTags.ContainsKey("NUM_ATTACKS_THIS_TURN"));
		}

		[TestMethod]
		public void GameStateExtractor_UsesPublicHealthAsWeaponOrLocationDurability()
		{
			var healthOnly = new Dictionary<string, int>(StringComparer.Ordinal)
			{
				{ "HEALTH", 2 }
			};
			Assert.AreEqual(
				2,
				AdvisorGameStateExtractor.ResolveDurability(
					healthOnly, (int)HearthDb.Enums.CardType.WEAPON, 2));
			Assert.AreEqual(
				2,
				AdvisorGameStateExtractor.ResolveDurability(
					healthOnly, (int)HearthDb.Enums.CardType.LOCATION, 2));
			Assert.AreEqual(
				0,
				AdvisorGameStateExtractor.ResolveDurability(
					healthOnly, (int)HearthDb.Enums.CardType.MINION, 2));

			healthOnly["DURABILITY"] = 1;
			Assert.AreEqual(
				1,
				AdvisorGameStateExtractor.ResolveDurability(
					healthOnly, (int)HearthDb.Enums.CardType.WEAPON, 2));
		}

		[TestMethod]
		public void WireProtocol_UsesSnakeCaseAndEmptyEntityIdsForObservations()
		{
			var state = NewState("state-1");
			state.Player.Board.Add(new AdvisorEntityState
			{
				EntityId = 41,
				CardId = "TEST_COMBAT_ENTITY",
				CardType = "MINION",
				HasWindfury = true,
				HasMegaWindfury = true,
				IsImmune = true
			});
			var solveJson = AdvisorWireProtocol.SerializeSolveRequest(new AdvisorSolveRequest
			{
				RequestId = "request-1",
				State = state,
				Options = new AdvisorSolveOptions
				{
					MaxIterations = 4321,
					MaxDepth = 9
				},
				Metadata = new Dictionary<string, string>
				{
					{ "trajectory_schema", "trajectory-readiness-v1" },
					{ "decision_id", "state-1" },
					{ "solve_stage", "single" },
					{ "snapshot_sequence", "7" },
					{ "capture_contract", "hdt-public-snapshot-v1" }
				}
			});
			StringAssert.Contains(solveJson, "\"api_version\":\"1.0\"");
			StringAssert.Contains(solveJson, "\"state_id\":\"state-1\"");
			StringAssert.Contains(solveJson, "\"has_mega_windfury\":true");
			StringAssert.Contains(solveJson, "\"is_immune\":true");
			Assert.IsFalse(solveJson.Contains("StateId"));
			var solveRoot = AdvisorWireProtocol.ParseObject(solveJson);
			Assert.IsTrue(solveRoot.ContainsKey("metadata"),
				"Trajectory metadata must be serialized at the solve-request root.");
			var solveMetadata = solveRoot["metadata"] as IDictionary<string, object>;
			Assert.IsNotNull(solveMetadata);
			Assert.AreEqual("trajectory-readiness-v1", solveMetadata["trajectory_schema"]);
			Assert.AreEqual("state-1", solveMetadata["decision_id"]);
			Assert.AreEqual("single", solveMetadata["solve_stage"]);
			Assert.AreEqual("7", solveMetadata["snapshot_sequence"]);
			Assert.AreEqual("hdt-public-snapshot-v1", solveMetadata["capture_contract"]);
			var solveOptions = solveRoot["options"] as IDictionary<string, object>;
			Assert.IsNotNull(solveOptions);
			Assert.AreEqual(4321, Convert.ToInt32(
				solveOptions["max_iterations"], CultureInfo.InvariantCulture));
			Assert.AreEqual(9, Convert.ToInt32(
				solveOptions["max_depth"], CultureInfo.InvariantCulture));
			Assert.IsFalse(solveOptions.ContainsKey("initial_max_iterations"));
			Assert.IsFalse(solveOptions.ContainsKey("initial_max_depth"));
			Assert.IsFalse(solveOptions.ContainsKey("metadata"),
				"Trajectory metadata must not be hidden inside solve options.");

			var observationJson = AdvisorWireProtocol.SerializeObservation(new AdvisorObservation
			{
				Kind = "action",
				StateId = "state-1",
				ObservedAtUtc = DateTime.UtcNow,
				Action = new AdvisorObservedAction { Kind = "end_turn" }
			});
			StringAssert.Contains(observationJson, "\"source_entity_id\":\"\"");
			StringAssert.Contains(observationJson, "\"target_entity_id\":\"\"");
		}

		[TestMethod]
		public void WireProtocol_UsesPermanentResourceCrystalsInsteadOfHdtRulesCap()
		{
			var state = NewState("mana-source");
			state.Player.MaxMana = 10;
			state.Player.Resources = new AdvisorResourceState { Total = 1, Available = 1 };
			state.Opponent.MaxMana = 10;
			state.Opponent.Resources = new AdvisorResourceState { Total = 0, Available = 0 };

			var root = AdvisorWireProtocol.ParseObject(
				AdvisorWireProtocol.SerializeSolveRequest(new AdvisorSolveRequest
				{
					RequestId = "mana-source-request",
					State = state
				}));
			var wireState = (IDictionary<string, object>)root["state"];
			var player = (IDictionary<string, object>)wireState["player"];
			var opponent = (IDictionary<string, object>)wireState["opponent"];

			Assert.AreEqual(1, Convert.ToInt32(
				player["max_mana"], CultureInfo.InvariantCulture));
			Assert.AreEqual(0, Convert.ToInt32(
				opponent["max_mana"], CultureInfo.InvariantCulture));
		}

		[TestMethod]
		public void WireProtocol_SerializesTheSameCompleteHdtRootContractForSolveAndObservation()
		{
			var state = NewState("root-wire-state");
			var candidates = NewHdtRootCandidates(state.StateId);
			var solve = AdvisorWireProtocol.ParseObject(
				AdvisorWireProtocol.SerializeSolveRequest(new AdvisorSolveRequest
				{
					RequestId = "root-wire-solve",
					State = state,
					HdtRootCandidates = candidates
				}));
			var solveCandidates = solve["hdt_root_candidates"] as IDictionary<string, object>;
			Assert.IsNotNull(solveCandidates);
			Assert.AreEqual(AdvisorHdtRootCandidateSet.ContractId, solveCandidates["contract"]);
			Assert.AreEqual(state.StateId, solveCandidates["state_id"]);
			Assert.AreEqual(42, Convert.ToInt32(solveCandidates["frame_id"],
				CultureInfo.InvariantCulture));
			Assert.AreEqual(7L, Convert.ToInt64(solveCandidates["collector_epoch"],
				CultureInfo.InvariantCulture));
			Assert.AreEqual(true, solveCandidates["candidate_set_complete"]);
			var solveItems = solveCandidates["candidates"] as System.Collections.IList;
			Assert.IsNotNull(solveItems);
			Assert.AreEqual(1, solveItems.Count);
			var solveItem = solveItems[0] as IDictionary<string, object>;
			var solveAction = solveItem["action"] as IDictionary<string, object>;
			Assert.AreEqual("end_turn", solveAction["kind"]);
			Assert.AreEqual("", solveAction["source_entity_id"]);

			var observation = AdvisorWireProtocol.ParseObject(
				AdvisorWireProtocol.SerializeObservation(new AdvisorObservation
				{
					Kind = "action",
					StateId = state.StateId,
					ObservedAtUtc = DateTime.UtcNow,
					Action = new AdvisorObservedAction
					{
						Kind = "end_turn",
						HdtRootCandidates = candidates
					}
				}));
			var observationAction = observation["action"] as IDictionary<string, object>;
			Assert.IsNotNull(observationAction);
			var observationCandidates = observationAction["hdt_root_candidates"]
				as IDictionary<string, object>;
			Assert.IsNotNull(observationCandidates);
			Assert.AreEqual(solveCandidates["contract"], observationCandidates["contract"]);
			Assert.AreEqual(solveCandidates["state_id"], observationCandidates["state_id"]);
			Assert.AreEqual(solveCandidates["frame_watermark"],
				observationCandidates["frame_watermark"]);
		}

		[TestMethod]
		public void WireProtocol_HealthKeepsRustPreviewOutOfProductionByDefault()
		{
			var preview = AdvisorWireProtocol.DeserializeHealth(
				"{\"api_version\":\"1.0\",\"status\":\"ready\",\"is_ready\":true," +
				"\"backend\":\"rust\",\"parity_profile\":\"combat-v1\"}");

			Assert.IsTrue(preview.IsReady);
			Assert.AreEqual("rust", preview.Backend);
			Assert.AreEqual("combat-v1", preview.ParityProfile);
			Assert.IsFalse(preview.IsProductionReady,
				"A Rust build must opt in only after the fixed full parity gate passes.");
			Assert.IsFalse(preview.SupportsCounterplayTurnpair);

			var contradictory = AdvisorWireProtocol.DeserializeHealth(
				"{\"api_version\":\"1.0\",\"status\":\"ready\",\"is_ready\":false}");
			Assert.IsFalse(contradictory.IsReady,
				"A ready status string must not override an explicit false readiness flag.");

			var promoted = AdvisorWireProtocol.DeserializeHealth(
				"{\"api_version\":\"1.0\",\"status\":\"ready\",\"is_ready\":true," +
				"\"backend\":\"rust\",\"parity_profile\":\"full\"," +
				"\"production_ready\":true,\"capabilities\":{\"counterplay_turnpair_v1\":true," +
				"\"behavior_search_ordering_prior_v1\":true," +
				"\"hdt_decision_ranker_v1\":true," +
				"\"hdt_behavior_reference_v1\":true}," +
				"\"behavior_prior\":{\"available\":true,\"status\":\"ready\"," +
				"\"reason\":\"只用于合法动作排序。\",\"artifact_sha256\":\"" +
				new string('a', 64) + "\"}," +
				"\"decision_ranker\":{\"available\":true,\"status\":\"ready\"," +
				"\"reason\":\"只用于本方合法动作排序。\",\"artifact_sha256\":\"" +
				new string('b', 64) + "\"}}");
			Assert.IsTrue(promoted.IsProductionReady);
			Assert.IsTrue(promoted.SupportsCounterplayTurnpair);
			Assert.IsTrue(promoted.SupportsBehaviorSearchOrderingPrior);
			Assert.IsTrue(promoted.SupportsDecisionRanker);
			Assert.IsTrue(promoted.SupportsBehaviorReference);
			Assert.IsTrue(promoted.BehaviorPriorAvailable);
			Assert.AreEqual("ready", promoted.BehaviorPriorStatus);
			Assert.AreEqual("只用于合法动作排序。", promoted.BehaviorPriorReason);
			Assert.AreEqual(new string('a', 64), promoted.BehaviorPriorArtifactSha256);
			Assert.IsTrue(promoted.DecisionRankerAvailable);
			Assert.AreEqual("ready", promoted.DecisionRankerStatus);
			Assert.AreEqual("只用于本方合法动作排序。", promoted.DecisionRankerReason);
			Assert.AreEqual(new string('b', 64), promoted.DecisionRankerArtifactSha256);

			var legacyPython = AdvisorWireProtocol.DeserializeHealth(
				"{\"api_version\":\"1.0\",\"status\":\"ready\",\"is_ready\":true}");
			Assert.IsTrue(legacyPython.IsProductionReady,
				"Existing Python workers remain backward compatible while migration is staged.");
		}

		[TestMethod]
		public void WireProtocol_BehaviorReferencesBindCompleteHdtCandidatesAndRenderChinese()
		{
			var request = BehaviorReferenceRequest();
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				ValidBehaviorReferenceJson(), request);

			var references = response.BehaviorReferences;
			Assert.IsTrue(references.IsDisplayEligible);
			Assert.IsTrue(references.Available);
			Assert.AreEqual(2, references.CandidateCount);
			Assert.AreEqual(2, references.RankedCandidateCount);
			Assert.AreEqual(2, references.DisplayedReferenceCount);
			Assert.AreEqual("location_activate:55:", references.References[0].LegalActionId);
			Assert.AreEqual("location_activate", references.References[0].Action.Type);
			Assert.IsFalse(references.References[0].ProbabilityCalibratedAsWinRate);
			Assert.IsFalse(references.References[0].OptimalityVerified);
			Assert.IsTrue(AdvisorPanel.ShouldDisplayBehaviorReferences(
				response.Status, references));
			Assert.IsFalse(AdvisorPanel.ShouldDisplayBehaviorReferences("ok", references));

			var line = AdvisorPanel.BuildBehaviorReferenceLine(references.References[0]);
			StringAssert.Contains(line, "激活地点");
			StringAssert.Contains(line, "历史选择倾向");
			StringAssert.Contains(line, "不是胜率");
			Assert.IsFalse(AdvisorUserMessages.ContainsTechnicalDetail(line));
			StringAssert.Contains(AdvisorPanel.BehaviorReferenceHeading, "不代表最优");
			StringAssert.Contains(AdvisorPanel.BehaviorReferenceDisclosure, "不会自动出牌");
		}

		[TestMethod]
		public void WireProtocol_TamperedBehaviorReferencesAreHiddenFailClosed()
		{
			var valid = ValidBehaviorReferenceJson();
			var hash = new string('b', 64);
			var tampered = new[]
			{
				valid.Replace("\"candidate_count\":2", "\"candidate_count\":3"),
				valid.Replace(
					"\"artifact_sha256\":\"" + hash + "\",\"candidate_set_contract\"",
					"\"artifact_sha256\":\"" + new string('c', 64) +
					"\",\"candidate_set_contract\""),
				valid.Replace("\"automatic_action_allowed\":false",
					"\"automatic_action_allowed\":true"),
				valid.Replace("\"score_override_allowed\":false",
					"\"score_override_allowed\":true"),
				valid.Replace("\"probability_calibrated_as_win_rate\":false",
					"\"probability_calibrated_as_win_rate\":true"),
				valid.Replace("\"legal_action_id\":\"location_activate:55:\"",
					"\"legal_action_id\":\"location_activate:999:\""),
				valid.Replace("\"candidate_set_complete\":true",
					"\"candidate_set_complete\":false"),
				valid.Replace("\"observed_choice_probability\":0.75",
					"\"observed_choice_probability\":\"0.75\""),
				valid.Replace("\"observed_choice_probability\":0.75",
					"\"observed_choice_probability\":0.20"),
				valid.Replace("\"card_id\":\"PUBLIC_LOCATION\"",
					"\"card_id\":\"OTHER_LOCATION\""),
				valid.Replace("\"outcome_used_as_action_optimality\":false",
					"\"outcome_used_as_action_optimality\":true"),
				valid.Replace("\"status\":\"partial\"", "\"status\":\"ok\"")
			};

			foreach (var json in tampered)
			{
				var response = AdvisorWireProtocol.DeserializeSolveResponse(
					json, BehaviorReferenceRequest());
				Assert.IsFalse(response.BehaviorReferences.IsDisplayEligible);
				Assert.IsFalse(response.BehaviorReferences.Available);
				Assert.AreEqual(0, response.BehaviorReferences.References.Count);
				Assert.IsFalse(AdvisorPanel.ShouldDisplayBehaviorReferences(
					response.Status, response.BehaviorReferences));
				Assert.IsTrue(response.Warnings.Any(
					warning => warning.Contains("历史打法参考") && warning.Contains("已安全隐藏")));
			}

			var unbound = AdvisorWireProtocol.DeserializeSolveResponse(valid);
			Assert.IsFalse(unbound.BehaviorReferences.IsDisplayEligible);
			Assert.AreEqual(0, unbound.BehaviorReferences.References.Count);
		}

		[TestMethod]
		public void WireProtocol_ParsesIndependentOrderingModelsForChineseUi()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\"," +
				"\"state_id\":\"s1\",\"status\":\"partial\",\"is_final\":true," +
				"\"coverage\":{" +
				"\"behavior_prior\":{\"status\":\"applied\"," +
				"\"artifact_sha256\":\"" + new string('a', 64) + "\"," +
				"\"ordering_attempt_count\":4,\"ordering_applied\":true," +
				"\"search_ordering_only\":true," +
				"\"candidate_generation_allowed\":false," +
				"\"score_override_allowed\":false,\"live_policy_eligible\":false," +
				"\"rl_training_eligible\":false,\"optimality_verified\":false}," +
				"\"decision_ranker\":{\"status\":\"applied\"," +
				"\"artifact_sha256\":\"" + new string('b', 64) + "\"," +
				"\"ordering_attempt_count\":3,\"ordering_applied\":true," +
				"\"local_actions_only\":true,\"search_ordering_only\":true," +
				"\"candidate_generation_allowed\":false," +
				"\"score_override_allowed\":false,\"live_policy_eligible\":false," +
				"\"rl_training_eligible\":false,\"optimality_verified\":false}}," +
				"\"recommendations\":[]}");

			Assert.AreEqual("applied", response.Coverage.BehaviorPrior.Status);
			Assert.AreEqual(4, response.Coverage.BehaviorPrior.OrderingAttemptCount);
			Assert.IsTrue(response.Coverage.BehaviorPrior.OrderingApplied);
			Assert.AreEqual("applied", response.Coverage.DecisionRanker.Status);
			Assert.AreEqual(3, response.Coverage.DecisionRanker.OrderingAttemptCount);
			Assert.IsTrue(response.Coverage.DecisionRanker.LocalActionsOnly);
			Assert.IsFalse(response.Coverage.DecisionRanker.CandidateGenerationAllowed);
			Assert.IsFalse(response.Coverage.DecisionRanker.OptimalityVerified);

			var subtitle = AdvisorPanel.BuildOrderingModelSubtitle(response.Coverage);
			StringAssert.Contains(subtitle, "本方决策排序已参与");
			StringAssert.Contains(subtitle, "对手行为先验已参与");
			StringAssert.Contains(subtitle, "不会自动操作游戏");
			Assert.AreEqual(0, AdvisorPanel.BuildOrderingModelNotices(response.Coverage).Count);
			var diagnostic = AdvisorUserMessages.SolveDiagnosticSummary(
				response, "final", 0);
			StringAssert.Contains(diagnostic, "本方排序=已应用");
			StringAssert.Contains(diagnostic, "对手排序=已应用");
			Assert.IsFalse(diagnostic.Contains(new string('a', 64)));
			Assert.IsFalse(diagnostic.Contains(new string('b', 64)));

			response.Coverage.DecisionRanker.CandidateGenerationAllowed = true;
			Assert.IsFalse(
				AdvisorPanel.BuildOrderingModelSubtitle(response.Coverage)
					.Contains("本方决策排序已参与"));
			StringAssert.Contains(
				AdvisorPanel.BuildOrderingModelNotices(response.Coverage)[0],
				"状态校验未通过");
		}

		[TestMethod]
		public void WireProtocol_TransitionCandidateCarriesDetachedPreAndPostStates()
		{
			var pre = NewState("state-pre");
			pre.StateHash = new string('a', 64);
			pre.SnapshotSequence = 10;
			var post = NewState("state-post");
			post.StateHash = new string('b', 64);
			post.SnapshotSequence = 12;
			var json = AdvisorWireProtocol.SerializeObservation(new AdvisorObservation
			{
				Kind = "action",
				StateId = "state-pre",
				GameId = "g1-test",
				ObservedAtUtc = DateTime.UtcNow,
				Action = new AdvisorObservedAction { Kind = "end_turn" },
				PreState = pre,
				PostState = post
			});

			var root = AdvisorWireProtocol.ParseObject(json);
			var preState = root["pre_state"] as IDictionary<string, object>;
			var postState = root["post_state"] as IDictionary<string, object>;
			Assert.IsNotNull(preState);
			Assert.IsNotNull(postState);
			Assert.AreEqual("state-pre", preState["state_id"]);
			Assert.AreEqual(10, preState["snapshot_sequence"]);
			Assert.AreEqual("state-post", postState["state_id"]);
			Assert.AreEqual(12, postState["snapshot_sequence"]);
			StringAssert.Contains(json, "\"pre_state\":{");
			StringAssert.Contains(json, "\"post_state\":{");
		}

		[TestMethod]
		public void TransitionCandidate_RequiresTwoQuietCapturesAndStaysIneligible()
		{
			var tracker = new AdvisorTransitionCandidateTracker();
			var pre = NewTransitionState("state-pre", 'a', 10);
			tracker.Register(new AdvisorPendingAction
			{
				PreState = pre,
				Kind = "end_turn",
				SourceEntityResolution = "not_applicable",
				TargetEntityResolution = "not_applicable",
				ObservedAtUtc = DateTime.UtcNow,
				GameGeneration = 7,
				ActionEventSequence = 1
			});

			var firstPost = NewTransitionState("state-post", 'b', 11);
			Assert.AreEqual(0, tracker.ObserveSnapshot(firstPost, 7, 20).Count);
			var confirmedPost = NewTransitionState("state-post", 'b', 12);
			var observations = tracker.ObserveSnapshot(confirmedPost, 7, 20);

			Assert.AreEqual(1, observations.Count);
			var observation = observations[0];
			Assert.AreSame(confirmedPost, observation.PostState);
			Assert.AreEqual("state-pre", observation.StateId);
			Assert.AreEqual("state-post", observation.Metadata["post_state_id"]);
			Assert.AreEqual(new string('a', 64), observation.Metadata["raw_pre_snapshot_hash"]);
			Assert.AreEqual(new string('b', 64), observation.Metadata["raw_post_snapshot_hash"]);
			Assert.AreEqual("10", observation.Metadata["pre_snapshot_sequence"]);
			Assert.AreEqual("12", observation.Metadata["post_snapshot_sequence"]);
			Assert.AreEqual("isolated", observation.Metadata["boundary_status"]);
			Assert.AreEqual("0", observation.Metadata["intervening_action_count"]);
			Assert.AreEqual("partial_hdt_transition_candidate_v1",
				observation.Metadata["capture_contract"]);
			Assert.AreEqual("post_state_candidate_unverified",
				observation.Metadata["transition_status"]);
			Assert.AreEqual("producer_candidate_unverified",
				observation.Metadata["transition_verification"]);
			Assert.AreEqual("partial_hdt_gameevents_v1",
				observation.Metadata["completeness"]);
			Assert.AreEqual("false", observation.Metadata["training_eligible"]);
			Assert.AreEqual(0, tracker.PendingCount);
		}

		[TestMethod]
		public void TransitionCandidate_DoesNotCrossEventRevisionOrHideOverlappingActions()
		{
			var tracker = new AdvisorTransitionCandidateTracker();
			var pre = NewTransitionState("state-pre", 'a', 10);
			tracker.Register(NewPendingAction(pre, "play_card", 1));
			tracker.Register(NewPendingAction(pre, "attack", 2));
			var post11 = NewTransitionState("state-post", 'b', 11);
			var post12 = NewTransitionState("state-post", 'b', 12);
			var post13 = NewTransitionState("state-post", 'b', 13);

			Assert.AreEqual(0, tracker.ObserveSnapshot(post11, 7, 20).Count);
			Assert.AreEqual(0, tracker.ObserveSnapshot(post12, 7, 21).Count,
				"A changed HDT event revision must restart boundary confirmation.");
			var observations = tracker.ObserveSnapshot(post13, 7, 21);

			Assert.AreEqual(2, observations.Count);
			Assert.IsTrue(observations.All(item =>
				item.Metadata["boundary_status"] == "overlapped"));
			Assert.AreEqual("1", observations[0].Metadata["intervening_action_count"]);
			Assert.AreEqual("0", observations[1].Metadata["intervening_action_count"]);
			Assert.IsTrue(observations.All(item =>
				item.Metadata["training_eligible"] == "false"));
		}

		[TestMethod]
		public void TransitionCandidate_InterveningEventAndCaptureWarningMarkBoundaryNonIsolated()
		{
			var tracker = new AdvisorTransitionCandidateTracker();
			var pre = NewTransitionState("state-pre", 'a', 10);
			tracker.Register(NewPendingAction(pre, "hero_power", 1));
			tracker.MarkInterveningAction();

			var firstPost = NewTransitionState("state-post", 'b', 11);
			firstPost.CaptureWarnings.Add("capture warning");
			Assert.AreEqual(0, tracker.ObserveSnapshot(firstPost, 7, 20).Count);
			var confirmedPost = NewTransitionState("state-post", 'b', 12);
			confirmedPost.CaptureWarnings.Add("capture warning");
			var observations = tracker.ObserveSnapshot(confirmedPost, 7, 20);

			Assert.AreEqual(1, observations.Count);
			Assert.AreEqual("unstable", observations[0].Metadata["boundary_status"]);
			Assert.AreEqual("1", observations[0].Metadata["intervening_action_count"]);
			Assert.AreEqual("1", observations[0].Metadata["capture_warning_count"]);
			Assert.AreEqual("false", observations[0].Metadata["training_eligible"]);
		}

		[TestMethod]
		public void TransitionCandidate_RejectsTransientAndCrossGameSnapshots()
		{
			var tracker = new AdvisorTransitionCandidateTracker();
			var pre = NewTransitionState("state-pre", 'a', 10);
			tracker.Register(NewPendingAction(pre, "attack", 1));

			var transient = NewTransitionState("state-post", 'b', 11);
			transient.Phase.ProposedAttackerEntityId = 42;
			Assert.AreEqual(0, tracker.ObserveSnapshot(transient, 7, 20).Count);
			var otherGame = NewTransitionState("state-post", 'b', 12);
			otherGame.GameId = "g1-other";
			Assert.AreEqual(0, tracker.ObserveSnapshot(otherGame, 7, 20).Count);
			Assert.AreEqual(1, tracker.PendingCount,
				"A mismatched candidate must not be silently attached or dropped.");
			Assert.AreEqual(1, tracker.DiscardUnresolved());
			Assert.AreEqual(0, tracker.PendingCount);
		}

		[TestMethod]
		public void FindUniqueEntityId_RejectsDuplicateCardIdsAndWrongSideFallbacks()
		{
			var state = NewState("state-1");
			state.Player.Hand.Add(new AdvisorEntityState { EntityId = 10, CardId = "CARD_A" });
			state.Player.Hand.Add(new AdvisorEntityState { EntityId = 11, CardId = "CARD_A" });
			state.Opponent.Hand.Add(new AdvisorEntityState { EntityId = 99, CardId = "CARD_B" });

			string resolution;
			Assert.IsNull(MetaCompanionPlugin.FindUniqueEntityId(
				state, "CARD_A", true, "hand", out resolution));
			Assert.AreEqual("ambiguous_card_id_match", resolution);
			state.Player.Hand.RemoveAt(1);
			Assert.AreEqual(10, MetaCompanionPlugin.FindUniqueEntityId(
				state, "CARD_A", true, "hand", out resolution));
			Assert.AreEqual("unique_card_id_match", resolution);
			Assert.IsNull(MetaCompanionPlugin.FindUniqueEntityId(
				state, "CARD_B", true, "hand", out resolution));
			Assert.AreEqual("not_found", resolution,
				"A local action must never fall back to an opponent entity with the same card ID.");
		}

		[TestMethod]
		public void WireProtocol_ParsesOnlyCanonicalOneBasedBoardPositions()
		{
			var validJson =
				"{\"api_version\":\"1.0\",\"request_id\":\"position\"," +
				"\"state_id\":\"s1\",\"status\":\"partial\",\"recommendations\":[{" +
				"\"rank\":1,\"actions\":[{\"index\":1," +
				"\"action_id\":\"play_card:21::position=2\"," +
				"\"kind\":\"play_card\",\"type\":\"play_card\"," +
				"\"source_entity_id\":21,\"target_entity_id\":null," +
				"\"board_position\":2,\"card_id\":\"TEST_MINION\"}]}]}";
			var valid = AdvisorWireProtocol.DeserializeSolveResponse(validJson)
				.Recommendations[0].Actions[0];

			Assert.AreEqual(2, valid.BoardPosition);
			Assert.IsTrue(valid.HasCanonicalActionId);

			foreach (var invalidValue in new[] { "0", "-1", "8", "\"2\"", "2.5", "true", "null" })
			{
				var invalid = AdvisorWireProtocol.DeserializeSolveResponse(
					validJson.Replace("\"board_position\":2", "\"board_position\":" + invalidValue))
					.Recommendations[0].Actions[0];
				Assert.IsFalse(invalid.HasCanonicalActionId, invalidValue);
				Assert.IsFalse(invalid.BoardPosition.HasValue, invalidValue);
			}

			var mismatchedIdentity = AdvisorWireProtocol.DeserializeSolveResponse(
				validJson.Replace("position=2", "position=3"))
				.Recommendations[0].Actions[0];
			Assert.AreEqual(2, mismatchedIdentity.BoardPosition);
			Assert.IsFalse(mismatchedIdentity.HasCanonicalActionId);

			var nonPlayPosition = AdvisorWireProtocol.DeserializeSolveResponse(
				validJson
					.Replace("play_card:21::position=2", "attack:21:30:position=2")
					.Replace("\"play_card\"", "\"attack\"")
					.Replace("\"target_entity_id\":null", "\"target_entity_id\":30"))
				.Recommendations[0].Actions[0];
			Assert.IsFalse(nonPlayPosition.HasCanonicalActionId);
			Assert.IsFalse(nonPlayPosition.BoardPosition.HasValue);
		}

		[TestMethod]
		public void WireProtocol_OpponentResponseEquivalenceIncludesBoardPosition()
		{
			var positionOne = "{\"index\":1," +
				"\"action_id\":\"play_card:21::position=1\"," +
				"\"kind\":\"play_card\",\"type\":\"play_card\"," +
				"\"source_entity_id\":21,\"target_entity_id\":null," +
				"\"board_position\":1,\"card_id\":\"TEST_MINION\"}";
			var positionTwo = positionOne
				.Replace("position=1", "position=2")
				.Replace("\"board_position\":1", "\"board_position\":2");
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"position-reply\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\",\"recommendations\":[{" +
				"\"rank\":1,\"expected_win_probability\":0.4," +
				"\"score_kind\":\"counterplay_tactical_state_value\"," +
				"\"response_scope\":\"visible_generic_turnpair_v1\"," +
				"\"response_kind\":\"minimax_best_response\"," +
				"\"is_response_verified\":true,\"response_search_complete\":true," +
				"\"response_is_proven_lethal\":false,\"minimax_value\":120," +
				"\"is_safe_after_response\":true,\"opponent_reply\":[" + positionTwo + "]," +
				"\"opponent_response\":{\"actions\":[" + positionOne + "]," +
				"\"tactical_value\":120},\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsFalse(recommendation.IsResponseVerified);
			Assert.AreEqual(1, recommendation.OpponentReply[0].BoardPosition);
			Assert.IsTrue(recommendation.Risks.Exists(
				item => item.Contains("标准对手回应与兼容回应动作不一致")));
		}

		[TestMethod]
		public void WireProtocol_ParsesModeledLethalProofWithoutCallingScoreAWinRate()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":1.0,\"score_kind\":\"heuristic_state_value\"," +
				"\"is_proven_lethal\":true,\"proof_kind\":\"modeled_lethal\"," +
				"\"proof_scope\":\"visible_generic_v2\",\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsTrue(recommendation.IsProvenLethal);
			Assert.AreEqual("modeled_lethal", recommendation.ProofKind);
			Assert.AreEqual("visible_generic_v2", recommendation.ProofScope);
			Assert.AreEqual("heuristic_state_value", recommendation.ScoreKind);
		}

		[TestMethod]
		public void WireProtocol_DowngradesInconsistentLethalProofToHeuristicRoute()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":0.5,\"is_proven_lethal\":true," +
				"\"proof_kind\":\"modeled_lethal\",\"proof_scope\":\"\",\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsFalse(recommendation.IsProvenLethal);
			Assert.IsTrue(recommendation.Risks.Exists(
				item => item.Contains("证明字段不一致")));
		}

		[TestMethod]
		public void WireProtocol_FinalVisibleResponsePartialStaysHonestAndRendersOnlyChinese()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"visible-partial\"," +
				"\"state_id\":\"visible-state\",\"status\":\"partial\",\"is_final\":true," +
				"\"message\":\"Visible response fallback completed.\"," +
				"\"coverage\":{\"exact\":false,\"exact_scope\":\"visible-response-v1\"," +
				"\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":3,\"legal_first_action_ids\":[" +
				"\"attack:101:201\",\"attack:101:30\",\"end_turn\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:101:201\",\"attack:101:30\"]," +
				"\"response_verified_first_action_count\":0," +
				"\"response_verified_first_action_ids\":[]," +
				"\"missing_first_action_ids\":[" +
				"\"attack:101:201\",\"attack:101:30\",\"end_turn\"]," +
				"\"root_action_coverage_complete\":false," +
				"\"portfolio_optimality_proven\":false," +
				"\"search_complete\":false}}}," +
				"\"recommendations\":[" +
				VisiblePartialRecommendationJson(1, 101, 201) + "," +
				VisiblePartialRecommendationJson(2, 101, 30) + "]," +
				"\"warnings\":[\"Unknown hidden opponent cards may change this line.\"]}"
			);

			Assert.AreEqual("partial", response.Status);
			Assert.IsTrue(response.IsFinal);
			Assert.IsTrue(response.Coverage.HasRootActionCoverageContract);
			Assert.IsTrue(response.Coverage.RootActionCoverageContractValid);
			Assert.IsFalse(response.Coverage.RootActionCoverageComplete);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual(0, response.Coverage.ResponseVerifiedFirstActionCount);
			Assert.AreEqual(2, response.Recommendations.Count);
			Assert.IsTrue(response.Recommendations.All(item =>
				!item.IsProvenLethal &&
				!item.IsResponseVerified &&
				!item.ResponseSearchComplete &&
				!item.ResponseIsProvenLethal &&
				!item.IsSafeAfterResponse.HasValue &&
				!item.VerifiedPortfolioRegret.HasValue &&
				item.AlternativeKind == "fallback"));
			Assert.IsTrue(response.Recommendations.All(item =>
				!item.Risks.Exists(risk => risk.Contains("契约不一致"))));

			var status = AdvisorPanel.BuildSolveStatusText(
				response.Status,
				response.IsFinal,
				false,
				false,
				false,
				true,
				false);
			Assert.AreEqual("计算完成 · 近似路线，仅供参考", status);
			foreach (var forbidden in new[] { "搜索中", "仍在校验", "已验证", "最优", "安全" })
				Assert.IsFalse(status.Contains(forbidden), status);

			var visibleText = string.Join("；", new[]
			{
				status,
				AdvisorUserMessages.ResponseStatus(
					response.Status, response.Message, response.IsFinal, true),
				AdvisorUserMessages.RecommendationSummary(response.Recommendations[0]),
				AdvisorPanel.BuildMetrics(response.Recommendations[0]),
				AdvisorPanel.BuildActionLine(response.Recommendations[0].Actions),
				AdvisorUserMessages.PortfolioCoverageSummary(response.Coverage),
				string.Join("；", AdvisorUserMessages.Notices(response.Warnings).ToArray())
			});
			Assert.IsFalse(
				System.Text.RegularExpressions.Regex.IsMatch(visibleText, "[A-Za-z]{3,}"),
				visibleText);
			Assert.IsFalse(visibleText.Contains("契约不一致"), visibleText);
			StringAssert.Contains(visibleText, "近似候选，不构成安全或最优证明");
		}

		[TestMethod]
		public void AdvisorPanel_StatusTextTreatsOnlyActivePartialWorkAsSearching()
		{
			Assert.AreEqual(
				"搜索中 · 对手回应尚未全部验证",
				AdvisorPanel.BuildSolveStatusText(
					"partial", false, false, false, false, true, false));
			Assert.AreEqual(
				"搜索中 · 对手回应尚未全部验证",
				AdvisorPanel.BuildSolveStatusText(
					"thinking", true, false, false, false, false, false));
			Assert.AreEqual(
				"计算完成 · 近似路线，仅供参考",
				AdvisorPanel.BuildSolveStatusText(
					"partial", true, false, false, false, true, false));
		}

		[TestMethod]
		public void WireProtocol_ParsesScopedCounterplayAndOpponentReply()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":0.1,\"score_kind\":\"counterplay_tactical_state_value\"," +
				"\"worst_case_score\":0.1,\"response_scope\":\"visible_generic_turnpair_v1\"," +
				"\"is_response_verified\":true,\"response_kind\":\"minimax_best_response\"," +
				"\"response_search_complete\":true,\"response_is_proven_lethal\":true," +
				"\"minimax_value\":-1000000,\"is_safe_after_response\":false," +
				"\"response_nodes_expanded\":7,\"response_searched_depth\":2," +
				"\"response_transposition_hits\":1," +
				"\"score_components\":{\"raw_score\":-9.5,\"minimax_value\":-1000000}," +
				"\"actions\":[{\"index\":1,\"type\":\"end_turn\",\"text\":\"End turn\"}]," +
				"\"opponent_reply\":[{\"index\":1,\"action_id\":\"attack:21:1\"," +
				"\"kind\":\"attack\",\"type\":\"attack\"," +
				"\"source_entity_id\":21,\"target_entity_id\":1,\"text\":\"Attack hero\"}]," +
				"\"opponent_response\":{\"actions\":[{\"index\":1," +
				"\"action_id\":\"attack:21:1\",\"kind\":\"attack\",\"type\":\"attack\"," +
				"\"source_entity_id\":21,\"target_entity_id\":1,\"text\":\"Attack hero\"}]," +
				"\"tactical_value\":-1000000}," +
				"\"counterplay\":{\"scope\":\"visible_generic_turnpair_v1\"," +
				"\"search_complete\":true,\"is_proven_lethal\":true," +
				"\"actions\":[{\"index\":1,\"action_id\":\"attack:21:1\"," +
				"\"kind\":\"attack\",\"type\":\"attack\"," +
				"\"source_entity_id\":21,\"target_entity_id\":1,\"text\":\"Attack hero\"}]}}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.AreEqual("counterplay_tactical_state_value", recommendation.ScoreKind);
			Assert.AreEqual(0.1, recommendation.WorstCaseScore.Value, 0.000001);
			Assert.AreEqual("visible_generic_turnpair_v1", recommendation.ResponseScope);
			Assert.AreEqual("minimax_best_response", recommendation.ResponseKind);
			Assert.IsTrue(recommendation.ResponseSearchComplete);
			Assert.IsTrue(recommendation.IsResponseVerified);
			Assert.IsTrue(recommendation.ResponseIsProvenLethal);
			Assert.AreEqual(-1000000, recommendation.MinimaxValue.Value, 0.000001);
			Assert.AreEqual(false, recommendation.IsSafeAfterResponse.Value);
			Assert.AreEqual(-1000000, recommendation.OpponentResponseTacticalValue.Value, 0.000001);
			Assert.AreEqual(7, recommendation.ResponseNodesExpanded);
			Assert.AreEqual(2, recommendation.ResponseSearchedDepth);
			Assert.AreEqual(1, recommendation.ResponseTranspositionHits);
			Assert.AreEqual(1, recommendation.OpponentReply.Count);
			Assert.AreEqual(21, recommendation.OpponentReply[0].SourceEntityId);
			Assert.AreEqual(-9.5, recommendation.ScoreComponents["raw_score"], 0.000001);
			StringAssert.Contains(AdvisorPanel.BuildMetrics(recommendation), "对手最坏可见回应后战术评分");
			StringAssert.Contains(AdvisorPanel.BuildMetrics(recommendation), "最差回应战术值 -1000000");
			Assert.AreEqual("对手最坏可见回应：", AdvisorPanel.BuildOpponentResponsePrefix(recommendation));
		}

		[TestMethod]
		public void WireProtocol_ParsesPortfolioCoverageAndRejectsPrematureCoOptimalClaim()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":3,\"legal_first_action_ids\":[" +
				"\"attack:1:2\",\"attack:3:4\",\"end_turn\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[\"end_turn\"]," +
				"\"root_action_coverage_complete\":false," +
				"\"portfolio_optimality_proven\":false}}}," +
				"\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}"
			);

			Assert.IsTrue(response.Coverage.HasRootActionCoverageContract);
			Assert.IsTrue(response.Coverage.RootActionCoverageContractValid);
			Assert.AreEqual(3, response.Coverage.LegalFirstActionCount);
			Assert.AreEqual(2, response.Coverage.GeneratedFirstActionCount);
			Assert.AreEqual(2, response.Coverage.ResponseVerifiedFirstActionCount);
			Assert.IsFalse(response.Coverage.RootActionCoverageComplete);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual(1, response.Coverage.MissingFirstActionIds.Count);
			Assert.AreEqual("best_found", response.Recommendations[0].AlternativeKind);
			Assert.AreEqual(0, response.Recommendations[0].VerifiedPortfolioRegret.Value, 0.000001);
			Assert.IsTrue(response.Recommendations[0].Risks.Exists(
				item => item.Contains("不能宣称共同最优")));
			StringAssert.Contains(
				AdvisorUserMessages.PortfolioCoverageSummary(response.Coverage),
				"已验证 2 / 3 种");
		}

		[TestMethod]
		public void WireProtocol_KeepsCoOptimalClaimAfterCompletePortfolioCoverage()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":2," +
				"\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":true}}}," +
				"\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}"
			);

			Assert.IsTrue(response.Coverage.RootActionCoverageComplete);
			Assert.IsTrue(response.Coverage.RootActionCoverageContractValid);
			Assert.IsTrue(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual("co_optimal", response.Recommendations[0].AlternativeKind);
			StringAssert.Contains(
				AdvisorPanel.BuildMetrics(response.Recommendations[0]),
				"完整搜索范围内共同最优");
			StringAssert.Contains(AdvisorPanel.BuildMetrics(response.Recommendations[0]), "与已验证最佳并列");
		}

		[TestMethod]
		public void WireProtocol_FailsClosedWhenPortfolioCompleteCountsContradictClaim()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":3,\"legal_first_action_ids\":[" +
				"\"attack:1:2\",\"attack:3:4\",\"end_turn\"]," +
				"\"generated_first_action_count\":3," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\",\"end_turn\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":true}}}," +
				"\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}"
			);

			Assert.IsTrue(response.Coverage.HasRootActionCoverageContract);
			Assert.IsFalse(response.Coverage.RootActionCoverageContractValid);
			Assert.IsFalse(response.Coverage.RootActionCoverageComplete);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual("backup", response.Recommendations[0].AlternativeKind);
			Assert.IsTrue(response.Recommendations[0].Risks.Exists(
				item => item.Contains("首步不在可信")));
		}

		[TestMethod]
		public void WireProtocol_RejectsNonCanonicalRootCoverageFieldTypes()
		{
			var validJson =
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":2," +
				"\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":true}}}," +
				"\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}";
			var mutations = new[]
			{
				new[] { "\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]", "\"legal_first_action_ids\":[1,\"attack:3:4\"]" },
				new[] { "\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]", "\"legal_first_action_ids\":[\"attack:3:4\",\"attack:1:2\"]" },
				new[] { "\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]", "\"generated_first_action_ids\":[\"attack:1:2\",\"attack:1:2\"]" },
				new[] { "\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]", "\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:5:6\"]" },
				new[] { "\"legal_first_action_count\":2", "\"legal_first_action_count\":\"2\"" },
				new[] { "\"legal_first_action_count\":2", "\"legal_first_action_count\":-1" },
				new[] { "\"generated_first_action_count\":2", "\"generated_first_action_count\":2.0" },
				new[] { "\"response_verified_first_action_count\":2", "\"response_verified_first_action_count\":true" },
				new[] { "\"missing_first_action_ids\":[]", "\"missing_first_action_ids\":\"[]\"" },
				new[] { "\"missing_first_action_ids\":[]", "\"missing_first_action_ids\":[\"attack:3:4\"]" },
				new[] { "\"root_action_coverage_complete\":true", "\"root_action_coverage_complete\":\"true\"" },
				new[] { "\"portfolio_optimality_proven\":true", "\"portfolio_optimality_proven\":\"true\"" }
			};

			foreach (var mutation in mutations)
			{
				var response = AdvisorWireProtocol.DeserializeSolveResponse(
					validJson.Replace(mutation[0], mutation[1]));
				Assert.IsTrue(response.Coverage.HasRootActionCoverageContract, mutation[1]);
				Assert.IsFalse(response.Coverage.RootActionCoverageContractValid, mutation[1]);
				Assert.IsFalse(response.Coverage.RootActionCoverageComplete, mutation[1]);
				Assert.IsFalse(response.Coverage.PortfolioOptimalityProven, mutation[1]);
				Assert.AreEqual("backup", response.Recommendations[0].AlternativeKind, mutation[1]);
			}
		}

		[TestMethod]
		public void WireProtocol_CompleteRootCoverageWithoutOptimalityProofUsesBoundedLabels()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":2," +
				"\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":false}}},\"recommendations\":[" +
				VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "," +
				VerifiedPortfolioRecommendationJson(
					2, 3, 4, 50, 50, "near_optimal", true, false) + "]}"
			);

			Assert.IsTrue(response.Coverage.RootActionCoverageContractValid);
			Assert.IsTrue(response.Coverage.RootActionCoverageComplete);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual("best_found", response.Recommendations[0].AlternativeKind);
			Assert.AreEqual("backup", response.Recommendations[1].AlternativeKind);
			Assert.IsFalse(AdvisorPanel.BuildMetrics(response.Recommendations[0]).Contains("共同最优"));
			Assert.IsFalse(AdvisorPanel.BuildMetrics(response.Recommendations[1]).Contains("近优"));
		}

		[TestMethod]
		public void WireProtocol_InvalidatesSelfConsistentBogusRootIdsAgainstReturnedFirstAction()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"bogus-roots\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("bogus:a", "bogus:b") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}"
			);

			Assert.AreEqual("attack:1:2", response.Recommendations[0].Actions[0].ActionId);
			Assert.IsFalse(response.Coverage.RootActionCoverageContractValid);
			Assert.IsFalse(response.Coverage.RootActionCoverageComplete);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual("backup", response.Recommendations[0].AlternativeKind);
			Assert.IsFalse(response.Recommendations[0].VerifiedPortfolioRegret.HasValue);
			Assert.IsTrue(response.Recommendations[0].Risks.Exists(
				item => item.Contains("首步不在可信")));
		}

		[TestMethod]
		public void WireProtocol_InferiorContinuationCannotClaimZeroPortfolioRegret()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"false-zero\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "," +
				VerifiedPortfolioRecommendationJson(
					2, 3, 4, 0, 0, "co_optimal", true, false) + "]}"
			);

			Assert.IsFalse(response.Coverage.RootActionCoverageContractValid);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.AreEqual("best_found", response.Recommendations[0].AlternativeKind);
			Assert.AreEqual("backup", response.Recommendations[1].AlternativeKind);
			Assert.IsFalse(response.Recommendations[1].VerifiedPortfolioRegret.HasValue);
			Assert.IsTrue(response.Recommendations[1].Risks.Exists(
				item => item.Contains("最坏回应战术值不一致")));
			Assert.IsFalse(AdvisorPanel.BuildMetrics(response.Recommendations[1]).Contains("并列"));
		}

		[TestMethod]
		public void WireProtocol_SafeVerifiedRouteRemovesKnownCounterlethalBackupAndReranks()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"safe-filter\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					2, 1, 2, 100, 0, "co_optimal", true, false) + "," +
				VerifiedPortfolioRecommendationJson(
					1, 3, 4, -1000000, 1000100, "backup", false, true) + "]}"
			);

			Assert.AreEqual(1, response.Recommendations.Count);
			Assert.AreEqual("attack:1:2", response.Recommendations[0].Actions[0].ActionId);
			Assert.AreEqual(1, response.Recommendations[0].Rank);
			Assert.IsTrue(response.Warnings.Exists(item => item.Contains("已隐藏 1 条确认会遭反杀")));
		}

		[TestMethod]
		public void WireProtocol_AllKnownCounterlethalRoutesKeepOnlyBestWithChineseWarning()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"all-dangerous\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, -100, 0, "co_optimal", false, true) + "," +
				VerifiedPortfolioRecommendationJson(
					2, 3, 4, -200, 100, "near_optimal", false, true) + "]}"
			);

			Assert.AreEqual(1, response.Recommendations.Count);
			Assert.AreEqual(1, response.Recommendations[0].Rank);
			Assert.IsTrue(response.Recommendations[0].ResponseIsProvenLethal);
			Assert.IsTrue(response.Warnings.Exists(item => item.Contains("仅保留排序最高的一条")));
		}

		[TestMethod]
		public void WireProtocol_ReportedPortfolioRegretsRequireFiniteZeroAnchor()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"missing-anchor\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 10, "near_optimal", true, false) + "," +
				VerifiedPortfolioRecommendationJson(
					2, 3, 4, 0, 110, "backup", true, false) + "]}"
			);

			Assert.IsFalse(response.Coverage.RootActionCoverageContractValid);
			Assert.IsTrue(response.Recommendations.All(item =>
				!item.VerifiedPortfolioRegret.HasValue && item.AlternativeKind == "backup"));
			Assert.IsTrue(response.Recommendations.All(item =>
				item.Risks.Exists(risk => risk.Contains("缺少零差值基准"))));
		}

		[TestMethod]
		public void WireProtocol_ProvenPortfolioCannotMixReportedAndMissingRegrets()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"mixed-regret\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "," +
				VerifiedPortfolioRecommendationJson(
					2, 3, 4, 0, null, "best_found", true, false) + "]}"
			);

			Assert.IsFalse(response.Coverage.RootActionCoverageContractValid);
			Assert.IsFalse(response.Coverage.PortfolioOptimalityProven);
			Assert.IsTrue(response.Recommendations.All(item => item.AlternativeKind == "backup"));
			Assert.IsTrue(response.Recommendations.All(item =>
				item.Risks.Exists(risk => risk.Contains("混用了有差值和无差值"))));
		}

		[TestMethod]
		public void WireProtocol_RejectsMalformedFriendlyPortfolioActionSequences()
		{
			var action = PortfolioActionJson(1, 2);
			var validRecommendation = VerifiedPortfolioRecommendationJson(
				1, 1, 2, 100, 0, "co_optimal", true, false);
			var validActions = "\"actions\":[" + action + "]";
			var endTurn1 = "{\"index\":1,\"action_id\":\"end_turn::\"," +
				"\"kind\":\"end_turn\",\"type\":\"end_turn\"," +
				"\"source_entity_id\":null,\"target_entity_id\":null}";
			var endTurn2 = endTurn1.Replace("\"index\":1", "\"index\":2");
			var endTurn3 = endTurn1.Replace("\"index\":1", "\"index\":3");
			var action2 = action.Replace("\"index\":1", "\"index\":2");
			var mutations = new[]
			{
				"\"actions\":[" + action + "," +
					"{\"index\":2,\"kind\":\"end_turn\",\"type\":\"end_turn\"}]",
				"\"actions\":[" + action.Replace("\"index\":1", "\"index\":2") + "]",
				"\"actions\":[" + endTurn1 + "," + action2 + "]",
				"\"actions\":[" + action + "," + endTurn2 + "," + endTurn3 + "]",
				"\"actions\":[{\"index\":1,\"action_id\":\"attack::\"," +
					"\"kind\":\"attack\",\"type\":\"attack\"," +
					"\"source_entity_id\":null,\"target_entity_id\":null}]"
			};

			foreach (var malformedActions in mutations)
			{
				var response = AdvisorWireProtocol.DeserializeSolveResponse(
					"{\"api_version\":\"1.0\",\"request_id\":\"bad-actions\"," +
					"\"state_id\":\"s1\",\"status\":\"ok\"," +
					CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
					",\"recommendations\":[" +
					validRecommendation.Replace(validActions, malformedActions) + "]}"
				);
				Assert.IsFalse(response.Coverage.RootActionCoverageContractValid, malformedActions);
				Assert.AreEqual("backup", response.Recommendations[0].AlternativeKind, malformedActions);
				Assert.IsFalse(response.Recommendations[0].VerifiedPortfolioRegret.HasValue, malformedActions);
			}
		}

		[TestMethod]
		public void WireProtocol_RejectsMutuallyMatchingButNonCanonicalOpponentActions()
		{
			var recommendation = VerifiedPortfolioRecommendationJson(
				1, 1, 2, 100, 0, "co_optimal", true, false);
			var invalidReply = "{\"index\":1,\"kind\":\"attack\",\"type\":\"attack\"," +
				"\"source_entity_id\":9,\"target_entity_id\":10}";
			recommendation = recommendation.Replace(
				"\"opponent_reply\":[],\"opponent_response\":{\"actions\":[]",
				"\"opponent_reply\":[" + invalidReply + "]," +
				"\"opponent_response\":{\"actions\":[" + invalidReply + "]");
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"bad-reply\"," +
				"\"state_id\":\"s1\",\"status\":\"ok\"," +
				CompletePortfolioCoverageJson("attack:1:2", "attack:3:4") +
				",\"recommendations\":[" + recommendation + "]}"
			);

			Assert.IsFalse(response.Recommendations[0].IsResponseVerified);
			Assert.AreEqual("fallback", response.Recommendations[0].AlternativeKind);
			Assert.IsFalse(response.Recommendations[0].VerifiedPortfolioRegret.HasValue);
			Assert.IsTrue(response.Recommendations[0].Risks.Exists(
				item => item.Contains("回应动作标识或顺序无效")));
		}

		[TestMethod]
		public void WireProtocol_FailsClosedWhenCoOptimalClaimHasNoRootCoverageContract()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[" +
				VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "]}"
			);

			Assert.IsFalse(response.Coverage.HasRootActionCoverageContract);
			Assert.AreEqual("backup", response.Recommendations[0].AlternativeKind);
			Assert.IsTrue(response.Recommendations[0].Risks.Exists(
				item => item.Contains("首步不在可信")));
		}

		[TestMethod]
		public void WireProtocol_RecomputesPortfolioKindAndHidesUnverifiedRegret()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":2," +
				"\"legal_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"generated_first_action_count\":2," +
				"\"generated_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":[\"attack:1:2\",\"attack:3:4\"]," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":true}}},\"recommendations\":[" +
				VerifiedPortfolioRecommendationJson(
					1, 1, 2, 100, 0, "co_optimal", true, false) + "," +
				"{\"rank\":2,\"expected_win_probability\":0.5," +
				"\"alternative_kind\":\"near_optimal\",\"verified_portfolio_regret\":50," +
				"\"actions\":[" + PortfolioActionJson(3, 4) + "]}]}"
			);

			Assert.AreEqual("co_optimal", response.Recommendations[0].AlternativeKind);
			Assert.AreEqual(0, response.Recommendations[0].VerifiedPortfolioRegret.Value, 0.000001);
			Assert.AreEqual("fallback", response.Recommendations[1].AlternativeKind);
			Assert.IsFalse(response.Recommendations[1].VerifiedPortfolioRegret.HasValue);
			Assert.IsFalse(AdvisorPanel.BuildMetrics(response.Recommendations[1]).Contains("战术值差距"));
		}

		[TestMethod]
		public void WireProtocol_DowngradesCounterlethalWithUnknownScope()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":0.2,\"response_is_proven_lethal\":true," +
				"\"response_scope\":\"untrusted_scope\",\"response_search_complete\":true," +
				"\"is_response_verified\":true,\"response_kind\":\"minimax_best_response\"," +
				"\"minimax_value\":-1000000,\"is_safe_after_response\":false," +
				"\"opponent_reply\":[],\"opponent_response\":{\"actions\":[]," +
				"\"tactical_value\":-1000000},\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsFalse(recommendation.ResponseIsProvenLethal);
			Assert.IsFalse(recommendation.IsResponseVerified);
			Assert.IsTrue(recommendation.ResponseSearchComplete);
			Assert.IsTrue(recommendation.Risks.Exists(
				item => item.Contains("回应契约不一致")));
			Assert.IsFalse(AdvisorPanel.BuildMetrics(recommendation).Contains("最坏可见回应后"));
			Assert.AreEqual(
				"对手已搜索回应（未验证）：",
				AdvisorPanel.BuildOpponentResponsePrefix(recommendation));
		}

		[TestMethod]
		public void WireProtocol_DowngradesMismatchedCanonicalAndLegacyOpponentResponses()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":0.4,\"score_kind\":\"counterplay_tactical_state_value\"," +
				"\"response_scope\":\"visible_generic_turnpair_v1\"," +
				"\"response_kind\":\"minimax_best_response\",\"is_response_verified\":true," +
				"\"response_search_complete\":true,\"response_is_proven_lethal\":false," +
				"\"minimax_value\":120,\"is_safe_after_response\":true," +
				"\"opponent_reply\":[{\"index\":1,\"type\":\"attack\"," +
				"\"source_entity_id\":22,\"target_entity_id\":2}]," +
				"\"opponent_response\":{\"actions\":[{\"index\":1,\"type\":\"attack\"," +
				"\"source_entity_id\":21,\"target_entity_id\":2}],\"tactical_value\":120}," +
				"\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsFalse(recommendation.IsResponseVerified);
			Assert.IsFalse(recommendation.ResponseIsProvenLethal);
			Assert.AreEqual(1, recommendation.OpponentReply.Count);
			Assert.AreEqual(21, recommendation.OpponentReply[0].SourceEntityId);
			Assert.IsTrue(recommendation.Risks.Exists(
				item => item.Contains("标准对手回应与兼容回应动作不一致")));
		}

		[TestMethod]
		public void WireProtocol_DowngradesContradictorySafetyField()
		{
			var response = AdvisorWireProtocol.DeserializeSolveResponse(
				"{\"api_version\":\"1.0\",\"request_id\":\"r1\",\"state_id\":\"s1\"," +
				"\"status\":\"ok\",\"recommendations\":[{\"rank\":1," +
				"\"expected_win_probability\":0.1,\"response_scope\":\"visible_generic_turnpair_v1\"," +
				"\"response_kind\":\"minimax_best_response\",\"is_response_verified\":true," +
				"\"response_search_complete\":true,\"response_is_proven_lethal\":true," +
				"\"minimax_value\":-1000000,\"is_safe_after_response\":true," +
				"\"opponent_reply\":[],\"opponent_response\":{\"actions\":[]," +
				"\"tactical_value\":-1000000},\"actions\":[]}]}"
			);

			var recommendation = response.Recommendations[0];
			Assert.IsFalse(recommendation.IsResponseVerified);
			Assert.IsFalse(recommendation.ResponseIsProvenLethal);
			Assert.IsTrue(recommendation.Risks.Exists(
				item => item.Contains("安全字段与反杀证明冲突")));
		}

		[TestMethod]
		public void HiddenOpponentEntity_IsScrubbedBeforeFingerprintAndWireSerialization()
		{
			var entity = new AdvisorEntityState
			{
				EntityId = 42,
				CardId = "SECRET_INTERNAL_CARD",
				DbfId = 123,
				Name = "Secret Internal Card",
				Zone = "HAND",
				ZoneId = 3,
				ZonePosition = 2,
				ControllerId = 2,
				CardType = "SPELL",
				CardTypeId = 5,
				Cost = 7,
				CardText = "Internal text",
				EnglishText = "Internal text",
				IsKnown = true,
				HasMegaWindfury = true,
				IsImmune = true,
				Visibility = "inferred",
				Tags = new Dictionary<string, int>
				{
					{ "ZONE", 3 }, { "ZONE_POSITION", 2 }, { "CONTROLLER", 2 },
					{ "COST", 7 }, { "ATK", 9 }, { "DBF_ID", 123 }
				}
			};

			AdvisorGameStateExtractor.ScrubHiddenOpponentEntity(entity);

			Assert.AreEqual(42, entity.EntityId);
			Assert.AreEqual("HAND", entity.Zone);
			Assert.AreEqual(2, entity.ZonePosition);
			Assert.AreEqual("", entity.CardId);
			Assert.AreEqual(0, entity.DbfId);
			Assert.AreEqual("", entity.Name);
			Assert.AreEqual("UNKNOWN", entity.CardType);
			Assert.AreEqual(0, entity.Cost);
			Assert.AreEqual("hidden", entity.Visibility);
			Assert.IsFalse(entity.IsKnown);
			Assert.IsFalse(entity.HasMegaWindfury);
			Assert.IsFalse(entity.IsImmune);
			Assert.AreEqual(3, entity.Tags.Count);
			Assert.IsFalse(entity.Tags.ContainsKey("COST"));
			Assert.IsFalse(entity.Tags.ContainsKey("DBF_ID"));

			var state = NewState("hidden-state");
			state.Opponent.Hand.Add(entity);
			var json = AdvisorWireProtocol.SerializeSolveRequest(new AdvisorSolveRequest
			{
				RequestId = "hidden-request",
				State = state
			});
			Assert.IsFalse(json.Contains("SECRET_INTERNAL_CARD"));
			Assert.IsFalse(json.Contains("Internal text"));
		}

		[TestMethod]
		public void AdvisorView_DelayedUpdatesCannotReactivateEndedSession()
		{
			var view = new AdvisorView(new PluginConfig(), false);
			view.OnGameStarted();
			view.OnStateChanged("live-state");
			Assert.AreEqual("live-state", view.CurrentStateId);

			view.OnGameEnded();
			view.OnThinking("late-state", "late callback");
			view.OnStateChanged("late-state");

			Assert.AreEqual("", view.CurrentStateId);
			view.OnUnload();
		}

		[TestMethod]
		public void AdvisorView_WorkerFailureInvalidatesQueuedStateResults()
		{
			var view = new AdvisorView(new PluginConfig(), false);
			view.OnGameStarted();
			view.OnStateChanged("live-state");

			view.OnWorkerUnavailable("live-state", "worker stopped");
			var invalidatedStateId = view.CurrentStateId;
			Assert.AreNotEqual("live-state", invalidatedStateId);
			Assert.IsTrue(invalidatedStateId.StartsWith(
				"live-state|worker-unavailable|", StringComparison.Ordinal));

			view.OnRecommendations(new AdvisorSolveResponse { StateId = "live-state" });
			Assert.AreEqual(invalidatedStateId, view.CurrentStateId);
			view.OnUnload();
		}

		[TestMethod]
		public void AdvisorView_CompatibilityStartupFailureUpdatesExistingFallbackStatus()
		{
			var view = new AdvisorView(new PluginConfig(), false);
			view.OnGameStarted();
			view.OnStateChanged("fallback-state");

			view.OnWorkerUnavailable("fallback-state", "正在切换兼容求解器");
			var switchingStateId = view.CurrentStateId;
			view.OnWorkerUnavailable("fallback-state", "兼容求解器启动失败");
			var failedStateId = view.CurrentStateId;

			Assert.AreNotEqual(switchingStateId, failedStateId);
			Assert.IsTrue(failedStateId.StartsWith(
				"fallback-state|worker-unavailable|",
				StringComparison.Ordinal));
			view.OnUnload();
		}

		[TestMethod]
		public void RecommendationController_DiscardsMismatchedStateResponse()
		{
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				new FakeAdvisorClient("different-state"), TimeSpan.Zero))
			{
				AdvisorRecommendationUpdateKind? observed = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.Stale)
					{
						observed = args.Kind;
						signal.Set();
					}
				};
				controller.SubmitSnapshot(NewState("state-1"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected stale response event.");
				Assert.AreEqual(AdvisorRecommendationUpdateKind.Stale, observed);
			}
		}

		[TestMethod]
		public void RecommendationController_MissingWorkerDegradesWithoutThrowing()
		{
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(null, TimeSpan.Zero))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.WorkerUnavailable)
						signal.Set();
				};
				Assert.IsTrue(controller.SubmitSnapshot(NewState("state-1"), force: true));
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected worker unavailable event.");
			}
		}

		[TestMethod]
		public void RecommendationController_TwoStageSearchKeepsInitialThenFinalResult()
		{
			var client = new FakeAdvisorClient("state-1");
			var finalSignal = new ManualResetEventSlim();
			try
			{
				var finalFlags = new System.Collections.Generic.List<bool>();
				using (var controller = new AdvisorRecommendationController(
					client,
					TimeSpan.Zero,
					new AdvisorSolveOptions
					{
						InitialBudgetMilliseconds = 25,
						TimeBudgetMilliseconds = 100
					},
					new ImmediateSynchronizationContext()))
				{
					controller.Updated += (sender, args) =>
					{
						if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations)
							return;
						finalFlags.Add(args.Response.IsFinal);
						if (args.Response.IsFinal)
							finalSignal.Set();
					};
					var state = NewState("state-1");
					state.SnapshotSequence = 41;
					var hdtRoots = NewHdtRootCandidates(state.StateId);
					controller.SubmitSnapshot(
						state, force: true, hdtRootCandidates: hdtRoots);
					Assert.IsTrue(finalSignal.Wait(TimeSpan.FromSeconds(3)), "Expected final stage.");
					CollectionAssert.AreEqual(new[] { false, true }, finalFlags);
					Assert.AreEqual(2, client.SolveCount);
					var requests = client.Requests;
					Assert.AreEqual(2, requests.Count);
					AssertTrajectoryMetadata(requests[0], "state-1", "initial", "41");
					AssertTrajectoryMetadata(requests[1], "state-1", "final", "41");
					Assert.AreSame(hdtRoots, requests[0].HdtRootCandidates);
					Assert.AreSame(hdtRoots, requests[1].HdtRootCandidates,
						"Both solve stages must use the identical state-bound HDT root portfolio.");
					Assert.AreEqual(25, requests[0].Options.TimeBudgetMilliseconds);
					Assert.AreEqual(
						AdvisorSolveOptions.DefaultInitialMaxIterations,
						requests[0].Options.MaxIterations);
					Assert.AreEqual(
						AdvisorSolveOptions.DefaultInitialMaxDepth,
						requests[0].Options.MaxDepth);
					Assert.IsTrue(requests[1].Options.TimeBudgetMilliseconds >= 25);
					Assert.IsTrue(requests[1].Options.TimeBudgetMilliseconds <= 100);
					Assert.AreEqual(
						AdvisorSolveOptions.DefaultMaxIterations,
						requests[1].Options.MaxIterations);
					Assert.AreEqual(
						AdvisorSolveOptions.DefaultMaxDepth,
						requests[1].Options.MaxDepth);
					Assert.AreEqual(
						requests[0].Metadata["decision_id"],
						requests[1].Metadata["decision_id"],
						"Initial and final solves must share one trajectory decision ID.");
				}
			}
			finally
			{
				finalSignal.Dispose();
			}
		}

		[TestMethod]
		public void RecommendationController_SingleStageSearchMarksTrajectoryStageSingle()
		{
			var client = new FakeAdvisorClient("state-1");
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 50,
					TimeBudgetMilliseconds = 50
				},
				new ImmediateSynchronizationContext()))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.Recommendations)
						signal.Set();
				};
				var state = NewState("state-1");
				state.SnapshotSequence = 73;
				controller.SubmitSnapshot(state, force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)), "Expected single-stage result.");
				Assert.AreEqual(1, client.SolveCount);
				var requests = client.Requests;
				Assert.AreEqual(1, requests.Count);
				AssertTrajectoryMetadata(requests[0], "state-1", "single", "73");
				Assert.AreEqual(
					AdvisorSolveOptions.DefaultMaxIterations,
					requests[0].Options.MaxIterations);
				Assert.AreEqual(
					AdvisorSolveOptions.DefaultMaxDepth,
					requests[0].Options.MaxDepth);
			}
		}

		[TestMethod]
		public void RecommendationController_CompleteInitialProofSkipsRedundantFinalSolve()
		{
			var client = new ScriptedAdvisorClient((call, request) =>
				new AdvisorSolveResponse
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = request.RequestId,
					StateId = request.State.StateId,
					Status = AdvisorProtocol.StatusOk,
					Coverage = new AdvisorCoverage
					{
						HasRootActionCoverageContract = true,
						RootActionCoverageContractValid = true,
						RootActionCoverageComplete = true,
						PortfolioOptimalityProven = true
					}
				});
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 100
				},
				new ImmediateSynchronizationContext()))
			{
				AdvisorSolveResponse observed = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations)
						return;
					observed = args.Response;
					signal.Set();
				};
				controller.SubmitSnapshot(NewState("complete-initial"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)));
				Thread.Sleep(50);
				Assert.AreEqual(1, client.SolveCount);
				Assert.IsNotNull(observed);
				Assert.IsTrue(observed.IsFinal);
			}
		}

		[TestMethod]
		public void RecommendationController_ProvenInitialLethalSkipsRedundantFinalSolve()
		{
			var client = new ScriptedAdvisorClient((call, request) =>
				new AdvisorSolveResponse
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = request.RequestId,
					StateId = request.State.StateId,
					Status = AdvisorProtocol.StatusOk,
					Recommendations = new List<AdvisorRecommendation>
					{
						new AdvisorRecommendation { Rank = 1, IsProvenLethal = true }
					}
				});
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 100
				},
				new ImmediateSynchronizationContext()))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.Recommendations)
						signal.Set();
				};
				controller.SubmitSnapshot(NewState("lethal-initial"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)));
				Thread.Sleep(50);
				Assert.AreEqual(1, client.SolveCount);
			}
		}

		[TestMethod]
		public void RecommendationController_FinalFailurePreservesInitialPartialAsFinal()
		{
			var finalError = new TimeoutException("final refinement timed out");
			var client = new ScriptedAdvisorClient((call, request) =>
			{
				if (call == 2)
					throw finalError;
				return new AdvisorSolveResponse
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = request.RequestId,
					StateId = request.State.StateId,
					Status = AdvisorProtocol.StatusPartial,
					Recommendations = new List<AdvisorRecommendation>
					{
						new AdvisorRecommendation { Rank = 1, LineId = "initial-line" }
					}
				};
			});
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 100
				},
				new ImmediateSynchronizationContext()))
			{
				var recommendationUpdates = new List<AdvisorRecommendationUpdateEventArgs>();
				var unavailableCount = 0;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.WorkerUnavailable)
						unavailableCount++;
					if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations)
						return;
					recommendationUpdates.Add(args);
					if (args.Response != null && args.Response.IsFinal)
						signal.Set();
				};
				controller.SubmitSnapshot(NewState("final-failure"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)));
				Assert.AreEqual(2, client.SolveCount);
				Assert.AreEqual(0, unavailableCount);
				Assert.AreEqual(2, recommendationUpdates.Count);
				Assert.IsFalse(recommendationUpdates[0].Response.IsFinal);
				var preserved = recommendationUpdates[1];
				Assert.IsTrue(preserved.Response.IsFinal);
				Assert.AreEqual(AdvisorProtocol.StatusPartial, preserved.Response.Status);
				Assert.AreSame(finalError, preserved.Error);
				Assert.AreEqual("initial-line", preserved.Response.Recommendations[0].LineId);
				StringAssert.Contains(preserved.Response.Message, "已保留首批建议");
				Assert.IsTrue(preserved.Response.Warnings.Any(
					warning => warning.Contains("首批近似结果")));
			}
		}

		[TestMethod]
		public void RecommendationController_DoesNotRefineUnsupportedInitialResult()
		{
			var client = new FakeAdvisorClient("state-1", AdvisorProtocol.StatusUnsupported);
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 100
				}))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.Recommendations)
						signal.Set();
				};
				controller.SubmitSnapshot(NewState("state-1"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)));
				Thread.Sleep(100);
				Assert.AreEqual(1, client.SolveCount);
			}
		}

		[TestMethod]
		public void RecommendationController_ForceRetriesUnchangedStateAfterWorkerRecovery()
		{
			using (var unavailable = new ManualResetEventSlim())
			using (var recommendation = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(null, TimeSpan.Zero))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind == AdvisorRecommendationUpdateKind.WorkerUnavailable)
						unavailable.Set();
					if (args.Kind == AdvisorRecommendationUpdateKind.Recommendations)
						recommendation.Set();
				};
				var state = NewState("same-state");
				Assert.IsTrue(controller.SubmitSnapshot(state));
				Assert.IsTrue(unavailable.Wait(TimeSpan.FromSeconds(3)));

				controller.SetClient(new FakeAdvisorClient("same-state"));
				Assert.IsFalse(controller.SubmitSnapshot(state));
				Assert.IsTrue(controller.SubmitSnapshot(state, force: true));
				Assert.IsTrue(recommendation.Wait(TimeSpan.FromSeconds(3)));
			}
		}

		[TestMethod]
		public void RecommendationController_CancelAllowsSameStateRetryWithoutForce()
		{
			var client = new FakeAdvisorClient("same-state");
			using (var first = new ManualResetEventSlim())
			using (var second = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 25
				},
				new ImmediateSynchronizationContext()))
			{
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations)
						return;
					if (client.SolveCount == 1)
						first.Set();
					else if (client.SolveCount == 2)
						second.Set();
				};

				var state = NewState("same-state");
				Assert.IsTrue(controller.SubmitSnapshot(state));
				Assert.IsTrue(first.Wait(TimeSpan.FromSeconds(3)));

				controller.CancelCurrent("duplicate HDT event");
				Assert.IsTrue(controller.SubmitSnapshot(state),
					"Cancellation must not permanently suppress an unchanged public state.");
				Assert.IsTrue(second.Wait(TimeSpan.FromSeconds(3)));
				Assert.AreEqual(2, client.SolveCount);
			}
		}

		[TestMethod]
		public void RecommendationController_SameStateKeepsSearchAndNewStateCancelsOnlyOnce()
		{
			using (var client = new BlockingCancellationClient())
			using (var controller = new AdvisorRecommendationController(
				client,
				TimeSpan.Zero,
				new AdvisorSolveOptions
				{
					InitialBudgetMilliseconds = 25,
					TimeBudgetMilliseconds = 25
				},
				new ImmediateSynchronizationContext()))
			{
				Assert.IsTrue(controller.SubmitSnapshot(NewState("state-a")));
				Assert.IsTrue(client.FirstStarted.Wait(TimeSpan.FromSeconds(3)));
				Assert.IsTrue(controller.HasPendingOrActiveForState("state-a"));

				Assert.IsFalse(controller.SubmitSnapshot(NewState("state-a")),
					"A duplicate HDT event with the same detached state must keep the search.");
				Thread.Sleep(50);
				Assert.AreEqual(0, client.CancelCount);
				Assert.IsTrue(controller.HasPendingOrActiveForState("state-a"));

				Assert.IsTrue(controller.SubmitSnapshot(NewState("state-b")));
				Assert.IsTrue(client.SecondStarted.Wait(TimeSpan.FromSeconds(3)));
				Assert.IsTrue(SpinWait.SpinUntil(
					() => client.CancelCount == 1,
					TimeSpan.FromSeconds(3)));
				Assert.AreEqual(1, client.CancelCount);
				Assert.IsFalse(controller.SubmitSnapshot(NewState("state-b")));
				Thread.Sleep(50);
				Assert.AreEqual(1, client.CancelCount,
					"Repeated captures of the new state must not cancel it again.");
			}
		}

		[TestMethod]
		public void RecommendationController_NonActionableCancellationIsAtomicAndIdempotent()
		{
			using (var client = new BlockingCancellationClient())
			using (var controller = new AdvisorRecommendationController(
				client, TimeSpan.Zero, null, new ImmediateSynchronizationContext()))
			{
				Assert.IsTrue(controller.SubmitSnapshot(NewState("actionable")));
				Assert.IsTrue(client.FirstStarted.Wait(TimeSpan.FromSeconds(3)));
				Assert.IsFalse(controller.CancelCurrentIfPendingOrActive(
					"different-state", "captured non-actionable state"));
				Assert.IsTrue(controller.CancelCurrentIfPendingOrActive(
					"actionable", "captured non-actionable state"));
				Assert.IsFalse(controller.CancelCurrentIfPendingOrActive(
					"actionable", "duplicate non-actionable capture"));
				Assert.IsTrue(SpinWait.SpinUntil(
					() => client.CancelCount == 1,
					TimeSpan.FromSeconds(3)));
				Assert.AreEqual(1, client.CancelCount);
				Assert.IsFalse(controller.HasPendingOrActiveForState("actionable"));
			}
		}

		[TestMethod]
		public void RecommendationController_Http422BecomesUnsupportedWithoutCompatibilityFallback()
		{
			var unsupported = new AdvisorWorkerException("unsupported state")
			{
				StatusCode = (System.Net.HttpStatusCode)422
			};
			using (var signal = new ManualResetEventSlim())
			using (var controller = new AdvisorRecommendationController(
				new FailingAdvisorClient(unsupported),
				TimeSpan.Zero,
				null,
				new ImmediateSynchronizationContext()))
			{
				AdvisorRecommendationUpdateEventArgs failure = null;
				controller.Updated += (sender, args) =>
				{
					if (args.Kind != AdvisorRecommendationUpdateKind.Recommendations ||
						args.Error == null)
						return;
					failure = args;
					signal.Set();
				};

				controller.SubmitSnapshot(NewState("state-422"), force: true);
				Assert.IsTrue(signal.Wait(TimeSpan.FromSeconds(3)));
				Assert.AreSame(unsupported, failure.Error);
				Assert.IsNotNull(failure.Response);
				Assert.AreEqual(AdvisorProtocol.StatusUnsupported, failure.Response.Status);
				Assert.AreEqual(AdvisorUserMessages.Unsupported, failure.Message);
				Assert.IsFalse(MetaCompanionPlugin.ShouldFallbackAdvisorSolveFailure(
					failure,
					true,
					AdvisorWorkerBackendMode.Auto,
					AdvisorWorkerBackendKind.Rust));
			}
		}

		[TestMethod]
		public void RecommendationController_ObservationsAreSentInInvocationOrder()
		{
			using (var client = new OrderedObservationClient())
			using (var controller = new AdvisorRecommendationController(client, TimeSpan.Zero))
			{
				var first = controller.ObserveAsync(new AdvisorObservation
				{
					Kind = "action",
					StateId = "state-first",
					Action = new AdvisorObservedAction { Kind = "end_turn" }
				});
				Assert.IsTrue(client.FirstStarted.Wait(TimeSpan.FromSeconds(2)));

				var second = controller.ObserveAsync(new AdvisorObservation
				{
					Kind = "result",
					StateId = "state-second",
					Result = "win"
				});
				Assert.IsFalse(client.SecondStarted.Wait(TimeSpan.FromMilliseconds(100)),
					"The second observation must not overtake the first pending write.");

				client.CompleteFirst();
				Assert.IsTrue(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(3)));
				CollectionAssert.AreEqual(
					new[] { "state-first", "state-second" },
					client.ObservedStateIds.ToArray());
			}
		}

		[TestMethod]
		public void WorkerClient_SolveTimeoutCancelsOnlyTheExactRequestId()
		{
			const string token = "worker-client-test-token-0123456789";
			const string requestId = "timed-out-request";
			using (var server = new LoopbackAdvisorWorkerServer())
			{
				var client = new AdvisorWorkerClient(
					server.BaseUri,
					token,
					new AdvisorWorkerClientOptions
					{
						SolveTimeout = TimeSpan.FromMilliseconds(300),
						CancelTimeout = TimeSpan.FromSeconds(2)
					});
				var request = new AdvisorSolveRequest
				{
					RequestId = requestId,
					State = NewState("shared-state")
				};

				TimeoutException timeout = null;
				try
				{
					client.SolveAsync(request, CancellationToken.None)
						.GetAwaiter().GetResult();
					Assert.Fail("Expected the blocked solve to time out.");
				}
				catch (TimeoutException ex)
				{
					timeout = ex;
				}

				Assert.IsNotNull(timeout);
				Assert.IsTrue(server.WaitForRequest(
					"/v1/cancel", requestId, TimeSpan.FromSeconds(3)),
					"The client did not notify the worker about the timed-out request.");
				var cancelBody = server.GetRequestBody("/v1/cancel", requestId);
				AssertExactCancellation(cancelBody, requestId);
				Assert.IsFalse(cancelBody.Contains(token),
					"The session token must remain in HTTP headers and never enter cancel JSON.");

				var visible = AdvisorUserMessages.Failure(timeout, AdvisorUserMessages.SolveFailed);
				Assert.AreEqual(AdvisorUserMessages.SolveTimedOut, visible);
				Assert.IsFalse(visible.Contains(token));
				Assert.IsFalse(visible.Contains("Advisor worker"),
					"Ordinary UI text must not expose the English transport exception.");
			}
		}

		[TestMethod]
		public void WorkerClient_CallerCancellationDoesNotCancelNewSameStateRequest()
		{
			const string token = "worker-client-test-token-0123456789";
			const string oldRequestId = "old-request";
			const string newRequestId = "new-request";
			using (var server = new LoopbackAdvisorWorkerServer
				{
					SuccessfulSolveRequestId = newRequestId,
					SuccessfulSolveDelay = TimeSpan.FromMilliseconds(400)
				})
			using (var cancellation = new CancellationTokenSource())
			{
				var client = new AdvisorWorkerClient(
					server.BaseUri,
					token,
					new AdvisorWorkerClientOptions
					{
						SolveTimeout = TimeSpan.FromSeconds(5),
						CancelTimeout = TimeSpan.FromSeconds(2)
					});
				var oldSolve = client.SolveAsync(new AdvisorSolveRequest
				{
					RequestId = oldRequestId,
					State = NewState("shared-state")
				}, cancellation.Token);
				Assert.IsTrue(server.WaitForRequest(
					"/v1/solve", oldRequestId, TimeSpan.FromSeconds(3)));

				var newSolve = client.SolveAsync(new AdvisorSolveRequest
				{
					RequestId = newRequestId,
					State = NewState("shared-state")
				}, CancellationToken.None);
				Assert.IsTrue(server.WaitForRequest(
					"/v1/solve", newRequestId, TimeSpan.FromSeconds(3)));
				cancellation.Cancel();

				try
				{
					oldSolve.GetAwaiter().GetResult();
					Assert.Fail("Expected the old solve to observe caller cancellation.");
				}
				catch (OperationCanceledException)
				{
				}

				Assert.IsTrue(server.WaitForRequest(
					"/v1/cancel", oldRequestId, TimeSpan.FromSeconds(3)));
				var cancelBody = server.GetRequestBody("/v1/cancel", oldRequestId);
				AssertExactCancellation(cancelBody, oldRequestId);
				Assert.IsFalse(cancelBody.Contains(newRequestId));

				var response = newSolve.GetAwaiter().GetResult();
				Assert.AreEqual(newRequestId, response.RequestId);
				Assert.AreEqual("shared-state", response.StateId);
			}
		}

		private static void AssertExactCancellation(string body, string requestId)
		{
			var root = AdvisorWireProtocol.ParseObject(body);
			Assert.AreEqual(2, root.Count,
				"Cancel JSON must contain only api_version and the exact request_id.");
			Assert.AreEqual(AdvisorProtocol.ApiVersion, root["api_version"]);
			Assert.AreEqual(requestId, root["request_id"]);
			Assert.IsFalse(root.ContainsKey("state_id"),
				"State-wide cancellation could terminate a newer request for the same state.");
		}

		private static AdvisorSolveRequest BehaviorReferenceRequest()
		{
			const string stateId = "behavior-reference-state";
			return new AdvisorSolveRequest
			{
				ApiVersion = AdvisorProtocol.ApiVersion,
				RequestId = "behavior-reference-request",
				State = NewState(stateId),
				Options = new AdvisorSolveOptions { MaxRecommendations = 3 },
				HdtRootCandidates = new AdvisorHdtRootCandidateSet
				{
					Contract = AdvisorHdtRootCandidateSet.ContractId,
					StateId = stateId,
					FrameId = 11,
					CollectorEpoch = 3,
					FrameWatermark = 21,
					CandidateSetComplete = true,
					Candidates = new List<AdvisorHdtRootCandidate>
					{
						new AdvisorHdtRootCandidate
						{
							OptionId = 0,
							Action = new AdvisorHdtRootAction { Kind = "end_turn" }
						},
						new AdvisorHdtRootCandidate
						{
							OptionId = 1,
							TargetEvidence = "hdt_no_legal_target",
							PositionEvidence = "not_applicable",
							Action = new AdvisorHdtRootAction
							{
								Kind = "location_activate",
								SourceEntityId = 55,
								CardId = "PUBLIC_LOCATION"
							}
						}
					}
				}
			};
		}

		private static string ValidBehaviorReferenceJson()
		{
			var hash = new string('b', 64);
			var decisionRanker =
				"\"decision_ranker\":{\"status\":\"available_not_applicable\"," +
				"\"artifact_sha256\":\"" + hash + "\"," +
				"\"ordering_attempt_count\":0,\"ordering_applied\":false," +
				"\"local_actions_only\":true,\"search_ordering_only\":true," +
				"\"candidate_generation_allowed\":false," +
				"\"score_override_allowed\":false,\"live_policy_eligible\":false," +
				"\"rl_training_eligible\":false,\"optimality_verified\":false}";
			var locationAction =
				"{\"index\":1,\"action_id\":\"location_activate:55:\"," +
				"\"kind\":\"location_activate\",\"type\":\"location_activate\"," +
				"\"source_entity_id\":\"55\",\"target_entity_id\":\"\"," +
				"\"card_id\":\"PUBLIC_LOCATION\",\"text\":\"Activate PUBLIC_LOCATION\"}";
			var endTurnAction =
				"{\"index\":1,\"action_id\":\"end_turn::\"," +
				"\"kind\":\"end_turn\",\"type\":\"end_turn\"," +
				"\"source_entity_id\":\"\",\"target_entity_id\":\"\"," +
				"\"card_id\":\"\",\"text\":\"End turn\"}";
			return "{\"api_version\":\"1.0\",\"request_id\":\"behavior-reference-request\"," +
				"\"state_id\":\"behavior-reference-state\",\"status\":\"partial\"," +
				"\"is_final\":true,\"coverage\":{" + decisionRanker + "}," +
				"\"recommendations\":[],\"behavior_references\":{" +
				"\"contract\":\"hdt_complete_candidate_behavior_reference_v1\"," +
				"\"status\":\"available\",\"available\":true,\"reason\":\"\"," +
				"\"source\":\"local_observed_behavior_cloning_v1\"," +
				"\"artifact_sha256\":\"" + hash + "\",\"candidate_set_contract\":" +
				"\"hdt_complete_main_action_options_v1\",\"candidate_set_complete\":true," +
				"\"candidate_count\":2,\"ranked_candidate_count\":2," +
				"\"displayed_reference_count\":2,\"references\":[{" +
				"\"rank\":1,\"legal_action_id\":\"location_activate:55:\"," +
				"\"action\":" + locationAction + ",\"observed_choice_probability\":0.75," +
				"\"probability_calibrated_as_win_rate\":false," +
				"\"optimality_verified\":false},{\"rank\":2," +
				"\"legal_action_id\":\"end_turn\",\"action\":" + endTurnAction + "," +
				"\"observed_choice_probability\":0.25," +
				"\"probability_calibrated_as_win_rate\":false," +
				"\"optimality_verified\":false}]," +
				"\"behavior_reference_eligible\":true," +
				"\"candidate_generation_allowed\":false," +
				"\"tactical_score_override_allowed\":false," +
				"\"automatic_action_allowed\":false,\"live_policy_eligible\":false," +
				"\"rl_training_eligible\":false,\"optimality_verified\":false," +
				"\"outcome_used_as_action_optimality\":false}}";
		}

		private static string PortfolioActionJson(int sourceEntityId, int targetEntityId)
		{
			return "{\"index\":1,\"action_id\":\"attack:" + sourceEntityId + ":" +
				targetEntityId + "\",\"kind\":\"attack\",\"type\":\"attack\"," +
				"\"source_entity_id\":" + sourceEntityId + ",\"target_entity_id\":" +
				targetEntityId + "}";
		}

		private static string VisiblePartialRecommendationJson(
			int rank, int sourceEntityId, int targetEntityId)
		{
			return "{\"rank\":" + rank + ",\"line_id\":\"visible-" + rank +
				"\",\"expected_win_probability\":0.5," +
				"\"score_kind\":\"visible_response_heuristic_v1\"," +
				"\"summary\":\"Hidden-information heuristic candidate.\"," +
				"\"risks\":[\"Unknown hidden cards may change this line.\"]," +
				"\"is_proven_lethal\":false,\"proof_kind\":\"\",\"proof_scope\":\"\"," +
				"\"response_search_complete\":false,\"is_response_verified\":false," +
				"\"response_is_proven_lethal\":false,\"minimax_value\":null," +
				"\"verified_portfolio_regret\":null,\"alternative_kind\":\"fallback\"," +
				"\"is_safe_after_response\":null,\"opponent_response\":null," +
				"\"actions\":[" + PortfolioActionJson(sourceEntityId, targetEntityId) + "]}";
		}

		private static string CompletePortfolioCoverageJson(
			string firstActionId,
			string secondActionId,
			bool optimalityProven = true)
		{
			var ids = "[\"" + firstActionId + "\",\"" + secondActionId + "\"]";
			return "\"coverage\":{\"details\":{\"counterplay\":{" +
				"\"legal_first_action_count\":2,\"legal_first_action_ids\":" + ids + "," +
				"\"generated_first_action_count\":2,\"generated_first_action_ids\":" + ids + "," +
				"\"response_verified_first_action_count\":2," +
				"\"response_verified_first_action_ids\":" + ids + "," +
				"\"missing_first_action_ids\":[],\"root_action_coverage_complete\":true," +
				"\"portfolio_optimality_proven\":" +
				(optimalityProven ? "true" : "false") + "}}}";
		}

		private static string VerifiedPortfolioRecommendationJson(
			int rank,
			int sourceEntityId,
			int targetEntityId,
			double minimaxValue,
			double? regret,
			string alternativeKind,
			bool safeAfterResponse,
			bool responseIsProvenLethal,
			bool immediateLethal = false)
		{
			var minimax = minimaxValue.ToString("R", CultureInfo.InvariantCulture);
			var regretValue = regret.HasValue
				? regret.Value.ToString("R", CultureInfo.InvariantCulture)
				: "null";
			var safe = safeAfterResponse ? "true" : "false";
			var responseLethal = responseIsProvenLethal ? "true" : "false";
			var proven = immediateLethal ? "true" : "false";
			var proofKind = immediateLethal ? "modeled_lethal" : "";
			var proofScope = immediateLethal ? "visible_generic_v2" : "";
			return "{\"rank\":" + rank + ",\"expected_win_probability\":1," +
				"\"is_proven_lethal\":" + proven + ",\"proof_kind\":\"" + proofKind +
				"\",\"proof_scope\":\"" + proofScope + "\",\"response_scope\":" +
				"\"visible_generic_turnpair_v1\",\"is_response_verified\":true," +
				"\"response_kind\":\"minimax_best_response\"," +
				"\"response_search_complete\":true,\"response_is_proven_lethal\":" +
				responseLethal + ",\"is_safe_after_response\":" + safe +
				",\"minimax_value\":" + minimax + ",\"verified_portfolio_regret\":" +
				regretValue + ",\"alternative_kind\":\"" + alternativeKind +
				"\",\"score_components\":{\"minimax_value\":" + minimax +
				"},\"actions\":[" + PortfolioActionJson(sourceEntityId, targetEntityId) +
				"],\"opponent_reply\":[],\"opponent_response\":{\"actions\":[]," +
				"\"tactical_value\":" + minimax + "}}";
		}

		private static AdvisorGameState NewState(string stateId)
		{
			return new AdvisorGameState
			{
				StateId = stateId,
				StateHash = "hash",
				CapturedAtUtc = DateTime.UtcNow,
				TurnNumber = 3,
				ActivePlayer = "player",
				IsLocalPlayerTurn = true,
				IsRunning = true,
				IsMulliganDone = true,
				Format = "Standard",
				GameMode = "Ranked",
				EnvironmentVersion = "36.2.0",
				Phase = new AdvisorGamePhaseState { CanLocalPlayerAct = true },
				Player = new AdvisorPlayerState
				{
					PlayerId = 1,
					IsLocalPlayer = true,
					Resources = new AdvisorResourceState { Total = 3, Available = 3 }
				},
				Opponent = new AdvisorPlayerState { PlayerId = 2 }
			};
		}

		private static AdvisorGameState NewTransitionState(
			string stateId, char hashCharacter, long snapshotSequence)
		{
			var state = NewState(stateId);
			state.StateHash = new string(hashCharacter, 64);
			state.SnapshotSequence = snapshotSequence;
			state.GameId = "g1-test";
			state.HdtVersion = "1.54.0.7376";
			state.EnvironmentVersion = "36.2.0";
			return state;
		}

		private static AdvisorHdtRootCandidateSet NewHdtRootCandidates(string stateId)
		{
			return new AdvisorHdtRootCandidateSet
			{
				Contract = AdvisorHdtRootCandidateSet.ContractId,
				StateId = stateId,
				FrameId = 42,
				CollectorEpoch = 7,
				FrameWatermark = 10,
				CandidateSetComplete = true,
				Candidates = new List<AdvisorHdtRootCandidate>
				{
					new AdvisorHdtRootCandidate
					{
						OptionId = 0,
						Action = new AdvisorHdtRootAction { Kind = "end_turn" },
						TargetEvidence = "not_applicable",
						PositionEvidence = "not_applicable"
					}
				}
			};
		}

		private static AdvisorPendingAction NewPendingAction(
			AdvisorGameState preState, string kind, long actionEventSequence)
		{
			return new AdvisorPendingAction
			{
				PreState = preState,
				Kind = kind,
				ObservedAtUtc = DateTime.UtcNow,
				GameGeneration = 7,
				ActionEventSequence = actionEventSequence,
				SourceEntityResolution = "missing",
				TargetEntityResolution = "missing"
			};
		}

		private static void AssertTrajectoryMetadata(
			AdvisorSolveRequest request,
			string expectedDecisionId,
			string expectedStage,
			string expectedSnapshotSequence)
		{
			Assert.IsNotNull(request);
			Assert.IsNotNull(request.State);
			Assert.IsNotNull(request.Metadata);
			Assert.AreEqual(request.State.StateId, request.Metadata["decision_id"]);
			Assert.AreEqual(expectedDecisionId, request.Metadata["decision_id"]);
			Assert.AreEqual(expectedStage, request.Metadata["solve_stage"]);
			Assert.AreEqual(
				"trajectory-readiness-v1",
				request.Metadata["trajectory_schema"]);
			Assert.AreEqual(
				"hdt-public-snapshot-v1",
				request.Metadata["capture_contract"]);
			Assert.AreEqual(
				expectedSnapshotSequence,
				request.Metadata["snapshot_sequence"]);
		}

		private sealed class LoopbackAdvisorWorkerServer : IDisposable
		{
			private sealed class RecordedRequest
			{
				public string Path { get; set; }
				public string RequestId { get; set; }
				public string StateId { get; set; }
				public string Body { get; set; }
			}

			private readonly TcpListener _listener;
			private readonly Thread _acceptThread;
			private readonly object _sync = new object();
			private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
			private readonly Dictionary<string, ManualResetEventSlim> _blockedSolves =
				new Dictionary<string, ManualResetEventSlim>(StringComparer.Ordinal);
			private volatile bool _disposed;
			private string _successfulSolveRequestId = "";

			public LoopbackAdvisorWorkerServer()
			{
				_listener = new TcpListener(IPAddress.Loopback, 0);
				_listener.Start();
				var endpoint = (IPEndPoint)_listener.LocalEndpoint;
				BaseUri = new Uri("http://127.0.0.1:" +
					endpoint.Port.ToString(CultureInfo.InvariantCulture) + "/");
				_acceptThread = new Thread(AcceptLoop)
				{
					IsBackground = true,
					Name = "MetaCompanion advisor client test server"
				};
				_acceptThread.Start();
			}

			public Uri BaseUri { get; }
			public string SuccessfulSolveRequestId
			{
				get { return _successfulSolveRequestId; }
				set { _successfulSolveRequestId = value ?? ""; }
			}
			public TimeSpan SuccessfulSolveDelay { get; set; } = TimeSpan.Zero;

			public bool WaitForRequest(string path, string requestId, TimeSpan timeout)
			{
				var deadline = DateTime.UtcNow + timeout;
				do
				{
					lock (_sync)
					{
						if (_requests.Any(item =>
							string.Equals(item.Path, path, StringComparison.Ordinal) &&
							string.Equals(item.RequestId, requestId, StringComparison.Ordinal)))
							return true;
					}
					Thread.Sleep(5);
				}
				while (DateTime.UtcNow < deadline);
				return false;
			}

			public string GetRequestBody(string path, string requestId)
			{
				lock (_sync)
				{
					var request = _requests.LastOrDefault(item =>
						string.Equals(item.Path, path, StringComparison.Ordinal) &&
						string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
					return request?.Body ?? "";
				}
			}

			private void AcceptLoop()
			{
				while (!_disposed)
				{
					TcpClient client;
					try { client = _listener.AcceptTcpClient(); }
					catch (SocketException) { break; }
					catch (ObjectDisposedException) { break; }
					ThreadPool.QueueUserWorkItem(ignored => HandleClient(client));
				}
			}

			private void HandleClient(TcpClient client)
			{
				using (client)
				{
					try
					{
						client.NoDelay = true;
						client.ReceiveTimeout = 5000;
						client.SendTimeout = 5000;
						using (var stream = client.GetStream())
						{
							var request = ReadRequest(stream);
							ManualResetEventSlim blocked = null;
							if (string.Equals(request.Path, "/v1/solve", StringComparison.Ordinal) &&
								!string.Equals(request.RequestId, SuccessfulSolveRequestId,
									StringComparison.Ordinal))
							{
								blocked = new ManualResetEventSlim();
								lock (_sync)
									_blockedSolves[request.RequestId] = blocked;
							}
							lock (_sync)
								_requests.Add(request);

							if (string.Equals(request.Path, "/v1/cancel", StringComparison.Ordinal))
							{
								ManualResetEventSlim solve;
								lock (_sync)
									_blockedSolves.TryGetValue(request.RequestId, out solve);
								solve?.Set();
								WriteJson(stream,
									"{\"api_version\":\"1.0\",\"status\":\"cancellation_requested\"}");
								return;
							}

							if (string.Equals(request.Path, "/v1/solve", StringComparison.Ordinal) &&
								string.Equals(request.RequestId, SuccessfulSolveRequestId,
									StringComparison.Ordinal))
							{
								if (SuccessfulSolveDelay > TimeSpan.Zero)
									Thread.Sleep(SuccessfulSolveDelay);
								WriteJson(stream,
									"{\"api_version\":\"1.0\",\"schema_version\":1," +
									"\"request_id\":\"" + request.RequestId + "\"," +
									"\"state_id\":\"" + request.StateId + "\"," +
									"\"status\":\"partial\",\"is_final\":true," +
									"\"recommendations\":[],\"warnings\":[],\"coverage\":{}}");
								return;
							}

							if (blocked != null)
							{
								blocked.Wait(TimeSpan.FromSeconds(5));
								lock (_sync)
									_blockedSolves.Remove(request.RequestId);
								blocked.Dispose();
							}
						}
					}
					catch
					{
						// A timed-out HttpWebRequest closes its socket by design.
					}
				}
			}

			private static RecordedRequest ReadRequest(NetworkStream stream)
			{
				var headerBytes = new List<byte>();
				while (headerBytes.Count < 64 * 1024)
				{
					var value = stream.ReadByte();
					if (value < 0)
						throw new InvalidOperationException("HTTP request ended before headers.");
					headerBytes.Add((byte)value);
					var count = headerBytes.Count;
					if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 &&
						headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10)
						break;
				}
				var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
				var lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
				var requestLine = lines[0].Split(' ');
				var contentLength = 0;
				foreach (var line in lines)
				{
					if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
						int.TryParse(line.Substring(line.IndexOf(':') + 1).Trim(),
							NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
				}
				if (headers.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
					stream.Write(interim, 0, interim.Length);
					stream.Flush();
				}
				var bodyBytes = new byte[contentLength];
				var offset = 0;
				while (offset < bodyBytes.Length)
				{
					var read = stream.Read(bodyBytes, offset, bodyBytes.Length - offset);
					if (read <= 0)
						throw new InvalidOperationException("HTTP request ended before its body.");
					offset += read;
				}
				var body = Encoding.UTF8.GetString(bodyBytes);
				var root = AdvisorWireProtocol.ParseObject(body);
				var requestId = Convert.ToString(root["request_id"], CultureInfo.InvariantCulture) ?? "";
				var stateId = "";
				object stateValue;
				var state = root.TryGetValue("state", out stateValue)
					? stateValue as IDictionary<string, object>
					: null;
				if (state != null && state.TryGetValue("state_id", out stateValue))
					stateId = Convert.ToString(stateValue, CultureInfo.InvariantCulture) ?? "";
				return new RecordedRequest
				{
					Path = requestLine.Length > 1 ? requestLine[1] : "",
					RequestId = requestId,
					StateId = stateId,
					Body = body
				};
			}

			private static void WriteJson(NetworkStream stream, string json)
			{
				var body = Encoding.UTF8.GetBytes(json);
				var headers = Encoding.ASCII.GetBytes(
					"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
					"Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
					"\r\nConnection: close\r\n\r\n");
				stream.Write(headers, 0, headers.Length);
				stream.Write(body, 0, body.Length);
				stream.Flush();
			}

			public void Dispose()
			{
				_disposed = true;
				lock (_sync)
				{
					foreach (var solve in _blockedSolves.Values)
						solve.Set();
				}
				try { _listener.Stop(); }
				catch { }
				_acceptThread.Join(TimeSpan.FromSeconds(2));
			}
		}

		private sealed class BlockingCancellationClient : IAdvisorWorkerClient, IDisposable
		{
			private readonly object _sync = new object();
			private readonly List<TaskCompletionSource<AdvisorSolveResponse>> _solves =
				new List<TaskCompletionSource<AdvisorSolveResponse>>();
			private int _solveCount;
			private int _cancelCount;

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");
			public ManualResetEventSlim FirstStarted { get; } = new ManualResetEventSlim();
			public ManualResetEventSlim SecondStarted { get; } = new ManualResetEventSlim();
			public int CancelCount { get { return Interlocked.CompareExchange(ref _cancelCount, 0, 0); } }

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				var completion = new TaskCompletionSource<AdvisorSolveResponse>();
				lock (_sync)
					_solves.Add(completion);
				var call = Interlocked.Increment(ref _solveCount);
				if (call == 1)
					FirstStarted.Set();
				else if (call == 2)
					SecondStarted.Set();
				cancellationToken.Register(() => completion.TrySetCanceled());
				return completion.Task;
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				Interlocked.Increment(ref _cancelCount);
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
			}

			public void Dispose()
			{
				lock (_sync)
				{
					foreach (var solve in _solves)
						solve.TrySetCanceled();
					_solves.Clear();
				}
				FirstStarted.Dispose();
				SecondStarted.Dispose();
			}
		}

		private sealed class FakeAdvisorClient : IAdvisorWorkerClient
		{
			private readonly string _responseStateId;
			private readonly string _status;
			private readonly object _requestsLock = new object();
			private readonly List<AdvisorSolveRequest> _requests =
				new List<AdvisorSolveRequest>();

			public FakeAdvisorClient(string responseStateId, string status = AdvisorProtocol.StatusOk)
			{
				_responseStateId = responseStateId;
				_status = status;
			}

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");
			public int SolveCount { get; private set; }
			public List<AdvisorSolveRequest> Requests
			{
				get
				{
					lock (_requestsLock)
						return new List<AdvisorSolveRequest>(_requests);
				}
			}

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				lock (_requestsLock)
				{
					_requests.Add(request);
					SolveCount++;
				}
				return Task.FromResult(new AdvisorSolveResponse
				{
					ApiVersion = AdvisorProtocol.ApiVersion,
					RequestId = request.RequestId,
					StateId = _responseStateId,
					Status = _status
				});
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
			}
		}

		private sealed class ScriptedAdvisorClient : IAdvisorWorkerClient
		{
			private readonly Func<int, AdvisorSolveRequest, AdvisorSolveResponse> _solve;
			private readonly object _sync = new object();
			private int _solveCount;

			public ScriptedAdvisorClient(
				Func<int, AdvisorSolveRequest, AdvisorSolveResponse> solve)
			{
				if (solve == null)
					throw new ArgumentNullException(nameof(solve));
				_solve = solve;
			}

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");
			public int SolveCount { get { lock (_sync) return _solveCount; } }

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				int call;
				lock (_sync)
					call = ++_solveCount;
				try
				{
					return Task.FromResult(_solve(call, request));
				}
				catch (Exception ex)
				{
					var completion = new TaskCompletionSource<AdvisorSolveResponse>();
					completion.SetException(ex);
					return completion.Task;
				}
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
			}
		}

		private sealed class FailingAdvisorClient : IAdvisorWorkerClient
		{
			private readonly Exception _error;

			public FailingAdvisorClient(Exception error)
			{
				_error = error;
			}

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				var completion = new TaskCompletionSource<AdvisorSolveResponse>();
				completion.SetException(_error);
				return completion.Task;
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
			}
		}

		private sealed class OrderedObservationClient : IAdvisorWorkerClient, IDisposable
		{
			private readonly object _sync = new object();
			private readonly TaskCompletionSource<AdvisorObservationResult> _firstCompletion =
				new TaskCompletionSource<AdvisorObservationResult>();
			private readonly List<string> _observedStateIds = new List<string>();

			public Uri BaseUri => new Uri("http://127.0.0.1:17853/");
			public ManualResetEventSlim FirstStarted { get; } = new ManualResetEventSlim();
			public ManualResetEventSlim SecondStarted { get; } = new ManualResetEventSlim();
			public List<string> ObservedStateIds
			{
				get
				{
					lock (_sync)
						return new List<string>(_observedStateIds);
				}
			}

			public Task<AdvisorWorkerHealth> GetHealthAsync(CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorWorkerHealth { IsReady = true });
			}

			public Task<AdvisorSolveResponse> SolveAsync(
				AdvisorSolveRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(new AdvisorSolveResponse());
			}

			public Task<bool> CancelAsync(
				AdvisorCancelRequest request, CancellationToken cancellationToken)
			{
				return Task.FromResult(true);
			}

			public Task<AdvisorObservationResult> ObserveAsync(
				AdvisorObservation observation, CancellationToken cancellationToken)
			{
				lock (_sync)
				{
					_observedStateIds.Add(observation?.StateId ?? "");
					if (_observedStateIds.Count == 1)
					{
						FirstStarted.Set();
						return _firstCompletion.Task;
					}
					SecondStarted.Set();
					return Task.FromResult(new AdvisorObservationResult { Status = "ok" });
				}
			}

			public void CompleteFirst()
			{
				_firstCompletion.TrySetResult(new AdvisorObservationResult { Status = "ok" });
			}

			public void Dispose()
			{
				CompleteFirst();
				FirstStarted.Dispose();
				SecondStarted.Dispose();
			}
		}

		private sealed class ImmediateSynchronizationContext : SynchronizationContext
		{
			public override void Post(SendOrPostCallback callback, object state)
			{
				callback(state);
			}
		}
	}
}
