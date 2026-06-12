using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MetaCompanion
{
	internal static class OverlayDragHelper
	{
		private class DragState
		{
			public bool IsDragging { get; set; }
			public bool HasCustomPosition { get; set; }
			public Point StartMouse { get; set; }
			public Point StartElement { get; set; }
			public Action<FrameworkElement> DragCompleted { get; set; }
		}

		private static readonly ConditionalWeakTable<FrameworkElement, DragState> States =
			new ConditionalWeakTable<FrameworkElement, DragState>();

		public static void Enable(
			FrameworkElement target, UIElement handle,
			Action<FrameworkElement> dragCompleted = null)
		{
			if (target == null || handle == null)
			{
				return;
			}

			States.GetOrCreateValue(target).DragCompleted = dragCompleted;
			handle.PreviewMouseLeftButtonDown += (sender, args) => BeginDrag(target, handle, args);
			handle.PreviewMouseMove += (sender, args) => ContinueDrag(target, handle, args);
			handle.PreviewMouseLeftButtonUp += (sender, args) => EndDrag(target, handle, args);
			handle.LostMouseCapture += (sender, args) => CancelDrag(target);
		}

		public static bool HasCustomPosition(FrameworkElement target)
		{
			if (target == null)
			{
				return false;
			}

			DragState state;
			return States.TryGetValue(target, out state) && state.HasCustomPosition;
		}

		public static void ClearCustomPosition(FrameworkElement target)
		{
			if (target == null)
			{
				return;
			}

			DragState state;
			if (States.TryGetValue(target, out state))
			{
				state.HasCustomPosition = false;
			}
		}

		public static Point GetNormalizedPosition(FrameworkElement target)
		{
			var canvas = target?.Parent as Canvas;
			if (canvas == null)
			{
				return new Point(0, 0);
			}

			var position = GetElementPosition(target, canvas);
			return NormalizePosition(position, GetElementSize(target), GetCanvasSize(canvas));
		}

		public static void ApplyNormalizedPosition(
			FrameworkElement target, double normalizedX, double normalizedY,
			bool markCustomPosition = true)
		{
			var canvas = target?.Parent as Canvas;
			if (canvas == null)
			{
				return;
			}

			var position = DenormalizePosition(
				new Point(normalizedX, normalizedY),
				GetElementSize(target),
				GetCanvasSize(canvas));
			Canvas.SetLeft(target, position.X);
			Canvas.SetTop(target, position.Y);
			Canvas.SetRight(target, Double.NaN);
			Canvas.SetBottom(target, Double.NaN);

			if (markCustomPosition)
			{
				States.GetOrCreateValue(target).HasCustomPosition = true;
			}
		}

		private static void BeginDrag(FrameworkElement target, UIElement handle, MouseButtonEventArgs args)
		{
			if (IsInteractiveSource(args.OriginalSource as DependencyObject, handle))
			{
				return;
			}

			var canvas = target.Parent as Canvas;
			if (canvas == null)
			{
				return;
			}

			var state = States.GetOrCreateValue(target);
			state.IsDragging = true;
			state.StartMouse = args.GetPosition(canvas);
			state.StartElement = GetElementPosition(target, canvas);
			Canvas.SetLeft(target, state.StartElement.X);
			Canvas.SetTop(target, state.StartElement.Y);
			Canvas.SetRight(target, Double.NaN);
			Canvas.SetBottom(target, Double.NaN);
			OverlayInteractivityHelper.HoldInteractive();
			handle.CaptureMouse();
			args.Handled = true;
		}

		private static void ContinueDrag(FrameworkElement target, UIElement handle, MouseEventArgs args)
		{
			DragState state;
			if (!States.TryGetValue(target, out state) || !state.IsDragging)
			{
				return;
			}

			var canvas = target.Parent as Canvas;
			if (canvas == null)
			{
				CancelDrag(target);
				return;
			}

			var currentMouse = args.GetPosition(canvas);
			var nextLeft = state.StartElement.X + currentMouse.X - state.StartMouse.X;
			var nextTop = state.StartElement.Y + currentMouse.Y - state.StartMouse.Y;
			var nextPosition = CalculateDragPosition(
				state.StartElement,
				state.StartMouse,
				currentMouse,
				GetElementSize(target),
				GetCanvasSize(canvas));
			Canvas.SetLeft(target, nextPosition.X);
			Canvas.SetTop(target, nextPosition.Y);
			Canvas.SetRight(target, Double.NaN);
			Canvas.SetBottom(target, Double.NaN);
			args.Handled = true;
		}

		private static void EndDrag(FrameworkElement target, UIElement handle, MouseButtonEventArgs args)
		{
			DragState state;
			if (!States.TryGetValue(target, out state) || !state.IsDragging)
			{
				return;
			}

			state.IsDragging = false;
			state.HasCustomPosition = true;
			handle.ReleaseMouseCapture();
			state.DragCompleted?.Invoke(target);
			OverlayInteractivityHelper.ReleaseInteractive();
			args.Handled = true;
		}

		private static bool IsInteractiveSource(DependencyObject source, UIElement handle)
		{
			while (source != null && !ReferenceEquals(source, handle))
			{
				if (source is ButtonBase || source is TextBox || source is PasswordBox ||
					source is ComboBox || source is Slider || source is ScrollBar)
				{
					return true;
				}
				source = GetParent(source);
			}
			return false;
		}

		private static DependencyObject GetParent(DependencyObject source)
		{
			if (source == null)
			{
				return null;
			}

			var frameworkElement = source as FrameworkElement;
			if (frameworkElement != null && frameworkElement.Parent != null)
			{
				return frameworkElement.Parent;
			}

			var frameworkContentElement = source as FrameworkContentElement;
			if (frameworkContentElement != null && frameworkContentElement.Parent != null)
			{
				return frameworkContentElement.Parent;
			}

			try
			{
				return VisualTreeHelper.GetParent(source);
			}
			catch (InvalidOperationException)
			{
				return null;
			}
		}

		private static void CancelDrag(FrameworkElement target)
		{
			DragState state;
			if (target != null && States.TryGetValue(target, out state))
			{
				if (state.IsDragging)
				{
					OverlayInteractivityHelper.ReleaseInteractive();
				}
				state.IsDragging = false;
			}
		}

		private static Point GetElementPosition(FrameworkElement target, Canvas canvas)
		{
			var left = Canvas.GetLeft(target);
			var top = Canvas.GetTop(target);
			var targetSize = GetElementSize(target);
			var canvasSize = GetCanvasSize(canvas);

			if (Double.IsNaN(left))
			{
				var right = Canvas.GetRight(target);
				left = Double.IsNaN(right)
					? 0
					: Math.Max(0, canvasSize.Width - right - targetSize.Width);
			}
			if (Double.IsNaN(top))
			{
				var bottom = Canvas.GetBottom(target);
				top = Double.IsNaN(bottom)
					? 0
					: Math.Max(0, canvasSize.Height - bottom - targetSize.Height);
			}

			return new Point(left, top);
		}

		internal static Point CalculateDragPosition(
			Point startElement, Point startMouse, Point currentMouse,
			Size targetSize, Size canvasSize)
		{
			var nextLeft = startElement.X + currentMouse.X - startMouse.X;
			var nextTop = startElement.Y + currentMouse.Y - startMouse.Y;
			var maxLeft = Math.Max(0, canvasSize.Width - targetSize.Width);
			var maxTop = Math.Max(0, canvasSize.Height - targetSize.Height);
			return new Point(Clamp(nextLeft, 0, maxLeft), Clamp(nextTop, 0, maxTop));
		}

		internal static Point NormalizePosition(Point position, Size targetSize, Size canvasSize)
		{
			var maxLeft = Math.Max(0, canvasSize.Width - targetSize.Width);
			var maxTop = Math.Max(0, canvasSize.Height - targetSize.Height);
			return new Point(
				maxLeft <= 0 ? 0 : Clamp(position.X / maxLeft, 0, 1),
				maxTop <= 0 ? 0 : Clamp(position.Y / maxTop, 0, 1));
		}

		internal static Point DenormalizePosition(Point normalized, Size targetSize, Size canvasSize)
		{
			var maxLeft = Math.Max(0, canvasSize.Width - targetSize.Width);
			var maxTop = Math.Max(0, canvasSize.Height - targetSize.Height);
			return new Point(
				Clamp(normalized.X, 0, 1) * maxLeft,
				Clamp(normalized.Y, 0, 1) * maxTop);
		}

		private static Size GetElementSize(FrameworkElement target)
		{
			if (target == null)
			{
				return new Size(0, 0);
			}

			target.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			var width = target.ActualWidth > 0 ? target.ActualWidth : target.DesiredSize.Width;
			var height = target.ActualHeight > 0 ? target.ActualHeight : target.DesiredSize.Height;
			return new Size(Math.Max(0, width), Math.Max(0, height));
		}

		private static Size GetCanvasSize(Canvas canvas)
		{
			if (canvas == null)
			{
				return new Size(0, 0);
			}

			var width = canvas.ActualWidth > 0 ? canvas.ActualWidth : canvas.RenderSize.Width;
			var height = canvas.ActualHeight > 0 ? canvas.ActualHeight : canvas.RenderSize.Height;
			if (width <= 0 && !Double.IsNaN(canvas.Width))
			{
				width = canvas.Width;
			}
			if (height <= 0 && !Double.IsNaN(canvas.Height))
			{
				height = canvas.Height;
			}
			return new Size(Math.Max(0, width), Math.Max(0, height));
		}

		private static double Clamp(double value, double min, double max)
		{
			if (value < min)
			{
				return min;
			}
			if (value > max)
			{
				return max;
			}
			return value;
		}
	}
}
