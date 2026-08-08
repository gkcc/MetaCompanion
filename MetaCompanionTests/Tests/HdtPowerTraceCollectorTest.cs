using MetaCompanion;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MetaCompanionTests
{
	[TestClass]
	public class HdtPowerTraceCollectorTest
	{
		private const string Prefix = "D 12:34:56.0000000 ";

		[TestMethod]
		public void Collector_JoinsCompletePlayOptionSendAndRootWithoutKeepingLocalizedName()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 7);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=3"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=已脱敏 id=11 zone=HAND zonePos=4 cardId=TIME_606 player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=2"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=另一个本地化名称 id=11 zone=HAND zonePos=4 cardId=TIME_606 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() -     BLOCK_START BlockType=POWER Entity=[entityName=已脱敏 id=11 zone=HAND zonePos=4 cardId=TIME_606 player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 ")
			});

			Assert.AreEqual(0, collector.Collect(stream, 7, stream).Count);
			stream.Add(Line("GameState.DebugPrintPower() -     BLOCK_END"));
			stream.Add(Line("GameState.DebugPrintPower() - BLOCK_END"));
			Assert.AreEqual(0, collector.Collect(stream, 7, stream).Count,
				"The root is not final until the following stable option frame rules out a choice.");
			stream.Add(Line("GameState.DebugPrintOptions() - id=4"));
			var evidence = collector.Collect(stream, 7, stream);

			Assert.AreEqual(1, evidence.Count);
			Assert.AreEqual("PLAY", evidence[0].PowerBlockType);
			Assert.AreEqual(3, evidence[0].FrameId);
			Assert.AreEqual(1, evidence[0].OptionId);
			Assert.AreEqual(-1, evidence[0].SubOption);
			Assert.AreEqual(2, evidence[0].BoardPosition);
			Assert.AreEqual(11, evidence[0].Source.EntityId);
			Assert.AreEqual("TIME_606", evidence[0].Source.CardId);
			Assert.AreEqual("exact_hdt_power_v1", evidence[0].ActionIdentityStatus);
			Assert.AreEqual("none", evidence[0].ChoiceStatus);
			Assert.AreEqual(
				AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				evidence[0].TargetBindingStatus);
			Assert.IsTrue(evidence[0].PowerEndWatermark >= evidence[0].PowerStartWatermark);
		}

		[TestMethod]
		public void Collector_AttackRequiresOptionsSendAndRootToAgreeOnExactTarget()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 2);
			AddAttack(stream, 8, 1, 51, 64, 2, 1, 64);
			var evidence = collector.Collect(stream, 2, stream);

			Assert.AreEqual(1, evidence.Count);
			Assert.AreEqual("ATTACK", evidence[0].PowerBlockType);
			Assert.AreEqual(51, evidence[0].Source.EntityId);
			Assert.AreEqual(64, evidence[0].Target.EntityId);
			Assert.AreEqual(
				AdvisorBehaviorTargetBindingStatus.ExactEntityId,
				evidence[0].TargetBindingStatus);

			var mismatchStream = new List<string>();
			var mismatchCollector = NewCollector(mismatchStream, 3);
			AddAttack(mismatchStream, 9, 1, 51, 64, 2, 1, 65);
			Assert.AreEqual(0, mismatchCollector.Collect(mismatchStream, 3, mismatchStream).Count,
				"A root target that differs from SendOption must fail closed.");
		}

		[TestMethod]
		public void Collector_EndTurnAcceptsHdtInvalidPseudoOptionOnlyWithMainEnd()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 5);
			stream.Add(Line("GameState.DebugPrintOptions() - id=12"));
			stream.Add(Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="));
			stream.Add(Line("GameState.SendOption() - selectedOption=0 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"));
			Assert.AreEqual(0, collector.Collect(stream, 5, stream).Count);
			stream.Add(Line("PowerTaskList.DebugPrintPower() -     TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END"));
			Assert.AreEqual(0, collector.Collect(stream, 5, stream).Count,
				"A replay/history line is not local-input evidence.");
			stream.Add(Line("GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END "));
			var evidence = collector.Collect(stream, 5, stream);

			Assert.AreEqual(1, evidence.Count);
			Assert.AreEqual("MAIN_END", evidence[0].PowerBlockType);
			Assert.AreEqual(0, evidence[0].OptionId);
			Assert.AreEqual("exact_hdt_power_v1", evidence[0].ActionIdentityStatus);
			Assert.AreEqual(
				AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				evidence[0].TargetBindingStatus);
		}

		[TestMethod]
		public void Collector_LegacyChoiceWithoutOfferProofStaysUnresolved()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 1);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=6"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=EDR_463 player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintOptions() -     subOption 0 entity=[entityName=脱敏 id=117 zone=SETASIDE zonePos=0 cardId=EDR_463a player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintOptions() -     subOption 1 entity=[entityName=脱敏 id=118 zone=SETASIDE zonePos=0 cardId=EDR_463b player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=EDR_463 player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END"),
				Line("GameState.SendChoices() - id=17 ChoiceType=GENERAL"),
				Line("GameState.SendChoices() -   m_chosenEntities[0]=[entityName=脱敏 id=118 zone=SETASIDE zonePos=0 cardId=EDR_463b player=1]"),
				Line("GameState.DebugPrintOptions() - id=7")
			});

			var evidence = collector.Collect(stream, 1, stream);
			Assert.AreEqual(1, evidence.Count);
			Assert.AreEqual("choice_unresolved", evidence[0].ActionIdentityStatus);
			Assert.AreEqual("unresolved", evidence[0].ChoiceStatus);
			Assert.AreEqual(1, evidence[0].SubOption);
			Assert.AreEqual(2, evidence[0].Choices.Count);
			Assert.AreEqual("SUB_OPTION", evidence[0].Choices[0].ChoiceType);
			CollectionAssert.AreEqual(new[] { 117, 118 },
				evidence[0].Choices[0].OptionEntityIds);
			CollectionAssert.AreEqual(new[] { 118 }, evidence[0].Choices[0].EntityIds);
			Assert.AreEqual("selected", evidence[0].Choices[0].Status);
			Assert.AreEqual("unresolved", evidence[0].Choices[1].Status);
		}

		[TestMethod]
		public void Collector_CurrentHdtOfferedAndChosenChoiceIsExactForBehavior()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 2);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=6"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END"),
				Line("GameState.DebugPrintEntityChoices() - id=17 Player=本方 TaskList= ChoiceType=GENERAL"),
				Line("GameState.DebugPrintEntityChoices() - Source=[entityName=脱敏 id=116 zone=PLAY zonePos=1 cardId=DISCOVER_CARD player=1]"),
				Line("GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=脱敏 id=117 zone=SETASIDE zonePos=0 cardId=OPTION_A player=1]"),
				Line("GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=脱敏 id=118 zone=SETASIDE zonePos=0 cardId=OPTION_B player=1]"),
				Line("GameState.DebugPrintEntitiesChosen() - id=17 Player=本方 EntitiesCount=1"),
				Line("GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=脱敏 id=118 zone=SETASIDE zonePos=0 cardId=OPTION_B player=1]"),
				Line("GameState.DebugPrintOptions() - id=7")
			});

			var evidence = collector.Collect(stream, 2, stream).Single();
			Assert.AreEqual("exact_hdt_power_choice_v1", evidence.ActionIdentityStatus);
			Assert.AreEqual("selected", evidence.ChoiceStatus);
			Assert.AreEqual(1, evidence.Choices.Count);
			Assert.AreEqual(17, evidence.Choices[0].ChoiceId);
			Assert.AreEqual(116, evidence.Choices[0].SourceEntityId);
			CollectionAssert.AreEqual(new[] { 117, 118 },
				evidence.Choices[0].OptionEntityIds);
			CollectionAssert.AreEqual(new[] { 118 }, evidence.Choices[0].EntityIds);
			Assert.AreEqual("selected", evidence.Choices[0].Status);
		}

		[TestMethod]
		public void Collector_UnrootedChoiceCannotBindToFollowingPlay()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 5);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=6"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintEntityChoices() - id=3 Player=本方 TaskList= ChoiceType=MULLIGAN"),
				Line("GameState.DebugPrintEntityChoices() - Source=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1]"),
				Line("GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=脱敏 id=201 zone=HAND zonePos=1 cardId=OPTION_A player=1]"),
				Line("GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=脱敏 id=202 zone=HAND zonePos=2 cardId=OPTION_B player=1]"),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END"),
				Line("GameState.DebugPrintEntitiesChosen() - id=3 Player=本方 EntitiesCount=1"),
				Line("GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=脱敏 id=202 zone=HAND zonePos=2 cardId=OPTION_B player=1]"),
				Line("GameState.DebugPrintOptions() - id=7")
			});

			var evidence = collector.Collect(stream, 5, stream).Single();
			Assert.AreEqual("exact_hdt_power_v1", evidence.ActionIdentityStatus);
			Assert.AreEqual("none", evidence.ChoiceStatus);
			Assert.AreEqual(0, evidence.Choices.Count,
				"A choice seen without a concrete root must not be rebound when a later play opens.");
		}

		[TestMethod]
		public void Collector_MalformedUnrootedChoiceCannotTaintFollowingPlay()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 6);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=6"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintEntityChoices() - id=3 Player=本方 TaskList= ChoiceType=MULLIGAN"),
				Line("GameState.DebugPrintEntityChoices() - Source=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1]"),
				Line("GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=脱敏 id=201 zone=HAND zonePos=1 cardId=OPTION_A player=1]"),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=NEXT_PLAY player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END"),
				Line("GameState.DebugPrintEntitiesChosen() - id=3 Player=本方 EntitiesCount=1"),
				Line("GameState.DebugPrintEntitiesChosen() - Entities[0]=malformed"),
				Line("GameState.DebugPrintOptions() - id=7")
			});

			var evidence = collector.Collect(stream, 6, stream).Single();
			Assert.AreEqual("exact_hdt_power_v1", evidence.ActionIdentityStatus);
			Assert.AreEqual("none", evidence.ChoiceStatus);
			Assert.AreEqual(0, evidence.Choices.Count);
		}

		[TestMethod]
		public void Collector_ChoiceNotOfferedOrFromWrongSourceCannotBecomeExact()
		{
			foreach (var wrongSource in new[] { false, true })
			{
				var stream = new List<string>();
				var collector = NewCollector(stream, wrongSource ? 4 : 3);
				stream.AddRange(new[]
				{
					Line("GameState.DebugPrintOptions() - id=6"),
					Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
					Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] error=NONE errorParam="),
					Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
					Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
					Line("GameState.DebugPrintPower() - BLOCK_END"),
					Line("GameState.DebugPrintEntityChoices() - id=17 Player=本方 TaskList= ChoiceType=GENERAL"),
					Line("GameState.DebugPrintEntityChoices() - Source=[entityName=脱敏 id=" +
						(wrongSource ? "999" : "116") + " zone=PLAY zonePos=1 cardId=DISCOVER_CARD player=1]"),
					Line("GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=脱敏 id=117 zone=SETASIDE zonePos=0 cardId=OPTION_A player=1]"),
					Line("GameState.DebugPrintEntitiesChosen() - id=17 Player=本方 EntitiesCount=1"),
					Line("GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=脱敏 id=" +
						(wrongSource ? "117" : "118") + " zone=SETASIDE zonePos=0 cardId=OPTION_B player=1]"),
					Line("GameState.DebugPrintOptions() - id=7")
				});

				var evidence = collector.Collect(stream, collector.Generation, stream).Single();
				Assert.AreEqual("choice_unresolved", evidence.ActionIdentityStatus);
				Assert.AreEqual("unresolved", evidence.ChoiceStatus);
			}
		}

		[TestMethod]
		public void Collector_ChoiceCountIndicesAndPlayerMustProveCompleteSelection()
		{
			for (var variant = 0; variant < 5; variant++)
			{
				var stream = new List<string>();
				var collector = NewCollector(stream, 20 + variant);
				stream.AddRange(new[]
				{
					Line("GameState.DebugPrintOptions() - id=6"),
					Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
					Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] error=NONE errorParam="),
					Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
					Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=116 zone=HAND zonePos=5 cardId=DISCOVER_CARD player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
					Line("GameState.DebugPrintPower() - BLOCK_END"),
					Line("GameState.DebugPrintEntityChoices() - id=17 Player=本方 TaskList= ChoiceType=GENERAL"),
					Line("GameState.DebugPrintEntityChoices() - Source=[entityName=脱敏 id=116 zone=PLAY zonePos=1 cardId=DISCOVER_CARD player=1]")
				});
				var offeredIndex = variant == 1 ? 1 : 0;
				stream.Add(Line("GameState.DebugPrintEntityChoices() - Entities[" +
					offeredIndex + "]=[entityName=脱敏 id=117 zone=SETASIDE zonePos=0 cardId=OPTION_A player=1]"));
				if (variant == 3)
				{
					stream.Add(Line("GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=脱敏 id=118 zone=SETASIDE zonePos=0 cardId=OPTION_B player=1]"));
				}
				var expectedCount = variant == 0 ? 2 : 1;
				var chosenPlayer = variant == 4 ? "对手" : "本方";
				stream.Add(Line("GameState.DebugPrintEntitiesChosen() - id=17 Player=" +
					chosenPlayer + " EntitiesCount=" + expectedCount));
				var selectedIndex = variant == 2 ? 1 : 0;
				stream.Add(Line("GameState.DebugPrintEntitiesChosen() - Entities[" +
					selectedIndex + "]=[entityName=脱敏 id=117 zone=SETASIDE zonePos=0 cardId=OPTION_A player=1]"));
				stream.Add(Line("GameState.DebugPrintOptions() - id=7"));

				var evidence = collector.Collect(stream, collector.Generation, stream).Single();
				Assert.AreEqual("choice_unresolved", evidence.ActionIdentityStatus,
					"variant=" + variant);
				Assert.AreEqual("unresolved", evidence.ChoiceStatus, "variant=" + variant);
			}
		}

		[TestMethod]
		public void Collector_StableOptionsRequireEmptyPollAndOneStateOwnsTheFrame()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 12);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=41"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=11 zone=HAND zonePos=1 cardId=TEST_SPELL player=1] error=NONE errorParam=")
			});

			collector.Collect(stream, 12, stream);
			HdtPowerOptionsFrameEvidence frame;
			Assert.IsFalse(collector.TryGetStableOptionsFrame(12, "state-a", out frame),
				"A frame must not become stable in the same poll that appended its final line.");

			collector.Collect(stream, 12, stream);
			Assert.IsTrue(collector.TryGetStableOptionsFrame(12, "state-a", out frame));
			Assert.AreEqual(41, frame.FrameId);
			Assert.AreEqual(2, frame.Options.Count);
			frame.Options[1].Entity.CardId = "MUTATED_BY_CALLER";

			HdtPowerOptionsFrameEvidence secondRead;
			Assert.IsTrue(collector.TryGetStableOptionsFrame(12, "state-a", out secondRead));
			Assert.AreEqual("TEST_SPELL", secondRead.Options[1].Entity.CardId,
				"Published evidence must be detached from parser-owned state.");
			Assert.IsFalse(collector.TryGetStableOptionsFrame(12, "state-b", out frame));

			collector.Collect(stream, 12, stream);
			Assert.IsFalse(collector.TryGetStableOptionsFrame(12, "state-b", out frame),
				"An empty poll must not republish a frame already owned by another state.");

			stream.Add(Line("GameState.DebugPrintOptions() - id=42"));
			stream.Add(Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="));
			collector.Collect(stream, 12, stream);
			collector.Collect(stream, 12, stream);
			Assert.IsTrue(collector.TryGetStableOptionsFrame(12, "state-b", out frame),
				"A genuinely new HDT frame may bind to the new state.");
			Assert.AreEqual(42, frame.FrameId);
		}

		[TestMethod]
		public void HdtRootBinder_ExpandsEveryAttackTargetAndBoardPlacement()
		{
			var state = NewState("root-state", 10);
			state.Player.Board.Add(new AdvisorEntityState
			{
				EntityId = 11, CardId = "ATTACKER", ControllerId = 1,
				Zone = "PLAY", CardType = "MINION"
			});
			state.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 12, CardId = "HAND_MINION", ControllerId = 1,
				Zone = "HAND", CardType = "MINION"
			});
			state.Opponent.Board.Add(new AdvisorEntityState
			{
				EntityId = 21, CardId = "DEFENDER", ControllerId = 2,
				Zone = "PLAY", CardType = "MINION"
			});
			var frame = NewRootFrame(
				new HdtPowerOptionEvidence
				{
					OptionId = 1, Type = "POWER", Error = "NONE",
					Entity = RootEntity(11, "PLAY", "ATTACKER", 1),
					Targets = new List<HdtPowerTargetEvidence>
					{
						new HdtPowerTargetEvidence
						{
							TargetId = 0, Error = "NONE",
							Entity = RootEntity(2, "PLAY", "HERO_2", 2)
						},
						new HdtPowerTargetEvidence
						{
							TargetId = 1, Error = "NONE",
							Entity = RootEntity(21, "PLAY", "DEFENDER", 2)
						}
					}
				},
				new HdtPowerOptionEvidence
				{
					OptionId = 2, Type = "POWER", Error = "NONE",
					Entity = RootEntity(12, "HAND", "HAND_MINION", 1)
				});

			AdvisorHdtRootCandidateSet candidates;
			string reason;
			Assert.IsTrue(AdvisorHdtRootCandidateBinder.TryBuild(
				frame, state, out candidates, out reason), reason);
			Assert.IsTrue(candidates.CandidateSetComplete);
			Assert.AreEqual(state.StateId, candidates.StateId);
			Assert.AreEqual(5, candidates.Candidates.Count,
				"End turn, two attack targets, and two legal insertion slots must all be present.");
			CollectionAssert.AreEquivalent(
				new[] { 2, 21 },
				candidates.Candidates.Where(item => item.Action.Kind == "attack")
					.Select(item => item.Action.TargetEntityId.Value).ToArray());
			CollectionAssert.AreEquivalent(
				new[] { 1, 2 },
				candidates.Candidates.Where(item => item.Action.Kind == "play_card")
					.Select(item => item.Action.BoardPosition).ToArray());
			Assert.IsTrue(candidates.Candidates.Where(item => item.Action.Kind == "play_card")
				.All(item => item.PositionEvidence == "core_board_slots_v1"));
		}

		[TestMethod]
		public void HdtRootBinder_FailsClosedForSubOptionsHiddenTargetsAndDuplicates()
		{
			var state = NewState("root-reject", 1);
			state.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 12, CardId = "SPELL", ControllerId = 1,
				Zone = "HAND", CardType = "SPELL"
			});
			state.Opponent.Hand.Add(new AdvisorEntityState
			{
				EntityId = 30, CardId = "", ControllerId = 2,
				Zone = "HAND", CardType = "UNKNOWN", Visibility = "hidden"
			});

			var subOptionFrame = NewRootFrame(new HdtPowerOptionEvidence
			{
				OptionId = 1, Type = "POWER", Error = "NONE",
				Entity = RootEntity(12, "HAND", "SPELL", 1),
				SubOptions = new List<HdtPowerSubOptionEvidence>
				{
					new HdtPowerSubOptionEvidence
					{
						SubOptionId = 0, Error = "NONE",
						Entity = RootEntity(12, "SETASIDE", "SPELL", 1)
					}
				}
			});
			AdvisorHdtRootCandidateSet ignored;
			string reason;
			Assert.IsFalse(AdvisorHdtRootCandidateBinder.TryBuild(
				subOptionFrame, state, out ignored, out reason));
			Assert.AreEqual("legal_option_shape_unsupported", reason);

			var hiddenTargetFrame = NewRootFrame(new HdtPowerOptionEvidence
			{
				OptionId = 1, Type = "POWER", Error = "NONE",
				Entity = RootEntity(12, "HAND", "SPELL", 1),
				Targets = new List<HdtPowerTargetEvidence>
				{
					new HdtPowerTargetEvidence
					{
						TargetId = 0, Error = "NONE",
						Entity = RootEntity(30, "HAND", "", 2)
					}
				}
			});
			Assert.IsFalse(AdvisorHdtRootCandidateBinder.TryBuild(
				hiddenTargetFrame, state, out ignored, out reason));
			Assert.AreEqual("legal_target_not_bound", reason);

			var duplicateFrame = NewRootFrame(
				new HdtPowerOptionEvidence
				{
					OptionId = 1, Type = "POWER", Error = "NONE",
					Entity = RootEntity(12, "HAND", "SPELL", 1)
				},
				new HdtPowerOptionEvidence
				{
					OptionId = 2, Type = "POWER", Error = "NONE",
					Entity = RootEntity(12, "HAND", "SPELL", 1)
				});
			Assert.IsFalse(AdvisorHdtRootCandidateBinder.TryBuild(
				duplicateFrame, state, out ignored, out reason));
			Assert.AreEqual("duplicate_candidate_action", reason);
		}

		[TestMethod]
		public void HdtRootBinder_RejectsActionResolutionFrameThatWouldDropJustPlayedCard()
		{
			var state = NewState("before-play", 1);
			state.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 12, CardId = "ONE_COST_SPELL", ControllerId = 1,
				Zone = "HAND", CardType = "SPELL"
			});
			state.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 13, CardId = "TRIGGER_MINION", ControllerId = 1,
				Zone = "HAND", CardType = "MINION"
			});
			var stream = new List<string>();
			var collector = NewCollector(stream, 7);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=60"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=12 zone=HAND zonePos=1 cardId=ONE_COST_SPELL player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintOptions() -   option 2 type=POWER mainEntity=[entityName=脱敏 id=13 zone=HAND zonePos=2 cardId=TRIGGER_MINION player=1] error=NONE errorParam=")
			});
			collector.Collect(stream, 7, stream);
			collector.Collect(stream, 7, stream);
			HdtPowerOptionsFrameEvidence frame;
			Assert.IsTrue(collector.TryGetStableOptionsFrame(
				7, state.StateId, out frame));

			AdvisorHdtRootCandidateSet stableCandidates;
			string reason;
			Assert.IsTrue(AdvisorHdtRootCandidateBinder.TryBuild(
				frame, state, out stableCandidates, out reason), reason);
			Assert.IsTrue(stableCandidates.Candidates.Any(item =>
				item.Action.SourceEntityId == 13 && item.Action.Kind == "play_card"));

			// This is the exact transitional shape seen in the real Power.log: after SendOption,
			// GameState emits a new frame while the played minion still appears in HAND and is
			// marked REQ_NOT_MINION_JUST_PLAYED.  It must not replace frame 60 as a complete
			// portfolio for the still-old public snapshot.
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=61"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=12 zone=HAND zonePos=1 cardId=ONE_COST_SPELL player=1] error=NONE errorParam="),
				Line("GameState.DebugPrintOptions() -   option 2 type=POWER mainEntity=[entityName=脱敏 id=13 zone=HAND zonePos=2 cardId=TRIGGER_MINION player=1] error=REQ_NOT_MINION_JUST_PLAYED errorParam=")
			});
			collector.Collect(stream, 7, stream);
			collector.Collect(stream, 7, stream);
			state.StateId = "during-play";
			Assert.IsTrue(collector.TryGetStableOptionsFrame(
				7, state.StateId, out frame));

			AdvisorHdtRootCandidateSet ignored;
			Assert.IsFalse(AdvisorHdtRootCandidateBinder.TryBuild(
				frame, state, out ignored, out reason));
			Assert.AreEqual("action_resolution_in_flight", reason);
			Assert.IsNull(ignored,
				"An in-flight post-input frame must not be advertised as a complete pre-input portfolio.");
		}

		[TestMethod]
		public void PowerObservation_DropsRootPortfolioWhenSelectedActionIsAbsent()
		{
			var pre = NewState("selected-missing-pre", 1);
			pre.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 12, CardId = "SPELL", ControllerId = 1,
				Zone = "HAND", CardType = "SPELL"
			});
			var evidence = new HdtPowerActionEvidence
			{
				PowerBlockType = "PLAY",
				FrameId = 42,
				OptionId = 2,
				SubOption = -1,
				BoardPosition = 0,
				Source = RootEntity(12, "HAND", "SPELL", 1),
				TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				PowerStartWatermark = 20,
				PowerEndWatermark = 25,
				PowerCollectorEpoch = 7,
				PowerActionOrdinal = 1,
				PowerGapCount = 0,
				ActionIdentityStatus = "exact_hdt_power_v1",
				ChoiceStatus = "none",
				OptionsFrame = NewRootFrame(new HdtPowerOptionEvidence
				{
					OptionId = 1, Type = "POWER", Error = "NONE",
					Entity = RootEntity(12, "HAND", "SPELL", 1)
				})
			};

			AdvisorPendingAction pending;
			Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 7, 1, out pending));
			Assert.IsNull(pending.HdtRootCandidates,
				"A complete candidate set is unsafe unless the observed input occurs exactly once.");

			var tracker = new AdvisorTransitionCandidateTracker();
			Assert.IsTrue(tracker.Register(pending));
			var firstPost = NewState("selected-missing-post", 2);
			firstPost.StateHash = new string('b', 64);
			var secondPost = NewState("selected-missing-post", 3);
			secondPost.StateHash = new string('b', 64);
			Assert.AreEqual(0, tracker.ObserveSnapshot(firstPost, 7, 20).Count);
			var observation = tracker.ObserveSnapshot(secondPost, 7, 20).Single();
			Assert.IsNull(observation.Action.HdtRootCandidates);
			Assert.IsFalse(AdvisorWireProtocol.SerializeObservation(observation)
				.Contains("hdt_root_candidates"));
		}

		[TestMethod]
		public void Collector_RewindAndGenerationChangeDiscardPendingAndHistoricalLines()
		{
			var historical = new List<string>
			{
				Line("GameState.DebugPrintOptions() - id=1"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.SendOption() - selectedOption=0 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END ")
			};
			var collector = NewCollector(historical, 10);
			Assert.AreEqual(0, collector.Collect(historical, 10, historical).Count,
				"BeginGeneration must anchor after existing history.");

			historical.Add(Line("GameState.DebugPrintOptions() - id=2"));
			collector.Collect(historical, 10, historical);
			historical[historical.Count - 1] = Line("GameState.DebugPrintOptions() - id=999");
			Assert.AreEqual(0, collector.Collect(historical, 10, historical).Count);
			AddEndTurn(historical, 3);
			Assert.AreEqual(1, collector.Collect(historical, 10, historical).Count,
				"After a rewind, only a newly appended complete frame may be accepted.");

			var replacement = new List<string>(historical);
			Assert.AreEqual(0, collector.Collect(replacement, 11, replacement).Count,
				"A new generation/stream must never replay the old list.");
		}

		[TestMethod]
		public void Collector_AppendedConsumedPrefixIsDiscardedAndPermanentlyTaintsTrace()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 12);
			AddEndTurn(stream, 1);
			var first = collector.Collect(stream, 12, stream).Single();
			collector.MarkActionRecorded(first.PowerActionOrdinal);
			AddEndTurn(stream, 2);
			var second = collector.Collect(stream, 12, stream).Single();
			collector.MarkActionRecorded(second.PowerActionOrdinal);
			Assert.AreEqual("complete", collector.GetTraceSummary().TraceStatus);

			var consumedPrefix = stream.ToArray();
			stream.AddRange(consumedPrefix);
			Assert.AreEqual(0, collector.Collect(stream, 12, stream).Count,
				"A same-list replay prefix must never become duplicate behavior evidence.");
			var summary = collector.GetTraceSummary();
			Assert.AreEqual(2, summary.CommittedActionCount);
			Assert.AreEqual(2, summary.RecordedActionCount);
			Assert.IsTrue(summary.GapCount > 0);
			Assert.AreEqual("tainted", summary.TraceStatus);
			Assert.AreEqual(stream.Count, collector.Cursor);
		}

		[TestMethod]
		public void PowerEvidence_ValidatesAgainstPreStateAndUpgradesGameEventWithoutDuplicate()
		{
			var pre = NewState("pre", 10);
			var source = new AdvisorEntityState
			{
				EntityId = 11,
				CardId = "TIME_606",
				ControllerId = 1,
				Zone = "HAND",
				CardType = "MINION"
			};
			pre.Player.Hand.Add(source);
			var evidence = new HdtPowerActionEvidence
			{
				PowerBlockType = "PLAY",
				FrameId = 3,
				OptionId = 1,
				SubOption = -1,
				BoardPosition = 2,
				Source = new HdtPowerEntityEvidence
				{
					EntityId = 11, CardId = "TIME_606", PlayerId = 1, Zone = "HAND"
				},
				TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				PowerStartWatermark = 100,
				PowerEndWatermark = 120,
				PowerCollectorEpoch = 7,
				PowerActionOrdinal = 1,
				PowerGapCount = 0,
				ActionIdentityStatus = "exact_hdt_power_v1",
				ChoiceStatus = "none"
			};
			AdvisorPendingAction exact;
			Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 7, 2, out exact));
			Assert.IsTrue(exact.HasExactPowerIdentity);
			Assert.AreEqual("exact_entity_id", exact.SourceEntityResolution);
			Assert.AreEqual("not_applicable", exact.TargetEntityResolution);
			Assert.AreEqual(
				AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				exact.BehaviorTargetBindingStatus);

			var tracker = new AdvisorTransitionCandidateTracker();
			Assert.IsTrue(tracker.Register(new AdvisorPendingAction
			{
				PreState = pre,
				Kind = "play_card",
				CardId = "TIME_606",
				SourceEvent = "player_play",
				ObservedAtUtc = DateTime.UtcNow,
				GameGeneration = 7,
				ActionEventSequence = 1
			}));
			Assert.IsFalse(tracker.Register(exact),
				"The exact record must upgrade, not allocate a second event sequence.");
			Assert.AreEqual(1, tracker.PendingCount,
				"Power evidence must upgrade the GameEvents candidate rather than add an action.");

			var post1 = NewState("post", 11);
			post1.StateHash = new string('b', 64);
			Assert.AreEqual(0, tracker.ObserveSnapshot(post1, 7, 20).Count);
			var post2 = NewState("post", 12);
			post2.StateHash = new string('b', 64);
			var observation = tracker.ObserveSnapshot(post2, 7, 20).Single();
			Assert.AreEqual("hdt_power_action_identity_v1",
				observation.Metadata["capture_contract"]);
			Assert.AreEqual("exact_action_identity_unverified_transition_v1",
				observation.Metadata["completeness"]);
			Assert.AreEqual("exact_hdt_power_v1",
				observation.Metadata["action_identity_status"]);
			Assert.AreEqual("false", observation.Metadata["training_eligible"]);
			Assert.AreEqual("1", observation.Metadata["action_sequence"]);
			Assert.AreEqual("1", observation.Metadata["action_event_sequence"]);
			Assert.AreEqual("7", observation.Metadata["power_collector_epoch"]);
			Assert.AreEqual("1", observation.Metadata["power_action_ordinal"]);
			Assert.AreEqual("0", observation.Metadata["power_gap_count"]);
			Assert.AreEqual(3, observation.Action.FrameId);
			Assert.AreEqual("g7:100", observation.Action.PowerStartWatermark);
			var wire = AdvisorWireProtocol.ParseObject(
				AdvisorWireProtocol.SerializeObservation(observation));
			var wireAction = wire["action"] as IDictionary<string, object>;
			Assert.IsNotNull(wireAction);
			Assert.AreEqual(1, Convert.ToInt32(wireAction["option_id"], CultureInfo.InvariantCulture));
			Assert.AreEqual(3, Convert.ToInt32(wireAction["frame_id"], CultureInfo.InvariantCulture));
			Assert.AreEqual(-1, Convert.ToInt32(wireAction["sub_option"], CultureInfo.InvariantCulture));
			Assert.AreEqual(2, Convert.ToInt32(wireAction["board_position"], CultureInfo.InvariantCulture));
			Assert.AreEqual("g7:100", wireAction["power_start_watermark"]);
			Assert.AreEqual("g7:120", wireAction["power_end_watermark"]);
			Assert.AreEqual(0, ((IList)wireAction["choices"]).Count);
			Assert.IsFalse(AdvisorWireProtocol.SerializeObservation(observation).Contains("已脱敏"),
				"No localized entity name or raw PowerLog text may enter the observation wire payload.");
		}

		[TestMethod]
		public void PowerEvidence_PlayAndHeroPowerProveExactTargetOrExplicitNone()
		{
			foreach (var kind in new[] { "play_card", "hero_power" })
			{
				foreach (var withTarget in new[] { true, false })
				{
					var suffix = kind + (withTarget ? "-target" : "-none");
					var pre = NewState("power-" + suffix + "-pre", 1);
					pre.TurnNumber = 4;
					pre.Player.HeroPower = new AdvisorEntityState
					{
						EntityId = 3,
						CardId = "LOCAL_POWER",
						ControllerId = 1,
						Zone = "PLAY",
						CardType = "HERO_POWER"
					};
					if (kind == "play_card")
					{
						pre.Player.Hand.Add(new AdvisorEntityState
						{
							EntityId = 11,
							CardId = "LOCAL_SPELL",
							ControllerId = 1,
							Zone = "HAND",
							CardType = "SPELL"
						});
					}
					var sourceEntityId = kind == "play_card" ? 11 : 3;
					var sourceCardId = kind == "play_card" ? "LOCAL_SPELL" : "LOCAL_POWER";
					var sourceZone = kind == "play_card" ? "HAND" : "PLAY";
					var targetStatus = withTarget
						? AdvisorBehaviorTargetBindingStatus.ExactEntityId
						: AdvisorBehaviorTargetBindingStatus.ExplicitNone;
					var evidence = new HdtPowerActionEvidence
					{
						PowerBlockType = "PLAY",
						FrameId = 5,
						OptionId = 1,
						SubOption = -1,
						BoardPosition = 0,
						Source = new HdtPowerEntityEvidence
						{
							EntityId = sourceEntityId,
							CardId = sourceCardId,
							PlayerId = 1,
							Zone = sourceZone
						},
						Target = withTarget
							? new HdtPowerEntityEvidence
							{
								EntityId = 2,
								CardId = "HERO_2",
								PlayerId = 2,
								Zone = "PLAY"
							}
							: null,
						TargetBindingStatus = targetStatus,
						PowerStartWatermark = 10,
						PowerEndWatermark = 20,
						PowerCollectorEpoch = 7,
						PowerActionOrdinal = 1,
						PowerGapCount = 0,
						ActionIdentityStatus = "exact_hdt_power_v1",
						ChoiceStatus = "none"
					};

					AdvisorPendingAction pending;
					Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
						evidence, pre, 7, 2, out pending), suffix);
					Assert.IsTrue(pending.HasExactPowerIdentity, suffix);
					Assert.AreEqual(targetStatus, pending.BehaviorTargetBindingStatus);
					Assert.AreEqual(withTarget ? (int?)2 : null, pending.TargetEntityId);
					Assert.IsTrue(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
						pre,
						true,
						kind,
						pending.SourceEntityId,
						pending.TargetEntityId,
						pending.BehaviorTargetBindingStatus,
						pending.CardId), suffix);

					var behaviorTracker = new AdvisorBehaviorPendingTracker();
					Assert.IsTrue(behaviorTracker.Register(new AdvisorBehaviorPendingEvidence
					{
						GameGeneration = 7,
						ObservedAtUtc = pending.ObservedAtUtc.AddMilliseconds(-50),
						PreState = pre,
						ActorSide = "local",
						ActorPlayerId = "friendly",
						ActorEvidence = "hdt_player_event",
						IdentityStatus = "unknown",
						VisibilityStatus = "public_pre_state",
						SourceEvent = kind == "play_card" ? "player_play" : "player_hero_power",
						Action = new AdvisorBehaviorAction
						{
							Kind = kind,
							SourceEntityId = sourceEntityId,
							CardId = sourceCardId
						},
						TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.Unknown
					}));
					Assert.IsFalse(behaviorTracker.Register(new AdvisorBehaviorPendingEvidence
					{
						GameGeneration = 7,
						ObservedAtUtc = pending.ObservedAtUtc,
						PreState = pre,
						ActorSide = "local",
						ActorPlayerId = "friendly",
						ActorEvidence = "hdt_power_log",
						IdentityStatus = "exact_public_entity",
						VisibilityStatus = "public_pre_state",
						SourceEvent = "hdt_power_log",
						Action = new AdvisorBehaviorAction
						{
							Kind = kind,
							SourceEntityId = pending.SourceEntityId,
							TargetEntityId = pending.TargetEntityId,
							CardId = pending.CardId
						},
						TargetBindingStatus = pending.BehaviorTargetBindingStatus,
						HasPowerIdentity = true,
						PowerIdentityKey = pending.PowerStartWatermark
					}));

					var firstPost = NewState("power-" + suffix + "-post", 2);
					firstPost.TurnNumber = 4;
					var secondPost = NewState("power-" + suffix + "-post", 3);
					secondPost.TurnNumber = 4;
					Assert.AreEqual(0, behaviorTracker.ObserveSnapshot(firstPost, 7, 25).Count);
					var capture = behaviorTracker.ObserveSnapshot(secondPost, 7, 25).Single();
					Assert.AreEqual("exact_public_entity", capture.IdentityStatus, suffix);
					var behaviorCollector = new AdvisorBehaviorCollector();
					behaviorCollector.BeginGame(pre.GameId);
					AdvisorBehaviorRecord record;
					string rejection;
					Assert.IsTrue(
						behaviorCollector.TryCollect(capture, out record, out rejection),
						suffix + ": " + rejection);
					Assert.IsTrue(record.BehaviorEligible, suffix);
					Assert.IsFalse(record.RlTrainingEligible, suffix);
					Assert.AreEqual(false, record.ToWireValue()["rl_training_eligible"]);
				}
			}
		}

		[TestMethod]
		public void LocationOptionsSendAndPlayBecomeBehaviorWithoutClaimingReplayableTrajectory()
		{
			var stream = new List<string>();
			var powerCollector = NewCollector(stream, 7);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=14"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏地点 id=31 zone=PLAY zonePos=2 cardId=LOCATION_CARD player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=另一个地点名称 id=31 zone=PLAY zonePos=2 cardId=LOCATION_CARD player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END"),
				Line("GameState.DebugPrintOptions() - id=15")
			});
			var evidence = powerCollector.Collect(stream, 7, stream).Single();
			Assert.AreEqual("PLAY", evidence.PowerBlockType);
			Assert.AreEqual(
				AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				evidence.TargetBindingStatus);

			var pre = NewState("location-pre", 1);
			pre.TurnNumber = 5;
			pre.Player.Board.Add(new AdvisorEntityState
			{
				EntityId = 31,
				CardId = "LOCATION_CARD",
				ControllerId = 1,
				Zone = "PLAY",
				CardType = "LOCATION"
			});
			AdvisorPendingAction pending;
			Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 7, 1, out pending));
			Assert.AreEqual("play_card", pending.Kind,
				"Strict trajectory stays on the canonical four-action surface.");
			Assert.AreEqual("location_activate", pending.BehaviorKind);
			Assert.AreEqual("unsupported_location_activation", pending.SimulatorStatus);
			Assert.AreEqual("exact_hdt_power_v1", pending.ActionIdentityStatus);
			Assert.IsTrue(pending.HasExactPowerIdentity,
				"Behavior evidence keeps exact local Power identity.");
			Assert.IsFalse(pending.HasStrictTrajectoryPowerIdentity,
				"The unsupported simulator action cannot claim strict trajectory identity.");
			Assert.IsTrue(MetaCompanionPlugin.HasExactPublicBehaviorBinding(
				pre,
				true,
				pending.BehaviorKind,
				pending.SourceEntityId,
				pending.TargetEntityId,
				pending.BehaviorTargetBindingStatus,
				pending.CardId));

			var transitionTracker = new AdvisorTransitionCandidateTracker();
			Assert.IsTrue(transitionTracker.Register(pending));
			var transitionPostOne = NewState("location-transition-post", 2);
			transitionPostOne.TurnNumber = 5;
			transitionPostOne.StateHash = new string('b', 64);
			var transitionPostTwo = NewState("location-transition-post", 3);
			transitionPostTwo.TurnNumber = 5;
			transitionPostTwo.StateHash = new string('b', 64);
			Assert.AreEqual(0, transitionTracker.ObserveSnapshot(
				transitionPostOne, 7, 17).Count);
			var strictObservation = transitionTracker.ObserveSnapshot(
				transitionPostTwo, 7, 17).Single();
			Assert.AreEqual("partial_hdt_transition_candidate_v1",
				strictObservation.Metadata["capture_contract"]);
			Assert.AreEqual("partial_hdt_gameevents_v1",
				strictObservation.Metadata["completeness"]);
			Assert.AreEqual("unsupported_location_activation",
				strictObservation.Metadata["action_identity_status"]);
			Assert.AreEqual("unsupported_location_activation",
				strictObservation.Metadata["simulator_status"]);
			Assert.AreEqual("false", strictObservation.Metadata["training_eligible"]);

			var behaviorTracker = new AdvisorBehaviorPendingTracker();
			Assert.IsTrue(behaviorTracker.Register(new AdvisorBehaviorPendingEvidence
			{
				GameGeneration = 7,
				ObservedAtUtc = pending.ObservedAtUtc,
				PreState = pre,
				ActorSide = "local",
				ActorPlayerId = "friendly",
				ActorEvidence = "hdt_power_log",
				IdentityStatus = "exact_public_entity",
				VisibilityStatus = "public_pre_state",
				SourceEvent = "hdt_power_log",
				Action = new AdvisorBehaviorAction
				{
					Kind = pending.BehaviorKind,
					SourceEntityId = pending.SourceEntityId,
					TargetEntityId = pending.TargetEntityId,
					CardId = pending.CardId
				},
				TargetBindingStatus = pending.BehaviorTargetBindingStatus,
				HasPowerIdentity = true,
				PowerIdentityKey = pending.PowerStartWatermark
			}));
			var postOne = NewState("location-behavior-post", 2);
			postOne.TurnNumber = 5;
			var postTwo = NewState("location-behavior-post", 3);
			postTwo.TurnNumber = 5;
			Assert.AreEqual(0, behaviorTracker.ObserveSnapshot(postOne, 7, 18).Count);
			var capture = behaviorTracker.ObserveSnapshot(postTwo, 7, 18).Single();
			Assert.AreEqual("location_activate", capture.Action.Kind);
			Assert.AreEqual("exact_public_entity", capture.IdentityStatus);
			Assert.AreEqual("isolated", capture.BoundaryStatus);

			var behaviorCollector = new AdvisorBehaviorCollector();
			behaviorCollector.BeginGame(pre.GameId);
			AdvisorBehaviorRecord record;
			string rejection;
			Assert.IsTrue(
				behaviorCollector.TryCollect(capture, out record, out rejection),
				rejection);
			Assert.AreEqual("location_activate", record.Action.Kind);
			Assert.IsTrue(record.BehaviorEligible);
			Assert.IsFalse(record.RlTrainingEligible);
		}

		[TestMethod]
		public void PowerEvidence_RejectsWrongControllerAndChoiceTierStaysNonExact()
		{
			var pre = NewState("pre", 1);
			pre.Player.Hand.Add(new AdvisorEntityState
			{
				EntityId = 11, CardId = "CARD", ControllerId = 1, Zone = "HAND"
			});
			var evidence = new HdtPowerActionEvidence
			{
				PowerBlockType = "PLAY", FrameId = 1, OptionId = 1, SubOption = -1,
				Source = new HdtPowerEntityEvidence
				{
					EntityId = 11, CardId = "CARD", PlayerId = 2, Zone = "HAND"
				},
				TargetBindingStatus = AdvisorBehaviorTargetBindingStatus.ExplicitNone,
				PowerStartWatermark = 1, PowerEndWatermark = 2,
				PowerCollectorEpoch = 1, PowerActionOrdinal = 1, PowerGapCount = 0,
				ActionIdentityStatus = "exact_hdt_power_v1", ChoiceStatus = "none"
			};
			AdvisorPendingAction pending;
			Assert.IsFalse(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 1, 1, out pending));

			evidence.Source.PlayerId = 1;
			evidence.SubOption = 1;
			evidence.ActionIdentityStatus = "choice_unresolved";
			evidence.ChoiceStatus = "unresolved";
			Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 1, 1, out pending));
			Assert.IsFalse(pending.HasExactPowerIdentity);
			Assert.IsFalse(pending.HasExactBehaviorPowerIdentity);

			evidence.ActionIdentityStatus = "exact_hdt_power_choice_v1";
			evidence.ChoiceStatus = "selected";
			evidence.Choices.Add(new HdtPowerChoiceEvidence
			{
				ChoiceType = "SUB_OPTION",
				SourceEntityId = 11,
				OptionEntityIds = new List<int> { 31, 32 },
				EntityIds = new List<int> { 32 },
				Status = "selected"
			});
			Assert.IsTrue(MetaCompanionPlugin.TryCreatePowerPendingAction(
				evidence, pre, 1, 1, out pending));
			Assert.IsFalse(pending.HasExactPowerIdentity,
				"Selected branches are still outside the strict replayable action tier.");
			Assert.IsTrue(pending.HasExactBehaviorPowerIdentity,
				"Fully offered and selected branches are exact behavior evidence.");
			Assert.AreEqual("selected", pending.ChoiceStatus);
			CollectionAssert.AreEqual(
				new[] { 31, 32 }, pending.Choices[0].OptionEntityIds);
		}

		[TestMethod]
		public void Collector_TraceSummaryRequiresEveryCommittedOrdinalToBeRecorded()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 5);
			AddEndTurn(stream, 12);

			var evidence = collector.Collect(stream, 5, stream).Single();
			Assert.AreEqual(5, evidence.PowerCollectorEpoch);
			Assert.AreEqual(1, evidence.PowerActionOrdinal);
			Assert.AreEqual(0, evidence.PowerGapCount);
			var pending = collector.GetTraceSummary();
			Assert.AreEqual(1, pending.CommittedActionCount);
			Assert.AreEqual(0, pending.RecordedActionCount);
			Assert.AreEqual(1, pending.GapCount);
			Assert.AreEqual("tainted", pending.TraceStatus);

			collector.MarkActionRecorded(evidence.PowerActionOrdinal);
			var complete = collector.GetTraceSummary();
			Assert.AreEqual(5, complete.GameGeneration);
			Assert.AreEqual(5, complete.PowerCollectorEpoch);
			Assert.AreEqual(1, complete.CommittedActionCount);
			Assert.AreEqual(1, complete.RecordedActionCount);
			Assert.AreEqual(0, complete.GapCount);
			Assert.AreEqual("complete", complete.TraceStatus);
			var terminal = MetaCompanionPlugin.BuildAdvisorResultMetadata(
				"game_won", NewState("terminal", 2), complete);
			Assert.AreEqual("5", terminal["game_generation"]);
			Assert.AreEqual("5", terminal["power_collector_epoch"]);
			Assert.AreEqual("1", terminal["power_committed_action_count"]);
			Assert.AreEqual("1", terminal["power_recorded_action_count"]);
			Assert.AreEqual("0", terminal["power_gap_count"]);
			Assert.AreEqual("complete", terminal["power_trace_status"]);
		}

		[TestMethod]
		public void Collector_RewindPermanentlyTaintsLaterRecordedActions()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 6);
			stream.Add(Line("GameState.DebugPrintOptions() - id=1"));
			collector.Collect(stream, 6, stream);
			stream[0] = Line("GameState.DebugPrintOptions() - id=999");
			collector.Collect(stream, 6, stream);
			AddEndTurn(stream, 2);

			var evidence = collector.Collect(stream, 6, stream).Single();
			Assert.AreEqual(1, evidence.PowerActionOrdinal);
			Assert.IsTrue(evidence.PowerGapCount > 0);
			collector.MarkActionRecorded(evidence.PowerActionOrdinal);
			var summary = collector.GetTraceSummary();
			Assert.AreEqual(1, summary.CommittedActionCount);
			Assert.AreEqual(1, summary.RecordedActionCount);
			Assert.IsTrue(summary.GapCount > 0);
			Assert.AreEqual("tainted", summary.TraceStatus);
		}

		[TestMethod]
		public void Collector_SendOptionWithoutMatchingRootIsNotACommittedGap()
		{
			var stream = new List<string>();
			var collector = NewCollector(stream, 8);
			stream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=1"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=11 zone=HAND zonePos=1 cardId=CARD player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=1")
			});
			collector.Collect(stream, 8, stream);
			AddEndTurn(stream, 2);

			var evidence = collector.Collect(stream, 8, stream).Single();
			Assert.AreEqual("MAIN_END", evidence.PowerBlockType);
			Assert.AreEqual(1, evidence.PowerActionOrdinal);
			Assert.AreEqual(0, evidence.PowerGapCount);
		}

		[TestMethod]
		public void Collector_TerminalFinalizationDowngradesClosedRootAndRejectsOpenRoot()
		{
			var closedStream = new List<string>();
			var closedCollector = NewCollector(closedStream, 9);
			closedStream.AddRange(new[]
			{
				Line("GameState.DebugPrintOptions() - id=1"),
				Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
				Line("GameState.DebugPrintOptions() -   option 1 type=POWER mainEntity=[entityName=脱敏 id=11 zone=HAND zonePos=1 cardId=CARD player=1] error=NONE errorParam="),
				Line("GameState.SendOption() - selectedOption=1 selectedSubOption=-1 selectedTarget=0 selectedPosition=1"),
				Line("GameState.DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=脱敏 id=11 zone=HAND zonePos=1 cardId=CARD player=1] EffectCardId=x EffectIndex=0 Target=0 SubOption=-1 "),
				Line("GameState.DebugPrintPower() - BLOCK_END")
			});
			Assert.AreEqual(0, closedCollector.Collect(
				closedStream, 9, closedStream).Count);
			var downgraded = closedCollector.FinalizeAtTerminal().Single();
			Assert.AreEqual("choice_unresolved", downgraded.ActionIdentityStatus);
			Assert.AreEqual("unresolved", downgraded.ChoiceStatus);

			var openStream = new List<string>();
			var openCollector = NewCollector(openStream, 10);
			openStream.AddRange(closedStream.Take(closedStream.Count - 1));
			openCollector.Collect(openStream, 10, openStream);
			Assert.AreEqual(0, openCollector.FinalizeAtTerminal().Count);
			var rejected = openCollector.GetTraceSummary();
			Assert.AreEqual(1, rejected.CommittedActionCount);
			Assert.AreEqual(0, rejected.RecordedActionCount);
			Assert.IsTrue(rejected.GapCount > 0);
			Assert.AreEqual("tainted", rejected.TraceStatus);
		}

		private static HdtPowerTraceCollector NewCollector(List<string> stream, long generation)
		{
			var collector = new HdtPowerTraceCollector();
			collector.BeginGeneration(generation, stream, stream.Count);
			return collector;
		}

		private static HdtPowerOptionsFrameEvidence NewRootFrame(
			params HdtPowerOptionEvidence[] options)
		{
			var result = new HdtPowerOptionsFrameEvidence
			{
				CollectorEpoch = 7,
				FrameId = 42,
				HeaderWatermark = 10,
				Options = new List<HdtPowerOptionEvidence>
				{
					new HdtPowerOptionEvidence
					{
						OptionId = 0,
						Type = "END_TURN",
						Error = "INVALID"
					}
				}
			};
			result.Options.AddRange(options ?? new HdtPowerOptionEvidence[0]);
			return result;
		}

		private static HdtPowerEntityEvidence RootEntity(
			int entityId, string zone, string cardId, int playerId)
		{
			return new HdtPowerEntityEvidence
			{
				EntityId = entityId,
				Zone = zone,
				CardId = cardId,
				PlayerId = playerId
			};
		}

		private static string Line(string payload)
		{
			return Prefix + payload;
		}

		private static void AddEndTurn(List<string> stream, int frameId)
		{
			stream.Add(Line("GameState.DebugPrintOptions() - id=" +
				frameId.ToString(CultureInfo.InvariantCulture)));
			stream.Add(Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="));
			stream.Add(Line("GameState.SendOption() - selectedOption=0 selectedSubOption=-1 selectedTarget=0 selectedPosition=0"));
			stream.Add(Line("GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END "));
		}

		private static void AddAttack(
			List<string> stream,
			int frameId,
			int optionId,
			int sourceId,
			int sentTargetId,
			int sourcePlayer,
			int targetPlayer,
			int rootTargetId)
		{
			stream.Add(Line("GameState.DebugPrintOptions() - id=" + frameId));
			stream.Add(Line("GameState.DebugPrintOptions() -   option 0 type=END_TURN mainEntity= error=INVALID errorParam="));
			stream.Add(Line("GameState.DebugPrintOptions() -   option " + optionId +
				" type=POWER mainEntity=[entityName=脱敏 id=" + sourceId +
				" zone=PLAY zonePos=1 cardId=ATTACKER player=" + sourcePlayer +
				"] error=NONE errorParam="));
			stream.Add(Line("GameState.DebugPrintOptions() -     target 0 entity=[entityName=脱敏 id=" +
				sentTargetId + " zone=PLAY zonePos=0 cardId=TARGET player=" + targetPlayer +
				"] error=NONE errorParam="));
			stream.Add(Line("GameState.SendOption() - selectedOption=" + optionId +
				" selectedSubOption=-1 selectedTarget=" + sentTargetId + " selectedPosition=0"));
			stream.Add(Line("GameState.DebugPrintPower() - BLOCK_START BlockType=ATTACK Entity=[entityName=脱敏 id=" +
				sourceId + " zone=PLAY zonePos=1 cardId=ATTACKER player=" + sourcePlayer +
				"] EffectCardId=x EffectIndex=0 Target=[entityName=脱敏 id=" + rootTargetId +
				" zone=PLAY zonePos=0 cardId=TARGET player=" + targetPlayer + "] SubOption=-1 "));
			stream.Add(Line("GameState.DebugPrintPower() - BLOCK_END"));
			stream.Add(Line("GameState.DebugPrintOptions() - id=" + (frameId + 1)));
		}

		private static AdvisorGameState NewState(string stateId, long sequence)
		{
			return new AdvisorGameState
			{
				StateId = stateId,
				StateHash = new string('a', 64),
				SnapshotSequence = sequence,
				GameId = "game-test",
				IsLocalPlayerTurn = true,
				IsRunning = true,
				IsMulliganDone = true,
				Player = new AdvisorPlayerState
				{
					PlayerId = 1,
					IsLocalPlayer = true,
					Hero = new AdvisorEntityState
					{
						EntityId = 1, CardId = "HERO_1", ControllerId = 1,
						Zone = "PLAY", CardType = "HERO"
					}
				},
				Opponent = new AdvisorPlayerState
				{
					PlayerId = 2,
					Hero = new AdvisorEntityState
					{
						EntityId = 2, CardId = "HERO_2", ControllerId = 2,
						Zone = "PLAY", CardType = "HERO"
					}
				},
				Phase = new AdvisorGamePhaseState { CanLocalPlayerAct = true }
			};
		}
	}
}
