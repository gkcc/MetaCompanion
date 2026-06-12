using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MetaCompanion;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace MetaCompanionTests.Mocks
{
	public class MockOpponent : IOpponent
	{
		public MockOpponent(string className)
		{
			Class = className;
			KnownCards = new List<Card>();
			KnownCardsInDeck = new List<Card>();
			RevealedCards = new List<Card>();
			Mana = 1;
		}

		public string Class { get; set; }

		public List<Card> KnownCards { get; set; }

		public IEnumerable<Card> KnownCardsInDeck { get; set; }

		public IEnumerable<Card> RevealedCards { get; set; }

		public int? RemainingDeckCards { get; set; }

		public int Mana { get; set; }
		public bool HasCoin { get; set; }

		public int GetAvailableManaNextTurn(bool considerCoin)
		{
			int mana = Mana;
			if (considerCoin && HasCoin)
			{
				mana++;
			}
			return mana;
		}
	}
}
