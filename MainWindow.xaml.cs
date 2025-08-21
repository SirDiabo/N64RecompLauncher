using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace N64RecompLauncher
{
    public partial class MainWindow : Window
    {
        private readonly GameManager _gameManager;

        public MainWindow()
        {
            InitializeComponent();
            _gameManager = new GameManager();
            DataContext = _gameManager;
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _gameManager.LoadGamesAsync();
        }

        private async void GameButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var game = button?.Tag as GameInfo;
            if (game != null)
            {
                await game.PerformActionAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
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
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Failed to open GitHub page: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void DeleteGame_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var game = menuItem?.Tag as GameInfo;

            if (game == null) return;

            // Only show confirmation if the game is actually installed
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
                        // Set loading state
                        game.Status = GameStatus.Installing; // Reuse this status for deletion indication
                        game.IsLoading = true;

                        // Delete the directory and all contents
                        Directory.Delete(gamePath, true);

                        // Update the game status
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);

                        MessageBox.Show($"{game.Name} has been successfully deleted.",
                            "Deletion Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // Directory doesn't exist, just update status
                        await game.CheckStatusAsync(_gameManager.HttpClient, _gameManager.GamesFolder);
                    }
                }
                catch (System.Exception ex)
                {
                    game.IsLoading = false;
                    MessageBox.Show($"Failed to delete {game.Name}: {ex.Message}",
                        "Deletion Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}