using Hearthstone_Deck_Tracker.API;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace MetaCompanion
{
	/// <summary>
	/// Owns the advisor overlay lifecycle and enforces state-id freshness at the final UI boundary.
	/// This class never invokes game actions or pointer automation.
	/// </summary>
	public sealed class AdvisorView
	{
		private const double DefaultTopRatio = .18;
		private const double DefaultRightRatio = .025;
		private const double MaxUsableSavedPositionY = .9;
		private readonly object _stateLock = new object();
		private readonly PluginConfig _config;
		private readonly bool _enableOverlay;
		private AdvisorPanel _panel;
		private string _currentStateId = "";
		private bool _gameActive;
		private bool _userDismissed;

		public AdvisorView()
			: this(null)
		{
		}

		public AdvisorView(PluginConfig config)
			: this(config, true)
		{
		}

		internal AdvisorView(PluginConfig config, bool enableOverlay)
		{
			_config = config ?? new PluginConfig();
			_enableOverlay = enableOverlay;
		}

		public string CurrentStateId
		{
			get
			{
				lock (_stateLock)
				{
					return _currentStateId;
				}
			}
		}

		public bool UserDismissed
		{
			get
			{
				lock (_stateLock)
				{
					return _userDismissed;
				}
			}
		}

		/// <summary>
		/// Starts a new game-scoped advisor session. A user dismissal is reset only here.
		/// </summary>
		public void OnGameStarted()
		{
			lock (_stateLock)
			{
				_gameActive = true;
				_currentStateId = "";
				_userDismissed = false;
			}
			Hide();
		}

		/// <summary>
		/// Invalidates the prior result as soon as HDT observes a new board state.
		/// </summary>
		public void OnStateChanged(string stateId)
		{
			if (string.IsNullOrWhiteSpace(stateId))
			{
				return;
			}

			var show = false;
			lock (_stateLock)
			{
				if (!_gameActive)
				{
					return;
				}
				_currentStateId = stateId;
				show = !_userDismissed;
			}
			if (!show)
			{
				return;
			}

			ShowForState(stateId, panel =>
				panel.ShowStale(stateId, "正在等待当前局面的新结果…"));
		}

		public void OnThinking(string stateId, string message = null)
		{
			if (string.IsNullOrWhiteSpace(stateId))
			{
				return;
			}

			var show = false;
			lock (_stateLock)
			{
				if (!_gameActive)
				{
					return;
				}
				_currentStateId = stateId;
				show = !_userDismissed;
			}
			if (!show)
			{
				return;
			}

			ShowForState(stateId, panel => panel.ShowThinking(stateId, message));
		}

		/// <summary>
		/// Displays up to three routes only when the response still belongs to the active state.
		/// Both the caller thread and overlay dispatcher verify the id to close the race where the
		/// state changes while a queued UI update is waiting.
		/// </summary>
		public void OnRecommendations(AdvisorSolveResponse response, bool isStale = false)
		{
			OnRecommendations(response, null, isStale);
		}

		public void OnRecommendations(
			AdvisorSolveResponse response,
			AdvisorGameState gameState,
			bool isStale = false)
		{
			if (response == null || !CanDisplay(response.StateId))
			{
				return;
			}

			ShowForState(
				response.StateId,
				panel => panel.Update(response, isStale, gameState));
		}

		public void OnWorkerUnavailable(string stateId, string message = null)
		{
			string effectiveStateId;
			string displayStateId;
			lock (_stateLock)
			{
				effectiveStateId = string.IsNullOrWhiteSpace(stateId)
					? _currentStateId
					: stateId;
				if (!_gameActive || _userDismissed ||
					!CanReplaceWorkerUnavailableState(_currentStateId, effectiveStateId))
				{
					return;
				}

				// Invalidate the solve-state identity before dispatching the UI update. Any
				// recommendation already queued for the dead worker's state will fail the
				// dispatcher-side CanDisplay check and cannot overwrite this message.
				displayStateId = effectiveStateId + "|worker-unavailable|" +
					Guid.NewGuid().ToString("N");
				_currentStateId = displayStateId;
			}
			ShowForState(
				displayStateId,
				panel => panel.ShowWorkerUnavailable(effectiveStateId, message));
		}

		public void OnStale(string stateId, string message = null)
		{
			if (!CanDisplay(stateId))
			{
				return;
			}
			ShowForState(stateId, panel => panel.ShowStale(stateId, message));
		}

		public void OnGameEnded()
		{
			EndSession();
		}

		public void OnMenu()
		{
			EndSession();
		}

		public void Hide()
		{
			if (!_enableOverlay)
			{
				return;
			}
			RunOnOverlayThread(() =>
				{
					if (_panel != null)
					{
						_panel.Visibility = Visibility.Collapsed;
					}
				});
		}

		public void OnUnload()
		{
			lock (_stateLock)
			{
				_gameActive = false;
				_currentStateId = "";
			}
			if (!_enableOverlay)
			{
				return;
			}
			RunOnOverlayThread(() =>
				{
					var parent = _panel?.Parent as Panel;
					if (parent != null)
					{
						parent.Children.Remove(_panel);
					}
					OverlayInteractivityHelper.Unregister(_panel);
					_panel = null;
				});
		}

		internal static bool StateIdsMatch(string currentStateId, string responseStateId)
		{
			return !string.IsNullOrWhiteSpace(currentStateId) &&
				!string.IsNullOrWhiteSpace(responseStateId) &&
				String.Equals(currentStateId, responseStateId, StringComparison.Ordinal);
		}

		internal static bool CanReplaceWorkerUnavailableState(
			string currentStateId, string stateId)
		{
			if (StateIdsMatch(currentStateId, stateId))
				return true;
			if (string.IsNullOrWhiteSpace(currentStateId) ||
				string.IsNullOrWhiteSpace(stateId))
			{
				return false;
			}
			return currentStateId.StartsWith(
				stateId + "|worker-unavailable|",
				StringComparison.Ordinal);
		}

		private void EndSession()
		{
			lock (_stateLock)
			{
				_gameActive = false;
				_currentStateId = "";
				_userDismissed = false;
			}
			Hide();
		}

		private void HideByUser()
		{
			lock (_stateLock)
			{
				_userDismissed = true;
			}
			Hide();
		}

		private bool CanDisplay(string stateId)
		{
			lock (_stateLock)
			{
				return _gameActive && !_userDismissed && StateIdsMatch(_currentStateId, stateId);
			}
		}

		private void ShowForState(string stateId, Action<AdvisorPanel> update)
		{
			if (!_enableOverlay)
			{
				return;
			}
			RunOnOverlayThread(() =>
				{
					if (!CanDisplay(stateId) || !EnsurePanel())
					{
						return;
					}

					update(_panel);
					_panel.Visibility = Visibility.Visible;
					PositionPanel();
				});
		}

		private bool EnsurePanel()
		{
			var canvas = GetOverlayCanvas();
			if (canvas == null)
			{
				return false;
			}

			if (_panel == null)
			{
				_panel = new AdvisorPanel(HideByUser);
				OverlayDragHelper.Enable(_panel, _panel.DragHandle, SavePanelPosition);
				Panel.SetZIndex(_panel, 900);
			}

			var currentParent = _panel.Parent as Panel;
			if (currentParent != null && currentParent != canvas)
			{
				currentParent.Children.Remove(_panel);
			}

			if (!canvas.Children.Contains(_panel))
			{
				canvas.Children.Add(_panel);
			}
			OverlayInteractivityHelper.Register(_panel);
			return true;
		}

		private void PositionPanel()
		{
			var overlay = Core.OverlayWindow;
			if (overlay == null || _panel == null)
			{
				return;
			}

			if (overlay.Height > 0 && !Double.IsNaN(overlay.Height))
			{
				_panel.MaxHeight = Math.Max(220, overlay.Height * .76);
			}

			if (OverlayDragHelper.HasCustomPosition(_panel))
			{
				return;
			}

			if (_config.HasAdvisorPanelPosition)
			{
				if (IsSavedPanelPositionUsable())
				{
					OverlayDragHelper.ApplyNormalizedPosition(
						_panel,
						_config.AdvisorPanelPositionX,
						_config.AdvisorPanelPositionY);
					return;
				}

				_config.HasAdvisorPanelPosition = false;
				_config.AdvisorPanelPositionX = .72;
				_config.AdvisorPanelPositionY = DefaultTopRatio;
				_config.Save();
			}

			Canvas.SetLeft(_panel, Double.NaN);
			Canvas.SetBottom(_panel, Double.NaN);
			Canvas.SetTop(_panel, Math.Max(0, overlay.Height * DefaultTopRatio));
			Canvas.SetRight(_panel, Math.Max(0, overlay.Width * DefaultRightRatio));
		}

		private void SavePanelPosition(FrameworkElement target)
		{
			var position = OverlayDragHelper.GetNormalizedPosition(target);
			_config.HasAdvisorPanelPosition = true;
			_config.AdvisorPanelPositionX = position.X;
			_config.AdvisorPanelPositionY = position.Y;
			_config.Save();
		}

		private bool IsSavedPanelPositionUsable()
		{
			return _config.AdvisorPanelPositionX >= 0 &&
				_config.AdvisorPanelPositionX <= 1 &&
				_config.AdvisorPanelPositionY >= 0 &&
				_config.AdvisorPanelPositionY <= MaxUsableSavedPositionY;
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
	}
}
