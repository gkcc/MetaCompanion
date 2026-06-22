using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows;
using System;
using HsMode = Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode;

namespace MetaCompanion
{
	internal enum GameStartDashboardAction
	{
		None,
		Hide
	}

	internal struct GameStartDecision
	{
		public GameStartDecision(bool shouldTrack, GameStartDashboardAction dashboardAction)
			: this(shouldTrack, dashboardAction, "")
		{
		}

		public GameStartDecision(
			bool shouldTrack,
			GameStartDashboardAction dashboardAction,
			string predictionUnavailableReason)
		{
			ShouldTrack = shouldTrack;
			DashboardAction = dashboardAction;
			PredictionUnavailableReason = predictionUnavailableReason ?? "";
		}

		public bool ShouldTrack { get; }
		public GameStartDashboardAction DashboardAction { get; }
		public string PredictionUnavailableReason { get; }
	}

	public class MetaCompanionPlugin : IPlugin
	{
		public static readonly string DataDirectory = Path.Combine(Config.AppDataPath, "MetaCompanion");
		public static readonly string PluginDirectory =
			Path.Combine(Config.AppDataPath, "Plugins", "MetaCompanion");
		private static readonly string LogDirectory = Path.Combine(DataDirectory, "Logs");

		private PluginConfig _config;
		private readonly object _metaDeckLock = new object();
		private ReadOnlyCollection<Deck> _metaDecks =
			new ReadOnlyCollection<Deck>(new List<Deck>());
		private MetaDeckLoadSnapshot _metaDeckLoadSnapshot =
			MetaDeckLoadSnapshot.Loading(DateTime.MinValue);
		private PredictionController _controller;
		private PredictionView _view;
		private MetaDashboardView _metaDashboardView;
		private PostGameMetaRefresher _postGameMetaRefresher;
		private QuickDashboardRefresher _quickDashboardRefresher;
		private MatchHistoryRecorder _matchHistoryRecorder;
		private DateTime _ignoreReplayEventsUntil = DateTime.MinValue;
		private DateTime _nextDashboardPoll = DateTime.MinValue;
		private bool _wasInRecommendationScene;
		private string _lastDashboardStateSignature;
		private static readonly TimeSpan DashboardPollInterval = TimeSpan.FromSeconds(1);

		private SettingsWindow _settingsWindow;

		public string Author
		{
			get { return "Meta Companion contributors"; }
		}

		public string Description
		{
			get { return "标准模式环境识别、对手卡组预测、赛后推荐助手。"; }
		}

		public System.Windows.Controls.MenuItem MenuItem
		{
			get { return null; }
		}

		public string Name
		{
			get { return "Meta Companion"; }
		}

		public string ButtonText
		{
			get { return "Settings"; }
		}

		public void OnButtonPress()
		{
			if (_settingsWindow == null)
			{
				_settingsWindow = new SettingsWindow(_config);
				_settingsWindow.Closed += (sender, args) =>
				{
				    _settingsWindow = null;
				};
				_settingsWindow.Show();
			}
			else
			{
				_settingsWindow.Activate();
			}
		}

