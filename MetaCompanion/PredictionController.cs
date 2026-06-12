using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Hearthstone_Deck_Tracker;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System;

namespace MetaCompanion
{
	public class PredictionController
	{
		private const string LogName = "prediction.txt";
		private IOpponent _opponent;
		private PredictionEngine _engine;
		private bool _firstOpponentDraw = true;
		private CustomLog _predictionLog;
		private readonly object _syncRoot = new object();
		private readonly List<Card> _observedOriginalCards = new List<Card>();

		public PredictionController(IOpponent opponent, ReadOnlyCollection<Deck> metaDecks)
		{
			_opponent = opponent;
			_engine = new PredictionEngine(opponent, metaDecks);
			_predictionLog = new CustomLog(LogName);
		}

		public string OpponentClass => _opponent?.Class;

		public List<Action<PredictionInfo>> OnPredictionUpdate = new List<Action<PredictionInfo>>();

		public void OnOpponentDraw()
		{
			lock (_syncRoot)
			{
				if (_firstOpponentDraw)
				{
					// This draw is mulligan, so check the values that weren't initialized at Game Start.
					// (i.e. - hero class, mana crystals)
					_engine.CheckOpponentClass();
					_engine.CheckOpponentMana();
					UpdatePrediction();
					_firstOpponentDraw = false;
				}
			}
		}

