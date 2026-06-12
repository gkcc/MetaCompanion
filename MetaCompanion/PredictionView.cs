using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Hearthstone;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace MetaCompanion
{
	public class PredictionView
	{
		private const int ArchetypeLabelMaxNames = 3;
		private const int LateGameMinimumEvidence = 4;
		private const int LateGameDeckCountEvidence = 6;
		private const int LateGameTallListEvidence = 6;

		private static readonly MethodInfo UpdateOpponentCardsMethod =
			typeof(Hearthstone_Deck_Tracker.Core).GetMethod(
				"UpdateOpponentCards",
				BindingFlags.Static | BindingFlags.NonPublic);

		private bool _lastHideOpponentCards;
		private bool _changedHideOpponentCards;
		private bool _wroteNativePredictions;
		private bool _enabled;
		private readonly PluginConfig _config;
		private Border _archetypeLabelContainer;
		private TextBlock _archetypeLabelText;
		private PredictionLayout _lateGamePanel;

		public PredictionView(PluginConfig config)
		{
			_config = config;
		}

		public void OnUnload()
		{
			SetEnabled(false);
		}

		public void SetEnabled(bool enabled)
		{
			if (enabled == _enabled)
			{
				return;
			}

			_enabled = enabled;
			if (_enabled)
			{
				if (ShouldUseNativeOpponentPredictions())
				{
					Log.Debug("Enabling Meta Companion native HDT opponent deck predictions");
					_lastHideOpponentCards = Config.Instance.HideOpponentCards;
					Config.Instance.HideOpponentCards = false;
					_changedHideOpponentCards = true;
				}
			}
			else
			{
				RunOnOverlayThread(() =>
					{
						if (_wroteNativePredictions)
						{
							ClearNativePredictionsCore();
							_wroteNativePredictions = false;
						}
						RemoveArchetypeLabel();
						RemoveLateGamePanel();
					});
				if (_changedHideOpponentCards)
				{
					Config.Instance.HideOpponentCards = _lastHideOpponentCards;
					_changedHideOpponentCards = false;
				}
			}
		}

		public void OnPredictionUpdate(PredictionInfo prediction)
		{
			if (!_enabled)
			{
				return;
			}

			RunOnOverlayThread(() =>
				{
					if (ShouldUseLateGamePanel(prediction, _config))
					{
						if (ShouldUseNativeOpponentPredictions() && _wroteNativePredictions)
						{
							ClearNativePredictionsCore();
							_wroteNativePredictions = false;
						}
						UpdateArchetypeLabel(prediction);
						UpdateLateGamePanel(prediction);
					}
					else
					{
						HideLateGamePanel();
						if (ShouldUseNativeOpponentPredictions())
						{
							_wroteNativePredictions =
								UpdateNativePredictions(prediction) || _wroteNativePredictions;
						}
						UpdateArchetypeLabel(prediction);
					}
				});
		}

		internal static bool ShouldUseLateGamePanel(PredictionInfo prediction, PluginConfig config)
		{
			if (prediction == null || config == null || !config.EnableLateGamePanel ||
				prediction.EvidenceCards <= 0 || prediction.NumPossibleDecks <= 0)
			{
				return false;
			}

			if (prediction.EvidenceCards < LateGameMinimumEvidence)
			{
				return false;
			}

			var lowDeckWithEnoughEvidence = prediction.RemainingDeckCards.HasValue &&
				prediction.RemainingDeckCards.Value <= config.LateGameRemainingDeckThreshold &&
				prediction.EvidenceCards >= LateGameDeckCountEvidence;
			var enoughEvidence = prediction.EvidenceCards >= config.LateGameEvidenceThreshold;
			var nativeListWouldBeTall =
				prediction.NumVisiblePredictedCards >= config.LateGamePredictionThreshold &&
				prediction.EvidenceCards >= LateGameTallListEvidence;

			return lowDeckWithEnoughEvidence || enoughEvidence || nativeListWouldBeTall;
		}

		private bool ShouldUseNativeOpponentPredictions()
		{
			return ShouldUseNativeOpponentPredictions(_config);
		}

		internal static bool ShouldUseNativeOpponentPredictions(PluginConfig config)
		{
			return config != null && config.EnableNativeHdtOpponentPredictions;
		}

		private static bool UpdateNativePredictions(PredictionInfo prediction)
		{
			var opponent = Core.Game?.Opponent;
			if (opponent == null)
			{
				return false;
			}

			try
			{
				opponent.InDeckPredictions.Clear();
				foreach (var card in BuildNativePredictionCards(
					prediction,
					GetNativeKnownOriginalCardCounts(opponent)))
				{
					opponent.InDeckPredictions.Add(new PredictedCard(card.Id, 0, false));
				}
				RefreshOpponentCards();
				return true;
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to update HDT native opponent predictions: " + ex.Message);
				return false;
			}
		}

		internal static List<Card> BuildNativePredictionCards(
			PredictionInfo prediction, IDictionary<string, int> knownOriginalCardCounts = null)
		{
			if (prediction == null || prediction.EvidenceCards <= 0 ||
				prediction.NumPossibleDecks <= 0)
			{
				return new List<Card>();
			}

			IEnumerable<NativePredictionCard> candidates = prediction.PredictedCards
				.Where(cardInfo => !cardInfo.Card.IsCreated && cardInfo.UnplayedCount > 0)
				.GroupBy(cardInfo => cardInfo.Card.Id)
				.Select(group => new
					{
						Card = group
							.OrderByDescending(cardInfo => cardInfo.Probabilities.Count)
							.ThenByDescending(cardInfo => cardInfo.Card.Count)
							.First()
							.Card,
						Count = Math.Min(2, Math.Min(
							group.Max(cardInfo => cardInfo.UnplayedCount),
							group
								.SelectMany(cardInfo => cardInfo.Probabilities.Skip(cardInfo.NumPlayed))
								.Count())),
						Probabilities = group
							.SelectMany(cardInfo => cardInfo.Probabilities.Skip(cardInfo.NumPlayed))
							.OrderByDescending(probability => probability)
							.ToList()
					})
				.Where(predictionCount => predictionCount.Count > 0)
				.SelectMany(predictionCount => predictionCount.Probabilities
					.Take(predictionCount.Count)
					.Select(probability => new NativePredictionCard(
						predictionCount.Card, probability)))
				.OrderByDescending(predictionCard => predictionCard.Probability)
				.ThenBy(predictionCard => predictionCard.Card.Cost)
				.ThenBy(predictionCard => predictionCard.Card.Name);
			if (knownOriginalCardCounts != null && knownOriginalCardCounts.Count > 0)
			{
				candidates = candidates
					.GroupBy(predictionCard => predictionCard.Card.Id)
					.SelectMany(group =>
						{
							var card = group.First().Card;
							var knownCount = knownOriginalCardCounts.ContainsKey(card.Id)
								? knownOriginalCardCounts[card.Id]
								: 0;
							var allowedPredictionCount = Math.Max(
								0, KnownOriginalCardCounter.GetConstructedCopyLimit(card) - knownCount);
							return group
								.OrderByDescending(predictionCard => predictionCard.Probability)
								.Take(allowedPredictionCount);
						})
					.OrderByDescending(predictionCard => predictionCard.Probability)
					.ThenBy(predictionCard => predictionCard.Card.Cost)
					.ThenBy(predictionCard => predictionCard.Card.Name);
			}
			if (prediction.RemainingDeckCards.HasValue)
			{
				candidates = candidates.Take(Math.Max(0, prediction.RemainingDeckCards.Value));
			}

			return candidates
				.OrderBy(predictionCount => predictionCount.Card.Cost)
				.ThenBy(predictionCount => predictionCount.Card.Name)
				.ThenBy(predictionCount => predictionCount.Card.Id)
				.Select(predictionCard => predictionCard.Card)
				.ToList();
		}

		private static Dictionary<string, int> GetNativeKnownOriginalCardCounts(Player opponent)
		{
			if (opponent == null)
			{
				return new Dictionary<string, int>();
			}

			return GetKnownOriginalCardCounts(
				opponent.OpponentCardList,
				opponent.KnownCardsInDeck,
				opponent.RevealedCards);
		}

		internal static Dictionary<string, int> GetKnownOriginalCardCounts(
			params IEnumerable<Card>[] knownCardSources)
		{
			return KnownOriginalCardCounter.Count(knownCardSources);
		}

		private class NativePredictionCard
		{
			public NativePredictionCard(Card card, decimal probability)
			{
				Card = card;
				Probability = probability;
			}

			public Card Card { get; }
			public decimal Probability { get; }
		}

		private static void ClearNativePredictionsCore()
		{
			var opponent = Core.Game?.Opponent;
			try
			{
				if (opponent != null)
				{
					opponent.InDeckPredictions.Clear();
				}
				RefreshOpponentCards();
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to clear HDT native opponent predictions: " + ex.Message);
			}
		}

		private void UpdateArchetypeLabel(PredictionInfo prediction)
		{
			var names = prediction.CandidateDeckNames
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Take(ArchetypeLabelMaxNames)
				.ToList();
			if (prediction.EvidenceCards <= 0 || names.Count == 0)
			{
				HideArchetypeLabel();
				return;
			}

			if (!EnsureArchetypeLabel())
			{
				return;
			}

			var text = prediction.CandidateArchetypes.Count > 0
				? string.Join(" / ", prediction.CandidateArchetypes
					.Take(ArchetypeLabelMaxNames)
					.Select(candidate => candidate.Name + " " + candidate.ConfidencePercent + "%"))
				: string.Join(" / ", names);
			_archetypeLabelText.Text = text;
			_archetypeLabelText.ToolTip = text;
			_archetypeLabelContainer.Visibility = Visibility.Visible;
		}

		private bool EnsureArchetypeLabel()
		{
			var opponentStackPanel = GetOpponentStackPanel();
			if (opponentStackPanel == null)
			{
				return false;
			}

			if (_archetypeLabelContainer == null)
			{
				_archetypeLabelText = new TextBlock
				{
					Foreground = new SolidColorBrush(Color.FromRgb(236, 241, 246)),
					FontSize = 12,
					FontWeight = FontWeights.SemiBold,
					TextAlignment = TextAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
					VerticalAlignment = VerticalAlignment.Center
				};
				_archetypeLabelContainer = new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(210, 27, 34, 43)),
					BorderBrush = new SolidColorBrush(Color.FromArgb(110, 109, 124, 140)),
					BorderThickness = new Thickness(1),
					CornerRadius = new CornerRadius(3),
					IsHitTestVisible = false,
					Margin = new Thickness(0, 0, 0, 3),
					Padding = new Thickness(7, 3, 7, 3),
					Child = _archetypeLabelText
				};
			}

			var currentParent = _archetypeLabelContainer.Parent as Panel;
			if (currentParent != null && currentParent != opponentStackPanel)
			{
				currentParent.Children.Remove(_archetypeLabelContainer);
			}

			if (!opponentStackPanel.Children.Contains(_archetypeLabelContainer))
			{
				opponentStackPanel.Children.Insert(0, _archetypeLabelContainer);
			}

			return true;
		}

		private void HideArchetypeLabel()
		{
			if (_archetypeLabelContainer != null)
			{
				_archetypeLabelContainer.Visibility = Visibility.Collapsed;
			}
		}

		private void RemoveArchetypeLabel()
		{
			var parent = _archetypeLabelContainer?.Parent as Panel;
			if (parent != null)
			{
				parent.Children.Remove(_archetypeLabelContainer);
			}
			_archetypeLabelContainer = null;
			_archetypeLabelText = null;
		}

		private void UpdateLateGamePanel(PredictionInfo prediction)
		{
			if (!EnsureLateGamePanel())
			{
				return;
			}

			_lateGamePanel.Visibility = Visibility.Visible;
			_lateGamePanel.Update(prediction, true);
		}

		private bool EnsureLateGamePanel()
		{
			var canvas = GetOverlayCanvas();
			if (canvas == null)
			{
				return false;
			}

			if (_lateGamePanel == null)
			{
				_lateGamePanel = new PredictionLayout(_config);
			}

			var currentParent = _lateGamePanel.Parent as Panel;
			if (currentParent != null && currentParent != canvas)
			{
				currentParent.Children.Remove(_lateGamePanel);
			}

			if (!canvas.Children.Contains(_lateGamePanel))
			{
				canvas.Children.Add(_lateGamePanel);
			}
			OverlayInteractivityHelper.Register(_lateGamePanel);

			return true;
		}

		private void HideLateGamePanel()
		{
			if (_lateGamePanel != null)
			{
				_lateGamePanel.Visibility = Visibility.Collapsed;
			}
		}

		private void RemoveLateGamePanel()
		{
			var parent = _lateGamePanel?.Parent as Panel;
			if (parent != null)
			{
				parent.Children.Remove(_lateGamePanel);
			}
			OverlayInteractivityHelper.Unregister(_lateGamePanel);
			_lateGamePanel = null;
		}

		private static StackPanel GetOpponentStackPanel()
		{
			var overlayWindow = Core.OverlayWindow;
			if (overlayWindow == null)
			{
				return null;
			}

			var field = overlayWindow.GetType().GetField(
				"StackPanelOpponent",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field?.GetValue(overlayWindow) as StackPanel;
		}

		private static Canvas GetOverlayCanvas()
		{
			var overlayWindow = Core.OverlayWindow;
			if (overlayWindow == null)
			{
				return null;
			}

			var field = overlayWindow.GetType().GetField(
				"CanvasInfo",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			return field?.GetValue(overlayWindow) as Canvas;
		}

		private static void RunOnOverlayThread(Action action)
		{
			var overlayWindow = Core.OverlayWindow;
			if (overlayWindow == null)
			{
				action();
				return;
			}

			if (overlayWindow.Dispatcher.CheckAccess())
			{
				action();
			}
			else
			{
				overlayWindow.Dispatcher.BeginInvoke(action);
			}
		}

		private static void RefreshOpponentCards()
		{
			try
			{
				UpdateOpponentCardsMethod?.Invoke(null, new object[] { false });
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to refresh HDT opponent cards: " + ex.Message);
			}
		}
	}
}
