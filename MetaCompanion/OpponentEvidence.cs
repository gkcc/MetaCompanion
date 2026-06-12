using Hearthstone_Deck_Tracker.Hearthstone;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaCompanion
{
	internal static class OpponentEvidence
	{
		public static IEnumerable<Card> GetKnownCardSources(IOpponent opponent)
		{
			if (opponent == null)
			{
				return Enumerable.Empty<Card>();
			}

			return SafeCards(opponent.KnownCards)
				.Concat(SafeCards(opponent.KnownCardsInDeck))
				.Concat(SafeCards(opponent.RevealedCards));
		}

		public static Dictionary<string, int> CountOriginalCards(
			IOpponent opponent, params IEnumerable<Card>[] extraSources)
		{
			var sources = new List<IEnumerable<Card>> { GetKnownCardSources(opponent) };
			if (extraSources != null)
			{
				sources.AddRange(extraSources.Where(source => source != null));
			}

			return KnownOriginalCardCounter.Count(sources.ToArray());
		}

		public static List<Card> BuildKnownDeckCards(IOpponent opponent)
		{
			return GetKnownCardSources(opponent)
				.Where(card => card != null &&
					!card.IsCreated &&
					card.Collectible &&
					!string.IsNullOrEmpty(card.Id))
				.GroupBy(card => card.Id)
				.Select(group =>
					{
						var card = Database.GetCardFromId(group.Key);
						if (card == null)
						{
							return null;
						}

						card.Count = Math.Min(
							KnownOriginalCardCounter.GetConstructedCopyLimit(card),
							group.Sum(knownCard => Math.Max(1, knownCard.Count)));
						card.IsCreated = false;
						return card;
					})
				.Where(card => card != null)
				.ToList();
		}

		public static List<Card> BuildKeyEvidenceCards(IDictionary<string, int> knownOriginalCards)
		{
			if (knownOriginalCards == null || knownOriginalCards.Count == 0)
			{
				return new List<Card>();
			}

			return knownOriginalCards
				.Select(knownCard =>
					{
						var card = Database.GetCardFromId(knownCard.Key);
						if (card == null)
						{
							return null;
						}

						card.Count = knownCard.Value;
						card.IsCreated = false;
						return card;
					})
				.Where(card => card != null)
				.OrderByDescending(card => card.Rarity == HearthDb.Enums.Rarity.LEGENDARY)
				.ThenByDescending(card => card.Cost)
				.ThenBy(card => card.Name)
				.ThenBy(card => card.Id)
				.ToList();
		}

		private static IEnumerable<Card> SafeCards(IEnumerable<Card> cards)
		{
			return cards ?? Enumerable.Empty<Card>();
		}
	}
}
