using MetaCompanion;
using MetaCompanionTests.Mocks;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PredictionControllerTest
	{
		private List<Deck> _metaDecks = new List<Deck>();

		private void AddMetaDeck(string className, List<string> cardNames = null, List<int> counts = null)
		{
			var deck = new Deck();
			deck.Class = className;
			CardList(cardNames, counts).ForEach(card => deck.Cards.Add(card));
			_metaDecks.Add(deck);
		}

		private List<Card> CardList(List<string> cardNames, List<int> counts = null)
		{
			if (cardNames == null)
			{
				return new List<Card>();
			}
			if (counts == null)
			{
				counts = Enumerable.Repeat(1, cardNames.Count).ToList();
			}
			return cardNames.Zip(counts, (cardName, count) =>
				{
					var card = Database.GetCardFromName(cardName);
					card.Count = count;
					return card;
				})
				.OrderBy(card => card.Cost).ThenBy(card => card.Name)
				.ToList();
		}

		private List<Card> CardsFromInfo(PredictionInfo info)
		{
			return info.PredictedCards.Select(i => i.Card).ToList();
		}

		private PredictionInfo GetPredictionInfo(PredictionController controller)
		{
			PredictionInfo info = null;
			controller.OnPredictionUpdate.Add(p => info = p);
			controller.UpdatePrediction();
			return info;
		}

		[TestMethod]
		public void OnOpponentDraw_CallsOnPredictionUpdate()
		{
			var opponent = new MockOpponent("Hunter");
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			bool called = false;
			controller.OnPredictionUpdate.Add(prediction => called = true);
			controller.OnOpponentDraw();

			Assert.IsTrue(called);
		}

		[TestMethod]
		public void OnOpponentDraw_SecondTimeDoesNotCallOnPredictionUpdate()
		{
			var opponent = new MockOpponent("Hunter");
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			controller.OnOpponentDraw();

			bool called = false;
			controller.OnPredictionUpdate.Add(prediction => called = true);
			controller.OnOpponentDraw();

			Assert.IsFalse(called);
		}

		[TestMethod]
		public void OnOpponentHandDiscard_CallsOnPredictionUpdate()
		{
			var opponent = new MockOpponent("Hunter");
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			bool called = false;
			controller.OnPredictionUpdate.Add(prediction => called = true);
			controller.OnOpponentHandDiscard(null);

			Assert.IsTrue(called);
		}

		[TestMethod]
		public void OnOpponentPlay_CallsOnPredictionUpdate()
		{
			var opponent = new MockOpponent("Hunter");
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			bool called = false;
			controller.OnPredictionUpdate.Add(prediction => called = true);
			controller.OnOpponentPlay(null);

			Assert.IsTrue(called);
		}

		[TestMethod]
		public void UpdatesWithMetaDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(1, info.NumPossibleDecks);
		}

		[TestMethod]
		public void UpdatesWithCandidateArchetypeName()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			_metaDecks[0].Name = "Quest Hunter";
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			CollectionAssert.AreEqual(new List<string> {"Quest Hunter"}, info.CandidateDeckNames);
		}

		[TestMethod]
		public void CandidateArchetypeName_PrefersEvidenceMatchOverBranchCount()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			_metaDecks[0].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Bear Trap"});
			_metaDecks[1].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			_metaDecks[2].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Tracking"});
			_metaDecks[3].Name = "Matching Hunter";
			opponent.KnownCards.Add(Database.GetCardFromName("Tracking"));
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);

			Assert.AreEqual("Matching Hunter", info.CandidateDeckNames[0]);
		}

		[TestMethod]
		public void CandidateArchetypeConfidence_PrefersEvidenceMatch()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			_metaDecks[0].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Tracking"});
			_metaDecks[1].Name = "Matching Hunter";
			var tracking = Database.GetCardFromName("Tracking");
			var knownCards = new Dictionary<string, int> {{tracking.Id, 1}};

			var candidates = PredictionController.BuildCandidateArchetypes(
				_metaDecks, knownCards, 1);

			Assert.AreEqual("Matching Hunter", candidates[0].Name);
			Assert.IsTrue(candidates[0].ConfidencePercent > candidates[1].ConfidencePercent);
			Assert.IsTrue(candidates[0].ConfidencePercent > 0);
		}

		[TestMethod]
		public void CandidateArchetype_UsesFullClassEvidenceAfterSequentialFilterNarrowsTooFar()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Animal Companion"});
			_metaDecks[0].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Animal Companion", "Deadly Shot"});
			_metaDecks[1].Name = "Popular Hunter";
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Flare", "Arcane Shot"});
			_metaDecks[2].Name = "Matching Hunter";
			opponent.KnownCards = CardList(new List<string>
			{
				"Tracking",
				"Animal Companion",
				"Flare",
				"Arcane Shot"
			});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			controller.OnTurnStart(ActivePlayer.Player);
			var info = GetPredictionInfo(controller);

			Assert.AreEqual("Matching Hunter", info.CandidateDeckNames[0]);
			Assert.AreEqual("Matching Hunter", info.ClosestDeckName);
		}

		[TestMethod]
		public void CandidateArchetype_UsesHdtKnownAndRevealedEvidence()
		{
			var opponent = new MockOpponent("Priest");
			AddMetaDeck("Priest", new List<string> {"Ysera, Emerald Aspect", "Holy Nova"});
			_metaDecks[0].Name = "Ysera Priest";
			AddMetaDeck("Priest", new List<string> {"Holy Nova", "Power Word: Shield"});
			_metaDecks[1].Name = "Shield Priest";
			opponent.RevealedCards = CardList(new List<string> {"Ysera, Emerald Aspect"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);

			Assert.AreEqual(1, info.EvidenceCards);
			Assert.AreEqual("Ysera Priest", info.CandidateDeckNames[0]);
			Assert.AreEqual("Ysera, Emerald Aspect", info.KeyEvidenceCards[0].Name);
		}

		[TestMethod]
		public void EvidenceCards_ZeroBeforeOpponentRevealsDeckCard()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(0, info.EvidenceCards);
		}

		[TestMethod]
		public void EvidenceCards_CountsOriginalOpponentCards()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(1, info.EvidenceCards);
		}

		[TestMethod]
		public void RemainingDeckCards_ComesFromOpponentState()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.RemainingDeckCards = 12;
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);

			Assert.AreEqual(12, info.RemainingDeckCards);
		}

		[TestMethod]
		public void ClosestDeckRemainingCards_UsesBestMatchingBranch()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat", "Bear Trap"});
			_metaDecks[0].Name = "Control Hunter";
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Tracking", "Animal Companion"});
			_metaDecks[1].Name = "Spell Hunter";
			opponent.KnownCards.Add(Database.GetCardFromName("Tracking"));
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);

			Assert.AreEqual("Spell Hunter", info.ClosestDeckName);
			CollectionAssert.AreEqual(
				new List<string> {"Animal Companion", "Deadly Shot"},
				info.ClosestDeckRemainingCards.Select(cardInfo => cardInfo.Card.Name).ToList());
		}

		[TestMethod]
		public void ClosestDeckRemainingCards_GroupsDuplicateDeckEntriesBeforeSubtractingKnownCards()
		{
			var tracking = Database.GetCardFromName("Tracking");
			var deck = new Deck();
			foreach (var card in CardList(
				new List<string> {"Tracking", "Tracking"},
				new List<int> {1, 1}))
			{
				deck.Cards.Add(card);
			}
			var knownCards = new Dictionary<string, int> {{tracking.Id, 1}};

			var remaining = PredictionController.BuildClosestDeckRemainingCards(deck, knownCards);

			Assert.AreEqual(1, remaining.Count);
			Assert.AreEqual("Tracking", remaining[0].Card.Name);
			Assert.AreEqual(1, remaining[0].Card.Count);
		}

		[TestMethod]
		public void ClosestDeckRemainingCards_SubtractsEventObservedCards()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter",
				new List<string> {"Tracking", "Deadly Shot"},
				new List<int> {2, 1});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			controller.OnOpponentPlay(Database.GetCardFromName("Tracking"));

			var info = GetPredictionInfo(controller);
			var tracking = info.ClosestDeckRemainingCards.First(cardInfo =>
				cardInfo.Card.Name == "Tracking");
			Assert.AreEqual(1, tracking.Card.Count);
		}

		[TestMethod]
		public void ClosestDeckRemainingCards_CapsDeckCopiesAtConstructedLimit()
		{
			var tracking = Database.GetCardFromName("Tracking");
			var deck = new Deck();
			foreach (var card in CardList(
				new List<string> {"Tracking", "Tracking", "Tracking"},
				new List<int> {1, 1, 1}))
			{
				deck.Cards.Add(card);
			}

			var remaining = PredictionController.BuildClosestDeckRemainingCards(
				deck,
				new Dictionary<string, int>());

			Assert.AreEqual(1, remaining.Count);
			Assert.AreEqual(tracking.Id, remaining[0].Card.Id);
			Assert.AreEqual(2, remaining[0].Card.Count);
		}

		[TestMethod]
		public void ClosestDeckRemainingCards_DoesNotShowSeenYseraAsRemaining()
		{
			var opponent = new MockOpponent("Priest");
			AddMetaDeck("Priest", new List<string> {"Ysera, Emerald Aspect", "Holy Nova"});
			_metaDecks[0].Name = "Ysera Priest";
			opponent.KnownCardsInDeck = CardList(new List<string> {"Ysera, Emerald Aspect"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);

			CollectionAssert.DoesNotContain(
				info.ClosestDeckRemainingCards.Select(cardInfo => cardInfo.Card.Name).ToList(),
				"Ysera, Emerald Aspect");
			Assert.AreEqual("Ysera, Emerald Aspect", info.KeyEvidenceCards[0].Name);
		}

		[TestMethod]
		public void KnownOriginalCards_CapsDuplicatesAcrossAllHdtSources()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.KnownCards = CardList(new List<string> {"King Krush", "Deadly Shot"});
			opponent.KnownCardsInDeck = CardList(new List<string> {"King Krush", "Deadly Shot"});
			opponent.RevealedCards = CardList(new List<string> {"King Krush", "Deadly Shot"});

			var counts = OpponentEvidence.CountOriginalCards(opponent);

			Assert.AreEqual(1, counts[Database.GetCardFromName("King Krush").Id]);
			Assert.AreEqual(2, counts[Database.GetCardFromName("Deadly Shot").Id]);
		}

		[TestMethod]
		public void VisiblePredictedCards_ExcludesFullyObservedCards()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(1, info.NumVisiblePredictedCards);
		}

		[TestMethod]
		public void VisiblePredictedCards_KeepsUnobservedCopies()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter",
				new List<string> {"Deadly Shot", "Alleycat"},
				new List<int> {2, 1});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(2, info.NumVisiblePredictedCards);
		}

		[TestMethod]
		public void VisiblePredictedCards_CapsDuplicatedKnownCardsFromReplay()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter",
				new List<string> {"Deadly Shot", "Alleycat"},
				new List<int> {2, 1});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {
				"Deadly Shot",
				"Deadly Shot",
				"Deadly Shot",
				"Deadly Shot"
			});

			var info = GetPredictionInfo(controller);
			var deadlyShotInfo = info.PredictedCards.First(cardInfo =>
				cardInfo.Card.Name == "Deadly Shot");
			Assert.AreEqual(2, deadlyShotInfo.Card.Count);
			Assert.AreEqual(2, deadlyShotInfo.NumPlayed);
			Assert.AreEqual(1, info.NumVisiblePredictedCards);
		}

		[TestMethod]
		public void VisiblePredictedCards_MergesJoustedAndPlayedCopiesOfSameCard()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter",
				new List<string> {"Deadly Shot", "Alleycat"},
				new List<int> {2, 1});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Deadly Shot", "Deadly Shot"});
			opponent.KnownCards[0].Jousted = true;

			var info = GetPredictionInfo(controller);
			var deadlyShotInfos = info.PredictedCards
				.Where(cardInfo => cardInfo.Card.Name == "Deadly Shot" &&
					!cardInfo.Card.IsCreated)
				.ToList();

			Assert.AreEqual(1, deadlyShotInfos.Count);
			Assert.AreEqual(2, deadlyShotInfos[0].Card.Count);
			Assert.AreEqual(1, deadlyShotInfos[0].NumPlayed);
			Assert.AreEqual(2, info.NumVisiblePredictedCards);
		}

		[TestMethod]
		public void UpdatesWithFullCardList()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat", "Bear Trap"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			CollectionAssert.AreEqual(CardList(new List<string> {"Deadly Shot", "Alleycat", "Bear Trap"}),
				CardsFromInfo(info));
		}

		[TestMethod]
		public void UpdatesWithNoDeckIfClassMismatch()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Mage", new List<string> {});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(0, info.NumPossibleDecks);
		}

		[TestMethod]
		public void UpdatesWithMultipleCopies()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter",
				new List<string> {"Alleycat", "Deadly Shot", "Bear Trap"},
				new List<int> {1, 2, 1});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());

			var info = GetPredictionInfo(controller);
			var expectedCardList = CardList(
				new List<string> {"Alleycat", "Deadly Shot", "Bear Trap"},
				new List<int> {1, 2, 1});
			CollectionAssert.AreEqual(expectedCardList, CardsFromInfo(info));
		}

		[TestMethod]
		public void MarksCardsAlreadyPlayed()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(1, info.PredictedCards[0].NumPlayed);
		}

		[TestMethod]
		public void AddsInCardsNotInMetaDecks()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			var info = GetPredictionInfo(controller);
			CollectionAssert.AreEqual(CardList(new List<string> {"Alleycat"}), CardsFromInfo(info));
			Assert.AreEqual(1, info.PredictedCards[0].NumPlayed);
		}

		[TestMethod]
		public void SeparateEntriesForCreatedCards()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			opponent.KnownCards[0].IsCreated = true;
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(2, info.PredictedCards.Count);
			var originalCardInfo = info.PredictedCards[0];
			Assert.AreEqual(0, originalCardInfo.NumPlayed);
			Assert.AreEqual(1, originalCardInfo.Card.Count);
			Assert.IsFalse(originalCardInfo.Card.IsCreated);
			var createdCardInfo = info.PredictedCards[1];
			Assert.AreEqual(1, createdCardInfo.NumPlayed);
			Assert.AreEqual(1, createdCardInfo.Card.Count);
			Assert.IsTrue(createdCardInfo.Card.IsCreated);
		}

		[TestMethod]
		public void SortEntriesByManaCost()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Bear Trap"});
			opponent.KnownCards[0].IsCreated = true;
			var info = GetPredictionInfo(controller);
			Assert.AreEqual("Alleycat", info.PredictedCards[0].Card.Name);
		}

		[TestMethod]
		public void SortEntriesByCreated()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Bear Trap"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards = CardList(new List<string> {"Bear Trap", "Bear Trap"});
			opponent.KnownCards[0].IsCreated = true;
			var info = GetPredictionInfo(controller);
			Assert.IsFalse(info.PredictedCards[0].Card.IsCreated);
		}

		[TestMethod]
		public void NativePredictionCards_EmptyBeforeEvidence()
		{
			var alleycat = Database.GetCardFromName("Alleycat");
			alleycat.Count = 1;
			var info = new PredictionInfo(
				1, 1, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(alleycat, new List<decimal> {1m}, 0)
				},
				new List<PredictionInfo.CardInfo>());

			var cards = PredictionView.BuildNativePredictionCards(info);

			Assert.AreEqual(0, cards.Count);
		}

		[TestMethod]
		public void NativeHdtOpponentPredictions_EnabledByDefault()
		{
			Assert.IsTrue(PredictionView.ShouldUseNativeOpponentPredictions(new PluginConfig()));
			Assert.IsFalse(PredictionView.ShouldUseNativeOpponentPredictions(new PluginConfig
			{
				EnableNativeHdtOpponentPredictions = false
			}));
		}

		[TestMethod]
		public void NativePredictionCards_MergesDuplicateEntriesAndSortsByManaCost()
		{
			var deadlyShot = Database.GetCardFromName("Deadly Shot");
			deadlyShot.Count = 2;
			var duplicateDeadlyShot = Database.GetCardFromName("Deadly Shot");
			duplicateDeadlyShot.Count = 1;
			var bearTrap = Database.GetCardFromName("Bear Trap");
			bearTrap.Count = 1;
			var alleycat = Database.GetCardFromName("Alleycat");
			alleycat.Count = 1;
			var info = new PredictionInfo(
				1, 4, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(deadlyShot, new List<decimal> {1m, 1m}, 1),
					new PredictionInfo.CardInfo(duplicateDeadlyShot, new List<decimal>(), 0),
					new PredictionInfo.CardInfo(bearTrap, new List<decimal> {1m}, 0),
					new PredictionInfo.CardInfo(alleycat, new List<decimal> {1m}, 0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 1);

			var cards = PredictionView.BuildNativePredictionCards(info);

			CollectionAssert.AreEqual(
				new List<string> {"Alleycat", "Bear Trap", "Deadly Shot"},
				cards.Select(card => card.Name).ToList());
		}

		[TestMethod]
		public void NativePredictionCards_LimitsToRemainingDeckCardsByProbabilityThenSortsByManaCost()
		{
			var alleycat = Database.GetCardFromName("Alleycat");
			alleycat.Count = 1;
			var bearTrap = Database.GetCardFromName("Bear Trap");
			bearTrap.Count = 1;
			var deadlyShot = Database.GetCardFromName("Deadly Shot");
			deadlyShot.Count = 1;
			var info = new PredictionInfo(
				1, 3, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(alleycat, new List<decimal> {.10m}, 0),
					new PredictionInfo.CardInfo(deadlyShot, new List<decimal> {.90m}, 0),
					new PredictionInfo.CardInfo(bearTrap, new List<decimal> {.80m}, 0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 1,
				remainingDeckCards: 2);

			var cards = PredictionView.BuildNativePredictionCards(info);

			CollectionAssert.AreEqual(
				new List<string> {"Bear Trap", "Deadly Shot"},
				cards.Select(card => card.Name).ToList());
		}

		[TestMethod]
		public void NativePredictionCards_SubtractsHdtKnownCopiesFromNativePredictionLimit()
		{
			var deadlyShot = Database.GetCardFromName("Deadly Shot");
			deadlyShot.Count = 2;
			var info = new PredictionInfo(
				1, 2, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(deadlyShot, new List<decimal> {1m, 1m}, 0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 1);

			var cards = PredictionView.BuildNativePredictionCards(
				info,
				new Dictionary<string, int> {{deadlyShot.Id, 1}});

			Assert.AreEqual(1, cards.Count);
			Assert.AreEqual("Deadly Shot", cards[0].Name);
		}

		[TestMethod]
		public void NativePredictionCards_SubtractsKnownCopiesAcrossNativeSources()
		{
			var deadlyShot = Database.GetCardFromName("Deadly Shot");
			deadlyShot.Count = 2;
			var knownPlayedCopy = Database.GetCardFromName("Deadly Shot");
			knownPlayedCopy.Count = 1;
			var knownRevealedCopy = Database.GetCardFromName("Deadly Shot");
			knownRevealedCopy.Count = 1;
			var info = new PredictionInfo(
				1, 2, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(deadlyShot, new List<decimal> {1m, 1m}, 0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 1);

			var knownCounts = PredictionView.GetKnownOriginalCardCounts(
				new[] {knownPlayedCopy},
				new[] {knownRevealedCopy});
			var cards = PredictionView.BuildNativePredictionCards(info, knownCounts);

			Assert.AreEqual(2, knownCounts[deadlyShot.Id]);
			Assert.AreEqual(0, cards.Count);
		}

		[TestMethod]
		public void NativePredictionCards_EmptyWhenNoCardsRemainInDeck()
		{
			var alleycat = Database.GetCardFromName("Alleycat");
			alleycat.Count = 1;
			var info = new PredictionInfo(
				1, 1, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(alleycat, new List<decimal> {1m}, 0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 1,
				remainingDeckCards: 0);

			var cards = PredictionView.BuildNativePredictionCards(info);

			Assert.AreEqual(0, cards.Count);
		}

		[TestMethod]
		public void ShouldUseLateGamePanel_DoesNotUseEvidenceAlone()
		{
			var info = new PredictionInfo(
				1, 20, 1, 1,
				new List<PredictionInfo.CardInfo>(),
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 10);

			Assert.IsFalse(PredictionView.ShouldUseLateGamePanel(info, new PluginConfig()));
		}

		[TestMethod]
		public void ShouldUseLateGamePanel_WhenDeckIsLow()
		{
			var info = new PredictionInfo(
				1, 20, 1, 1,
				new List<PredictionInfo.CardInfo>(),
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 8,
				remainingDeckCards: 15);

			Assert.IsTrue(PredictionView.ShouldUseLateGamePanel(info, new PluginConfig()));
		}

		[TestMethod]
		public void ShouldUseLateGamePanel_DoesNotUseRemainingDeckAloneWithLittleEvidence()
		{
			var info = new PredictionInfo(
				1, 20, 1, 1,
				new List<PredictionInfo.CardInfo>(),
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 7,
				remainingDeckCards: 15);

			Assert.IsFalse(PredictionView.ShouldUseLateGamePanel(info, new PluginConfig()));
		}

		[TestMethod]
		public void ShouldUseLateGamePanel_DoesNotUseTallPredictionListAlone()
		{
			var alleycat = Database.GetCardFromName("Alleycat");
			alleycat.Count = 18;
			var info = new PredictionInfo(
				1, 20, 1, 1,
				new List<PredictionInfo.CardInfo>
				{
					new PredictionInfo.CardInfo(
						alleycat,
						Enumerable.Repeat(.5m, 18).ToList(),
						0)
				},
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 6);

			Assert.IsFalse(PredictionView.ShouldUseLateGamePanel(info, new PluginConfig()));
		}

		[TestMethod]
		public void ShouldUseLateGamePanel_DisabledByConfig()
		{
			var config = new PluginConfig { EnableLateGamePanel = false };
			var info = new PredictionInfo(
				1, 20, 1, 1,
				new List<PredictionInfo.CardInfo>(),
				new List<PredictionInfo.CardInfo>(),
				evidenceCards: 10,
				remainingDeckCards: 8);

			Assert.IsFalse(PredictionView.ShouldUseLateGamePanel(info, config));
		}

		[TestMethod]
		public void JoustedCardsAreAddedButPlayedCountIsZero()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {});
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			opponent.KnownCards[0].Jousted = true;
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(0, info.PredictedCards[0].NumPlayed);
		}

		[TestMethod]
		public void JoustedCardCanBeOffMeta()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {});
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			opponent.KnownCards[0].Jousted = true;
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.IsTrue(info.PredictedCards[0].OffMeta);
		}

		[TestMethod]
		public void OnTurnStart_UpdatesAvailableManaOnPlayerTurn()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Bear Trap"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.Mana = 2;
			controller.OnTurnStart(ActivePlayer.Player);

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(2, info.PredictedCards.Count);
		}

		[TestMethod]
		public void OnTurnStart_DoesNotUpdateAvailableManaOnOpponentTurn()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Bear Trap"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			opponent.Mana = 2;
			controller.OnTurnStart(ActivePlayer.Opponent);

			var info = GetPredictionInfo(controller);
			Assert.AreEqual(1, info.PredictedCards.Count);
		}

		[TestMethod]
		public void CardPlayabilityAtAvailableManaForNextTurn()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(PlayableType.AtAvailableMana, info.PredictedCards[0].Playability);
		}

		[TestMethod]
		public void CardPlayabilityAboveAvailableManaForNextTurn()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(PlayableType.AboveAvailableMana, info.PredictedCards[0].Playability);
		}

		[TestMethod]
		public void CardPlayabilityBelowAvailableManaForNextTurn()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 5;
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(PlayableType.BelowAvailableMana, info.PredictedCards[0].Playability);
		}

		[TestMethod]
		public void CardPlayabilityAtAvailableManaWithCoinForNextTurn()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 2;
			opponent.HasCoin = true;
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(PlayableType.AtAvailableManaWithCoin, info.PredictedCards[0].Playability);
		}

		[TestMethod]
		public void CardPlayedNotInMetaDecksIsMarkedOffMeta()
		{
			var opponent = new MockOpponent("Hunter");
			opponent.KnownCards = CardList(new List<string> {"Alleycat"});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.IsTrue(info.PredictedCards[0].OffMeta);
		}

		[TestMethod]
		public void SecondOffMetaCardIncludedInPrediction()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var opponent = new MockOpponent("Hunter");
			opponent.KnownCards = CardList(new List<string> {"Alleycat"}, new List<int> {2});
			var controller = new PredictionController(opponent, _metaDecks.AsReadOnly());
			var info = GetPredictionInfo(controller);
			Assert.AreEqual(2, info.PredictedCards[0].Card.Count);
		}
	}
}
