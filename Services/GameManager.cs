using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
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
        private static readonly N64RecompLauncherProfile Profile = N64RecompLauncherProfile.Instance;
        public AppSettings _settings = new();
        private readonly HttpClient _httpClient;
        private bool _disposed = false;
        private string _gamesFolder;
        private readonly string _cacheFolder;
        private readonly string _gamesConfigPath;

        public ObservableCollection<GameInfo> Games { get; set; } = [];
        public HttpClient HttpClient => _httpClient;
        public string GamesFolder => _gamesFolder;
        public string CacheFolder => _cacheFolder;

        private string _CurrentVersionString = string.Empty;
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

        public bool IsDefaultGame(string repository)
        {
            if (string.IsNullOrEmpty(repository)) return false;

            var defaults = GetDefaultGamesData();
            var allDefaults = new List<object>();
            allDefaults.AddRange(defaults.standard);
            allDefaults.AddRange(defaults.experimental);
            allDefaults.AddRange(defaults.custom);

            return allDefaults.Any(g => {
                var dict = ObjectToDict(g);
                return dict.ContainsKey("repository") &&
                       dict["repository"]?.ToString()?.Equals(repository, StringComparison.OrdinalIgnoreCase) == true;
            });
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
            _httpClient.DefaultRequestHeaders.Add("User-Agent", Profile.UserAgent);
            _httpClient.Timeout = TimeSpan.FromMinutes(30);

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
                _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);
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

                await RenameOldGameFoldersAsync(root);

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

        private async Task RenameOldGameFoldersAsync(JsonElement root)
        {
            if (string.IsNullOrEmpty(_gamesFolder))
                return;

            var defaultGames = GetDefaultGamesData();
            var allDefaults = new List<object>();
            allDefaults.AddRange(defaultGames.standard);
            allDefaults.AddRange(defaultGames.experimental);
            allDefaults.AddRange(defaultGames.custom);

            // Check each section
            foreach (var sectionName in new[] { "standard", "experimental", "custom" })
            {
                if (!root.TryGetProperty(sectionName, out var sectionArray))
                    continue;

                foreach (var gameElement in sectionArray.EnumerateArray())
                {
                    if (!gameElement.TryGetProperty("repository", out var repoElement))
                        continue;

                    string? repository = repoElement.GetString();
                    if (string.IsNullOrEmpty(repository))
                        continue;

                    // Find matching default game
                    var defaultGame = allDefaults.FirstOrDefault(g =>
                        ObjectToDict(g).ContainsKey("repository") &&
                        ObjectToDict(g)["repository"]?.ToString()?.Equals(repository, StringComparison.OrdinalIgnoreCase) == true);

                    if (defaultGame == null)
                        continue;

                    var defaultDict = ObjectToDict(defaultGame);
                    string? currentFolderName = gameElement.TryGetProperty("folderName", out var folderElement) ? folderElement.GetString() : null;

                    if (string.IsNullOrEmpty(currentFolderName))
                        continue;
                }
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
                    { "folderName", string.Empty },
                    { "gameIconUrl", null }
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

                // Migrate old properties to new schema
                if (gameDict.ContainsKey("customDefaultIconUrl"))
                {
                    if (!gameDict.ContainsKey("gameIconUrl") || gameDict["gameIconUrl"] == null)
                        gameDict["gameIconUrl"] = gameDict["customDefaultIconUrl"];
                    gameDict.Remove("customDefaultIconUrl");
                    gameNeedsFix = true;
                }
                if (gameDict.ContainsKey("branch")) { gameDict.Remove("branch"); gameNeedsFix = true; }
                if (gameDict.ContainsKey("imageRes")) { gameDict.Remove("imageRes"); gameNeedsFix = true; }
                if (gameDict.ContainsKey("repository") && "Francessco121/dino-recomp".Equals(gameDict["repository"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    gameDict["repository"] = "DinosaurPlanetRecomp/dino-recomp";
                    gameDict["gameIconUrl"] = "https://raw.githubusercontent.com/DinosaurPlanetRecomp/dino-recomp/main/icons/64.png";
                    gameNeedsFix = true;
                    System.Diagnostics.Debug.WriteLine("Migrated Dinosaur Planet repository to DinosaurPlanetRecomp/dino-recomp");
                }

                // Fill missing folder names from defaults without overwriting user edits
                var defaultGames = GetDefaultGamesData();
                var allDefaults = new List<object>();
                allDefaults.AddRange(defaultGames.standard);
                allDefaults.AddRange(defaultGames.experimental);
                allDefaults.AddRange(defaultGames.custom);

                if (gameDict.ContainsKey("repository"))
                {
                    string? repository = gameDict["repository"]?.ToString();
                    var matchingDefault = allDefaults.FirstOrDefault(g =>
                    {
                        var dict = ObjectToDict(g);
                        return dict.ContainsKey("repository") &&
                               dict["repository"]?.ToString()?.Equals(repository, StringComparison.OrdinalIgnoreCase) == true;
                    });

                    if (matchingDefault != null)
                    {
                        var defaultDict = ObjectToDict(matchingDefault);
                        if (defaultDict.ContainsKey("folderName"))
                        {
                            string? correctFolderName = defaultDict["folderName"]?.ToString();
                            string? currentFolderName = gameDict.ContainsKey("folderName") ? gameDict["folderName"]?.ToString() : null;

                            if (!string.IsNullOrEmpty(correctFolderName) &&
                                string.IsNullOrWhiteSpace(currentFolderName))
                            {
                                gameDict["folderName"] = correctFolderName;
                                gameNeedsFix = true;
                                System.Diagnostics.Debug.WriteLine($"Filled missing folderName with '{correctFolderName}' for repository {repository}");
                            }
                        }

                        // Sync gameIconUrl from defaults if missing or null
                        if (defaultDict.ContainsKey("gameIconUrl"))
                        {
                            string? defaultIconUrl = defaultDict["gameIconUrl"]?.ToString();
                            bool currentIsEmpty = !gameDict.ContainsKey("gameIconUrl") || gameDict["gameIconUrl"] == null || string.IsNullOrEmpty(gameDict["gameIconUrl"]?.ToString());
                            if (!string.IsNullOrEmpty(defaultIconUrl) && currentIsEmpty)
                            {
                                gameDict["gameIconUrl"] = defaultIconUrl;
                                gameNeedsFix = true;
                                System.Diagnostics.Debug.WriteLine($"Synced gameIconUrl from defaults for repository {repository}");
                            }
                        }
                    }
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

                var gamePath = game.GetInstallPath(_gamesFolder);
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
                        Name = (gameElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null) ?? string.Empty,
                        Repository = (gameElement.TryGetProperty("repository", out var repoElement) ? repoElement.GetString() : null) ?? string.Empty,
                        FolderName = (gameElement.TryGetProperty("folderName", out var folderElement) ? folderElement.GetString() : null) ?? string.Empty,
                        InstallPath = (gameElement.TryGetProperty("installPath", out var installPathElement) ? installPathElement.GetString() : null),
                        GameIconUrl = string.Empty,
                        PreferredVersion = gameElement.TryGetProperty("preferredVersion", out var preferredVersionElement) ? preferredVersionElement.GetString() : null,
                        SkippedUpdateVersion = gameElement.TryGetProperty("skippedUpdateVersion", out var skippedUpdateVersionElement) ? skippedUpdateVersionElement.GetString() : null,
                        IsExperimental = isExperimental,
                        IsCustom = isCustom,
                        GameManager = this,
                    };

                    if (gameElement.TryGetProperty("gameIconUrl", out var gameIconUrlElement) &&
                        gameIconUrlElement.ValueKind != JsonValueKind.Null)
                    {
                        game.GameIconUrl = gameIconUrlElement.GetString();
                    }
                    else if (gameElement.TryGetProperty("customDefaultIconUrl", out var legacyIconElement) &&
                            legacyIconElement.ValueKind != JsonValueKind.Null)
                    {
                        game.GameIconUrl = legacyIconElement.GetString();
                    }

                    games.Add(game);
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
            return Profile.GetDefaultGamesData();
        }

        private string BuildDefaultGamesJson()
        {
            var defaultData = GetDefaultGamesData();

            var data = new
            {
                standard = defaultData.standard,
                experimental = defaultData.experimental,
                custom = defaultData.custom
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(data, options);
        }

        private void CreateDefaultGamesJson()
        {
            try
            {
                string json = BuildDefaultGamesJson();
                File.WriteAllText(_gamesConfigPath, json);
                System.Diagnostics.Debug.WriteLine($"Default games.json created at {_gamesConfigPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating default games.json: {ex.Message}");
            }
        }

        private async Task CreateDefaultGamesJsonAsync()
        {
            try
            {
                string json = BuildDefaultGamesJson();
                await File.WriteAllTextAsync(_gamesConfigPath, json).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"Default games.json created at {_gamesConfigPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating default games.json: {ex.Message}");
            }
        }

        private async Task LoadCustomAndCachedIconsAsync()
        {
            if (Games == null || string.IsNullOrEmpty(_cacheFolder))
                return;

            // Load custom covers
            foreach (var game in Games)
            {
                if (game != null)
                {
                    game.LoadCustomIcon(_cacheFolder);
                }
            }

            // Download/load cached default icons asynchronously
            var tasks = Games
                .Where(g => g != null)
                .Select(g => g.LoadAndCacheDefaultIconAsync(_cacheFolder));

            await Task.WhenAll(tasks);
        }

        public async Task ClearIconCacheAsync()
        {
            try
            {
                var iconsDir = Path.Combine(_cacheFolder, "Icons");
                if (Directory.Exists(iconsDir))
                {
                    Directory.Delete(iconsDir, true);
                    System.Diagnostics.Debug.WriteLine("Icon cache cleared successfully");

                    // Reload icons for all games
                    await LoadCustomAndCachedIconsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to clear icon cache: {ex.Message}");
            }
        }

        public GameInfo? FindGameByName(string name)
        {
            return Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public GameInfo? FindGameByFolderName(string folderName)
        {
            return Games.FirstOrDefault(g => string.Equals(g.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
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
            .Where(game => game != null && !IsGameHidden(settings, game))
            .ToList();

            Games.Clear();

            foreach (var game in filteredGames)
            {
                if (game != null)
                    Games.Add(game);
            }

            await LoadCustomAndCachedIconsAsync();

            if (string.IsNullOrEmpty(_gamesFolder))
                return;

            if (!forceUpdateCheck)
            {
                int cachedCount = Games.Count(g => !GitHubApiCache.NeedsUpdateCheck(g.Repository ?? string.Empty,
                    Directory.Exists(g.GetInstallPath(_gamesFolder))));
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
                            g.FolderName,
                            g.InstallPath,
                            g.GameIconUrl
                        }).ToList(),
                    experimental = allGames
                        .Where(g => g.IsExperimental)
                        .Select(g => new
                        {
                            g.Name,
                            g.Repository,
                            g.FolderName,
                            g.InstallPath,
                            g.GameIconUrl
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
                    targetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);
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
                _gamesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Profile.DefaultInstallFolderName);
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

        private static string GetHiddenGameKey(GameInfo game)
        {
            if (!string.IsNullOrWhiteSpace(game.FolderName))
                return $"folder:{game.FolderName}";

            if (!string.IsNullOrWhiteSpace(game.Repository))
                return $"repo:{game.Repository}";

            return $"name:{game.Name ?? string.Empty}";
        }

        private static bool IsGameHidden(AppSettings settings, GameInfo game)
        {
            if (settings?.HiddenGames == null)
                return false;

            var hiddenKey = GetHiddenGameKey(game);
            return settings.HiddenGames.Contains(hiddenKey) ||
                   (!string.IsNullOrWhiteSpace(game.Name) && settings.HiddenGames.Contains(game.Name));
        }

        private static void AddHiddenGame(AppSettings settings, GameInfo game)
        {
            if (settings?.HiddenGames == null)
                return;

            var hiddenKey = GetHiddenGameKey(game);
            if (!settings.HiddenGames.Contains(hiddenKey))
            {
                settings.HiddenGames.Add(hiddenKey);
            }
        }

        public void HideGame(GameInfo game)
        {
            if (game == null)
                return;

            var settings = AppSettings.Load();
            if (!IsGameHidden(settings, game))
            {
                AddHiddenGame(settings, game);
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
                if (game != null && game.Status == GameStatus.NotInstalled && !IsGameHidden(settings, game))
                {
                    AddHiddenGame(settings, game);
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
                if (game != null && game.IsExperimental == true && !IsGameHidden(settings, game))
                {
                    AddHiddenGame(settings, game);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        public List<GameInfo> GetDefaultGames()
        {
            var games = LoadGamesFromJson();
            return games.Where(g => !g.IsCustom).ToList();
        }

        private List<GameInfo> LoadGamesFromJson()
        {
            var allGames = new List<GameInfo>();

            try
            {
                if (!File.Exists(_gamesConfigPath))
                {
                    CreateDefaultGamesJson();
                }

                string json = File.ReadAllText(_gamesConfigPath);
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
                if (game != null && game.IsExperimental == false && !IsGameHidden(settings, game))
                {
                    AddHiddenGame(settings, game);
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
                if (game != null && !game.IsCustom && !IsGameHidden(settings, game))
                {
                    AddHiddenGame(settings, game);
                }
            }
            AppSettings.Save(settings);
            await LoadGamesAsync();
        }

        public async Task OnlyShowN64RecompGames()
        {
            var settings = AppSettings.Load();
            settings.HiddenGames.Clear();
            AppSettings.Save(settings);

            await LoadGamesAsync();

            if (Games == null)
                return;

            foreach (var game in Games)
            {
                if (game != null && game.IsCustom && !IsGameHidden(settings, game))
                {
                    AddHiddenGame(settings, game);
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
                if (Games[i] != null && IsGameHidden(settings, Games[i]))
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