		public void OnTurnStart(ActivePlayer player)
		{
			lock (_syncRoot)
			{
				Log.Info("OnTurnStart: " + player);
				// At the beginning of the player turn, update the opponent's available mana for the next turn.
				if (player == ActivePlayer.Player)
				{
					_engine.CheckOpponentMana();
				}
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentPlay(Card cardPlayed)
		{
			lock (_syncRoot)
			{
				Log.Info("cardPlayed: " + cardPlayed);
				RememberObservedOriginalCard(cardPlayed);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentHandDiscard(Card cardDiscarded)
		{
			lock (_syncRoot)
			{
				Log.Info("cardDiscarded: " + cardDiscarded);
				RememberObservedOriginalCard(cardDiscarded);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentDeckDiscard(Card cardDiscarded)
		{
			lock (_syncRoot)
			{
				Log.Info("cardDiscarded: " + cardDiscarded);
				RememberObservedOriginalCard(cardDiscarded);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentSecretTriggered(Card secretTriggered)
		{
			lock (_syncRoot)
			{
				Log.Info("secretTriggered: " + secretTriggered);
				RememberObservedOriginalCard(secretTriggered);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentJoustReveal(Card cardRevealed)
		{
			lock (_syncRoot)
			{
				Log.Info("cardRevealed: " + cardRevealed);
				RememberObservedOriginalCard(cardRevealed);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void OnOpponentDeckToPlay(Card cardPlayed)
		{
			lock (_syncRoot)
			{
				Log.Info("cardPlayed: " + cardPlayed);
				RememberObservedOriginalCard(cardPlayed);
				_engine.CheckOpponentCards();
				UpdatePrediction();
			}
		}

		public void UpdatePrediction()
		{
			lock (_syncRoot)
			{
				UpdatePredictionCore();
			}
		}

		private void UpdatePredictionCore()
		{
			var knownOriginalCards = GetKnownOriginalCardCounts();
			var evidenceCards = knownOriginalCards.Values.Sum();

			// Make CardInfos for all cards that have already been played
			var cardInfos = BuildKnownCardInfos();

			// Get the predicted cards from the original deck list and group them together by id.
			// Then find the ones that have already been played and update their probabilities.
			// Otherwise, make a new CardInfo with the predicted cards.
			_engine.PredictedCards
				.GroupBy(predictedCard => predictedCard.Card.Id, predictedCard => predictedCard)
				.ToList()
				.ForEach(group =>
				{
					// Find a played card that started in the original deck
					var playedCardInfo = cardInfos.FirstOrDefault(cardInfo => cardInfo.Card.Id == group.Key
						&& cardInfo.Card.Collectible && !cardInfo.Card.IsCreated);
					var probabilities = group
						.Select(predictedCard => predictedCard.Probability)
						.Take(2)
						.ToList();
					int numPredictedCards = probabilities.Count;
					if (playedCardInfo != null)
					{
						playedCardInfo.Card.Count = Math.Max(numPredictedCards, playedCardInfo.Card.Count);
						playedCardInfo.Probabilities.AddRange(probabilities);
					}
					else
					{
						// This predicted card hasn't been played yet.
						var card = Database.GetCardFromId(group.Key);
						card.Count = numPredictedCards;
						cardInfos.Add(new PredictionInfo.CardInfo(card, probabilities, 0));
					}
				});

			var predictedCards = cardInfos
					.OrderBy(cardInfo => cardInfo.Card.Cost)
					.ThenBy(cardInfo => cardInfo.Card.Name)
					.ThenBy(cardInfo => cardInfo.Card.Id)
					.ThenBy(cardInfo => cardInfo.Card.IsCreated)
					.ToList();
			var runnerUps = _engine.GetNextPredictedCards(30).Select(cardInfo =>
				{
					// Don't group runnerUps, they all should have a count of 1 and are unplayed.
					var card = Database.GetCardFromId(cardInfo.Card.Id);
					var probabilities = new List<decimal> {cardInfo.Probability};
					return new PredictionInfo.CardInfo(card, probabilities, 0);
				}).ToList();
			var closestDeck = SelectClosestDeck(_engine.ClassDecks, knownOriginalCards);
			var closestDeckRemainingCards = BuildClosestDeckRemainingCards(
				closestDeck, knownOriginalCards);
			var candidateArchetypes = BuildCandidateArchetypes(
				_engine.ClassDecks, knownOriginalCards, evidenceCards);
			var candidateDeckNames = candidateArchetypes
				.Select(candidate => candidate.Name)
				.ToList();
			var keyEvidenceCards = OpponentEvidence.BuildKeyEvidenceCards(knownOriginalCards);

			var predictionInfo = new PredictionInfo(
				_engine.PossibleDecks.Count, _engine.PossibleCards.Count,
				_engine.AvailableMana, _engine.AvailableManaWithCoin, predictedCards, runnerUps,
				candidateDeckNames,
				evidenceCards,
				_opponent.RemainingDeckCards,
				closestDeck?.Name,
				closestDeckRemainingCards,
				candidateArchetypes,
				keyEvidenceCards);
			_predictionLog.Write(predictionInfo);
			OnPredictionUpdate.ForEach(callback => callback.Invoke(predictionInfo));
		}

		private List<PredictionInfo.CardInfo> BuildKnownCardInfos()
		{
			return _opponent.KnownCards
				.Where(card => !string.IsNullOrEmpty(card.Id) &&
					(card.IsCreated || card.Collectible))
				.GroupBy(card => new
					{
						card.Id,
						card.IsCreated
					})
				.Select(group =>
					{
						var playedCard = Database.GetCardFromId(group.Key.Id);
						if (playedCard == null)
						{
							return null;
						}

						var rawCount = group.Sum(card => Math.Max(1, card.Count));
						var knownCount = group.Key.IsCreated ? 1 : Math.Min(2, rawCount);
						var nonJoustedCount = group
							.Where(card => !card.Jousted)
							.Sum(card => Math.Max(1, card.Count));
						playedCard.Count = knownCount;
						playedCard.IsCreated = group.Key.IsCreated;
						var numPlayed = Math.Min(knownCount,
							group.Key.IsCreated ? Math.Min(1, nonJoustedCount) :
							Math.Min(2, nonJoustedCount));
						return new PredictionInfo.CardInfo(playedCard, numPlayed);
					})
				.Where(cardInfo => cardInfo != null)
				.ToList();
		}

		private Dictionary<string, int> GetKnownOriginalCardCounts()
		{
			return OpponentEvidence.CountOriginalCards(_opponent, _observedOriginalCards);
		}

		private void RememberObservedOriginalCard(Card card)
		{
			if (!KnownOriginalCardCounter.IsOriginalConstructedCard(card))
			{
				return;
			}

			var observedCard = Database.GetCardFromId(card.Id);
			if (observedCard == null)
			{
				return;
			}

			observedCard.Count = Math.Max(1, card.Count);
			observedCard.IsCreated = false;
			_observedOriginalCards.Add(observedCard);
		}

		internal static Deck SelectClosestDeck(
			IEnumerable<Deck> decks, IDictionary<string, int> knownOriginalCardCounts)
		{
			if (decks == null)
			{
				return null;
			}

			return decks
				.OrderByDescending(deck => ScoreDeck(deck, knownOriginalCardCounts))
				.ThenBy(deck => deck.Name)
				.FirstOrDefault();
		}

		internal static List<string> BuildCandidateDeckNames(
			IEnumerable<Deck> decks, IDictionary<string, int> knownOriginalCardCounts)
		{
			return BuildCandidateArchetypes(decks, knownOriginalCardCounts,
					knownOriginalCardCounts == null ? 0 : knownOriginalCardCounts.Values.Sum())
				.Select(candidate => candidate.Name)
				.ToList();
		}

		internal static List<PredictionInfo.ArchetypeCandidate> BuildCandidateArchetypes(
			IEnumerable<Deck> decks, IDictionary<string, int> knownOriginalCardCounts,
			int evidenceCards)
		{
			if (decks == null)
			{
				return new List<PredictionInfo.ArchetypeCandidate>();
			}

			var groups = decks
				.Where(deck => !string.IsNullOrWhiteSpace(deck.Name) &&
					deck.Name != "Imported Meta Deck")
				.GroupBy(deck => deck.Name)
				.Select(group => new
					{
						Name = group.Key,
						BestScore = group.Max(deck => ScoreDeck(deck, knownOriginalCardCounts)),
						AverageScore = group.Average(deck => ScoreDeck(deck, knownOriginalCardCounts)),
						BranchCount = group.Count()
					})
				.OrderByDescending(group => group.BestScore)
				.ThenByDescending(group => group.AverageScore)
				.ThenByDescending(group => group.BranchCount)
				.ThenBy(group => group.Name)
				.ToList();

			if (groups.Count == 0)
			{
				return new List<PredictionInfo.ArchetypeCandidate>();
			}

			var maxScore = groups.Max(group => group.BestScore);
			var weights = groups
				.Select(group => Math.Exp((group.BestScore - maxScore) / 125.0))
				.ToList();
			var totalWeight = weights.Sum();
			var evidenceFactor = evidenceCards <= 0
				? 0.0
				: Math.Min(1.0, 0.25 + evidenceCards * 0.125);

			return groups
				.Select((group, index) => new PredictionInfo.ArchetypeCandidate(
					group.Name,
					(int)Math.Round(weights[index] / totalWeight * 100 * evidenceFactor),
					group.BestScore,
					group.BranchCount))
				.Take(3)
				.ToList();
		}

		private static int ScoreDeck(Deck deck, IDictionary<string, int> knownOriginalCardCounts)
		{
			if (deck == null || knownOriginalCardCounts == null)
			{
				return 0;
			}

			var deckCounts = deck.Cards
				.GroupBy(card => card.Id)
				.ToDictionary(
					group => group.Key,
					group => group.Sum(card => Math.Max(1, card.Count)));
			var matchedCopies = 0;
			var missingCopies = 0;
			foreach (var knownCard in knownOriginalCardCounts)
			{
				var deckCount = deckCounts.ContainsKey(knownCard.Key) ? deckCounts[knownCard.Key] : 0;
				matchedCopies += Math.Min(knownCard.Value, deckCount);
				missingCopies += Math.Max(0, knownCard.Value - deckCount);
			}
			return matchedCopies * 100 - missingCopies * 75;
		}

		internal static List<PredictionInfo.CardInfo> BuildClosestDeckRemainingCards(
			Deck deck, IDictionary<string, int> knownOriginalCardCounts)
		{
			if (deck == null)
			{
				return new List<PredictionInfo.CardInfo>();
			}

			return deck.Cards
				.Where(card => !string.IsNullOrEmpty(card.Id))
				.GroupBy(card => card.Id)
				.Select(group =>
					{
						var card = group.First();
						var deckCount = group.Sum(deckCard => Math.Max(1, deckCard.Count));
						deckCount = Math.Min(
							KnownOriginalCardCounter.GetConstructedCopyLimit(card),
							deckCount);
						var knownCount = knownOriginalCardCounts != null &&
							knownOriginalCardCounts.ContainsKey(card.Id)
								? knownOriginalCardCounts[card.Id]
								: 0;
						var remainingCount = Math.Max(0, deckCount - knownCount);
						if (remainingCount == 0)
						{
							return null;
						}

						var remainingCard = Database.GetCardFromId(card.Id);
						if (remainingCard == null)
						{
							return null;
						}
						remainingCard.Count = remainingCount;
						return new PredictionInfo.CardInfo(
							remainingCard,
							Enumerable.Repeat(1m, remainingCount).ToList(),
							0);
					})
				.Where(cardInfo => cardInfo != null)
				.OrderBy(cardInfo => cardInfo.Card.Cost)
				.ThenBy(cardInfo => cardInfo.Card.Name)
				.ThenBy(cardInfo => cardInfo.Card.Id)
				.ToList();
		}

	}
}


