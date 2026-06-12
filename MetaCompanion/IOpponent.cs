using Hearthstone_Deck_Tracker.Hearthstone;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace MetaCompanion
{
	public interface IOpponent
	{
		// May be empty or null if API has not yet detected the opponent class.
		string Class { get; }

		// List of all cards known to be in the opponent's deck.
		List<Card> KnownCards { get; }

		// Cards HDT has confirmed as being in the opponent's deck.
		IEnumerable<Card> KnownCardsInDeck { get; }

		// Cards HDT has revealed from the opponent's deck or hand.
		IEnumerable<Card> RevealedCards { get; }

		// Current count of cards left in the opponent's deck, if HDT has initialized it.
		int? RemainingDeckCards { get; }

		// Return how much mana the opponent will have on the next turn. Takes overload into account.
		// If considerCoin is true, value will be greater by 1 if the opponent has the coin.
		// Returns -1 if API has not yet initialized the opponent state.
		// Valid values will always be in [0, 10]
		int GetAvailableManaNextTurn(bool considerCoin);
	}
}
