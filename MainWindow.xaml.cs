using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
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
    }
}
