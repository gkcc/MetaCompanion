using MetaCompanion;
using MetaCompanionTests.Mocks;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using System;

namespace MetaCompanionTests.Tests
{
	[TestClass]
	public class PredictionEngineTest
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
				.ToList();
		}

		private string Key(string cardName, int copyCount)
		{
			var card = Database.GetCardFromName(cardName);
			return PredictionEngine.CardInfo.Key(card, copyCount);
		}

		[TestMethod]
		public void PossibleDecks_EmptyByDefault()
		{
			var engine = new PredictionEngine(new MockOpponent("Mage"), _metaDecks.AsReadOnly());
			Assert.AreEqual(0, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void PossibleDecks_OneMetaDeckSameClass()
		{
			AddMetaDeck("Hunter");
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.IsTrue(_metaDecks.SequenceEqual(engine.PossibleDecks));
		}

		[TestMethod]
		public void PossibleDecks_OneMetaDeckDifferentClass()
		{
			AddMetaDeck("Hunter");
			var engine = new PredictionEngine(new MockOpponent("Mage"), _metaDecks.AsReadOnly());
			Assert.AreEqual(0, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void PossibleDecks_NormalizesClassFormatting()
		{
			AddMetaDeck("Death Knight");
			var engine = new PredictionEngine(new MockOpponent("DEATH_KNIGHT"), _metaDecks.AsReadOnly());
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void CheckOpponentCards_MissingCardFiltersDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void CheckOpponentCards_KnownCardsInDeckFiltersDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCardsInDeck = CardList(new List<string> {"Deadly Shot"});
			engine.CheckOpponentCards();

			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MissingSecondCardFiltersDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));
			opponent.KnownCards.Add(Database.GetCardFromName("Alleycat"));
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MatchingCardDoesNotFilter()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var hunterCard = Database.GetCardFromName("Deadly Shot");
			opponent.KnownCards.Add(hunterCard);
			engine.CheckOpponentCards();
			Assert.AreEqual(2, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MissingCreatedCardDoesNotFilter()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var hunterCard = Database.GetCardFromName("Deadly Shot");
			hunterCard.IsCreated = true;
			opponent.KnownCards.Add(hunterCard);
			engine.CheckOpponentCards();
			Assert.AreEqual(2, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MissingSecondCardAfterCreatedCardFiltersDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var hunterCard = Database.GetCardFromName("Deadly Shot");
			hunterCard.IsCreated = true;
			opponent.KnownCards.Add(hunterCard);
			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MissingNonCollectibleCardDoesNotFilter()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var nonCollectibleHunterCard = HearthDb.Cards.All.Values.First(card =>
				!card.Collectible && card.Class.ToString() == "HUNTER");
			var hunterCard = new Card(nonCollectibleHunterCard, false);
			opponent.KnownCards.Add(hunterCard);
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_MissingSecondCopyFiltersDeck()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var hunterCard2Copies = Database.GetCardFromName("Deadly Shot");
			hunterCard2Copies.Count = 2;
			opponent.KnownCards.Add(hunterCard2Copies);
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_UnchangedIfCheckOpponentCardsCalledTwice()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCards.Add(Database.GetCardFromName("Deadly Shot"));
			engine.CheckOpponentCards();
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPossibleDecks_IgnoresOffMetaCard()
		{
			var opponent = new MockOpponent("Hunter");
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot"});
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCards = CardList(new List<string> {"Deadly Shot", "Alleycat"});
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.PossibleDecks.Count);
		}

		[TestMethod]
		public void GetPredictedCards_EmptyByDefault()
		{
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(0, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_SameAsSingleMetaDeck()
		{
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});

			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(2, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_EmptyAfterClassFiltered()
		{
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});

			var engine = new PredictionEngine(new MockOpponent("Mage"), _metaDecks.AsReadOnly());
			Assert.AreEqual(0, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_FewerAfterDeckFiltered()
		{
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Bear Trap"});

			var opponent = new MockOpponent("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards.Add(Database.GetCardFromName("Alleycat"));
			engine.CheckOpponentCards();
			Assert.AreEqual(2, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_CombinesContentsOfTwoDecks()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Arcane Shot", "Tracking"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(4, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_UnionOfTwoDecks()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(3, engine.PredictedCards.Count);
			// First copy of Alleycat.
			Assert.IsNotNull(engine.GetPredictedCard(Key("Alleycat", 1)));
			// No second copy of Alleycat.
			Assert.IsNull(engine.GetPredictedCard(Key("Alleycat", 2)));
		}

		[TestMethod]
		public void GetPredictedCards_SameAsFirstDeckAfterSecondFiltered()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});

			var opponent = new MockOpponent("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			opponent.KnownCards.Add(Database.GetCardFromName("Tracking"));
			engine.CheckOpponentCards();
			Assert.AreEqual(2, engine.PredictedCards.Count);
			Assert.IsNotNull(engine.GetPredictedCard(Key("Alleycat", 1)));
			Assert.IsNotNull(engine.GetPredictedCard(Key("Tracking", 1)));
		}

		[TestMethod]
		public void GetPredictedCards_UnionTakesHigherCardCount()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			_metaDecks[1].Cards[0].Count = 2;

			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(4, engine.PredictedCards.Count);
			Assert.IsNotNull(engine.GetPredictedCard(Key("Alleycat", 2)));
		}

		[TestMethod]
		public void GetPredictedCards_SortedByDescendingProbability()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			var firstPredictedCard = engine.PredictedCards.ElementAt(0);
			Assert.AreEqual("Alleycat", firstPredictedCard.Card.Name);
		}

		[TestMethod]
		public void GetPredictedCards_SortedSecondaryByLowerManaCost()
		{
			AddMetaDeck("Hunter", new List<string> {"Bear Trap", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 2;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			var firstPredictedCard = engine.PredictedCards.ElementAt(1);
			Assert.AreEqual("Tracking", firstPredictedCard.Card.Name);
		}

		[TestMethod]
		public void GetPredictedCards_ReturnsNoMoreThanDeckSize()
		{
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Tracking"});
			_metaDecks[0].Cards[0].Count = 30;
			_metaDecks[1].Cards[0].Count = 30;

			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(30, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_GreaterThanDeckSizeIfAtSameProbability()
		{
			AddMetaDeck("Hunter", new List<string> {"Tracking"});
			_metaDecks[0].Cards[0].Count = 40;

			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			// All the Deadly Shots are at the same probability, so include them all.
			Assert.AreEqual(40, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_LessThanDeckSizeIfAtSameProbabilityAndTooExpensive()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			_metaDecks[1].Cards[0].Count = 40;
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			// All the Deadly Shots are at the same probability and can't yet be played,
			// so don't include any of them.
			Assert.AreEqual(1, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetPredictedCards_DoesNotIncludeCardsBelowThresholdIfNotPlayableYet()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			// Deadly Shot only has a 50% chance and can't be played next turn.
			Assert.AreEqual(1, engine.PredictedCards.Count);
			Assert.AreEqual("Alleycat", engine.PredictedCards[0].Card.Name);
		}

		[TestMethod]
		public void GetPredictedCards_DoesNotIncludeCardWithLowProbability()
		{
			for (int n = 0; n < 9; n++)
			{
				AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			}
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Alleycat"});
			var opponent = new MockOpponent("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			// Deadly Shot only has a 10% chance and won't be included.
			Assert.AreEqual(1, engine.PredictedCards.Count);
			Assert.AreEqual("Alleycat", engine.PredictedCards[0].Card.Name);
		}

		[TestMethod]
		public void GetPredictedCards_IncludeCardIfPlayable()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			var opponent = new MockOpponent("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			engine.ProbabilityIncludeIfPlayable = .50m;
			opponent.Mana = 5;
			engine.CheckOpponentMana();

			// Deadly Shot has a 50% chance and is playable.
			Assert.AreEqual(2, engine.PredictedCards.Count);
		}

		[TestMethod]
		public void GetNextPredictedCards_EmptyByDefault()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(0, engine.GetNextPredictedCards(10).Count);
		}

		[TestMethod]
		public void GetNextPredictedCards_ContainsLeftoverCards()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			_metaDecks[1].Cards[0].Count = 40;
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			// All 40 Deadly Shots
			Assert.AreEqual(40, engine.GetNextPredictedCards(40).Count);
		}

		[TestMethod]
		public void GetNextPredictedCards_TruncatesLeftoverCards()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat"});
			_metaDecks[1].Cards[0].Count = 40;
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());
			Assert.AreEqual(10, engine.GetNextPredictedCards(10).Count);
		}

		[TestMethod]
		public void GetNextPredictedCards_SortedByProbability()
		{
			AddMetaDeck("Hunter", new List<string> {"Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Bear Trap", "Alleycat"});
			_metaDecks[1].Cards[0].Count = 40;
			_metaDecks[1].Cards[1].Count = 40;
			AddMetaDeck("Hunter", new List<string> {"Deadly Shot", "Alleycat",});
			_metaDecks[2].Cards[0].Count = 40;
			var opponent = new MockOpponent("Hunter");
			opponent.Mana = 1;
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			var nextPredictedCards = engine.GetNextPredictedCards(50);
			Assert.AreEqual("Deadly Shot", nextPredictedCards.ElementAt(0).Card.Name);
			Assert.AreEqual("Deadly Shot", nextPredictedCards.ElementAt(1).Card.Name);
			Assert.AreEqual("Bear Trap", nextPredictedCards.ElementAt(40).Card.Name);
		}

		[TestMethod]
		public void GetPredictedCard_ProbabilityIsOneForSinglePossibleDeck()
		{
			AddMetaDeck("Hunter", new List<string> {"Tracking", "Alleycat"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(1, engine.GetPredictedCard(Key("Tracking", 1)).Probability);
		}

		[TestMethod]
		public void GetPredictedCard_ProbabilityIsHalfForOneOfTwoDecks()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(.5m, engine.GetPredictedCard(Key("Tracking", 1)).Probability);
		}

		[TestMethod]
		public void GetPredictedCard_ProbabilityIsOneWhenInBothDecks()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var engine = new PredictionEngine(new MockOpponent("Hunter"), _metaDecks.AsReadOnly());
			Assert.AreEqual(1, engine.GetPredictedCard(Key("Alleycat", 1)).Probability);
		}

		[TestMethod]
		public void GetPredictedCard_ProbabilityReturnsToOneAfterSecondDeckFiltered()
		{
			AddMetaDeck("Hunter", new List<string> {"Hunter's Mark", "Alleycat"});
			AddMetaDeck("Hunter", new List<string> {"Alleycat", "Tracking"});
			var opponent = new MockOpponent("Hunter");
			var engine = new PredictionEngine(opponent, _metaDecks.AsReadOnly());

			opponent.KnownCards.Add(Database.GetCardFromName("Tracking"));
			engine.CheckOpponentCards();
			Assert.AreEqual(1, engine.GetPredictedCard(Key("Tracking", 1)).Probability);
		}
	}
}


