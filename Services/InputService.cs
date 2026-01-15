using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SDL2;
using System;
using System.Collections.Generic;
using System.Linq;

namespace N64RecompLauncher.Services
{
	public class InputService : IDisposable
	{
		private readonly Window _mainWindow;
		private DispatcherTimer? _gamepadTimer;
		private readonly Dictionary<int, GamepadState> _gamepadStates = new();
		private readonly Dictionary<int, IntPtr> _gameControllers = new();
		private bool _disposed = false;
		private bool _isWindowActive = true;

		// Gamepad deadzone threshold
		private const float DeadZone = 0.3f;
		private const short AxisMax = 32767;

		// Input repeat delays
		private const int InitialRepeatDelay = 500;
		private const int RepeatDelay = 150;

		private DateTime _lastNavigationTime = DateTime.MinValue;
		private bool _isInitialNavigation = true;

		private DateTime _lastConfirmTime = DateTime.MinValue;
		private DateTime _lastCancelTime = DateTime.MinValue;
		private const int ButtonRepeatDelay = 300;

		public event Action<NavigationDirection>? OnNavigate;
		public event Action? OnConfirm;
		public event Action? OnCancel;

		public InputService(Window mainWindow)
		{
			_mainWindow = mainWindow;
			InitializeSDL();
			InitializeGamepadPolling();
		}

		private void InitializeSDL()
		{
			if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER) < 0)
			{
				System.Diagnostics.Debug.WriteLine($"SDL initialization failed: {SDL.SDL_GetError()}");
				return;
			}

