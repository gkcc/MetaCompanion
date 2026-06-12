using Hearthstone_Deck_Tracker.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MetaCompanion
{
	internal static class OverlayInteractivityHelper
	{
		private const int GwlExStyle = -20;
		private const int WsExTransparent = 0x00000020;
		private static readonly List<WeakReference<FrameworkElement>> Targets =
			new List<WeakReference<FrameworkElement>>();
		private static DispatcherTimer _timer;
		private static int? _originalExStyle;
		private static int _interactionHolds;

		public static void Register(FrameworkElement target)
		{
			if (target == null)
			{
				return;
			}

			if (!Targets.Any(reference =>
				{
					FrameworkElement existing;
					return reference.TryGetTarget(out existing) && ReferenceEquals(existing, target);
				}))
			{
				Targets.Add(new WeakReference<FrameworkElement>(target));
				target.Unloaded += (sender, args) => Unregister(target);
			}

			SafeEnsureTimerAndUpdate();
		}

		public static void Unregister(FrameworkElement target)
		{
			Targets.RemoveAll(reference =>
				{
					FrameworkElement existing;
					return !reference.TryGetTarget(out existing) || ReferenceEquals(existing, target);
				});
			SafeUpdate();
		}

		public static void HoldInteractive()
		{
			_interactionHolds++;
			SafeEnsureTimerAndUpdate();
		}

		public static void ReleaseInteractive()
		{
			_interactionHolds = Math.Max(0, _interactionHolds - 1);
			SafeUpdate();
		}

		private static void SafeEnsureTimerAndUpdate()
		{
			try
			{
				EnsureTimer();
				Update();
			}
			catch (Exception ex)
			{
				Log.Debug("Overlay interactivity update failed: " + ex.Message);
			}
		}

		private static void SafeUpdate()
		{
			try
			{
				Update();
			}
			catch (Exception ex)
			{
				Log.Debug("Overlay interactivity update failed: " + ex.Message);
			}
		}

		private static void EnsureTimer()
		{
			if (_timer != null)
			{
				return;
			}

			var overlay = Core.OverlayWindow;
			var dispatcher = overlay?.Dispatcher ?? Dispatcher.CurrentDispatcher;
			_timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
			{
				Interval = TimeSpan.FromMilliseconds(75)
			};
			_timer.Tick += (sender, args) => SafeUpdate();
			_timer.Start();
		}

		private static void Update()
		{
			CleanupTargets();
			var overlay = Core.OverlayWindow;
			var handle = overlay == null ? IntPtr.Zero : new WindowInteropHelper(overlay).Handle;
			if (handle == IntPtr.Zero)
			{
				return;
			}

			var activeTargets = GetActiveTargets().ToList();
			if (_originalExStyle == null)
			{
				_originalExStyle = GetWindowLong(handle, GwlExStyle);
			}

			if (activeTargets.Count == 0 && _interactionHolds <= 0)
			{
				RestoreOriginalStyle(handle);
				return;
			}

			var mouseOverTarget = _interactionHolds > 0 || activeTargets.Any(IsMouseOver);
			SetClickThrough(handle, !mouseOverTarget);
		}

		private static IEnumerable<FrameworkElement> GetActiveTargets()
		{
			foreach (var reference in Targets)
			{
				FrameworkElement target;
				if (reference.TryGetTarget(out target) &&
					target != null &&
					target.IsVisible &&
					target.ActualWidth > 0 &&
					target.ActualHeight > 0)
				{
					yield return target;
				}
			}
		}

		private static void CleanupTargets()
		{
			Targets.RemoveAll(reference =>
				{
					FrameworkElement target;
					return !reference.TryGetTarget(out target) || target == null;
				});
		}

		private static bool IsMouseOver(FrameworkElement target)
		{
			NativePoint cursor;
			if (!GetCursorPos(out cursor))
			{
				return false;
			}

			try
			{
				var topLeft = target.PointToScreen(new Point(0, 0));
				var bottomRight = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
				return cursor.X >= topLeft.X &&
					cursor.X <= bottomRight.X &&
					cursor.Y >= topLeft.Y &&
					cursor.Y <= bottomRight.Y;
			}
			catch
			{
				return false;
			}
		}

		private static void SetClickThrough(IntPtr handle, bool clickThrough)
		{
			var style = GetWindowLong(handle, GwlExStyle);
			var nextStyle = clickThrough
				? style | WsExTransparent
				: style & ~WsExTransparent;
			if (nextStyle != style)
			{
				SetWindowLong(handle, GwlExStyle, nextStyle);
			}
		}

		private static void RestoreOriginalStyle(IntPtr handle)
		{
			if (_originalExStyle.HasValue)
			{
				SetWindowLong(handle, GwlExStyle, _originalExStyle.Value);
			}
		}

		[DllImport("user32.dll")]
		private static extern int GetWindowLong(IntPtr hwnd, int index);

		[DllImport("user32.dll")]
		private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out NativePoint point);

		[StructLayout(LayoutKind.Sequential)]
		private struct NativePoint
		{
			public int X;
			public int Y;
		}
	}
}
