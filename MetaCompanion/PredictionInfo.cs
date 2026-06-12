using Hearthstone_Deck_Tracker.Hearthstone;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MetaCompanion
{
	public class PredictionInfo : CustomLog.ILogProvider
	{
		public PredictionInfo(int numPossibleDecks, int numPossibleCards, int availableMana,
			int availableManaWithCoin, List<CardInfo> predictedCards, List<CardInfo> runnerUpCards,
			List<string> candidateDeckNames = null, int evidenceCards = 0,
			int? remainingDeckCards = null, string closestDeckName = null,
			List<CardInfo> closestDeckRemainingCards = null,
			List<ArchetypeCandidate> candidateArchetypes = null,
			List<Card> keyEvidenceCards = null)
		{
			NumPossibleDecks = numPossibleDecks;
			NumPossibleCards = numPossibleCards;
			PredictedCards = predictedCards;
			RunnerUpCards = runnerUpCards;
			CandidateDeckNames = candidateDeckNames ?? new List<string>();
			CandidateArchetypes = candidateArchetypes ??
				CandidateDeckNames.Select(name => new ArchetypeCandidate(name, 0, 0, 0)).ToList();
			EvidenceCards = evidenceCards;
			RemainingDeckCards = remainingDeckCards;
			ClosestDeckName = closestDeckName;
			ClosestDeckRemainingCards = closestDeckRemainingCards ?? new List<CardInfo>();
			KeyEvidenceCards = keyEvidenceCards ?? new List<Card>();
			PredictedCards.Concat(RunnerUpCards).Concat(ClosestDeckRemainingCards).ToList().ForEach(cardInfo =>
				{
					cardInfo.Playability =
						cardInfo.Card.Cost < availableMana ? PlayableType.BelowAvailableMana :
						(cardInfo.Card.Cost == availableMana ? PlayableType.AtAvailableMana :
						(cardInfo.Card.Cost == availableManaWithCoin ? PlayableType.AtAvailableManaWithCoin :
						PlayableType.AboveAvailableMana));
				});
		}

		public int NumPossibleDecks { get; }

		public int NumPossibleCards { get; }

		public List<CardInfo> PredictedCards { get; }

		public List<CardInfo> RunnerUpCards { get; }

		public List<string> CandidateDeckNames { get; }

		public List<ArchetypeCandidate> CandidateArchetypes { get; }

		public int EvidenceCards { get; }

		public int? RemainingDeckCards { get; }

		public string ClosestDeckName { get; }

		public List<CardInfo> ClosestDeckRemainingCards { get; }

		public List<Card> KeyEvidenceCards { get; }

		public int NumPredictedCards => PredictedCards.Sum(cardInfo => cardInfo.Probabilities.Count);

		public int NumVisiblePredictedCards => PredictedCards.Sum(cardInfo =>
			Math.Max(0, cardInfo.Probabilities.Count - cardInfo.NumPlayed));

		public int ConfidencePercent => CandidateArchetypes.Count == 0
			? 0
			: CandidateArchetypes[0].ConfidencePercent;

		public string ConfidenceLabel
		{
			get
			{
				if (ConfidencePercent >= 70)
				{
					return "高";
				}
				if (ConfidencePercent >= 40)
				{
					return "中";
				}
				if (ConfidencePercent > 0)
				{
					return "低";
				}
				return "未知";
			}
		}

		public void OnWriteLog(TextWriter writer)
		{
			writer.WriteLine(NumPossibleDecks + " possible decks");
			writer.WriteLine(NumPossibleCards + " possible cards");
			writer.WriteLine(EvidenceCards + " evidence cards");
			writer.WriteLine("Remaining deck cards: " +
				(RemainingDeckCards.HasValue ? RemainingDeckCards.Value.ToString() : "unknown"));
			writer.WriteLine("Candidate archetypes: " + string.Join(", ", CandidateDeckNames));
			writer.WriteLine("Archetype confidence: " + string.Join(", ",
				CandidateArchetypes.Select(candidate => candidate.ToString())));
			writer.WriteLine("Confidence: " + ConfidencePercent + "% (" + ConfidenceLabel + ")");
			writer.WriteLine("Closest deck: " + (string.IsNullOrEmpty(ClosestDeckName) ?
				"unknown" : ClosestDeckName));
			writer.WriteLine("Key evidence: " + FormatKeyEvidence(6));
			writer.WriteLine("");

			writer.WriteLine(NumPredictedCards + " predicted cards:");
			PredictedCards.ForEach(cardInfo => writer.WriteLine(cardInfo.ToString()));
			writer.WriteLine("");

			writer.WriteLine("Next " + RunnerUpCards.Count + " most likely cards:");
			RunnerUpCards.ForEach(cardInfo => writer.WriteLine(cardInfo.ToString()));
			writer.WriteLine("");

			writer.WriteLine("Closest deck remaining cards:");
			ClosestDeckRemainingCards.ForEach(cardInfo => writer.WriteLine(cardInfo.ToString()));
		}

		public string FormatKeyEvidence(int maxCards)
		{
			if (KeyEvidenceCards == null || KeyEvidenceCards.Count == 0 || maxCards <= 0)
			{
				return "";
			}

			return string.Join(", ", KeyEvidenceCards
				.Take(maxCards)
				.Select(card => card.Name + (card.Count > 1 ? "x" + card.Count : "")));
		}

		public class ArchetypeCandidate
		{
			public ArchetypeCandidate(string name, int confidencePercent, int score, int branchCount)
			{
				Name = name;
				ConfidencePercent = confidencePercent;
				Score = score;
				BranchCount = branchCount;
			}

			public string Name { get; }
			public int ConfidencePercent { get; }
			public int Score { get; }
			public int BranchCount { get; }

			public override string ToString()
			{
				return Name + " " + ConfidencePercent + "% score=" + Score +
					" branches=" + BranchCount;
			}
		}

		public class CardInfo
		{
			public Card Card { get; }
			public List<decimal> Probabilities { get; }
			public int NumPlayed { get; }
			public PlayableType Playability { get; set; }

			public CardInfo(Card card, List<decimal> probabilities, int numPlayed)
			{
				Card = card;
				Probabilities = probabilities;
				NumPlayed = numPlayed;
			}

			public CardInfo(Card card, int numPlayed) : this(card, new List<decimal>(), numPlayed) {}

			public int UnplayedCount => Card.Count - NumPlayed;

			public Card GetCardWithUnplayedCount()
			{
				var card = Database.GetCardFromId(Card.Id);
				card.Count = UnplayedCount;
				card.IsCreated = Card.IsCreated;
				return card;
			}

			public bool OffMeta => !Card.IsCreated && Card.Collectible && Card.Count > Probabilities.Count;

			public override string ToString()
			{
				string playabilityChar = "";
				switch (Playability)
				{
					case PlayableType.BelowAvailableMana:
						playabilityChar = "-";
						break;
					case PlayableType.AtAvailableMana:
						playabilityChar = "*";
						break;
					case PlayableType.AtAvailableManaWithCoin:
						playabilityChar = "o";
						break;
					case PlayableType.AboveAvailableMana:
						playabilityChar = "+";
						break;
				}

				List<string> probStrings = new List<string>();
				for (int n = 0; n < Probabilities.Count || n < NumPlayed; n++)
				{
					if (n < Probabilities.Count)
					{
						string probString = Math.Truncate(Probabilities[n] * 100) + "%";
						string playedString = n < NumPlayed ? "(P)" : "";
						probStrings.Add(probString + playedString);
					}
					else if (OffMeta)
					{
						probStrings.Add("XX");
					}
				}
				string percentageString = String.Join(" / ", probStrings);

				string costString = "[" + Card.Cost + playabilityChar + "] ";
				string createdString = Card.IsCreated || !Card.Collectible ? "[C]" : "";
				return costString + Card.Name + "(" + Card.Count + ")" +
					createdString + " - " + percentageString;
			}
		}
	}

	public enum PlayableType
	{
		BelowAvailableMana,
		AtAvailableMana,
		AtAvailableManaWithCoin,
		AboveAvailableMana
	}
}
