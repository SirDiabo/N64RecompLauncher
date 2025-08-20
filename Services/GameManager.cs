using N64RecompLauncher.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace N64RecompLauncher.Services
{
    public class GameManager : INotifyPropertyChanged
    {
        private readonly HttpClient _httpClient;
        private readonly string _gamesFolder;

        public ObservableCollection<GameInfo> Games { get; }
        public HttpClient HttpClient => _httpClient;
        public string GamesFolder => _gamesFolder;

        public GameManager()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "N64Recomp-Launcher/1.0");
            _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecompiledGames");
            Directory.CreateDirectory(_gamesFolder);

            Games = new ObservableCollection<GameInfo>
            {
                new GameInfo
                {
                    Name = "Zelda 64 Recomp",
                    Repository = "Zelda64Recomp/Zelda64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Zelda64Recomp",
                },
                new GameInfo
                {
                    Name = "Goemon 64 Recomp",
                    Repository = "klorfmorf/Goemon64Recomp",
                    Branch = "dev",
                    ImageRes = "512",
                    FolderName = "Goemon64Recomp",
                },
                new GameInfo
                {
                    Name = "Mario Kart 64 Recomp",
                    Repository = "sonicdcer/MarioKart64Recomp",
                    Branch = "main",
                    ImageRes = "512",
                    FolderName = "MarioKart64Recomp",
                },
                new GameInfo
                {
                    Name = "Dinosaur Planet Recomp",
                    Repository = "Francessco121/dino-recomp",
                    Branch = "main",
                    ImageRes = "64",
                    FolderName = "DinosaurPlanetRecomp",
                },
            };
        }

        public async Task LoadGamesAsync()
        {
            foreach (var game in Games)
            {
                await game.CheckStatusAsync(_httpClient, _gamesFolder);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
