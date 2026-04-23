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
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "N64Recomp-Launcher/1.0");
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
            var standard = new List<object>
    {
        new { name = "Zelda 64",
            repository = "Zelda64Recomp/Zelda64Recomp",
            folderName = "Zelda64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/Zelda64Recomp/Zelda64Recomp/refs/heads/dev/icons/512.png" },

        new { name = "Goemon 64",
            repository = "klorfmorf/Goemon64Recomp",
            folderName = "Goemon64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/klorfmorf/Goemon64Recomp/refs/heads/dev/icons/512.png" },

        new { name = "Mario Kart 64",
            repository = "sonicdcer/MarioKart64Recomp",
            folderName = "MarioKart64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/MarioKart64Recomp/refs/heads/main/icons/512.png" },

        new { name = "Dinosaur Planet",
            repository = "DinosaurPlanetRecomp/dino-recomp",
            folderName = "DinoPlanetRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/DinosaurPlanetRecomp/dino-recomp/refs/heads/main/icons/64.png" },

        new { name = "Dr. Mario 64",
            repository = "theboy181/drmario64_recomp_plus",
            folderName = "drmario64_recomp",
            gameIconUrl  = "https://raw.githubusercontent.com/theboy181/drmario64_recomp_plus/refs/heads/main/icons/512.png" },

        new { name = "Duke Nukem: Zero Hour",
            repository = "sonicdcer/DNZHRecomp",
            folderName = "DNZHRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/DNZHRecomp/refs/heads/main/icons/512.png" },

        new { name = "Star Fox 64",
            repository = "sonicdcer/Starfox64Recomp",
            folderName = "Starfox64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/sonicdcer/Starfox64Recomp/refs/heads/main/icons/512.png" },

        new  { name = "Banjo 64",
            repository = "BanjoRecomp/BanjoRecomp",
            folderName = "BanjoRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/BanjoRecomp/BanjoRecomp/refs/heads/main/icons/app.png" },
            
        new  { name = "Bomberman 64",
            repository = "RevoSucks/BM64Recomp",
            folderName = "BM64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/RevoSucks/BM64Recomp/refs/heads/master/icons/512.png" },
    };

            var experimental = new List<object>
    {
        new { name = "Chameleon Twist",
            repository = "Rainchus/ChameleonTwist1-JP-Recomp",
            folderName = "ChameleonTwistRecompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/c1f22f4c38899f51f1ed3ce20120bbd9.png" },

        new { name = "Mega Man 64",
            repository = "MegaMan64Recomp/MegaMan64Recompiled",
            folderName = "MegaMan64Recompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/850618e22f83f152773d2a3e51168812.png" },

        new { name = "Quest 64",
            repository = "Rainchus/Quest64-Recomp",
            folderName = "Quest64Recompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/Rainchus/Quest64-Recomp/refs/heads/main/icons/512.png" },

        new { name = "Space Station Silicon Valley",
            repository = "Cellenseres/SSSV_Recomp",
            folderName = "SSSVRecompiled",
            gameIconUrl  = "https://raw.githubusercontent.com/Cellenseres/SSSV_Recomp/refs/heads/main/icons/512.png" },
    };

            var custom = new List<object>
    {
        new { name = "Star Fox 64 (Starship)",
            repository = "harbourmasters/starship",
            folderName = "harbourmasters.starship",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/dc2ee2a5add7154447a4644326e33386/32/256x256.png" },

        new { name = "Zelda OoT (Ship of Harkinian)",
            repository = "harbourmasters/shipwright",
            folderName = "harbourmasters.shipofharkinian",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/d1cd0a8c9b28f58703a097d5a25534e3/32/256x256.png" },

        new { name = "Mario Kart 64 (SpaghettiKart)",
            repository = "harbourmasters/spaghettikart",
            folderName = "harbourmasters.spaghettikart",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/5e5e0bd5ad7c2ca72b0c5ff8b6debbba.png" },

        new { name = "Zelda MM (2 Ship 2 Harkinian)",
            repository = "harbourmasters/2ship2harkinian",
            folderName = "harbourmasters.2ship2harkinian",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/6c7dbdd98cd70f67f102524761f3b4d2/24/256x256.png" },

        new { name = "Super Mario 64 (Ghostship)",
            repository = "harbourmasters/ghostship",
            folderName = "harbourmasters.ghostship",
            gameIconUrl  = "https://raw.githubusercontent.com/HarbourMasters/Ghostship/refs/heads/develop/nx-logo.jpg" },

        new { name = "Sonic Unleashed Recompiled",
            repository = "hedge-dev/UnleashedRecomp",
            folderName = "Sonic Unleashed Recompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/63a99723ebb3af94d52b474c3b21dbe1/24/512x512.png" },

        new { name = "Super Metroid Launcher",
            repository = "RadzPrower/Super-Metroid-Launcher",
            folderName = "RadzPrower.Super-Metroid-Launcher",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/26e3dcb90aa10011db5b660c463f325f/32/256x256.png" },

        new { name = "Zelda: ALttP (Zelda 3 Launcher)",
            repository = "RadzPrower/Zelda-3-Launcher",
            folderName = "RadzPrower.Zelda-3-Launcher",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/1b39a10cc39ee53e5b2fdc1eda1eb5da.png" },

        new { name = "WipeOut Phantom Edition",
            repository = "wipeout-phantom-edition/wipeout-phantom-edition",
            folderName = "WipeOut Phantom Edition",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/9fcb5252180dc29b22976c5c63b322e7.png" },

        new { name = "Perfect Dark",
            repository = "fgsfdsfgs/perfect_dark",
            folderName = "fgsfdsfgs.perfect_dark",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/64314c17210c549a854f1f1c7adce8b6/32/256x256.png" },

        new { name = "SM64 CoopDX",
            repository = "coop-deluxe/sm64coopdx",
            folderName = "coop-deluxe.sm64coopdx",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/e3dd863ef4277e82f712a5bd8fefe7d7.png" },

        new { name = "LoD: Severed Chains",
            repository = "Legend-of-Dragoon-Modding/Severed-Chains",
            folderName = "Legend-of-Dragoon-Modding.Severed-Chains",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/ddc2c94d27d2c6d46b33acf21b21a641.png" },

        new { name = "REDRIVER 2",
            repository = "OpenDriver2/REDRIVER2",
            folderName = "OpenDriver2.REDRIVER2",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/ca0739bf1344242a820fe79d0ac17d65.png" },

        new { name = "Super Mario World",
            repository = "snesrev/smw",
            folderName = "snesrev.smw",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/5ba01c0e82cd96577309302faf900a0d/32/1024x1024.png" },

        new {name = "Super Mario Bros. Remastered",
            repository = "JHDev2006/Super-Mario-Bros.-Remastered-Public",
            folderName = "JHDev2006.SuperMarioBrosRemastered",
            gameIconUrl  = "https://raw.githubusercontent.com/JHDev2006/Super-Mario-Bros.-Remastered-Public/refs/heads/main/icon.png" },

        new {name = "Link's Awakening DX HD",
            repository = "BigheadSMZ/Zelda-LA-DX-HD-Updated",
            folderName = "BigheadSMZ.ZeldaLAHD",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/17ba4b0f5b8bff185d7359c88548f8b7/32/256x256.png" },

        new {name = "Dragon Ball Z Budokai",
            repository = "WistfulHopes/DBZ1",
            folderName = "DBZ1Recompiled",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon_thumb/61501c1f651da2774f8bb6bbec365d84.png" },

        new {name = "Animal Crossing (Game Cube)",
            repository = "flyngmt/ACGC-PC-Port",
            folderName = "flyngmt.acgc_pcport",
            gameIconUrl  = "https://cdn2.steamgriddb.com/icon/63d9e8a72c3a2f029c635f9b194839d2.png" },

        new {name = "Banjo-Kazooie: Nuts & Bolts",
            repository = "masterspike52/reNut",
            folderName = "masterspike52.re-nut",
            gameIconUrl  = "https://raw.githubusercontent.com/masterspike52/reNut/refs/heads/main/icon/app.ico" },

        new {name = "Infinite Mario 64",
            repository = "Brawmario/infinite-mario-64-ever",
            folderName = "Brawmario.infinite-mario-64-ever",
            gameIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/9b1347ebd516dd05210fcc9e8291b9d1.png" },
        
        new {name = "Sonic 1 Forever",
            repository = "ElspethThePict/S1Forever",
            folderName = "ElspethThePict.S1Forever",
            gameIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/7884a9652e94555c70f96b6be63be216.png" },

        new {name = "Sonic 3 AIR",
            repository = "Eukaryot/sonic3air",
            folderName = "Eukaryot.sonic3air",
            gameIconUrl = "https://cdn2.steamgriddb.com/icon/a70dab11c90d06b809d0be230731762a/32/256x256.png" },

        new {name = "Viva Pinata Trouble in Paradise",
            repository = "SolarCookies/TiP-Recomp",
            folderName = "SolarCookies.TiP-Recomp",
            gameIconUrl = "https://raw.githubusercontent.com/SolarCookies/TiP-Recomp/refs/heads/main/icon/app.ico" },

        new {name = "Jak & Daxter",
            repository = "open-goal/jak-project",
            folderName = "open-goal.jak-project",
            gameIconUrl = "https://cdn2.steamgriddb.com/icon_thumb/48ecd78598456095d654c9196f973b00.png" }
    };

            return (standard, experimental, custom);
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
