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
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;

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
        public string FullScreen
            {
            get => _settings.StartFullscreen ? "Fullscreen" : "Normal";
            set
            {
                OnPropertyChanged(nameof(FullScreen));
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

            _gameManager.UnhideAllGames();
            LoadCurrentVersion();
            LoadCurrentPlatform();
            UpdateSettingsUI();

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
            _ = Task.Run(async () =>
            {
                try
                {
                    await _gameManager.LoadGamesAsync();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        DataContext = this;
                        UpdateContinueButtonState();

                        // Set initial focus after UI is loaded
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
            });
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
            Close();
        }

        public void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            IsFullscreen = !IsFullscreen;
            if (IsFullscreen)
            {
                WindowState = WindowState.FullScreen;
                FullScreen = "Fullscreen";
            }
            else
            {
                WindowState = WindowState.Normal;
                FullScreen = "Normal";
            }
        }

        private async void GameButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var game = button?.Tag as GameInfo;
            if (game != null)
            {
                try
                {
                    await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable, _settings);
                }
                catch (Exception ex)
                {
                    _ = ShowMessageBoxAsync($"Failed to perform action for {game.Name}: {ex.Message}", "Action Error");
                }
            }
            UpdateContinueButtonState();
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

                if (PortraitCheckBox != null)
                    PortraitCheckBox.IsChecked = _settings.PortraitFrame;

                if (ShowExperimentalCheckBox != null)
                    ShowExperimentalCheckBox.IsChecked = _settings.ShowExperimentalGames;

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

        private void PortraitCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.PortraitFrame = true;
                OnSettingChanged();
            }
        }

        private void PortraitCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_settings != null)
            {
                _settings.PortraitFrame = false;
                OnSettingChanged();
            }
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

        private void CheckforUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_app != null)
            {
                _app.OnFrameworkInitializationCompleted();
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

                        Directory.Delete(gamePath, true);

                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
                    }
                    else
                    {
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
                    }
                    if (_gameManager != null)
                    {
                        await _gameManager.LoadGamesAsync();
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
                    Process.Start("xdg-open", url);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", url);
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
                _gameManager.HideAllNonInstalledGames();
                await _gameManager.LoadGamesAsync();
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
                _gameManager.HideAllNonStableGames();
                await _gameManager.LoadGamesAsync();
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
                _gameManager.OnlyShowExperimentalGames();
                await _gameManager.LoadGamesAsync();
            }
            catch (Exception ex)
            {
                _ = ShowMessageBoxAsync($"Failed to hide stable games: {ex.Message}", "Error");
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

            _gamePathUpdateCts?.Cancel();
            _gamePathUpdateCts = new System.Threading.CancellationTokenSource();
            var token = _gamePathUpdateCts.Token;

            try
            {
                await Task.Delay(500, token);

                var newPath = textBox.Text?.Trim() ?? string.Empty;

                if (_settings.GamesPath == newPath)
                    return;

                _settings.GamesPath = newPath;
                OnSettingChanged();

                if (_gameManager != null)
                {
                    if (!string.IsNullOrEmpty(newPath))
                    {
                        if (!Directory.Exists(newPath))
                        {
                            var result = await ShowMessageBoxAsync(
                                $"The directory '{newPath}' does not exist. Create it?",
                                "Directory Not Found",
                                true);

                            if (!result)
                            {
                                // Revert to previous value
                                textBox.Text = _settings.GamesPath;
                                return;
                            }
                        }
                    }

                    await _gameManager.UpdateGamesFolderAsync(_settings.GamesPath);
                    await _gameManager.LoadGamesAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // User is still typing, ignore
            }
            catch (Exception ex)
            {
                await ShowMessageBoxAsync($"Failed to update games path: {ex.Message}", "Error");
                if (GamePathTextBox != null)
                    GamePathTextBox.Text = _settings.GamesPath;
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