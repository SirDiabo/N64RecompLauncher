using Microsoft.Win32;
using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace N64RecompLauncher
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly GameManager _gameManager;
        private AppSettings _settings;
        private bool isSettingsPanelOpen = false;
        public string IconFillStretch = "Uniform";

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                _settings = AppSettings.Load();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                _settings = new AppSettings();
            }

            try
            {
                _gameManager = new GameManager();
                DataContext = _gameManager;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize GameManager: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            UpdateSettingsUI();

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _gameManager.LoadGamesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load games: {ex.Message}", "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder, _settings.IsPortable);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to perform action for {game.Name}: {ex.Message}", "Action Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (isSettingsPanelOpen)
            {
                var collapseStoryboard = (Storyboard)FindResource("CollapseSettingsPanel");
                collapseStoryboard.Completed += (s, args) =>
                {
                    SettingsPanelColumn.Width = new GridLength(0);
                };
                collapseStoryboard.Begin();
            }
            else
            {
                SettingsPanelColumn.Width = new GridLength(300);
                var expandStoryboard = (Storyboard)FindResource("ExpandSettingsPanel");
                expandStoryboard.Begin();
            }

            isSettingsPanelOpen = !isSettingsPanelOpen;
        }

        private void UpdateSettingsUI()
        {
            if (_settings != null)
            {
                if (PortableCheckBox != null)
                    PortableCheckBox.IsChecked = _settings.IsPortable;
                if (IconSizeSlider != null)
                    IconSizeSlider.Value = _settings.IconSize;
                if (IconFillCheckBox != null)
                    IconFillCheckBox.IsChecked = _settings.IconFill;
                if (TextMarginSlider != null)
                    TextMarginSlider.Value = _settings.SlotTextMargin;
                if (IconMarginSlider != null)
                    IconMarginSlider.Value = _settings.IconMargin;
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
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void IconSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
            {
                _settings.IconSize = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void IconMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings != null)
            {
                _settings.IconMargin = (int)e.NewValue;
                OnSettingChanged();
            }
        }

        private void TextMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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

        private void DiscordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string url = "https://discord.gg/DptggHetGZ";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Discord link: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenGitHubPage_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.Tag as GameInfo;

            if (game != null && !string.IsNullOrEmpty(game.Repository))
            {
                try
                {
                    var githubUrl = $"https://github.com/{game.Repository}";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = githubUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open GitHub page: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SetCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var selectedGame = menuItem?.Tag as GameInfo;

            if (selectedGame == null)
            {
                MessageBox.Show("Unable to identify the selected game.", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool hasExistingCustomIcon = !string.IsNullOrEmpty(selectedGame.CustomIconPath);
            string confirmMessage = hasExistingCustomIcon
                ? $"Replace the existing custom icon for {selectedGame.Name}?"
                : $"Set custom icon for {selectedGame.Name}?";

            if (hasExistingCustomIcon)
            {
                var confirmResult = MessageBox.Show(confirmMessage, "Confirm Icon Replacement",
                                                  MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirmResult != MessageBoxResult.Yes)
                    return;
            }

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = $"Select Custom Icon for {selectedGame.Name}",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|" +
                        "PNG Files|*.png|" +
                        "JPEG Files|*.jpg;*.jpeg|" +
                        "Bitmap Files|*.bmp|" +
                        "Icon Files|*.ico|" +
                        "All Files|*.*",
                FilterIndex = 1,
                Multiselect = false
            };

            bool? result = openFileDialog.ShowDialog();

            if (result == true && !string.IsNullOrEmpty(openFileDialog.FileName))
            {
                try
                {
                    if (hasExistingCustomIcon)
                    {
                        try
                        {
                            selectedGame.RemoveCustomIcon();
                        }
                        catch (Exception removeEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"Warning: Failed to remove existing custom icon: {removeEx.Message}");
                        }
                    }

                    selectedGame.SetCustomIcon(openFileDialog.FileName, _gameManager.CacheFolder);
                }
                catch (Exception ex)
                {
                    string errorMessage = hasExistingCustomIcon
                        ? $"Failed to replace custom icon: {ex.Message}"
                        : $"Failed to set custom icon: {ex.Message}";

                    MessageBox.Show(errorMessage, "Error",
                                   MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RemoveCustomIcon_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var selectedGame = menuItem?.Tag as GameInfo;

            if (selectedGame == null)
            {
                MessageBox.Show("Unable to identify the selected game.", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(selectedGame.CustomIconPath))
            {
                MessageBox.Show($"{selectedGame.Name} is already using the default icon.",
                               "No Custom Icon", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Remove custom icon for {selectedGame.Name}?",
                                        "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    selectedGame.RemoveCustomIcon();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to remove custom icon: {ex.Message}",
                                   "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void DeleteGame_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.Tag as GameInfo;

            if (game == null) return;

            if (game.Status == GameStatus.NotInstalled)
            {
                MessageBox.Show($"{game.Name} is not installed.",
                    "Nothing to Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Are you sure you want to delete {game.Name}?\n\nThis will permanently remove all game files and cannot be undone.",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
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

                        MessageBox.Show($"{game.Name} has been successfully deleted.",
                            "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
                    }
                }
                catch (Exception ex)
                {
                    game.IsLoading = false;
                    MessageBox.Show($"Failed to delete {game.Name}: {ex.Message}",
                        "Deletion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}