			// Open all available game controllers
			int numJoysticks = SDL.SDL_NumJoysticks();
			for (int i = 0; i < numJoysticks; i++)
			{
				if (SDL.SDL_IsGameController(i) == SDL.SDL_bool.SDL_TRUE)
				{
					IntPtr controller = SDL.SDL_GameControllerOpen(i);
					if (controller != IntPtr.Zero)
					{
						_gameControllers[i] = controller;
						_gamepadStates[i] = new GamepadState();
						System.Diagnostics.Debug.WriteLine($"Game controller {i} connected: {SDL.SDL_GameControllerName(controller)}");
					}
				}
			}
		}

		private void InitializeGamepadPolling()
		{
			_gamepadTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(16)
			};
			_gamepadTimer.Tick += GamepadTimer_Tick;
			_gamepadTimer.Start();
		}

		private void GamepadTimer_Tick(object? sender, EventArgs e)
		{
			if (!_isWindowActive)
				return;

			SDL.SDL_GameControllerUpdate();

			foreach (var kvp in _gameControllers)
			{
				int index = kvp.Key;
				IntPtr controller = kvp.Value;

				if (!_gamepadStates.ContainsKey(index))
					_gamepadStates[index] = new GamepadState();

				var currentState = _gamepadStates[index];
				var previousState = currentState.Clone();

				// Read analog stick
				short leftX = SDL.SDL_GameControllerGetAxis(controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTX);
				short leftY = SDL.SDL_GameControllerGetAxis(controller, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_LEFTY);

				currentState.LeftStickX = leftX / (float)AxisMax;
				currentState.LeftStickY = leftY / (float)AxisMax;

				// Apply deadzone
				if (Math.Abs(currentState.LeftStickX) < DeadZone)
					currentState.LeftStickX = 0;
				if (Math.Abs(currentState.LeftStickY) < DeadZone)
					currentState.LeftStickY = 0;

				// Read buttons
				currentState.AButton = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A) == 1;
				currentState.XButton = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X) == 1;
				currentState.DPadUp = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP) == 1;
				currentState.DPadDown = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN) == 1;
				currentState.DPadLeft = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT) == 1;
				currentState.DPadRight = SDL.SDL_GameControllerGetButton(controller, SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT) == 1;

				// Handle navigation from D-Pad
				if (currentState.DPadUp && !previousState.DPadUp)
					HandleNavigation(NavigationDirection.Up);
				else if (currentState.DPadDown && !previousState.DPadDown)
					HandleNavigation(NavigationDirection.Down);
				else if (currentState.DPadLeft && !previousState.DPadLeft)
					HandleNavigation(NavigationDirection.Left);
				else if (currentState.DPadRight && !previousState.DPadRight)
					HandleNavigation(NavigationDirection.Right);

				// Handle navigation from analog stick (with repeat)
				if (currentState.LeftStickY < -DeadZone)
					HandleNavigation(NavigationDirection.Up);
				else if (currentState.LeftStickY > DeadZone)
					HandleNavigation(NavigationDirection.Down);
				else if (currentState.LeftStickX < -DeadZone)
					HandleNavigation(NavigationDirection.Left);
				else if (currentState.LeftStickX > DeadZone)
					HandleNavigation(NavigationDirection.Right);
				else
					ResetNavigationTimer();

				// Handle A button (Confirm)
				if (currentState.AButton && !previousState.AButton)
				{
					var now = DateTime.Now;
					if ((now - _lastConfirmTime).TotalMilliseconds > ButtonRepeatDelay)
					{
						_lastConfirmTime = now;
						OnConfirm?.Invoke();
					}
				}

				// Handle X button (Cancel)
				if (currentState.XButton && !previousState.XButton)
				{
					var now = DateTime.Now;
					if ((now - _lastCancelTime).TotalMilliseconds > ButtonRepeatDelay)
					{
						_lastCancelTime = now;
						OnCancel?.Invoke();
					}
				}
			}
		}

		public void HandleNavigation(NavigationDirection direction)
		{
			var now = DateTime.Now;
			var timeSinceLastNav = (now - _lastNavigationTime).TotalMilliseconds;

			if (!_isInitialNavigation)
			{
				if (timeSinceLastNav < RepeatDelay)
					return;
			}
			else
			{
				if (timeSinceLastNav < InitialRepeatDelay && _lastNavigationTime != DateTime.MinValue)
					return;
				_isInitialNavigation = false;
			}

			_lastNavigationTime = now;

			var focused = TopLevel.GetTopLevel(_mainWindow)?.FocusManager?.GetFocusedElement() as Control;

			if (focused == null)
			{
				FocusFirstElement();
				return;
			}

			Control? nextControl = GetNextControl(focused, direction);

			if (nextControl != null && nextControl.Focusable)
			{
				nextControl.Focus();
			}

			OnNavigate?.Invoke(direction);
		}

		public void ResetNavigationTimer()
		{
			_isInitialNavigation = true;
			_lastNavigationTime = DateTime.MinValue;
		}

        private Control? GetNextControl(Control current, NavigationDirection direction)
        {
            var allControls = GetFocusableControls(_mainWindow);

            if (!allControls.Any())
                return null;

            var currentIndex = allControls.IndexOf(current);
            if (currentIndex == -1)
                return allControls.FirstOrDefault();

            var currentCenter = GetControlCenter(current);
            if (!currentCenter.HasValue)
                return GetNextControlSimple(allControls, currentIndex, direction);

            Control? bestCandidate = null;
            double bestScore = double.MaxValue;

            foreach (var candidate in allControls)
            {
                if (candidate == current)
                    continue;

                var candidateCenter = GetControlCenter(candidate);
                if (!candidateCenter.HasValue)
                    continue;

                var score = CalculateNavigationScore(currentCenter.Value, candidateCenter.Value, direction);
                if (score.HasValue && score.Value < bestScore)
                {
                    bestScore = score.Value;
                    bestCandidate = candidate;
                }
            }

            return bestCandidate ?? GetNextControlWithWrapping(allControls, currentIndex, direction, currentCenter.Value);
        }

        private Avalonia.Point? GetControlCenter(Control control)
        {
            var topLeft = control.TranslatePoint(new Avalonia.Point(0, 0), _mainWindow);
            if (!topLeft.HasValue)
                return null;

            var bounds = control.Bounds;
            return new Avalonia.Point(
                topLeft.Value.X + bounds.Width / 2,
                topLeft.Value.Y + bounds.Height / 2
            );
        }

        private double? CalculateNavigationScore(Avalonia.Point current, Avalonia.Point candidate, NavigationDirection direction)
        {
            double dx = candidate.X - current.X;
            double dy = candidate.Y - current.Y;

            bool isInDirection;
            double primaryDistance;
            double secondaryDistance;

            switch (direction)
            {
                case NavigationDirection.Up:
                    if (dy >= -1) return null;
                    isInDirection = true;
                    primaryDistance = Math.Abs(dy);
                    secondaryDistance = Math.Abs(dx);
                    break;
                case NavigationDirection.Down:
                    if (dy <= 1) return null;
                    isInDirection = true;
                    primaryDistance = Math.Abs(dy);
                    secondaryDistance = Math.Abs(dx);
                    break;
                case NavigationDirection.Left:
                    if (dx >= -1) return null;
                    isInDirection = true;
                    primaryDistance = Math.Abs(dx);
                    secondaryDistance = Math.Abs(dy);
                    break;
                case NavigationDirection.Right:
                    if (dx <= 1) return null;
                    isInDirection = true;
                    primaryDistance = Math.Abs(dx);
                    secondaryDistance = Math.Abs(dy);
                    break;
                default:
                    return null;
            }

            if (!isInDirection)
                return null;

            double offAxisPenalty = secondaryDistance > 10 ? secondaryDistance * 2.5 : 0;
            return primaryDistance + (secondaryDistance * 0.3) + offAxisPenalty;
        }

        private Control? GetNextControlWithWrapping(List<Control> allControls, int currentIndex, NavigationDirection direction, Avalonia.Point currentCenter)
		{
			// Try to wrap around in a smart way based on position
			Control? wrapCandidate = null;
			double bestWrapScore = double.MaxValue;

			foreach (var candidate in allControls)
			{
				var candidateTopLeft = candidate.TranslatePoint(new Avalonia.Point(0, 0), _mainWindow);
				if (!candidateTopLeft.HasValue)
					continue;

				var candidateBounds = candidate.Bounds;
				var candidateCenter = new Avalonia.Point(
					candidateTopLeft.Value.X + candidateBounds.Width / 2,
					candidateTopLeft.Value.Y + candidateBounds.Height / 2
				);

				double score = 0;

				switch (direction)
				{
					case NavigationDirection.Up:
						// Wrap to bottom-most control with similar X position
						score = Math.Abs(candidateCenter.X - currentCenter.X) + (10000 - candidateCenter.Y);
						break;
					case NavigationDirection.Down:
						// Wrap to top-most control with similar X position
						score = Math.Abs(candidateCenter.X - currentCenter.X) + candidateCenter.Y;
						break;
					case NavigationDirection.Left:
						// Wrap to right-most control with similar Y position
						score = Math.Abs(candidateCenter.Y - currentCenter.Y) + (10000 - candidateCenter.X);
						break;
					case NavigationDirection.Right:
						// Wrap to left-most control with similar Y position
						score = Math.Abs(candidateCenter.Y - currentCenter.Y) + candidateCenter.X;
						break;
				}

				if (score < bestWrapScore)
				{
					bestWrapScore = score;
					wrapCandidate = candidate;
				}
			}

			// If wrap candidate is still null or not good, fall back to simple navigation
			if (wrapCandidate == null)
			{
				return GetNextControlSimple(allControls, currentIndex, direction);
			}

			return wrapCandidate;
		}

		private Control? GetNextControlSimple(List<Control> allControls, int currentIndex, NavigationDirection direction)
		{
			switch (direction)
			{
				case NavigationDirection.Down:
				case NavigationDirection.Right:
					return allControls[(currentIndex + 1) % allControls.Count];

				case NavigationDirection.Up:
				case NavigationDirection.Left:
					return allControls[(currentIndex - 1 + allControls.Count) % allControls.Count];

				default:
					return null;
			}
		}

		private List<Control> GetFocusableControls(Control parent)
		{
			var controls = new List<Control>();

			if (parent.IsVisible && parent.IsEnabled && parent.Focusable)
			{
				controls.Add(parent);
			}

			// Check for open context menus
			if (parent is Button button && button.ContextMenu?.IsOpen == true)
			{
				foreach (var menuItem in button.ContextMenu.Items.OfType<MenuItem>())
				{
					if (menuItem.IsVisible && menuItem.IsEnabled)
					{
						controls.Add(menuItem);
					}
				}
				return controls; // Only return menu items when context menu is open
			}

			foreach (var child in parent.GetVisualChildren().OfType<Control>())
			{
				controls.AddRange(GetFocusableControls(child));
			}

			return controls;
		}

		private void FocusFirstElement()
		{
			var focusable = GetFocusableControls(_mainWindow).FirstOrDefault();
			if (focusable != null)
			{
				focusable.Focus();

				// Special handling for MenuItems
				if (focusable is MenuItem menuItem)
				{
					menuItem.IsSubMenuOpen = false; // Ensure submenu isn't open
				}
			}
		}
		public void SetWindowActive(bool isActive)
		{
			_isWindowActive = isActive;
		}
		public void Dispose()
		{
			if (!_disposed)
			{
				_gamepadTimer?.Stop();
				_gamepadTimer = null;

				foreach (var controller in _gameControllers.Values)
				{
					SDL.SDL_GameControllerClose(controller);
				}
				_gameControllers.Clear();

				SDL.SDL_Quit();
				_disposed = true;
			}
		}

		private class GamepadState
		{
			public float LeftStickX { get; set; }
			public float LeftStickY { get; set; }
			public bool AButton { get; set; }
			public bool XButton { get; set; }
			public bool DPadUp { get; set; }
			public bool DPadDown { get; set; }
			public bool DPadLeft { get; set; }
			public bool DPadRight { get; set; }

			public GamepadState Clone()
			{
				return new GamepadState
				{
					LeftStickX = this.LeftStickX,
					LeftStickY = this.LeftStickY,
					AButton = this.AButton,
					XButton = this.XButton,
					DPadUp = this.DPadUp,
					DPadDown = this.DPadDown,
					DPadLeft = this.DPadLeft,
					DPadRight = this.DPadRight
				};
			}
		}
	}

	public enum NavigationDirection
	{
		Up,
		Down,
		Left,
		Right
	}
}