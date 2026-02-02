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
                GitHubApiCache.Initialize(_cacheFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create directories: {ex.Message}");
            }

            LoadVersionString();
            _ = ValidateAndFixGamesJsonAsync();
            Games = new ObservableCollection<GameInfo>();
        }

        public async Task CheckAllUpdatesAsync()
        {
            await LoadGamesAsync(forceUpdateCheck: true);
        }

        private async Task ValidateAndFixGamesJsonAsync()
        {
            try
            {
                if (!File.Exists(_gamesConfigPath))
                {
                    System.Diagnostics.Debug.WriteLine("games.json does not exist, skipping integrity check");
                    return;
                }

                string json = await File.ReadAllTextAsync(_gamesConfigPath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                bool needsFix = false;
                var fixedData = new
                {
                    standard = ValidateAndFixGameSection(root, "standard", ref needsFix),
                    experimental = ValidateAndFixGameSection(root, "experimental", ref needsFix),
                    custom = ValidateAndFixGameSection(root, "custom", ref needsFix)
                };

                if (needsFix)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string fixedJson = JsonSerializer.Serialize(fixedData, options);
                    await File.WriteAllTextAsync(_gamesConfigPath, fixedJson);
                    System.Diagnostics.Debug.WriteLine("games.json integrity check: Fixed missing or invalid properties");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("games.json integrity check: No issues found");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during games.json integrity check: {ex.Message}");
            }
        }

        private List<Dictionary<string, object?>> ValidateAndFixGameSection(JsonElement root, string sectionName, ref bool needsFix)
        {
            var fixedGames = new List<Dictionary<string, object?>>();

            if (!root.TryGetProperty(sectionName, out var sectionArray))
            {
                return fixedGames;
            }

            foreach (var gameElement in sectionArray.EnumerateArray())
            {
                var gameDict = new Dictionary<string, object?>();
                bool gameNeedsFix = false;

                // Required properties with defaults
                var requiredProps = new Dictionary<string, object?>
                {
                    { "name", string.Empty },
                    { "repository", string.Empty },
                    { "branch", "main" },
                    { "imageRes", "512" },
                    { "folderName", string.Empty },
                    { "customDefaultIconUrl", null }
                };

                // Copy existing properties
                foreach (var prop in gameElement.EnumerateObject())
                {
                    gameDict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }

                // Check for missing or invalid properties
                foreach (var requiredProp in requiredProps)
                {
                    if (!gameDict.ContainsKey(requiredProp.Key))
                    {
                        gameDict[requiredProp.Key] = requiredProp.Value;
                        gameNeedsFix = true;
                        System.Diagnostics.Debug.WriteLine($"Fixed missing property '{requiredProp.Key}' in {sectionName} game");
                    }
                    else if (gameDict[requiredProp.Key] is string str && string.IsNullOrWhiteSpace(str) &&
                             requiredProp.Value is string defaultStr && !string.IsNullOrWhiteSpace(defaultStr))
                    {
                        // Fix empty required string properties (except those that can be null)
                        if (requiredProp.Key == "name" || requiredProp.Key == "repository" || requiredProp.Key == "folderName")
                        {
                            // Don't fix these as empty - they indicate invalid game entry
                            continue;
                        }
                        gameDict[requiredProp.Key] = requiredProp.Value;
                        gameNeedsFix = true;
                        System.Diagnostics.Debug.WriteLine($"Fixed empty property '{requiredProp.Key}' in {sectionName} game");
                    }
                }

                if (gameNeedsFix)
                {
                    needsFix = true;
                }

                fixedGames.Add(gameDict);
            }

            return fixedGames;
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
                        CustomDefaultIconUrl = gameElement.GetProperty("customDefaultIconUrl").GetString() ?? string.Empty,
                        IsExperimental = isExperimental,
                        IsCustom = isCustom,
                        GameManager = this,
                    };

                    if (gameElement.TryGetProperty("customDefaultIconUrl", out var customDefaultUrlElement) &&
                    customDefaultUrlElement.ValueKind != JsonValueKind.Null)
                    {
                        game.CustomDefaultIconUrl = customDefaultUrlElement.GetString();
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
                var gameRepository = defaultDict.ContainsKey("repository") ? defaultDict["repository"]?.ToString() : null;

                if (string.IsNullOrEmpty(gameRepository))
                    continue;

                // Check if game already exists
                bool exists = existing.Any(g =>
                    g.ContainsKey("repository") &&
                    g["repository"]?.ToString()?.Equals(gameRepository, StringComparison.OrdinalIgnoreCase) == true);

                if (!exists)
                {
                    merged.Add(defaultDict);
                    System.Diagnostics.Debug.WriteLine($"Added new game: {gameRepository}");
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
            customDefaultIconUrl = (string?)null },

        new { name = "Goemon 64",
            repository = "klorfmorf/Goemon64Recomp",
            branch = "dev",
            imageRes = "512",
            folderName = "Goemon64Recomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Mario Kart 64",
            repository = "sonicdcer/MarioKart64Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "MarioKart64Recomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Dinosaur Planet",
            repository = "Francessco121/dino-recomp",
            branch = "main",
            imageRes = "64",
            folderName = "DinosaurPlanetRecomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Dr. Mario 64",
            repository = "theboy181/drmario64_recomp_plus",
            branch = "main",
            imageRes = "512",
            folderName = "DrMario64RecompPlus",
            customDefaultIconUrl = (string?)null },

        new { name = "Duke Nukem: Zero Hour",
            repository = "sonicdcer/DNZHRecomp",
            branch = "main",
            imageRes = "512",
            folderName = "DNZHRecomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Star Fox 64",
            repository = "sonicdcer/Starfox64Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "Starfox64Recomp",
            customDefaultIconUrl = (string?)null },

        new  { name = "Banjo 64",
            repository = "BanjoRecomp/BanjoRecomp",
            branch = "main",
            imageRes = "app",
            folderName = "BanjoRecomp",
            customDefaultIconUrl = (string?)null },
    };

            var experimental = new List<object>
    {
        new { name = "Chameleon Twist",
            repository = "Rainchus/ChameleonTwist1-JP-Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "ChameleonTwistRecomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Mega Man 64",
            repository = "MegaMan64Recomp/MegaMan64Recompiled",
            branch = "main",
            imageRes = "512",
            folderName = "MegaMan64Recomp",
            customDefaultIconUrl = (string?)null },

        new { name = "Quest 64",
            repository = "Rainchus/Quest64-Recomp",
            branch = "main",
            imageRes = "512",
            folderName = "Quest64Recomp",
            customDefaultIconUrl = (string?)null },
    };

            var custom = new List<object>
    {
        new { name = "Star Fox 64 (Starship)",
            repository = "harbourmasters/starship",
            branch = "main",
            imageRes = "512",
            folderName = "harbourmasters.starship",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/dc2ee2a5add7154447a4644326e33386/32/256x256.png" },

        new { name = "Zelda OoT (Ship of Harkinian)",
            repository = "harbourmasters/shipwright",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.shipofharkinian",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/d1cd0a8c9b28f58703a097d5a25534e3/32/256x256.png" },

        new { name = "Mario Kart 64 (SpaghettiKart)",
            repository = "harbourmasters/spaghettikart",
            branch = "main",
            imageRes = "512",
            folderName = "harbourmasters.spaghettikkart",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/5e5e0bd5ad7c2ca72b0c5ff8b6debbba.png" },

        new { name = "Zelda MM (2 Ship 2 Harkinian)",
            repository = "harbourmasters/2ship2harkinian",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.2ship2harkinian",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/6c7dbdd98cd70f67f102524761f3b4d2/24/256x256.png" },

        new { name = "Super Mario 64 (Ghostship)",
            repository = "harbourmasters/ghostship",
            branch = "develop",
            imageRes = "512",
            folderName = "harbourmasters.ghostship",
            customDefaultIconUrl = "https://github.com/HarbourMasters/Ghostship/blob/develop/port/textures/icons/g2ShipIcon.png?raw=true" },

        new { name = "Sonic Unleashed Recompiled",
            repository = "hedge-dev/UnleashedRecomp",
            branch = "main",
            imageRes = "512",
            folderName = "Sonic Unleashed Recompiled",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/63a99723ebb3af94d52b474c3b21dbe1/24/512x512.png" },

        new { name = "Super Metroid Launcher",
            repository = "RadzPrower/Super-Metroid-Launcher",
            branch = "master",
            imageRes = "512",
            folderName = "RadzPrower.Super-Metroid-Launcher",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/26e3dcb90aa10011db5b660c463f325f/32/256x256.png" },

        new { name = "Zelda: ALttP (Zelda 3 Launcher)",
            repository = "RadzPrower/Zelda-3-Launcher",
            branch = "master",
            imageRes = "512",
            folderName = "RadzPrower.Zelda-3-Launcher",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/1b39a10cc39ee53e5b2fdc1eda1eb5da.png" },

        new { name = "WipeOut Phantom Edition",
            repository = "wipeout-phantom-edition/wipeout-phantom-edition",
            branch = "main",
            imageRes = "512",
            folderName = "WipeOut Phantom Edition",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/9fcb5252180dc29b22976c5c63b322e7.png" },

        new { name = "Perfect Dark",
            repository = "fgsfdsfgs/perfect_dark",
            branch = "port",
            imageRes = "512",
            folderName = "fgsfdsfgs.perfect_dark",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/64314c17210c549a854f1f1c7adce8b6/32/256x256.png" },

        new { name = "SM64 CoopDX",
            repository = "coop-deluxe/sm64coopdx",
            branch = "main",
            imageRes = "512",
            folderName = "coop-deluxe.sm64coopdx",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/e3dd863ef4277e82f712a5bd8fefe7d7.png" },

        new { name = "LoD: Severed Chains",
            repository = "Legend-of-Dragoon-Modding/Severed-Chains",
            branch = "main",
            imageRes = "512",
            folderName = "Legend-of-Dragoon-Modding.Severed-Chains",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/ddc2c94d27d2c6d46b33acf21b21a641.png" },

        new { name = "REDRIVER 2",
            repository = "OpenDriver2/REDRIVER2",
            branch = "master",
            imageRes = "512",
            folderName = "OpenDriver2.REDRIVER2",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/ca0739bf1344242a820fe79d0ac17d65.png" },

        new { name = "Super Mario World",
            repository = "snesrev/smw",
            branch = "main",
            imageRes = "512",
            folderName = "snesrev.smw",
            customDefaultIconUrl = "https://cdn2.steamgriddb.com/icon/5ba01c0e82cd96577309302faf900a0d/32/1024x1024.png" },
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

        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadGamesAsync(bool forceUpdateCheck = false)
        {
            var settings = AppSettings.Load();

            if (Games == null)
                Games = new ObservableCollection<GameInfo>();

            var allGames = await LoadGamesFromJsonAsync();

            if (allGames == null)
                allGames = new List<GameInfo>();

            var filteredGames = allGames
                .Where(game => game != null && (!game.IsExperimental || settings.ShowExperimentalGames))
                .Where(game => game != null && (!game.IsCustom || settings.ShowCustomGames))
                .Where(game => game != null && !settings.HiddenGames.Contains(game.Name))
                .ToList();

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

            if (!forceUpdateCheck)
            {
                int cachedCount = Games.Count(g => !GitHubApiCache.NeedsUpdateCheck(g.Repository,
                    Directory.Exists(Path.Combine(_gamesFolder, g.FolderName ?? ""))));
                int apiCallCount = Games.Count - cachedCount;
                System.Diagnostics.Debug.WriteLine($"LoadGamesAsync: {cachedCount} games using cache, {apiCallCount} will check for updates");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"LoadGamesAsync: Force update check for all {Games.Count} games");
            }

            await Task.WhenAll(Games.Where(game => game != null).Select(async game =>
            {
                try
                {
                    await game.CheckStatusAsync(_httpClient, _gamesFolder, forceUpdateCheck);
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
                            g.CustomDefaultIconUrl
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
                            g.CustomDefaultIconUrl
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