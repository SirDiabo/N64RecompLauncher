using N64RecompLauncher.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace N64RecompLauncher.Services
{
    public class GameManager : INotifyPropertyChanged, IDisposable
    {
        public AppSettings _settings;
        private readonly HttpClient _httpClient;
        private bool _disposed = false;
        private string _gamesFolder;
        private readonly string _cacheFolder;
        private readonly string _gamesConfigPath;

        public ObservableCollection<GameInfo> Games { get; set; } = [];
        public HttpClient HttpClient => _httpClient;
        public string? GamesFolder => _gamesFolder;
        public string? CacheFolder => _cacheFolder;

        private string _CurrentVersionString;
        public string CurrentVersionString
        {
            get => _CurrentVersionString;
            set
            {
                if (_CurrentVersionString != value)
                {
                    _CurrentVersionString = value;
                    OnPropertyChanged(nameof(CurrentVersionString));
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                }
                _disposed = true;
            }
        }

        public GameManager()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "N64Recomp-Launcher/1.0");

            try
            {
                _settings = AppSettings.Load();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings in GameManager: {ex.Message}");
                _settings = new AppSettings();
            }

            // Initialize games folder with null check
            if (!string.IsNullOrEmpty(_settings?.GamesPath))
            {
                _gamesFolder = _settings.GamesPath;
            }
            else
            {
                _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecompiledGames");
            }

            _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache");
            _gamesConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games.json");

            try
            {
                Directory.CreateDirectory(_gamesFolder);
                Directory.CreateDirectory(_cacheFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create directories: {ex.Message}");
            }

            LoadVersionString();

            Games = new ObservableCollection<GameInfo>();
        }

        private void LoadVersionString()
        {
            try
            {
                string versionFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");
                CurrentVersionString = File.Exists(versionFilePath)
                    ? File.ReadAllText(versionFilePath).Trim()
                    : "Version information not found";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading version: {ex.Message}");
                CurrentVersionString = "Version loading failed";
            }
        }

        public GameInfo? GetLatestPlayedInstalledGame()
        {
            if (Games == null || string.IsNullOrEmpty(_gamesFolder))
                return null;

            DateTime latestTime = DateTime.MinValue;
            GameInfo? latestGame = null;
            foreach (var game in Games)
            {
                if (game == null || string.IsNullOrEmpty(game.FolderName))
                    continue;

                var gamePath = Path.Combine(_gamesFolder, game.FolderName);
                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                if (File.Exists(lastPlayedPath))
                {
                    var timeString = File.ReadAllText(lastPlayedPath).Trim();
                    if (DateTime.TryParseExact(timeString, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime lastPlayed))
                    {
                        if (lastPlayed > latestTime)
                        {
                            latestTime = lastPlayed;
                            latestGame = game;
                        }
                    }
                }
            }
            return latestGame;
        }

        private async Task<List<GameInfo>> LoadGamesFromJsonAsync()
        {
            var allGames = new List<GameInfo>();

            try
            {
                if (!File.Exists(_gamesConfigPath))
                {
                    // Create default games.json if it doesn't exist
                    await CreateDefaultGamesJsonAsync();
                }
                else
                {
                    // Merge defaults with existing to add any new games
                    await MergeDefaultGamesAsync();
                }

                string json = await File.ReadAllTextAsync(_gamesConfigPath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                // Load standard games
                if (root.TryGetProperty("standard", out var standardArray))
                {
                    allGames.AddRange(ParseGameArray(standardArray, isExperimental: false, isCustom: false));
                }

                // Load experimental games
                if (root.TryGetProperty("experimental", out var experimentalArray))
                {
                    allGames.AddRange(ParseGameArray(experimentalArray, isExperimental: true, isCustom: false));
                }

                // Load custom games
                if (root.TryGetProperty("custom", out var customArray))
                {
                    allGames.AddRange(ParseGameArray(customArray, isExperimental: false, isCustom: true));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading games.json: {ex.Message}");
            }

            return allGames;
        }

        private async Task MergeDefaultGamesAsync()
        {
            try
            {
                // Read existing games.json
                string existingJson = await File.ReadAllTextAsync(_gamesConfigPath);
                using var existingDoc = JsonDocument.Parse(existingJson);
                var existingRoot = existingDoc.RootElement;

                // Get default games
                var defaultGames = GetDefaultGamesData();

                // Parse existing games into lists
                var existingStandard = new List<Dictionary<string, object?>>();
                var existingExperimental = new List<Dictionary<string, object?>>();
                var existingCustom = new List<Dictionary<string, object?>>();

                if (existingRoot.TryGetProperty("standard", out var stdArray))
                {
                    existingStandard = ParseToDict(stdArray);
                }
                if (existingRoot.TryGetProperty("experimental", out var expArray))
                {
                    existingExperimental = ParseToDict(expArray);
                }
                if (existingRoot.TryGetProperty("custom", out var custArray))
                {
                    existingCustom = ParseToDict(custArray);
                }

                // Merge defaults with existing (only add new ones)
                var mergedStandard = MergeGameLists(existingStandard, defaultGames.standard);
                var mergedExperimental = MergeGameLists(existingExperimental, defaultGames.experimental);
                var mergedCustom = MergeGameLists(existingCustom, defaultGames.custom);

                // Check if anything was actually added
                bool hasChanges = mergedStandard.Count != existingStandard.Count ||
                                  mergedExperimental.Count != existingExperimental.Count ||
                                  mergedCustom.Count != existingCustom.Count;

                // Only write if there were changes
                if (hasChanges)
                {
                    // Create merged structure
                    var mergedData = new
                    {
                        standard = mergedStandard,
                        experimental = mergedExperimental,
                        custom = mergedCustom
                    };

                    // Save merged data
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(mergedData, options);
                    await File.WriteAllTextAsync(_gamesConfigPath, json);

                    System.Diagnostics.Debug.WriteLine($"New games merged successfully at {_gamesConfigPath}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No new games to merge.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error merging default games: {ex.Message}");
            }
        }

        private List<GameInfo> ParseGameArray(JsonElement gamesArray, bool isExperimental, bool isCustom = false)
        {
            var games = new List<GameInfo>();

            foreach (var gameElement in gamesArray.EnumerateArray())
            {
                try
                {
                    var game = new GameInfo
                    {
                        Name = gameElement.GetProperty("name").GetString() ?? string.Empty,
                        Repository = gameElement.GetProperty("repository").GetString() ?? string.Empty,
                        Branch = gameElement.GetProperty("branch").GetString() ?? "main",
                        ImageRes = gameElement.GetProperty("imageRes").GetString() ?? "512",
                        FolderName = gameElement.GetProperty("folderName").GetString() ?? string.Empty,
                        IsExperimental = isExperimental,
                        IsCustom = isCustom,
                        GameManager = this,
                    };

                    if (gameElement.TryGetProperty("platformOverride", out var overrideElement) &&
                        overrideElement.ValueKind != JsonValueKind.Null)
                    {
                        game.PlatformOverride = overrideElement.GetString();
                    }

                    if (!string.IsNullOrEmpty(game.Name) && !string.IsNullOrEmpty(game.Repository))
                    {
                        games.Add(game);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing game: {ex.Message}");
                }
            }

            return games;
        }

        private List<Dictionary<string, object?>> ParseToDict(JsonElement array)
        {
            var result = new List<Dictionary<string, object?>>();

            foreach (var item in array.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();

                foreach (var prop in item.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }

                result.Add(dict);
            }

            return result;
        }

        private List<Dictionary<string, object?>> MergeGameLists(
            List<Dictionary<string, object?>> existing,
            List<object> defaults)
        {
            var merged = new List<Dictionary<string, object?>>(existing);

            foreach (var defaultGame in defaults)
            {
                var defaultDict = ObjectToDict(defaultGame);
                var gameName = defaultDict.ContainsKey("name") ? defaultDict["name"]?.ToString() : null;

                if (string.IsNullOrEmpty(gameName))
                    continue;

                // Check if game already exists
                bool exists = existing.Any(g =>
                    g.ContainsKey("name") &&
                    g["name"]?.ToString()?.Equals(gameName, StringComparison.OrdinalIgnoreCase) == true);

                if (!exists)
                {
                    merged.Add(defaultDict);
                    System.Diagnostics.Debug.WriteLine($"Added new game: {gameName}");
                }
            }

            return merged;
        }

        private Dictionary<string, object?> ObjectToDict(object obj)
        {
            var dict = new Dictionary<string, object?>();
            var props = obj.GetType().GetProperties();

            foreach (var prop in props)
            {
                dict[char.ToLower(prop.Name[0]) + prop.Name.Substring(1)] = prop.GetValue(obj);
            }

            return dict;
        }

        private (List<object> standard, List<object> experimental, List<object> custom) GetDefaultGamesData()
        {
            var standard = new List<object>
    {
        new { name = "Zelda 64",
            repository = "Zelda64Recomp/Zelda64Recomp",
            branch = "dev",
            imageRes = "512",
            folderName = "Zelda64Recomp",
            platformOverride = (string?)null },

        new { name = "Goemon 64",
            repository = "klorfmorf/Goemon64Recomp",
            branch = "dev",
            imageRes = "512",
            folderName = "Goemon64Recomp",
            platformOverride = (string?)null },

        new { name = "Mario Kart 64",
            repository = "sonicdcer/MarioKart64Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "MarioKart64Recomp",
            platformOverride = (string?)null },

        new { name = "Dinosaur Planet",
            repository = "Francessco121/dino-recomp",
            branch = "main",
            imageRes = "64",
            folderName = "DinosaurPlanetRecomp",
            platformOverride = (string?)null },

        new { name = "Dr. Mario 64",
            repository = "theboy181/drmario64_recomp_plus",
            branch = "main",
            imageRes = "512",
            folderName = "DrMario64RecompPlus",
            platformOverride = (string?)null },

        new { name = "Duke Nukem: Zero Hour",
            repository = "sonicdcer/DNZHRecomp",
            branch = "main",
            imageRes = "512",
            folderName = "DNZHRecomp",
            platformOverride = (string?)null },

        new { name = "Star Fox 64",
            repository = "sonicdcer/Starfox64Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "Starfox64Recomp",
            platformOverride = (string?)null },

        new  { name = "Banjo 64",
            repository = "BanjoRecomp/BanjoRecomp",
            branch = "main",
            imageRes = "app",
            folderName = "BanjoRecomp",
            platformOverride = (string?)null },
    };

            var experimental = new List<object>
    {
        new { name = "Chameleon Twist",
            repository = "Rainchus/ChameleonTwist1-JP-Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "ChameleonTwistRecomp",
            platformOverride = "ChameleonTwistJPRecompiled" },

        new { name = "Mega Man 64",
            repository = "MegaMan64Recomp/MegaMan64Recompiled",
            branch = "main",
            imageRes = "512",
            folderName = "MegaMan64Recomp",
            platformOverride = "MegaMan64Recompiled" },

        new { name = "Quest 64",
            repository = "Rainchus/Quest64-Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "Quest64Recomp",
            platformOverride = "Quest64Recompiled" },
    };

            var custom = new List<object>
    {
        new { name = "Star Fox 64 (Starship)",
            repository = "harbourmasters/starship",
            branch = "main",
            imageRes = "512",
            folderName = "harbourmasters.starship",
            platformOverride = (string?)null },

        new { name = "Zelda OoT (Ship of Harkinian)",
            repository = "harbourmasters/shipwright",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.shipofharkinian",
            platformOverride = (string?)null },

        new { name = "Mario Kart 64 (SpaghettiKart)",
            repository = "harbourmasters/spaghettikart",
            branch = "main",
            imageRes = "512",
            folderName = "harbourmasters.spaghettikkart",
            platformOverride = (string?)null },

        new { name = "Zelda MM (2 Ship 2 Harkinian)",
            repository = "harbourmasters/2ship2harkinian",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.2ship2harkinian",
            platformOverride = (string?)null },

        new { name = "Super Mario 64 (Ghostship)",
            repository = "harbourmasters/ghostship",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.ghostship",
            platformOverride = (string?)null },

        new { name = "Sonic Unleashed Recompiled",
            repository = "hedge-dev/UnleashedRecomp",
            branch = "main",
            imageRes = "512",
            folderName = "Sonic Unleashed Recompiled",
            platformOverride = (string?)null },

        new { name = "Super Metroid Launcher",
            repository = "RadzPrower/Super-Metroid-Launcher",
            branch = "master",
            imageRes = "512",
            folderName = "RadzPrower.Super-Metroid-Launcher",
            platformOverride = "Super_Metroid_Launcher" },

        new { name = "Zelda: ALttP (Zelda 3 Launcher)",
            repository = "RadzPrower/Zelda-3-Launcher",
            branch = "master",
            imageRes = "512",
            folderName = "RadzPrower.Zelda-3-Launcher",
            platformOverride = "Zelda_3_Launcher" },

        new { name = "WipeOut Phantom Edition",
            repository = "wipeout-phantom-edition/wipeout-phantom-edition",
            branch = "main",
            imageRes = "512",
            folderName = "WipeOut Phantom Edition",
            platformOverride = "wipeout-x64-release" },
    };

            return (standard, experimental, custom);
        }

        private async Task CreateDefaultGamesJsonAsync()
        {
            try
            {
                var defaultData = GetDefaultGamesData();

                var data = new
                {
                    standard = defaultData.standard,
                    experimental = defaultData.experimental,
                    custom = defaultData.custom
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(data, options);

                await File.WriteAllTextAsync(_gamesConfigPath, json).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine($"Default games.json created at {_gamesConfigPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating default games.json: {ex.Message}");
            }
        }

        private void LoadCustomIcons()
        {
            if (Games == null || string.IsNullOrEmpty(_cacheFolder))
                return;

            foreach (var game in Games)
            {
                if (game != null)
                {
                    game.LoadCustomIcon(_cacheFolder);
                }
            }
        }

        public GameInfo? FindGameByName(string name)
        {
            return Games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public GameInfo? FindGameByFolderName(string folderName)
        {
            return Games.FirstOrDefault(predicate: g => g.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadGamesAsync()
        {
            var settings = AppSettings.Load();

            if (Games == null)
                Games = new ObservableCollection<GameInfo>();

            // Load games from JSON (this happens on background thread due to ConfigureAwait(false))
            var allGames = await LoadGamesFromJsonAsync();

            if (allGames == null)
                allGames = new List<GameInfo>();

            var filteredGames = allGames
                .Where(game => game != null && (!game.IsExperimental || settings.ShowExperimentalGames))
                .Where(game => game != null && (!game.IsCustom || settings.ShowCustomGames))
                .Where(game => game != null && !settings.HiddenGames.Contains(game.Name))
                .ToList();

            // Clear and add on the current synchronization context
            Games.Clear();

            foreach (var game in filteredGames)
            {
                if (game != null)
                    Games.Add(game);
            }

            LoadCustomIcons();

            if (string.IsNullOrEmpty(_gamesFolder))
                return;

            var gameStatuses = Games
                .Where(g => g != null && !string.IsNullOrEmpty(g.FolderName))
                .Select(g => new {
                    Game = g,
                    IsInstalled = Directory.Exists(Path.Combine(_gamesFolder, g.FolderName)),
                    LastPlayed = GetLastPlayedTime(g.FolderName)
                })
                .OrderBy(x => x.IsInstalled ? 0 : 1)
                .ThenByDescending(x => x.LastPlayed)
                .ThenBy(x => x.Game?.Name ?? "")
                .Select(x => x.Game)
                .ToList();

            for (int i = 0; i < gameStatuses.Count; i++)
            {
                var game = gameStatuses[i];
                if (game == null)
                    continue;

                var currentIndex = Games.IndexOf(game);
                if (currentIndex != i && currentIndex != -1)
                {
                    Games.Move(currentIndex, i);
                }
            }

            await Task.WhenAll(Games.Where(game => game != null).Select(async game =>
            {
                try
                {
                    await game.CheckStatusAsync(_httpClient, _gamesFolder);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error checking status for {game.Name}: {ex.Message}");
                }
            }));
        }

        public async Task ExportGamesAsync()
        {
            try
            {
                var allGames = await LoadGamesFromJsonAsync().ConfigureAwait(false);

                var groupedGames = new
                {
                    standard = allGames
                        .Where(g => !g.IsExperimental)
                        .Select(g => new
                        {
                            g.Name,
                            g.Repository,
                            g.Branch,
                            g.ImageRes,
                            g.FolderName,
                            g.PlatformOverride
                        }).ToList(),
                    experimental = allGames
                        .Where(g => g.IsExperimental)
                        .Select(g => new
                        {
                            g.Name,
                            g.Repository,
                            g.Branch,
                            g.ImageRes,
                            g.FolderName,
                            g.PlatformOverride
                        }).ToList(),
                    custom = Array.Empty<object>()
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(groupedGames, options);

                await File.WriteAllTextAsync(_gamesConfigPath, json).ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine($"Games exported successfully to {_gamesConfigPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error exporting games: {ex.Message}");
            }
        }

        public async Task UpdateGamesFolderAsync(string newPath)
        {
            try
            {
                string targetPath;

                if (!string.IsNullOrWhiteSpace(newPath))
                {
                    // Validate the path exists or can be created
                    if (!Directory.Exists(newPath))
                    {
                        try
                        {
                            Directory.CreateDirectory(newPath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to create custom games directory: {ex.Message}");
                            throw new InvalidOperationException($"Cannot create directory at {newPath}", ex);
                        }
                    }
                    targetPath = newPath;
                }
                else
                {
                    targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecompiledGames");
                    Directory.CreateDirectory(targetPath);
                }

                _gamesFolder = targetPath;
                Games.Clear();

                await LoadGamesAsync();

                OnPropertyChanged(nameof(Games));
                OnPropertyChanged(nameof(GamesFolder));

                System.Diagnostics.Debug.WriteLine($"Games folder updated to: {_gamesFolder}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating games folder: {ex.Message}");

                // Fallback to default path on error
                _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecompiledGames");
                Directory.CreateDirectory(_gamesFolder);

                throw;
            }
        }

        private DateTime GetLastPlayedTime(string folderName)
        {
            if (string.IsNullOrEmpty(_gamesFolder) || string.IsNullOrEmpty(folderName))
                return DateTime.MinValue;

            try
            {
                var gamePath = Path.Combine(_gamesFolder, folderName);
                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");

                if (File.Exists(lastPlayedPath))
                {
                    var timeString = File.ReadAllText(lastPlayedPath).Trim();
                    if (DateTime.TryParseExact(timeString, "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out DateTime lastPlayed))
                    {
                        return lastPlayed;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read LastPlayed.txt for {folderName}: {ex.Message}");
            }

            return DateTime.MinValue;
        }

        public void HideGame(string gameName)
        {
            if (string.IsNullOrEmpty(gameName))
                return;

            var settings = AppSettings.Load();
            if (!settings.HiddenGames.Contains(gameName))
            {
                settings.HiddenGames.Add(gameName);
                AppSettings.Save(settings);
                FilterGames(settings);
            }
        }

        public void UnhideAllGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);
            FilterGames(settings);
        }

        public async Task HideAllNonInstalledGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            if (Games == null)
                return;

            foreach (var game in Games)
            {
                if (game != null && game.Status == GameStatus.NotInstalled && !settings.HiddenGames.Contains(game.Name))
                {
                    settings.HiddenGames.Add(game.Name);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        public async Task HideAllNonStableGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            foreach (var game in Games)
            {
                if ((game.IsExperimental == true || game.IsCustom == true) && !settings.HiddenGames.Contains(game.Name))
                {
                    settings.HiddenGames.Add(game.Name);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        public async Task OnlyShowExperimentalGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            if (Games == null)
                return;

            foreach (var game in Games)
            {
                if (game != null && game.IsExperimental == false && !settings.HiddenGames.Contains(game.Name))
                {
                    settings.HiddenGames.Add(game.Name);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        public async Task OnlyShowCustomGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            if (Games == null)
                return;

            foreach (var game in Games)
            {
                if (game != null && !game.IsCustom && !settings.HiddenGames.Contains(game.Name))
                {
                    settings.HiddenGames.Add(game.Name);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        private void FilterGames(AppSettings settings)
        {
            if (Games == null || settings?.HiddenGames == null)
                return;

            for (int i = Games.Count - 1; i >= 0; i--)
            {
                if (Games[i] != null && settings.HiddenGames.Contains(Games[i].Name))
                {
                    Games.RemoveAt(i);
                }
            }
        }

        public void RefreshGamesWithFilter(AppSettings settings)
        {
            _ = LoadGamesAsync();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}