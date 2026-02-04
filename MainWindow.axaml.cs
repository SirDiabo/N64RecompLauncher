using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace N64RecompLauncher
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly GameManager _gameManager;
        public ObservableCollection<GameInfo> Games => _gameManager?.Games ?? new ObservableCollection<GameInfo>();
        public AppSettings _settings;
        public App _app;
        public AppSettings Settings => _settings;
        private bool isSettingsPanelOpen = false;
        public string IconFillStretch = "Uniform";
        public bool IsFullscreen
        {
            get => _settings.StartFullscreen;
            set
            {
                if (_settings.StartFullscreen != value)
                {
                    _settings.StartFullscreen = value;
                    OnPropertyChanged(nameof(IsFullscreen));
                }
            }
        }
        private bool _showExperimentalGames;
        public bool ShowExperimentalGames
        {
            get => _showExperimentalGames;
            set
            {
                if (_showExperimentalGames != value)
                {
                    _showExperimentalGames = value;
                    OnPropertyChanged(nameof(ShowExperimentalGames));
                }
            }
        }
        private bool _showCustomGames;
        public bool ShowCustomGames
        {
            get => _showCustomGames;
            set
            {
                if (_showCustomGames != value)
                {
                    _showCustomGames = value;
                    OnPropertyChanged(nameof(ShowCustomGames));
                }
            }
        }
        private string _currentVersionString;
        public string currentVersionString
        {
            get => _currentVersionString;
            set
            {
                if (_currentVersionString != value)
                {
                    _currentVersionString = value;
                    OnPropertyChanged(nameof(currentVersionString));
                }
            }
        }
        private string _platformstring;
        public string PlatformString
        {             
            get => _platformstring;
            set
            {
                if (_platformstring != value)
                {
                    _platformstring = value;
                    OnPropertyChanged(nameof(PlatformString));
                }
            }
        }

        private bool _isContinueVisible;
        public bool IsContinueVisible
        {
            get => _isContinueVisible;
            set
            {
                if (_isContinueVisible != value)
                {
                    _isContinueVisible = value;
                    OnPropertyChanged(nameof(IsContinueVisible));
                }
            }
        }

        private InputService? _inputService;
        private bool _isProcessingInput = false;
        private bool _hasInitializedFocus = false;

        private GameInfo? _continueGameInfo;
        public GameInfo? ContinueGameInfo
        {
            get => _continueGameInfo;
            set
            {
                if (_continueGameInfo != value)
                {
                    _continueGameInfo = value;
                    OnPropertyChanged(nameof(ContinueGameInfo));
                }
            }
        }
        public string InfoTextLength = "*";
        private SolidColorBrush _themeColorBrush;
        public SolidColorBrush ThemeColorBrush
        {
            get => _themeColorBrush;
            set
            {
                if (_themeColorBrush != value)
                {
                    _themeColorBrush = value;
                    OnPropertyChanged(nameof(ThemeColorBrush));
                    UpdateThemeColors();
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                _settings = AppSettings.Load();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to load settings: {ex.Message}", "Settings Error");
                _settings = new AppSettings();
            }

            _gameManager = new GameManager();

            // Initialize theme BEFORE other UI setup
            ThemeColorBrush = new SolidColorBrush(Color.Parse(_settings?.ThemeColor ?? "#18181b"));
            UpdateThemeColors();

            _gameManager.UnhideAllGames();
            LoadCurrentVersion();
            LoadCurrentPlatform();
            UpdateSettingsUI();

            // Apply fullscreen from settings immediately
            if (_settings.StartFullscreen)
            {
                WindowState = WindowState.FullScreen;
            }

            _inputService = new InputService(this);
            _inputService.OnConfirm += HandleConfirmAction;
            _inputService.OnCancel += HandleCancelAction;

            this.KeyDown += MainWindow_KeyDown;
            this.KeyUp += MainWindow_KeyUp;

            this.Activated += MainWindow_Activated;
            this.Deactivated += MainWindow_Deactivated;

            _gameManager.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(GameManager.Games))
                {
                    OnPropertyChanged(nameof(Games));
                    UpdateContinueButtonState();
                    Debug.WriteLine($"Games collection changed. Count: {_gameManager.Games?.Count ?? 0}");
                    foreach (var game in _gameManager.Games ?? new())
                    {
                        Debug.WriteLine($"Game: {game.Name}, IconUrl: {game.IconUrl}");
                    }
                }
            };
        }

        // Is Theme Color Light
        private bool IsLightColor(Color color)
        {
            // Calculate perceived brightness using standard formula
            double brightness = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            return brightness > 0.5;
        }

        // Theme Color Shades
        private Color GetShadedColor(Color baseColor, double factor)
        {
            byte r = (byte)Math.Min(255, Math.Max(0, baseColor.R * factor));
            byte g = (byte)Math.Min(255, Math.Max(0, baseColor.G * factor));
            byte b = (byte)Math.Min(255, Math.Max(0, baseColor.B * factor));
            return Color.FromRgb(r, g, b);
        }

        // Update all Theme Colors
        private void UpdateThemeColors()
        {
            if (_themeColorBrush == null) return;

            var baseColor = _themeColorBrush.Color;
            bool isLight = IsLightColor(baseColor);

            // Update resources in THIS WINDOW, not Application
            if (this.Resources != null)
            {
                this.Resources["ThemeBase"] = new SolidColorBrush(baseColor);
                this.Resources["ThemeLighter"] = new SolidColorBrush(GetShadedColor(baseColor, 1.2));
                this.Resources["ThemeDarker"] = new SolidColorBrush(GetShadedColor(baseColor, 0.8));
                this.Resources["ThemeBorder"] = new SolidColorBrush(GetShadedColor(baseColor, 1.1));
                this.Resources["ThemeText"] = new SolidColorBrush(isLight ? Colors.Black : Colors.White);
                this.Resources["ThemeTextSecondary"] = new SolidColorBrush(isLight ? Color.FromRgb(90, 90, 90) : Color.FromRgb(180, 180, 180));
            }
        }

        // Color Picker Preset Dialog
        private async void ThemeColorPicker_Click(object sender, RoutedEventArgs e)
        {
            // Simple color presets dialog
            var presets = new Dictionary<string, string>
            {
                { "Black", "#000000" },
                { "Darker Gray", "#101010" },
                { "Dark Gray (Default)", "#18181b" },
                { "Charcoal Gray", "#2c2c2c" },
                { "Slate Gray", "#36454f" },

                { "Dark Blue", "#1e3a5f" },
                { "Navy Blue", "#0f2b46" },
                { "Deep Indigo", "#2c3e50" },
                { "Dark Greyish Blue", "#45475a" },
                { "Very dark (mostly black) Blue", "#19191c" },

                { "Dark Green", "#1a4d2e" },
                { "Darker Green", "#063204" },
                { "Forest Green", "#228b22" },
                { "Deep Moss Green", "#2c5f2d" },
                { "Fern Green", "#134411" },
                { "Black Forest Green", "#051D01" },
                { "Dark Olive Green", "#556b2f" },
                { "Dark Yello Olive", "#2a2922" },

                { "Dark Purple", "#2d1b4e" },
                { "Deep Plum", "#4b0082" },
                { "Dark Eggplant", "#614051" },

                { "Dark Red", "#4d1f1f" },
                { "Burgundy", "#800020" },
                { "Deep Maroon", "#5c0b0b" },

                { "Light Gray", "#e5e5e5" },
                { "Silver Gray", "#c0c0c0" },
                { "Pale Gray", "#f0f0f0" },

                { "Light Blue", "#d4e4f7" },
                { "Sky Blue", "#87ceeb" },
                { "Powder Blue", "#b0e0e6" },

                { "Light Green", "#d4f1e8" },
                { "Mint Green", "#98fb98" },
                { "Sea Foam Green", "#98ff98" }
            };

            await ShowColorPresetsDialog(presets);
        }

        private async Task ShowColorPresetsDialog(Dictionary<string, string> presets)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var stackPanel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

                    foreach (var preset in presets)
                    {
                        var button = new Button
                        {
                            Content = preset.Key,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Height = 40,
                            Background = new SolidColorBrush(Color.Parse(preset.Value)),
                            Foreground = new SolidColorBrush(IsLightColor(Color.Parse(preset.Value)) ? Colors.Black : Colors.White),
                            Tag = preset.Value
                        };

                        button.Click += (s, e) =>
                        {
                            var colorHex = (s as Button)?.Tag as string;
                            if (!string.IsNullOrEmpty(colorHex))
                            {
                                _settings.ThemeColor = colorHex;
                                ThemeColorBrush = new SolidColorBrush(Color.Parse(colorHex));
                                OnSettingChanged();

                                // Close the dialog after selection
                                if (s is Button btn && btn.Parent != null)
                                {
                                    var window = btn.GetVisualRoot() as Window;
                                    window?.Close();
                                }
                            }
                        };

                        stackPanel.Children.Add(button);
                    }

                    var messageBox = new Window
                    {
                        Title = "Select Theme Color",
                        Width = 300,
                        Height = 1000,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new ScrollViewer { Content = stackPanel }
                    };

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });
        }

        private void UpdateContinueButtonState()
        {
            ContinueGameInfo = _gameManager.GetLatestPlayedInstalledGame();
            IsContinueVisible = ContinueGameInfo != null;
        }

        private void LoadCurrentPlatform()
        {
            if (_settings != null)
            {
                PlatformString = _settings.Platform switch
                {
                    TargetOS.Auto => "Automatic",
                    TargetOS.Windows => "Windows",
                    TargetOS.MacOS => "macOS",
                    TargetOS.LinuxX64 => "Linux x64",
                    TargetOS.LinuxARM64 => "Linux ARM64",
                    TargetOS.Flatpak => "Flatpak",
                    _ => "Unknown"
                };
            }
            else
            {
                PlatformString = "Unknown";
            }
        }

        private void LoadCurrentVersion()
        {
            try
            {
                string currentAppDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string updateCheckFilePath = Path.Combine(currentAppDirectory, "update_check.json");

                if (File.Exists(updateCheckFilePath))
                {
                    try
                    {
                        var json = File.ReadAllText(updateCheckFilePath);
                        var updateInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                        if (updateInfo != null && updateInfo.TryGetValue("CurrentVersion", out var versionElement))
                        {
                            currentVersionString = versionElement.GetString() ?? "v0.0";
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to parse update_check.json: {ex.Message}");
                        // Fall through to version.txt check
                    }
                }

                // Fallback to version.txt
                string versionFilePath = Path.Combine(currentAppDirectory, "version.txt");
                if (File.Exists(versionFilePath))
                {
                    currentVersionString = File.ReadAllText(versionFilePath).Trim();
                }
                else
                {
                    currentVersionString = "v0.0";
                }
            }
            catch (Exception ex)
            {
                currentVersionString = "Unknown";
                Debug.WriteLine($"Failed to load version: {ex.Message}");
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _ = InitializeGamesAsync();
        }

        private async Task InitializeGamesAsync()
        {
            try
            {
                await _gameManager.LoadGamesAsync();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DataContext = this;
                    UpdateContinueButtonState();

                    if (!_hasInitializedFocus)
                    {
                        SetInitialFocus();
                        _hasInitializedFocus = true;
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _ = ShowMessageBoxAsync($"Failed to load games: {ex.Message}", "Load Error"));
            }
        }

        private void SetInitialFocus()
        {
            // Small delay to ensure UI is fully rendered
            Dispatcher.UIThread.Post(() =>
            {
                // Try to focus the Continue button if visible
                if (IsContinueVisible && this.FindControl<Button>("ContinueButton") is Button continueBtn)
                {
                    continueBtn.Focus();
                    return;
                }

                // Try Settings button
                if (this.FindControl<Button>("SettingsButton") is Button settingsBtn)
                {
                    settingsBtn.Focus();
                    return;
                }

                // Fallback to first focusable control
                var firstFocusable = this.GetVisualDescendants()
                    .OfType<Control>()
                    .FirstOrDefault(c => c.IsVisible && c.IsEnabled && c.Focusable);

                firstFocusable?.Focus();
            }, DispatcherPriority.Loaded);
        }

        public void CloseLauncher_Click(object sender, RoutedEventArgs e)
        {
            // Close settings panel if open
            if (isSettingsPanelOpen && SettingsPanel != null)
            {
                isSettingsPanelOpen = false;
                SettingsPanel.IsVisible = false;
                return;
            }

            Close();
        }

        public void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            IsFullscreen = !IsFullscreen;
            if (IsFullscreen)
            {
                WindowState = WindowState.FullScreen;
            }
            else
            {
                WindowState = WindowState.Normal;
            }
        }

        public void LayoutPreset_Landscape_Click(object sender, RoutedEventArgs e)
        {
            _settings.IconFill = true;
            _settings.UseGridView = true;
            _settings.SlotSize = 276;
            _settings.IconSize = 200;
            _settings.IconMargin = 0;
            _settings.SlotTextMargin = 0;
            _settings.IconOpacity = 1.0f;
            InfoTextLength = "90";
            OnSettingChanged();
            UpdateSettingsUI();
        }

        public void LayoutPreset_Portrait_Click(object sender, RoutedEventArgs e)
        {
            _settings.IconFill = true;
            _settings.UseGridView = true;
            _settings.SlotSize = 144;
            _settings.IconSize = 200;
            _settings.IconMargin = 0;
            _settings.SlotTextMargin = 0;
            _settings.IconOpacity = 1.0f;
            InfoTextLength = "90";
            OnSettingChanged();
            UpdateSettingsUI();
        }

        public void LayoutPreset_Square_Click(object sender, RoutedEventArgs e)
        {
            _settings.IconFill = true;
            _settings.UseGridView = true;
            _settings.SlotSize = 208;
            _settings.IconSize = 208;
            _settings.IconMargin = 0;
            _settings.SlotTextMargin = 0;
            _settings.IconOpacity = 1.0f;
            InfoTextLength = "*";
            OnSettingChanged();
            UpdateSettingsUI();
        }

        public void LayoutPreset_Grid_Click(object sender, RoutedEventArgs e)
        {
            _settings.IconFill = true;
            _settings.UseGridView = true;
            _settings.SlotSize = 272;
            _settings.IconSize = 200;
            _settings.IconMargin = 0;
            _settings.SlotTextMargin = 0;
            _settings.IconOpacity = 1.0f;
            InfoTextLength = "*";
            OnSettingChanged();
            UpdateSettingsUI();
        }

        public void LayoutPreset_List_Click(object sender, RoutedEventArgs e)
        {
            _settings.IconFill = false;
            _settings.UseGridView = false;
            _settings.SlotSize = 120;
            _settings.IconSize = 116;
            _settings.IconMargin = 8;
            _settings.SlotTextMargin = 112;
            _settings.IconOpacity = 1.0f;
            InfoTextLength = "*";
            OnSettingChanged();
            UpdateSettingsUI();
        }

        private async void GameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is GameInfo game)
            {
                try
                {
                    await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable, _settings);

                    // Check if multiple downloads need selection
                    if ((game.Status == GameStatus.NotInstalled || game.Status == GameStatus.UpdateAvailable) &&
                        game.HasMultipleDownloads && game.SelectedDownload == null)
                    {
                        if (button != null)
                        {
                            ShowDownloadSelectionMenu(button, game);
                        }
                        return;
                    }

                    // Check if multiple executables need selection
                    if (game.Status == GameStatus.Installed && game.HasMultipleExecutables && string.IsNullOrEmpty(game.SelectedExecutable))
                    {
                        if (button != null)
                        {
                            ShowExecutableSelectionMenu(button, game);
                        }
                        return;
                    }

                    UpdateContinueButtonState();
                }
                catch (Exception ex)
                {
                    await ShowMessageBoxAsync($"Failed to perform action for {game.Name}: {ex.Message}", "Action Error");
                }
            }
        }

        private void ShowDownloadSelectionMenu(Button sourceButton, GameInfo game)
        {
            if (game.AvailableDownloads == null || game.AvailableDownloads.Count == 0)
                return;

            var contextMenu = new ContextMenu();

            // Add header
            var headerItem = new MenuItem
            {
                Header = "Select download file:",
                IsEnabled = false,
                FontWeight = FontWeight.Bold
            };
            contextMenu.Items.Add(headerItem);
            contextMenu.Items.Add(new Separator());

            // Get platform identifier for matching
            string platformIdentifier = GameInfo.GetPlatformIdentifier(_settings);

            // Sort downloads: preferred platform first, then others
            var sortedDownloads = game.AvailableDownloads
                .OrderByDescending(asset => GameInfo.MatchesPlatform(asset.name, platformIdentifier))
                .ToList();

            // Add download options
            foreach (var asset in sortedDownloads)
            {
                bool isPreferred = GameInfo.MatchesPlatform(asset.name, platformIdentifier);

                // Detect platform icon
                string? iconPath = GameInfo.GetPlatformIcon(asset.name);

                // Create grid
                var contentGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // Add icon if detected
                if (!string.IsNullOrEmpty(iconPath))
                {
                    var icon = new Avalonia.Controls.Image
                    {
                        Source = new Avalonia.Media.Imaging.Bitmap(
                            Avalonia.Platform.AssetLoader.Open(new Uri(iconPath))),
                        Width = 28,
                        Height = 28,
                        Margin = new Thickness(0, 0, 10, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(icon, 0);
                    contentGrid.Children.Add(icon);
                }

                // Add filename text
                var displayName = asset.name + (isPreferred ? " (Recommended)" : "");
                var textBlock = new TextBlock
                {
                    Text = displayName,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(textBlock, 1);
                contentGrid.Children.Add(textBlock);

                var menuItem = new MenuItem
                {
                    Header = contentGrid,
                    Tag = asset
                };

                if (isPreferred)
                {
                    menuItem.Classes.Add("accent");
                }

                menuItem.Click += async (s, e) =>
                {
                    var selectedAsset = (s as MenuItem)?.Tag as GitHubAsset;
                    game.SelectedDownload = selectedAsset;
                    try
                    {
                        await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable, _settings);
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageBoxAsync($"Failed to download {game.Name}: {ex.Message}", "Download Error");
                    }
                };

                contextMenu.Items.Add(menuItem);
            }

            contextMenu.Items.Add(new Separator());

            // Add cancel option
            var cancelItem = new MenuItem
            {
                Header = "Cancel"
            };
            cancelItem.Click += (s, e) =>
            {
                game.SelectedDownload = null;
            };
            contextMenu.Items.Add(cancelItem);

            // Attach to button and open
            sourceButton.ContextMenu = contextMenu;
            contextMenu.PlacementTarget = sourceButton;
            contextMenu.Placement = PlacementMode.Bottom;

            // Focus first download item when opened
            contextMenu.Opened += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var firstDownloadItem = contextMenu.Items.OfType<MenuItem>()
                        .Skip(1)
                        .FirstOrDefault(item => item is MenuItem mi && mi.IsEnabled);
                    firstDownloadItem?.Focus();
                }, DispatcherPriority.Loaded);
            };

            contextMenu.Open(sourceButton);
        }

        private void ShowExecutableSelectionMenu(Button sourceButton, GameInfo game)
        {
            if (game.AvailableExecutables == null || game.AvailableExecutables.Count == 0)
                return;

            var contextMenu = new ContextMenu();

            // Add header
            var headerItem = new MenuItem
            {
                Header = "Select executable to launch:",
                IsEnabled = false,
                FontWeight = FontWeight.Bold
            };
            contextMenu.Items.Add(headerItem);
            contextMenu.Items.Add(new Separator());

            // Add executable options
            foreach (var exe in game.AvailableExecutables)
            {
                var displayName = Path.GetFileName(exe);
                var menuItem = new MenuItem
                {
                    Header = displayName,
                    Tag = exe
                };

                menuItem.Click += async (s, e) =>
                {
                    var selectedExe = (s as MenuItem)?.Tag as string;
                    game.SelectedExecutable = selectedExe;
                    try
                    {
                        await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable, _settings);
                    }
                    catch (Exception ex)
                    {
                        await ShowMessageBoxAsync($"Failed to launch {game.Name}: {ex.Message}", "Launch Error");
                    }
                };

                contextMenu.Items.Add(menuItem);
            }

            contextMenu.Items.Add(new Separator());

            // Add cancel option
            var cancelItem = new MenuItem
            {
                Header = "Cancel"
            };
            cancelItem.Click += (s, e) =>
            {
                game.SelectedExecutable = null;
            };
            contextMenu.Items.Add(cancelItem);

            // Attach to button and open
            sourceButton.ContextMenu = contextMenu;
            contextMenu.PlacementTarget = sourceButton;
            contextMenu.Placement = PlacementMode.Bottom;

            // Focus first executable item when opened
            contextMenu.Opened += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var firstExecutableItem = contextMenu.Items.OfType<MenuItem>()
                        .Skip(1)
                        .FirstOrDefault(item => item is MenuItem mi && mi.IsEnabled);
                    firstExecutableItem?.Focus();
                }, DispatcherPriority.Loaded);
            };

            contextMenu.Open(sourceButton);
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = PlacementMode.Bottom;
                button.ContextMenu.Open();
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            var latestGame = _gameManager.GetLatestPlayedInstalledGame();
            if (latestGame != null)
            {
                try
                {
                    _ = latestGame.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable, _settings);
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to launch {latestGame.Name}: {ex.Message}", "Launch Error");
                }
            }
            else
            {
                _ = ShowMessageBoxAsync("No installed games found to continue.", "No Game Found");
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            isSettingsPanelOpen = !isSettingsPanelOpen;
            SettingsPanel.IsVisible = isSettingsPanelOpen;

            if (isSettingsPanelOpen)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var firstSettingsControl = SettingsContent?.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(c => c.IsVisible && c.IsEnabled && c.Focusable);
                    firstSettingsControl?.Focus();
                }, DispatcherPriority.Loaded);
            }
        }

        private void UpdateSettingsUI()
        {
            if (_settings != null)
            {
                if (PortableCheckBox != null)
                    PortableCheckBox.IsChecked = _settings.IsPortable;

                if (IconOpacitySlider != null)
                    IconOpacitySlider.Value = _settings.IconOpacity;

                if (IconSizeSlider != null)
                    IconSizeSlider.Value = _settings.IconSize;

                if (IconFillCheckBox != null)
                    IconFillCheckBox.IsChecked = _settings.IconFill;

                if (TextMarginSlider != null)
                    TextMarginSlider.Value = _settings.SlotTextMargin;

                if (IconMarginSlider != null)
                    IconMarginSlider.Value = _settings.IconMargin;

                if (SlotSizeSlider != null)
                    SlotSizeSlider.Value = _settings.SlotSize;

                if (UseGridViewCheckBox != null)
                    UseGridViewCheckBox.IsChecked = _settings.UseGridView;

                if (ShowExperimentalCheckBox != null)
                    ShowExperimentalCheckBox.IsChecked = _settings.ShowExperimentalGames;

                if (ShowCustomGamesCheckBox != null)
                    ShowCustomGamesCheckBox.IsChecked = _settings.ShowCustomGames;

                if (GitHubTokenTextBox != null)
                    GitHubTokenTextBox.Text = _settings.GitHubApiToken;

                if (GamePathTextBox != null)
                    GamePathTextBox.Text = _settings.GamesPath;

                if (StartFullscreenCheckBox != null)
                    StartFullscreenCheckBox.IsChecked = _settings.StartFullscreen;

                PlatformString = _settings.Platform switch
                {
                    TargetOS.Auto => "Automatic",
                    TargetOS.Windows => "Windows",
                    TargetOS.MacOS => "macOS",
                    TargetOS.LinuxX64 => "Linux x64",
                    TargetOS.LinuxARM64 => "Linux ARM64",
                    TargetOS.Flatpak => "Flatpak",
                    _ => "Unknown"
                };

                // Initialize theme
                ThemeColorBrush = new SolidColorBrush(Color.Parse(_settings?.ThemeColor ?? "#18181b"));
                UpdateThemeColors();
            }
        }

        private void OnSettingChanged()
        {
            try
            {
                AppSettings.Save(_settings);
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to save settings: {ex.Message}", "Save Error");
            }
        }

        private void StartFullscreenCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.StartFullscreen = true;
                OnSettingChanged();
            }
        }

        private void StartFullscreenCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.StartFullscreen = false;
                OnSettingChanged();
            }
        }

        private void UseGridViewCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _settings.UseGridView = true;
            OnSettingChanged();
        }

        private void UseGridViewCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _settings.UseGridView = false;
            OnSettingChanged();
        }

        private void PortableCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IsPortable = true;
                OnSettingChanged();
            }
        }

        private void PortableCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IsPortable = false;
                OnSettingChanged();
            }
        }

        private void SlotSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.SlotSize = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void IconOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IconOpacity = (float)e.NewValue;
                OnSettingChanged();
            }
        }

        private void IconSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IconSize = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void IconMarginSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IconMargin = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void TextMarginSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.SlotTextMargin = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void IconFillCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IconFill = true;
                OnSettingChanged();
            }
        }

        private void IconFillCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.IconFill = false;
                OnSettingChanged();
            }
        }

        private void PlatformAuto_Click(object sender, RoutedEventArgs e)
        {             
            if (_settings != null)
            {
                _settings.Platform = TargetOS.Auto;
                PlatformString = "Automatic";
                OnSettingChanged();
            }
        }

        private void PlatformWindows_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.Platform = TargetOS.Windows;
                PlatformString = "Windows";
                OnSettingChanged();
            }
        }

        private void PlatformMacOS_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.Platform = TargetOS.MacOS;
                PlatformString = "macOS";
                OnSettingChanged();
            }
        }

        private void PlatformLinuxX64_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.Platform = TargetOS.LinuxX64;
                PlatformString = "Linux x64";
                OnSettingChanged();
            }
        }

        private void PlatformLinuxARM64_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.Platform = TargetOS.LinuxARM64;
                PlatformString = "Linux ARM64";
                OnSettingChanged();
            }
        }

        private void PlatformFlatpak_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.Platform = TargetOS.Flatpak;
                PlatformString = "Flatpak";
                OnSettingChanged();
            }
        }

        private async void CheckforUpdates_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = sender as Button;
                bool wasEnabled = button?.IsEnabled ?? true;
                string originalContent = string.Empty;

                if (button != null)
                {
                    // Store original content
                    if (button.Content is StackPanel panel)
                    {
                        originalContent = "original_stackpanel";
                    }

                    button.IsEnabled = false;

                    // Create a temporary text block for status
                    var statusText = new TextBlock
                    {
                        Text = "Checking launcher...",
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    button.Content = statusText;
                }

                // Check for app updates
                if (_app != null)
                {
                    await _app.CheckForAppUpdatesManually();
                }

                // Update status text
                if (button?.Content is TextBlock textBlock)
                {
                    textBlock.Text = "Checking games...";
                }

                // Check game updates
                await _gameManager.CheckAllUpdatesAsync();

                // Restore original button state
                if (button != null)
                {
                    button.IsEnabled = true;

                    // Restore original content
                    button.Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                {
                    new Image
                    {
                        Width = 32,
                        Height = 32,
                        Source = new Avalonia.Media.Imaging.Bitmap(
                            Avalonia.Platform.AssetLoader.Open(
                                new Uri("avares://N64RecompLauncher/Assets/CheckForUpdates.png"))),
                        Margin = new Thickness(0, 0, 12, 0)
                    },
                    new TextBlock
                    {
                        Text = "Check for Updates",
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
                    };
                }

                await ShowMessageBoxAsync("Update check completed!", "Updates");
            }
            catch (Exception ex)
            {
                var button = sender as Button;
                if (button != null)
                {
                    button.IsEnabled = true;

                    // Restore original content on error
                    button.Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                {
                    new Image
                    {
                        Width = 32,
                        Height = 32,
                        Source = new Avalonia.Media.Imaging.Bitmap(
                            Avalonia.Platform.AssetLoader.Open(
                                new Uri("avares://N64RecompLauncher/Assets/CheckForUpdates.png"))),
                        Margin = new Thickness(0, 0, 12, 0)
                    },
                    new TextBlock
                    {
                        Text = "Check for Updates",
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
                    };
                }
                await ShowMessageBoxAsync($"Failed to check for updates: {ex.Message}", "Error");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.CommandParameter as GameInfo;
            if (game != null)
            {
                try
                {
                    string folderPath = Path.Combine(_gameManager.GamesFolder, game.FolderName);
                    OpenUrl(folderPath);
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to open folder: {ex.Message}", "Action Error");
                }
            }
        }

        private void GithubButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = "https://github.com/SirDiabo/N64RecompLauncher/";
                OpenUrl(url);
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to open Github link: {ex.Message}", "Action Error");
            }
        }

        private void DiscordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = "https://discord.gg/DptggHetGZ";
                OpenUrl(url);
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to open Discord link: {ex.Message}", "Action Error");
            }
        }

        private void OpenGitHubPage_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.CommandParameter as GameInfo;

            if (game != null && !string.IsNullOrEmpty(game.Repository))
            {
                try
                {
                    var githubUrl = $"https://github.com/{game.Repository}";
                    OpenUrl(githubUrl);
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to open GitHub page: {ex.Message}", "Error");
                }
            }
            else _ = ShowMessageBoxAsync($"Failed to open GitHub page", "Error");
        }

        private async void SetCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var selectedGame = menuItem?.CommandParameter as GameInfo;
            if (selectedGame == null)
            {
                _ = ShowMessageBoxAsync("Unable to identify the selected game.", "Error");
                return;
            }

            bool hasExistingCustomIcon = !string.IsNullOrEmpty(selectedGame.CustomIconPath);
            string confirmMessage = hasExistingCustomIcon
                ? $"Replace the existing custom icon for {selectedGame.Name}?"
                : $"Set custom icon for {selectedGame.Name}?";

            if (hasExistingCustomIcon)
            {
                var confirmResult = await ShowMessageBoxAsync(confirmMessage, "Confirm Icon Replacement", true);
                if (!confirmResult)
                    return;
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Select Custom Icon for {selectedGame.Name}",
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif", "*.ico" }
                    },
                    new FilePickerFileType("PNG Files") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG Files") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("WebP Files") { Patterns = new[] { "*.webp" } },
                    new FilePickerFileType("Bitmap Files") { Patterns = new[] { "*.bmp" } },
                    new FilePickerFileType("Icon Files") { Patterns = new[] { "*.ico" } },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                },
                AllowMultiple = false
            });

            if (files?.Count > 0)
            {
                try
                {
                    var filePath = files[0].Path.LocalPath;
                    selectedGame.SetCustomIcon(filePath, _gameManager.CacheFolder);
                }
                catch (Exception ex)
                {
                    string errorMessage = hasExistingCustomIcon
                        ? $"Failed to replace custom icon: {ex.Message}"
                        : $"Failed to set custom icon: {ex.Message}";
                    _ = ShowMessageBoxAsync(errorMessage, "Error");
                }
            }
        }

        private async void RemoveCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var selectedGame = menuItem?.CommandParameter as GameInfo;
            if (selectedGame == null)
            {
                _ = ShowMessageBoxAsync("Unable to identify the selected game.", "Error");
                return;
            }

            if (string.IsNullOrEmpty(selectedGame.CustomIconPath))
            {
                _ = ShowMessageBoxAsync($"{selectedGame.Name} is already using the default icon.", "No Custom Icon");
                return;
            }

            var result = await ShowMessageBoxAsync($"Remove custom icon for {selectedGame.Name}?", "Confirm Removal", true);
            if (result)
            {
                try
                {
                    selectedGame.RemoveCustomIcon();
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to remove custom icon: {ex.Message}", "Error");
                }
            }
        }

        private async void DeleteGame_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.CommandParameter as GameInfo;

            if (game == null) return;

            if (game.Status == GameStatus.NotInstalled)
            {
                _ = ShowMessageBoxAsync($"{game.Name} is not installed.", "Nothing to Delete");
                return;
            }

            var result = await ShowMessageBoxAsync(
                $"Are you sure you want to delete {game.Name}?\n\nThis will permanently remove all game files and cannot be undone.",
                "Confirm Deletion",
                true);

            if (result)
            {
                try
                {
                    var gamePath = Path.Combine(_gameManager.GamesFolder, game.FolderName);

                    if (Directory.Exists(gamePath))
                    {
                        game.Status = GameStatus.Installing;
                        game.IsLoading = true;

                        await Task.Run(() => Directory.Delete(gamePath, true));

                        game.IsLoading = false;
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);

                        UpdateContinueButtonState();
                    }
                    else
                    {
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
                    }
                }
                catch (Exception ex)
                {
                    game.IsLoading = false;
                    _ = ShowMessageBoxAsync($"Failed to delete {game.Name}: {ex.Message}", "Deletion Failed");
                }
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", $"\"{url}\"");
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", $"\"{url}\"");
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to open URL: {ex.Message}", "Error");
            }
        }

        private async Task ShowMessageBoxAsync(string message, string title)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = title,
                        Width = 400,
                        Height = 150,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) },
                        new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center }
                    }
                        }
                    };
                    var okButton = ((StackPanel)messageBox.Content).Children[1] as Button;
                    okButton.Click += (s, e) => messageBox.Close();
                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });
        }

        private async void UnhideAllGamesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _gameManager.UnhideAllGames();
                await _gameManager.LoadGamesAsync();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to unhide games: {ex.Message}", "Error");
            }
        }

        private async void HideNonInstalledButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.HideAllNonInstalledGames();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to hide non-installed games: {ex.Message}", "Error");
            }
        }

        private async void HideNonStableButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.HideAllNonStableGames();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to hide non-stable games: {ex.Message}", "Error");
            }
        }

        private async void OnlyExperimentalGamesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.OnlyShowExperimentalGames();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to hide stable games: {ex.Message}", "Error");
            }
        }

        private async void OnlyCustomGamesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.OnlyShowCustomGames();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to hide non-custom games: {ex.Message}", "Error");
            }
        }

        private async void HideGame_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.CommandParameter as GameInfo;

            if (game == null)
            {
                _ = ShowMessageBoxAsync("Unable to identify the selected game.", "Error");
                return;
            }

            var result = await ShowMessageBoxAsync($"Hide {game.Name} from the game list?", "Confirm Hide", true);
            if (result)
            {
                try
                {
                    _gameManager.HideGame(game.Name);
                    await _gameManager.LoadGamesAsync();
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to hide game: {ex.Message}", "Error");
                }
            }
        }

        private async Task<bool> ShowMessageBoxAsync(string message, string title, bool isQuestion = false)
        {
            if (!isQuestion)
            {
                await ShowMessageBoxAsync(message, title);
                return true;
            }

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    bool result = false;
                    var messageBox = new Window
                    {
                        Title = title,
                        Width = 450,
                        Height = 170,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 20)
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Children =
                            {
                                new Button
                                {
                                    Content = "Yes",
                                    Margin = new Thickness(0, 0, 10, 0),
                                    MinWidth = 80
                                },
                                new Button
                                {
                                    Content = "No",
                                    MinWidth = 80
                                }
                            }
                        }
                    }
                        }
                    };

                    var buttonPanel = ((StackPanel)messageBox.Content).Children[1] as StackPanel;
                    var yesButton = buttonPanel.Children[0] as Button;
                    var noButton = buttonPanel.Children[1] as Button;

                    yesButton.Click += (s, e) =>
                    {
                        result = true;
                        messageBox.Close();
                    };

                    noButton.Click += (s, e) =>
                    {
                        result = false;
                        messageBox.Close();
                    };

                    await messageBox.ShowDialog(desktop.MainWindow);
                    return result;
                }
                return false;
            });
        }

        private async void ShowExperimentalCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                ShowExperimentalGames = true;
                _settings.ShowExperimentalGames = true;
                OnSettingChanged();
                await _gameManager.LoadGamesAsync();
            }
        }

        private async void ShowExperimentalCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                ShowExperimentalGames = false;
                _settings.ShowExperimentalGames = false;
                OnSettingChanged();
                await _gameManager.LoadGamesAsync();
            }
        }

        private async void ShowCustomGamesCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                ShowCustomGames = true;
                _settings.ShowCustomGames = true;
                OnSettingChanged();
                await _gameManager.LoadGamesAsync();
            }
        }

        private async void ShowCustomGamesCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                ShowCustomGames = false;
                _settings.ShowCustomGames = false;
                OnSettingChanged();
                await _gameManager.LoadGamesAsync();
            }
        }

        private void GitHubTokenTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings != null && sender is TextBox textBox)
            {
                _settings.GitHubApiToken = textBox.Text ?? string.Empty;
                OnSettingChanged();
            }
        }

        private System.Threading.CancellationTokenSource? _gamePathUpdateCts;

        private async void GamePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settings == null || sender is not TextBox textBox)
                return;

            // Cancel previous update
            var oldCts = _gamePathUpdateCts;
            _gamePathUpdateCts = new System.Threading.CancellationTokenSource();

            // Dispose old token after a delay
            if (oldCts != null)
            {
                var tokenToDispose = oldCts;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    tokenToDispose.Dispose();
                });
            }

            try
            {
                await Task.Delay(500, _gamePathUpdateCts.Token);
                await UpdateGamePath(textBox.Text?.Trim() ?? string.Empty, textBox);
            }
            catch (OperationCanceledException)
            {
                // User is still typing
            }
        }

        private async Task UpdateGamePath(string newPath, TextBox textBox)
        {
            if (_settings.GamesPath == newPath)
                return;

            _settings.GamesPath = newPath;
            OnSettingChanged();

            if (_gameManager == null)
                return;

            if (!string.IsNullOrEmpty(newPath) && !Directory.Exists(newPath))
            {
                var result = await ShowMessageBoxAsync(
                    $"The directory '{newPath}' does not exist. Create it?",
                    "Directory Not Found",
                    true);

                if (!result)
                {
                    textBox.Text = _settings.GamesPath;
                    return;
                }
            }

            try
            {
                await _gameManager.UpdateGamesFolderAsync(_settings.GamesPath);
                await _gameManager.LoadGamesAsync();
            }
            catch (Exception ex)
            {
                await ShowMessageBoxAsync($"Failed to update games path: {ex.Message}", "Error");
                textBox.Text = _settings.GamesPath;
            }
        }

        private void ClearGitHubToken_Click(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.GitHubApiToken = string.Empty;
                if (GitHubTokenTextBox != null)
                    GitHubTokenTextBox.Text = string.Empty;
                OnSettingChanged();
                _ = ShowMessageBoxAsync("GitHub API token cleared.", "Token Cleared");
            }
        }

        private async void ClearGamePath_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null)
                return;

            try
            {
                _settings.GamesPath = string.Empty;
                if (GamePathTextBox != null)
                    GamePathTextBox.Text = string.Empty;
                OnSettingChanged();

                if (_gameManager != null)
                {
                    await _gameManager.UpdateGamesFolderAsync(string.Empty);
                    await _gameManager.LoadGamesAsync();
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to clear games path: {ex.Message}", "Error");
            }
        }

        private async void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Games Folder",
                    AllowMultiple = false
                });

                if (folders?.Count > 0)
                {
                    var selectedPath = folders[0].Path.LocalPath;

                    if (!Directory.Exists(selectedPath))
                    {
                        _ = ShowMessageBoxAsync("Selected folder does not exist.", "Invalid Selection");
                        return;
                    }

                    if (_settings != null)
                    {
                        _settings.GamesPath = selectedPath;

                        if (GamePathTextBox != null)
                            GamePathTextBox.Text = selectedPath;

                        OnSettingChanged();

                        if (_gameManager != null)
                        {
                            await _gameManager.UpdateGamesFolderAsync(selectedPath);
                            await _gameManager.LoadGamesAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to select folder: {ex.Message}", "Error");
            }
        }

        private async void ClearIconCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.ClearIconCacheAsync();
                await ShowMessageBoxAsync("Icon cache cleared. Icons will be re-downloaded.", "Cache Cleared");
            }
            catch (Exception ex)
            {
                await ShowMessageBoxAsync($"Failed to clear icon cache: {ex.Message}", "Error");
            }
        }

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_isProcessingInput || !IsActive)
                return;

            _isProcessingInput = true;

            try
            {
                switch (e.Key)
                {
                    case Key.Space:
                        HandleConfirmAction();
                        e.Handled = true;
                        break;

                    case Key.LeftShift:
                        HandleCancelAction();
                        e.Handled = true;
                        break;

                    case Key.Up:
                        _inputService?.HandleNavigation(Services.NavigationDirection.Up);
                        e.Handled = true;
                        break;

                    case Key.Down:
                        _inputService?.HandleNavigation(Services.NavigationDirection.Down);
                        e.Handled = true;
                        break;

                    case Key.Left:
                        _inputService?.HandleNavigation(Services.NavigationDirection.Left);
                        e.Handled = true;
                        break;

                    case Key.Right:
                        _inputService?.HandleNavigation(Services.NavigationDirection.Right);
                        e.Handled = true;
                        break;
                }
            }
            finally
            {
                _isProcessingInput = false;
            }
        }

        private void MainWindow_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
            {
                _inputService?.ResetNavigationTimer();
            }
        }

        private void HandleConfirmAction()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

            if (focused is MenuItem menuItem)
            {
                // Trigger the menu item click
                menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            else if (focused is Button button)
            {
                // If button has a context menu, open it
                if (button.ContextMenu != null)
                {
                    button.ContextMenu.PlacementTarget = button;
                    button.ContextMenu.Placement = PlacementMode.Bottom;
                    button.ContextMenu.Open();

                    // Focus first menu item after opening
                    Dispatcher.UIThread.Post(() =>
                    {
                        var firstMenuItem = button.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault();
                        firstMenuItem?.Focus();
                    }, DispatcherPriority.Loaded);
                }
                else
                {
                    // Normal button click
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            else if (focused is CheckBox checkBox)
            {
                checkBox.IsChecked = !checkBox.IsChecked;
            }
            else if (focused is ToggleButton toggleButton)
            {
                toggleButton.IsChecked = !toggleButton.IsChecked;
            }
        }

        private void HandleCancelAction()
        {
            // First check if any context menu is open and close it
            var allButtons = this.GetVisualDescendants().OfType<Button>();
            foreach (var button in allButtons)
            {
                if (button.ContextMenu?.IsOpen == true)
                {
                    button.ContextMenu.Close();
                    button.Focus(); // Return focus to the button
                    return;
                }
            }

            // Close settings panel if open
            if (isSettingsPanelOpen && SettingsPanel != null)
            {
                isSettingsPanelOpen = false;
                SettingsPanel.IsVisible = false;

                // Return focus to settings button
                var settingsButton = this.FindControl<Button>("SettingsButton");
                if (settingsButton != null)
                {
                    settingsButton.Focus();
                }
                else
                {
                    // Fallback: focus the first focusable element outside settings panel
                    var firstFocusable = this.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(c => c.IsVisible && c.IsEnabled && c.Focusable && !IsInsideSettingsPanel(c));
                    firstFocusable?.Focus();
                }
                return;
            }
        }

        private bool IsInsideSettingsPanel(Control control)
        {
            var parent = control.Parent;
            while (parent != null)
            {
                if (parent == SettingsPanel)
                    return true;
                parent = parent.Parent;
            }
            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // Unsubscribe from events
            this.Activated -= MainWindow_Activated;
            this.Deactivated -= MainWindow_Deactivated;

            if (_inputService != null)
            {
                _inputService.OnConfirm -= HandleConfirmAction;
                _inputService.OnCancel -= HandleCancelAction;
                _inputService.Dispose();
            }
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            _inputService?.SetWindowActive(true);
        }

        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            _inputService?.SetWindowActive(false);
        }

        public new event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
