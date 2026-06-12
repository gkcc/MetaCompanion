using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MetaCompanion
{
	internal static class KnownOriginalCardCounter
	{
		public static Dictionary<string, int> Count(params IEnumerable<Card>[] sources)
		{
			if (sources == null || sources.Length == 0)
			{
				return new Dictionary<string, int>();
			}

			return sources
				.Where(source => source != null)
				.SelectMany(source => source)
				.Where(IsOriginalConstructedCard)
				.GroupBy(card => card.Id)
				.ToDictionary(
					group => group.Key,
					group =>
					{
						var copyLimit = GetConstructedCopyLimit(group.First());
						return Math.Min(copyLimit, group.Sum(card => Math.Max(1, card.Count)));
					});
		}

		public static bool IsOriginalConstructedCard(Card card)
		{
			return card != null &&
				!card.IsCreated &&
				card.Collectible &&
				!card.Jousted &&
				!string.IsNullOrEmpty(card.Id);
		}

		public static int GetConstructedCopyLimit(Card card)
		{
			if (card != null && card.Rarity == Rarity.LEGENDARY)
			{
				return 1;
			}
			return 2;
		}
	}
}
