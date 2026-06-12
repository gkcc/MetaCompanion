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
		{
			ShouldTrack = shouldTrack;
			DashboardAction = dashboardAction;
		}

		public bool ShouldTrack { get; }
		public GameStartDashboardAction DashboardAction { get; }
	}

	public class MetaCompanionPlugin : IPlugin
	{
		public static readonly string DataDirectory = Path.Combine(Config.AppDataPath, "MetaCompanion");
		public static readonly string PluginDirectory =
			Path.Combine(Config.AppDataPath, "Plugins", "MetaCompanion");
		private static readonly string LogDirectory = Path.Combine(DataDirectory, "Logs");

		private PluginConfig _config;
		private ReadOnlyCollection<Deck> _metaDecks;
		private PredictionController _controller;
		private PredictionView _view;
		private MetaDashboardView _metaDashboardView;
		private PostGameMetaRefresher _postGameMetaRefresher;
		private MatchHistoryRecorder _matchHistoryRecorder;
		private bool _postGameDashboardShownForGame;
		private DateTime _ignoreReplayEventsUntil = DateTime.MinValue;

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
			Log.Info("Starting Meta Companion local HSReplay build 2026-06-12 (v1.4.0)");
			Log.Info("Plugin assembly: " + typeof(MetaCompanionPlugin).Assembly.Location);
			Log.Info("Plugin data directory: " + DataDirectory);
			if (!Directory.Exists(DataDirectory))
			{
				Directory.CreateDirectory(DataDirectory);
			}
			CustomLog.Initialize(LogDirectory);

			_config = PluginConfig.Load();

			Log.Info("Skipping upstream auto-update for local HSReplay data-source build.");

			// Synchronously retrieve our meta decks and keep them in memory.
			var metaRetriever = new MetaRetriever();
			var retrieveTask =
				Task.Run<List<Deck>>(async () => await metaRetriever.RetrieveMetaDecks(_config));
			_metaDecks = new ReadOnlyCollection<Deck>(retrieveTask.Result);
			_view = new PredictionView(_config);
			_metaDashboardView = new MetaDashboardView(_config);
			_postGameMetaRefresher = new PostGameMetaRefresher();

			GameEvents.OnGameStart.Add(() =>
				{
					var format = Hearthstone_Deck_Tracker.Core.Game.CurrentFormat;
					var mode = Hearthstone_Deck_Tracker.Core.Game.CurrentGameMode;
					var decision = GetGameStartDecision(format, mode, _controller != null);
					if (decision.ShouldTrack)
					{
						Log.Info("Enabling Meta Companion for " + format + " " + mode + " game");
						_postGameDashboardShownForGame = false;
						_matchHistoryRecorder = new MatchHistoryRecorder(_config);
						_matchHistoryRecorder.Start(format.ToString(), mode.ToString());
						var opponent = new Opponent(Hearthstone_Deck_Tracker.Core.Game);
						var controller = new PredictionController(opponent, _metaDecks);
						_controller = controller;
						_view.SetEnabled(true);
						if (decision.DashboardAction == GameStartDashboardAction.Hide)
						{
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
							Log.Info("No deck predictions for " + format + " " + mode + " game");
						}
					}
				});
			GameEvents.OnGameWon.Add(() => _matchHistoryRecorder?.SetResult("win"));
			GameEvents.OnGameLost.Add(() => _matchHistoryRecorder?.SetResult("loss"));
			GameEvents.OnGameTied.Add(() => _matchHistoryRecorder?.SetResult("tie"));
			GameEvents.OnGameEnd.Add(() =>
				{
					_matchHistoryRecorder?.Complete("game_end");
					ShowPostGameDashboard("game_end");
					_postGameMetaRefresher?.TryRefreshAfterGame(
						_config,
						() => _metaDashboardView?.ShowPostGame());
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
						ShowPostGameDashboard("in_menu");
					}
					_matchHistoryRecorder = null;
					_controller = null;
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
		}

		public void OnUpdate()
		{
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

		private void ShowPostGameDashboard(string reason)
		{
			if (_postGameDashboardShownForGame)
			{
				return;
			}

			_postGameDashboardShownForGame = true;
			Log.Info("Showing post-game dashboard after " + reason);
			_metaDashboardView?.ShowPostGame();
		}

		private bool ShouldIgnoreReplayEvent()
		{
			return DateTime.Now < _ignoreReplayEventsUntil;
		}

		public Version Version
		{
			get { return new Version(1, 4, 0); }
		}
	}
}