		public void OnLoad()
		{
			Log.Initialize();
			Log.Info("Starting Meta Companion v0.1.0");
			Log.Info("Plugin assembly: " + typeof(MetaCompanionPlugin).Assembly.Location);
			Log.Info("Plugin data directory: " + DataDirectory);
			if (!Directory.Exists(DataDirectory))
			{
				Directory.CreateDirectory(DataDirectory);
			}
			CustomLog.Initialize(LogDirectory);

			_config = PluginConfig.Load();

			Log.Info("Skipping upstream auto-update for local HSReplay data-source build.");

			StartMetaDeckLoad(new MetaRetriever());
			_view = new PredictionView(_config);
			_metaDashboardView = new MetaDashboardView(_config);
			_postGameMetaRefresher = new PostGameMetaRefresher();
			_quickDashboardRefresher = new QuickDashboardRefresher();

			GameEvents.OnGameStart.Add(() =>
				{
					var format = Hearthstone_Deck_Tracker.Core.Game.CurrentFormat;
					var mode = Hearthstone_Deck_Tracker.Core.Game.CurrentGameMode;
					MetaDeckLoadSnapshot metaDeckLoadSnapshot;
					var metaDecks = GetLoadedMetaDecks(out metaDeckLoadSnapshot);
					var decision = GetGameStartDecision(
						format,
						mode,
						_controller != null,
						metaDeckLoadSnapshot);
					if (decision.ShouldTrack)
					{
						Log.Info("Enabling Meta Companion for " + format + " " + mode + " game");
						_matchHistoryRecorder = new MatchHistoryRecorder(_config);
						_matchHistoryRecorder.Start(format.ToString(), mode.ToString());
						var opponent = new Opponent(Hearthstone_Deck_Tracker.Core.Game);
						var controller = new PredictionController(opponent, metaDecks);
						_controller = controller;
						_view.SetEnabled(true);
						if (decision.DashboardAction == GameStartDashboardAction.Hide)
						{
							_wasInRecommendationScene = false;
							_metaDashboardView?.ResetUserDismissed();
							_metaDashboardView?.Hide();
						}
						controller.OnPredictionUpdate.Add(_view.OnPredictionUpdate);
						controller.OnPredictionUpdate.Add(prediction =>
							_matchHistoryRecorder?.RecordPrediction(prediction, controller.OpponentClass));
					}
					else
					{
						if (_controller != null)
						{
							Log.Info("Ignoring duplicate game start while Meta Companion is already tracking a game");
							_ignoreReplayEventsUntil = DateTime.Now.AddSeconds(3);
						}
						else
						{
							if (!string.IsNullOrWhiteSpace(decision.PredictionUnavailableReason))
							{
								Log.Warn("No deck predictions for " + format + " " + mode +
									" game: " + decision.PredictionUnavailableReason);
							}
							else
							{
								Log.Info("No deck predictions for " + format + " " + mode + " game");
							}
						}
					}
				});
			GameEvents.OnGameWon.Add(() => _matchHistoryRecorder?.SetResult("win"));
			GameEvents.OnGameLost.Add(() => _matchHistoryRecorder?.SetResult("loss"));
			GameEvents.OnGameTied.Add(() => _matchHistoryRecorder?.SetResult("tie"));
			GameEvents.OnGameEnd.Add(() =>
				{
					_matchHistoryRecorder?.Complete("game_end");
					_quickDashboardRefresher?.TryRefreshAfterGame(
						_config,
						() => UpdateStandardRecommendationDashboard(true));
					_postGameMetaRefresher?.TryRefreshAfterGame(
						_config,
						() => UpdateStandardRecommendationDashboard(true));
				});
			GameEvents.OnInMenu.Add(() =>
				{
					if (ShouldIgnoreReplayEvent())
					{
						return;
					}
					var wasTrackingGame = _controller != null || _matchHistoryRecorder != null;
					if (_controller != null)
					{
						_view.SetEnabled(false);
						Log.Debug("Disabling Meta Companion for end of game");
					}
					_matchHistoryRecorder?.Complete("in_menu");
					if (wasTrackingGame)
					{
						_quickDashboardRefresher?.TryRefreshAfterGame(
							_config,
							() => UpdateStandardRecommendationDashboard(true));
					}
					_matchHistoryRecorder = null;
					_controller = null;
					if (wasTrackingGame)
					{
						UpdateStandardRecommendationDashboard(true);
					}
				});
			GameEvents.OnOpponentDraw.Add(() =>
				{
					if (!ShouldIgnoreReplayEvent())
					{
						_controller?.OnOpponentDraw();
					}
				});
			GameEvents.OnTurnStart.Add(activePlayer =>
				{
					if (!ShouldIgnoreReplayEvent())
					{
						_controller?.OnTurnStart(activePlayer);
					}
				});

			// Events that reveal cards need a 100ms delay. This is because HDT takes some extra
			// time to process all the tags we need, but it doesn't wait to send these callbacks.
			int delayMs = 250;
			GameEvents.OnOpponentPlay.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentPlay(card);
					}
				});
			GameEvents.OnOpponentHandDiscard.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentHandDiscard(card);
					}
				});
			GameEvents.OnOpponentDeckDiscard.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentDeckDiscard(card);
					}
				});
			GameEvents.OnOpponentSecretTriggered.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentSecretTriggered(card);
					}
				});
			GameEvents.OnOpponentJoustReveal.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentJoustReveal(card);
					}
				});
			GameEvents.OnOpponentDeckToPlay.Add(async card =>
				{
					var controller = _controller;
					await Task.Delay(delayMs);
					if (ReferenceEquals(controller, _controller) && !ShouldIgnoreReplayEvent())
					{
						controller?.OnOpponentDeckToPlay(card);
					}
				});
		}

		private void StartMetaDeckLoad(IMetaRetriever metaRetriever)
		{
			var startedAt = DateTime.Now;
			var loading = MetaDeckLoadSnapshot.Loading(startedAt);
			SetMetaDeckLoadState(new List<Deck>(), loading);
			TryWriteMetaDeckLoadStatus(loading);
			Log.Info("Meta deck library loading started in the background.");

			Task.Run(async () =>
			{
				try
				{
					var decks = await metaRetriever.RetrieveMetaDecks(_config);
					decks = decks ?? new List<Deck>();
					var completedAt = DateTime.Now;
					var snapshot = MetaDeckLoadSnapshot.Ready(decks.Count, startedAt, completedAt);
					SetMetaDeckLoadState(decks, snapshot);
					TryWriteMetaDeckLoadStatus(snapshot);
					if (snapshot.IsReady)
					{
						Log.Info("Meta deck library loaded: " + snapshot.DeckCount + " decks.");
					}
					else
					{
						Log.Warn("Meta deck library loaded no decks; predictions remain unavailable.");
					}
				}
				catch (Exception ex)
				{
					var summary = SummarizeException(ex);
					var snapshot = MetaDeckLoadSnapshot.Failed(summary, startedAt, DateTime.Now);
					SetMetaDeckLoadState(new List<Deck>(), snapshot);
					TryWriteMetaDeckLoadStatus(snapshot);
					Log.Warn("Meta deck library load failed: " + summary);
					Log.Error(ex);
				}
			});
		}

		private void SetMetaDeckLoadState(List<Deck> decks, MetaDeckLoadSnapshot snapshot)
		{
			lock (_metaDeckLock)
			{
				_metaDecks = new ReadOnlyCollection<Deck>(decks ?? new List<Deck>());
				_metaDeckLoadSnapshot = snapshot ?? MetaDeckLoadSnapshot.Loading(DateTime.Now);
			}
		}

		private ReadOnlyCollection<Deck> GetLoadedMetaDecks(out MetaDeckLoadSnapshot snapshot)
		{
			lock (_metaDeckLock)
			{
				snapshot = _metaDeckLoadSnapshot;
				return _metaDecks;
			}
		}

		private static void TryWriteMetaDeckLoadStatus(MetaDeckLoadSnapshot snapshot)
		{
			try
			{
				MetaDeckLoadStatusStore.Write(DataDirectory, snapshot);
			}
			catch (Exception ex)
			{
				Log.Warn("Unable to write meta deck load status: " + ex.Message);
			}
		}

		internal static string SummarizeException(Exception ex)
		{
			return ex == null
				? "Unknown error"
				: ex.GetType().Name + ": " + ex.Message;
		}

		public void OnUnload()
		{
			if (_settingsWindow != null)
			{
			    if (_settingsWindow.IsVisible)
			    {
			        _settingsWindow.Close();
			    }
			    _settingsWindow = null;
			}
			_config?.Save();
			_matchHistoryRecorder?.Complete("plugin_unload");
			_matchHistoryRecorder = null;
			_view?.OnUnload();
			_metaDashboardView?.OnUnload();
			_metaDashboardView = null;
			_postGameMetaRefresher = null;
			_quickDashboardRefresher = null;
		}

		public void OnUpdate()
		{
			UpdateStandardRecommendationDashboard(false);
		}

		internal static bool ShouldStartTrackingGame(Format? format, GameMode mode, bool alreadyTracking)
		{
			if (alreadyTracking)
			{
				return false;
			}

			return format == Format.Standard &&
				(mode == GameMode.Ranked || mode == GameMode.Casual || mode == GameMode.Friendly);
		}

		internal static GameStartDecision GetGameStartDecision(
			Format? format,
			GameMode mode,
			bool alreadyTracking)
		{
			var shouldTrack = ShouldStartTrackingGame(format, mode, alreadyTracking);
			return new GameStartDecision(
				shouldTrack,
				shouldTrack ? GameStartDashboardAction.Hide : GameStartDashboardAction.None);
		}

		internal static GameStartDecision GetGameStartDecision(
			Format? format,
			GameMode mode,
			bool alreadyTracking,
			MetaDeckLoadSnapshot metaDeckLoadSnapshot)
		{
			if (!ShouldStartTrackingGame(format, mode, alreadyTracking))
			{
				return new GameStartDecision(false, GameStartDashboardAction.None);
			}

			var snapshot = metaDeckLoadSnapshot ?? MetaDeckLoadSnapshot.Loading(DateTime.Now);
			if (!snapshot.IsReady)
			{
				return new GameStartDecision(
					false,
					GameStartDashboardAction.None,
					snapshot.UserMessage);
			}

			return new GameStartDecision(true, GameStartDashboardAction.Hide);
		}

		internal static bool ShouldShowStandardRecommendations(
			Format? format,
			GameMode gameMode,
			HsMode currentMode,
			bool trackingGame,
			bool enabled)
		{
			if (!enabled || trackingGame)
			{
				return false;
			}

			if (format.HasValue && format.Value != Format.Standard && format.Value != Format.All)
			{
				return false;
			}

			if (gameMode != GameMode.Ranked &&
				gameMode != GameMode.Casual &&
				gameMode != GameMode.Friendly &&
				gameMode != GameMode.None)
			{
				return false;
			}

			return currentMode == HsMode.TOURNAMENT;
		}

		private void UpdateStandardRecommendationDashboard(bool force)
		{
			if (!force && DateTime.Now < _nextDashboardPoll)
			{
				return;
			}
			_nextDashboardPoll = DateTime.Now.Add(DashboardPollInterval);

			var game = Hearthstone_Deck_Tracker.Core.Game;
			var shouldShow = game != null && ShouldShowStandardRecommendations(
				game.CurrentFormat,
				game.CurrentGameMode,
				game.CurrentMode,
				_controller != null,
				_config != null && _config.EnableMetaDashboard);
			LogDashboardStateIfChanged(game, shouldShow);

			if (shouldShow)
			{
				if (!_wasInRecommendationScene)
				{
					_wasInRecommendationScene = true;
					_metaDashboardView?.ResetUserDismissed();
				}
				if (!(_metaDashboardView?.UserDismissed ?? true))
				{
					_metaDashboardView?.ShowRecommendations();
				}
				return;
			}

			if (_wasInRecommendationScene)
			{
				_metaDashboardView?.ResetUserDismissed();
			}
			_wasInRecommendationScene = false;
			_metaDashboardView?.Hide();
		}

		private void LogDashboardStateIfChanged(
			Hearthstone_Deck_Tracker.Hearthstone.GameV2 game,
			bool shouldShow)
		{
			var signature = game == null
				? "no-game"
				: game.CurrentFormat + "|" + game.CurrentGameMode + "|" + game.CurrentMode +
					"|tracking=" + (_controller != null) +
					"|enabled=" + (_config != null && _config.EnableMetaDashboard) +
					"|show=" + shouldShow;
			if (signature == _lastDashboardStateSignature)
			{
				return;
			}

			_lastDashboardStateSignature = signature;
			Log.Debug("Recommendation dashboard state: " + signature);
		}

		private bool ShouldIgnoreReplayEvent()
		{
			return DateTime.Now < _ignoreReplayEventsUntil;
		}

		public Version Version
		{
			get { return new Version(0, 1, 0); }
		}
	}
}

