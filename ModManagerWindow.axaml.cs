using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using N64RecompLauncher.Models;
using N64RecompLauncher.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace N64RecompLauncher
{
    public partial class ModManagerWindow : Window, INotifyPropertyChanged
    {
        private readonly GameInfo _game;
        private readonly string _gamesFolder;
        private readonly ThunderstoreService _thunderstoreService;
        private readonly string? _community;
        private ModsManifest _modsManifest;
        private string _modsManifestPath = string.Empty;
        private List<ModPackageViewModel> _allMods = new();
        private string _searchText = string.Empty;
        private string _selectedFilter = "All";
        private string _selectedSort = "Top Rated";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(NoModsAvailable));
                }
            }
        }

        private bool _hasError;
        public bool HasError
        {
            get => _hasError;
            set
            {
                if (_hasError != value)
                {
                    _hasError = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged();
                    ApplyFilters();
                }
            }
        }

        public string SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                if (_selectedFilter != value)
                {
                    _selectedFilter = value;
                    OnPropertyChanged();
                    ApplyFilters();
                }
            }
        }

        public string SelectedSort
        {
            get => _selectedSort;
            set
            {
                if (_selectedSort != value)
                {
                    _selectedSort = value;
                    OnPropertyChanged();
                    ApplyFilters();
                }
            }
        }

        public ObservableCollection<ModPackageViewModel> Mods { get; set; } = new();

        public string GameName => _game?.Name ?? "Unknown Game";

        public bool HasMods => Mods.Count > 0 && !IsLoading;

        public bool NoModsAvailable => Mods.Count == 0 && !IsLoading && !HasError;

        public bool HasActiveDownloads => Mods.Any(m => m.IsDownloading);

        public bool HasUpdatesAvailable => _allMods.Any(m => m.HasUpdate);

        public ModManagerWindow()
        {
            InitializeComponent();
            _thunderstoreService = new ThunderstoreService();
            _modsManifest = new ModsManifest();
        }

        public ModManagerWindow(GameInfo game, string gamesFolder) : this()
        {
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _gamesFolder = gamesFolder ?? throw new ArgumentNullException(nameof(gamesFolder));

            _community = ThunderstoreService.GetCommunityForRepository(game.Repository ?? string.Empty);

            DataContext = this;

            var settings = AppSettings.Load();
            var gamePath = GetGameModsPath(settings.IsPortable);
            _modsManifestPath = Path.Combine(gamePath, "mods.json");

            if (_community == null)
            {
                HasError = true;
                ErrorMessage = $"{game.Name} does not have a Thunderstore community configured. " +
                               "Mod support may be added in the future.";
                StatusMessage = "No Thunderstore community available";
            }
            else
            {
                StatusMessage = $"Community: {_community}";
            }
            
            OnPropertyChanged(nameof(GameName));

            // Load existing mods manifest
            LoadModsManifest();
        }


        private string GetGameModsPath(bool isPortable)
        {
            if (isPortable)
            {
                var gamePath = Path.Combine(_gamesFolder, _game.FolderName ?? "");
                return Path.Combine(gamePath, "mods");
            }
            else
            {
                // Non-portable mode: mods go in AppData/LocalLow
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var gameModsPath = Path.Combine(appDataPath, _game.FolderName ?? "", "mods");
                return gameModsPath;
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            
            if (_community != null)
            {
                _ = LoadModsAsync();
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);

            // Prevent closing if there are active downloads
            if (HasActiveDownloads)
            {
                e.Cancel = true;
                _ = ShowMessageAsync("Downloads in Progress", 
                    "Please wait for all mod downloads to complete or cancel them before closing the window.");
            }
        }

        private void LoadModsManifest()
        {
            try
            {
                if (File.Exists(_modsManifestPath))
                {
                    var json = File.ReadAllText(_modsManifestPath);
                    _modsManifest = JsonSerializer.Deserialize<ModsManifest>(json) ?? new ModsManifest();
                }
                else
                {
                    _modsManifest = new ModsManifest();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load mods manifest: {ex.Message}");
                _modsManifest = new ModsManifest();
            }
        }

        private void SaveModsManifest()
        {
            try
            {
                var modsDir = Path.GetDirectoryName(_modsManifestPath);
                if (!string.IsNullOrEmpty(modsDir))
                {
                    Directory.CreateDirectory(modsDir);
                }

                _modsManifest.LastUpdated = DateTime.UtcNow;
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_modsManifest, options);
                File.WriteAllText(_modsManifestPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save mods manifest: {ex.Message}");
            }
        }

        private async Task LoadModsAsync()
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
            _allMods.Clear();

            try
            {
                var packages = await _thunderstoreService.GetPackagesAsync(_community!);

                // Filter and sort packages
                var filteredPackages = packages
                    .Where(p => !p.IsDeprecated && p.Versions.Any())
                    .OrderByDescending(p => p.IsPinned)
                    .ThenByDescending(p => p.RatingScore)
                    .ThenByDescending(p => p.Versions.Sum(v => v.Downloads))
                    .ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Create view models with installation status
                    foreach (var package in filteredPackages)
                    {
                        var installedMod = _modsManifest.Mods.FirstOrDefault(m =>
                            m.Owner == package.Owner && m.Name == package.Name);

                        var viewModel = new ModPackageViewModel(package, installedMod);
                        _allMods.Add(viewModel);
                    }

                    // Apply current filters
                    ApplyFilters();

                    // Set loading to false and update status
                    IsLoading = false;

                    // Explicitly notify computed properties after IsLoading changes
                    OnPropertyChanged(nameof(HasMods));
                    OnPropertyChanged(nameof(NoModsAvailable));

                    UpdateStatusMessage();
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsLoading = false;
                    HasError = true;
                    ErrorMessage = $"Failed to load mods: {ex.Message}";
                    StatusMessage = "Error loading mods";
                    Debug.WriteLine($"Error loading mods: {ex}");
                });
            }
        }

        private void ApplyFilters()
        {
            if (_allMods == null || _allMods.Count == 0)
                return;

            var filtered = _allMods.AsEnumerable();

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var search = SearchText.ToLowerInvariant();
                filtered = filtered.Where(m =>
                    m.Name.ToLowerInvariant().Contains(search) ||
                    m.Owner.ToLowerInvariant().Contains(search) ||
                    (m.LatestVersion?.Description?.ToLowerInvariant().Contains(search) ?? false));
            }

            // Apply installation status filter
            filtered = SelectedFilter switch
            {
                "Downloaded" => filtered.Where(m => m.IsInstalled),
                "Not Downloaded" => filtered.Where(m => !m.IsInstalled),
                "Updates Available" => filtered.Where(m => m.HasUpdate),
                _ => filtered // "All"
            };

            // Apply sorting
            filtered = SelectedSort switch
            {
                "Most Downloaded" => filtered.OrderByDescending(m => m.TotalDownloads),
                "Newest" => filtered.OrderByDescending(m => m.Package.DateCreated),
                "Last Updated" => filtered.OrderByDescending(m => m.Package.DateUpdated),
                "Top Rated" => filtered.OrderByDescending(m => m.Package.IsPinned)
                                       .ThenByDescending(m => m.Package.RatingScore),
                _ => filtered.OrderByDescending(m => m.Package.IsPinned)
                            .ThenByDescending(m => m.Package.RatingScore) // Default: Top Rated
            };

            Mods.Clear();
            foreach (var mod in filtered)
            {
                Mods.Add(mod);
            }

            OnPropertyChanged(nameof(HasMods));
            OnPropertyChanged(nameof(NoModsAvailable));
            OnPropertyChanged(nameof(HasUpdatesAvailable));
            UpdateStatusMessage();
        }

        private void UpdateStatusMessage()
        {
            if (_allMods.Count > 0)
            {
                var installedCount = _allMods.Count(m => m.IsInstalled);
                var updateCount = _allMods.Count(m => m.HasUpdate);
                var displayCount = Mods.Count;

                if (displayCount < _allMods.Count)
                {
                    StatusMessage = $"Showing {displayCount} of {_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")}";
                }
                else if (installedCount > 0)
                {
                    StatusMessage = $"Found {_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")} ({installedCount} installed";
                    if (updateCount > 0)
                        StatusMessage += $", {updateCount} update{(updateCount == 1 ? "" : "s")} available";
                    StatusMessage += ")";
                }
                else
                {
                    StatusMessage = $"Found {_allMods.Count} mod{(_allMods.Count == 1 ? "" : "s")}";
                }
            }
            else
            {
                StatusMessage = "No mods available";
            }
        }

        private void RefreshMods_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadModsAsync();
        }

        private async void UpdateAll_Click(object sender, RoutedEventArgs e)
        {
            var modsWithUpdates = _allMods.Where(m => m.HasUpdate && !m.IsDownloading).ToList();
            
            if (!modsWithUpdates.Any())
            {
                await ShowMessageAsync("No Updates", "All mods are up to date!");
                return;
            }

            var result = await ShowConfirmAsync(
                "Update All Mods",
                $"Are you sure you want to update {modsWithUpdates.Count} mod{(modsWithUpdates.Count == 1 ? "" : "s")}?\n\nThis may take a few minutes.");

            if (!result)
                return;

            StatusMessage = $"Updating {modsWithUpdates.Count} mod{(modsWithUpdates.Count == 1 ? "" : "s")}...";
            
            foreach (var modViewModel in modsWithUpdates)
            {
                var package = modViewModel.Package;
                var latestVersion = package.Versions.FirstOrDefault();
                if (latestVersion == null)
                    continue;

                var cts = new CancellationTokenSource();
                modViewModel.CancellationTokenSource = cts;

                try
                {
                    modViewModel.IsDownloading = true;
                    modViewModel.DownloadProgress = 0;
                    OnPropertyChanged(nameof(HasActiveDownloads));

                    var settings = AppSettings.Load();
                    var modsPath = GetGameModsPath(settings.IsPortable);
                    Directory.CreateDirectory(modsPath);

                    StatusMessage = $"Updating {package.Name}...";

                    // Download dependencies first if any exist
                    var downloadedDependencies = new List<string>();
                    if (latestVersion.Dependencies?.Any() == true)
                    {
                        downloadedDependencies = await DownloadDependenciesAsync(latestVersion.Dependencies, modsPath, cts.Token);
                    }

                    var tempFile = Path.Combine(Path.GetTempPath(), $"{package.FullName}-{latestVersion.VersionNumber}.zip");

                    var progress = new Progress<double>(percent =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            modViewModel.DownloadProgress = percent;
                        });
                    });

                    await _thunderstoreService.DownloadModAsync(latestVersion.DownloadUrl, tempFile, progress, cts.Token);

                    modViewModel.IsDownloading = false;
                    OnPropertyChanged(nameof(HasActiveDownloads));

                    // Remove old files
                    var existingMod = _modsManifest.Mods.FirstOrDefault(m =>
                        m.Owner == package.Owner && m.Name == package.Name);

                    if (existingMod != null)
                    {
                        foreach (var file in existingMod.Files)
                        {
                            var filePath = Path.Combine(modsPath, file);
                            if (File.Exists(filePath))
                            {
                                try { File.Delete(filePath); }
                                catch { }
                            }
                        }
                        _modsManifest.Mods.Remove(existingMod);
                    }

                    // Extract files
                    var installedFiles = new List<string>();
                    using (var archive = ZipFile.OpenRead(tempFile))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                            if (extension == ".rtz" || extension == ".nrm")
                            {
                                var destinationPath = Path.Combine(modsPath, entry.Name);
                                entry.ExtractToFile(destinationPath, overwrite: true);
                                installedFiles.Add(entry.Name);
                            }
                        }
                    }

                    if (File.Exists(tempFile))
                        File.Delete(tempFile);

                    // Update manifest
                    if (installedFiles.Any())
                    {
                        var newModInfo = new InstalledModInfo
                        {
                            Owner = package.Owner,
                            Name = package.Name,
                            Version = latestVersion.VersionNumber,
                            InstalledDate = DateTime.UtcNow,
                            Files = installedFiles,
                            Dependencies = downloadedDependencies
                        };

                        _modsManifest.Mods.Add(newModInfo);
                        SaveModsManifest();
                        
                        // Update the viewmodel
                        modViewModel.UpdateInstalledInfo(newModInfo);
                    }

                    Debug.WriteLine($"Updated: {package.Name} to v{latestVersion.VersionNumber}");
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine($"Update cancelled for {package.Name}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to update {package.Name}: {ex.Message}");
                }
                finally
                {
                    modViewModel.IsDownloading = false;
                    modViewModel.DownloadProgress = 0;
                    modViewModel.CancellationTokenSource = null;
                    OnPropertyChanged(nameof(HasActiveDownloads));
                    cts?.Dispose();
                }
            }

            // Show completion message briefly, then restore normal status
            StatusMessage = "All updates completed!";
            await Task.Delay(2000);
            
            OnPropertyChanged(nameof(HasUpdatesAvailable));
            RefreshModInstallationStatus();
            // UpdateStatusMessage is already called by RefreshModInstallationStatus -> ApplyFilters
        }

        /// <summary>
        /// Updates the installation status of existing mods in the UI without full reload
        /// </summary>
        private void RefreshModInstallationStatus()
        {
            // Reload the manifest to get latest installed mods
            LoadModsManifest();

            // Update all existing viewmodels with new installation info
            foreach (var viewModel in _allMods)
            {
                var installedMod = _modsManifest.Mods.FirstOrDefault(m =>
                    m.Owner.Equals(viewModel.Owner, StringComparison.OrdinalIgnoreCase) &&
                    m.Name.Equals(viewModel.Name, StringComparison.OrdinalIgnoreCase));

                // Update the viewmodel with new installation info
                viewModel.UpdateInstalledInfo(installedMod);
            }

            // Refresh the filtered list to reflect changes
            ApplyFilters();
        }

        /// <summary>
        /// Downloads and installs mod dependencies recursively
        /// </summary>
        private async Task<List<string>> DownloadDependenciesAsync(List<string> dependencies, string modsPath, CancellationToken cancellationToken)
        {
            var downloadedDependencies = new List<string>();
            
            if (dependencies == null || !dependencies.Any())
                return downloadedDependencies;

            foreach (var dependency in dependencies)
            {
                // Parse dependency string (format: "Owner-ModName-Version")
                var parts = dependency.Split('-');
                if (parts.Length < 2)
                {
                    Debug.WriteLine($"Invalid dependency format: {dependency}");
                    continue;
                }

                var owner = parts[0];
                var modName = parts[1];
                var fullDependencyName = $"{owner}/{modName}";

                // Check if dependency is already installed
                var existingDep = _modsManifest.Mods.FirstOrDefault(m =>
                    m.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                    m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));

                if (existingDep != null)
                {
                    Debug.WriteLine($"Dependency {fullDependencyName} already installed, skipping");
                    downloadedDependencies.Add(dependency);
                    continue;
                }

                Debug.WriteLine($"Downloading dependency: {fullDependencyName}");
                StatusMessage = $"Downloading dependency: {modName}...";

                try
                {
                    // Fetch dependency package info from current community
                    var depPackage = await _thunderstoreService.GetPackageAsync(_community!, owner, modName);
                    if (depPackage == null || !depPackage.Versions.Any())
                    {
                        Debug.WriteLine($"Could not find dependency {fullDependencyName} in community {_community} via direct API");
                        
                        // Fallback: Search through all loaded packages
                        Debug.WriteLine($"Trying fallback: searching through loaded packages for {fullDependencyName}");
                        var allPackages = await _thunderstoreService.GetPackagesAsync(_community!);
                        depPackage = allPackages.FirstOrDefault(p => 
                            p.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) && 
                            p.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
                        
                        if (depPackage == null || !depPackage.Versions.Any())
                        {
                            Debug.WriteLine($"Fallback failed: Could not find dependency {fullDependencyName} in package list either");
                            // Still add to list even if not found, to avoid repeated warnings
                            downloadedDependencies.Add(dependency);
                            continue;
                        }
                        else
                        {
                            Debug.WriteLine($"Fallback succeeded: Found {fullDependencyName} in package list");
                        }
                    }

                    var depVersion = depPackage.Versions.FirstOrDefault();
                    if (depVersion == null)
                    {
                        Debug.WriteLine($"No version found for dependency: {fullDependencyName}");
                        downloadedDependencies.Add(dependency);
                        continue;
                    }

                    // Download dependency
                    var tempFile = Path.Combine(Path.GetTempPath(), $"{depPackage.FullName}-{depVersion.VersionNumber}.zip");

                    await _thunderstoreService.DownloadModAsync(depVersion.DownloadUrl, tempFile, null, cancellationToken);

                    // Extract dependency files
                    var installedFiles = new List<string>();
                    using (var archive = ZipFile.OpenRead(tempFile))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                                continue;

                            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                            if (extension == ".rtz" || extension == ".nrm")
                            {
                                var destinationPath = Path.Combine(modsPath, entry.Name);
                                entry.ExtractToFile(destinationPath, overwrite: true);
                                installedFiles.Add(entry.Name);
                                Debug.WriteLine($"Extracted dependency file: {entry.Name}");
                            }
                        }
                    }

                    // Clean up temp file
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);

                    // Recursively download dependencies of this dependency (do this even if no files extracted)
                    var nestedDeps = new List<string>();
                    if (depVersion.Dependencies?.Any() == true)
                    {
                        nestedDeps = await DownloadDependenciesAsync(depVersion.Dependencies, modsPath, cancellationToken);
                    }

                    // Add to manifest even if no files were extracted
                    // Some dependencies are just libraries/frameworks without mod files
                    var depModInfo = new InstalledModInfo
                    {
                        Owner = depPackage.Owner,
                        Name = depPackage.Name,
                        Version = depVersion.VersionNumber,
                        InstalledDate = DateTime.UtcNow,
                        Files = installedFiles,
                        Dependencies = nestedDeps
                    };
                    _modsManifest.Mods.Add(depModInfo);
                    SaveModsManifest();

                    if (installedFiles.Any())
                    {
                        Debug.WriteLine($"Installed dependency {fullDependencyName} with {installedFiles.Count} file(s)");
                    }
                    else
                    {
                        Debug.WriteLine($"Installed dependency {fullDependencyName} (no mod files, may be a library/framework)");
                    }

                    downloadedDependencies.Add(dependency);

                    // Update UI to reflect the new installation
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        RefreshModInstallationStatus();
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to download dependency {fullDependencyName}: {ex.Message}");
                    Debug.WriteLine($"Exception details: {ex}");
                    // Continue with other dependencies even if one fails
                }
            }

            return downloadedDependencies;
        }

        private async void DownloadMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ModPackageViewModel viewModel)
                return;

            // If already downloading, cancel it
            if (viewModel.IsDownloading)
            {
                viewModel.CancelDownload();
                return;
            }

            var package = viewModel.Package;
            var latestVersion = package.Versions.FirstOrDefault();
            if (latestVersion == null)
            {
                await ShowMessageAsync("Error", "No version available for this mod.");
                return;
            }

            // Check if already up to date
            if (viewModel.IsInstalled && !viewModel.HasUpdate)
            {
                await ShowMessageAsync("Already Downloaded", $"{package.Name} is already up to date (v{viewModel.InstalledVersion}).");
                return;
            }

            var cts = new CancellationTokenSource();
            viewModel.CancellationTokenSource = cts;

            try
            {
                var originalContent = button.Content;
                button.Content = viewModel.HasUpdate ? "Updating..." : "Downloading...";
                
                viewModel.IsDownloading = true;
                viewModel.DownloadProgress = 0;
                OnPropertyChanged(nameof(HasActiveDownloads));

                var settings = AppSettings.Load();
                var modsPath = GetGameModsPath(settings.IsPortable);
                Directory.CreateDirectory(modsPath);

                // Debug: Log dependency information
                Debug.WriteLine($"=== Starting download for {package.Name} ===");
                Debug.WriteLine($"Has Dependencies: {latestVersion.Dependencies != null}");
                if (latestVersion.Dependencies != null)
                {
                    Debug.WriteLine($"Dependency Count: {latestVersion.Dependencies.Count}");
                    foreach (var dep in latestVersion.Dependencies)
                    {
                        Debug.WriteLine($"  - Dependency: {dep}");
                    }
                }

                // Download dependencies first if any exist
                var downloadedDependencies = new List<string>();
                if (latestVersion.Dependencies?.Any() == true)
                {
                    Debug.WriteLine($"Entering dependency download block for {package.Name}...");
                    StatusMessage = $"Downloading dependencies for {package.Name}...";
                    downloadedDependencies = await DownloadDependenciesAsync(latestVersion.Dependencies, modsPath, cts.Token);
                    
                    Debug.WriteLine($"Downloaded {downloadedDependencies.Count} of {latestVersion.Dependencies.Count} dependencies");
                    
                    // Only show warning if NO dependencies were downloaded successfully
                    // (All downloads failed, not just cross-community dependencies)
                    if (downloadedDependencies.Count == 0 && latestVersion.Dependencies.Count > 0)
                    {
                        await ShowMessageAsync("Warning", $"Dependencies for {package.Name} could not be downloaded.\n\nThe mod may not work correctly if it requires specific dependencies.");
                    }
                }
                else
                {
                    Debug.WriteLine($"No dependencies to download for {package.Name}");
                }

                var tempFile = Path.Combine(Path.GetTempPath(), $"{package.FullName}-{latestVersion.VersionNumber}.zip");

                var progress = new Progress<double>(percent =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        viewModel.DownloadProgress = percent;
                        StatusMessage = $"{(viewModel.HasUpdate ? "Updating" : "Downloading")} {package.Name}... {percent:F1}%";
                    });
                });

                await _thunderstoreService.DownloadModAsync(latestVersion.DownloadUrl, tempFile, progress, cts.Token);

                viewModel.IsDownloading = false;
                OnPropertyChanged(nameof(HasActiveDownloads));
                StatusMessage = $"Installing {package.Name}...";

                var existingMod = _modsManifest.Mods.FirstOrDefault(m =>
                    m.Owner == package.Owner && m.Name == package.Name);

                if (existingMod != null)
                {
                    foreach (var file in existingMod.Files)
                    {
                        var filePath = Path.Combine(modsPath, file);
                        if (File.Exists(filePath))
                        {
                            try
                            {
                                File.Delete(filePath);
                                Debug.WriteLine($"Deleted: {file}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to delete old file {file}: {ex.Message}");
                            }
                        }
                    }
                    _modsManifest.Mods.Remove(existingMod);
                }

                // Extract only .rtz and .nrm files directly to Mods folder
                int extractedCount = 0;
                var installedFiles = new List<string>();

                using (var archive = ZipFile.OpenRead(tempFile))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                            continue;

                        // Get file extension
                        var extension = Path.GetExtension(entry.Name).ToLowerInvariant();

                        if (extension == ".rtz" || extension == ".nrm")
                        {
                            var destinationPath = Path.Combine(modsPath, entry.Name);

                            entry.ExtractToFile(destinationPath, overwrite: true);
                            extractedCount++;
                            installedFiles.Add(entry.Name);
                            Debug.WriteLine($"Extracted: {entry.Name}");
                        }
                    }
                }

                // Clean up temp file
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                if (extractedCount == 0)
                {
                    StatusMessage = $"No mod files found in {package.Name}";
                    await ShowMessageAsync("Warning", $"{package.Name} was downloaded but contained no .rtz or .nrm mod files.\n\nThis may not be a valid mod for this game.");
                }
                else
                {
                    // Update manifest
                    var newModInfo = new InstalledModInfo
                    {
                        Owner = package.Owner,
                        Name = package.Name,
                        Version = latestVersion.VersionNumber,
                        InstalledDate = DateTime.UtcNow,
                        Files = installedFiles,
                        Dependencies = downloadedDependencies
                    };

                    _modsManifest.Mods.Add(newModInfo);
                    SaveModsManifest();

                    // Update the viewmodel with new installation info
                    viewModel.UpdateInstalledInfo(newModInfo);
                    
                    // Refresh installation status for all mods without recreating ViewModels
                    RefreshModInstallationStatus();
                    
                    // Show success message briefly, then restore normal status
                    StatusMessage = $"Successfully installed {extractedCount} mod file{(extractedCount == 1 ? "" : "s")} from {package.Name}";
                    await Task.Delay(2000);
                    UpdateStatusMessage();
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"Download cancelled: {package.Name}";
                Debug.WriteLine($"Download cancelled for {package.Name}");
                // Restore normal status after brief delay
                await Task.Delay(1500);
                UpdateStatusMessage();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to install {package.Name}";
                Debug.WriteLine($"Error downloading mod: {ex}");
                // Show error dialog and restore normal status
                await ShowMessageAsync("Error", $"Failed to download/install mod: {ex.Message}");
                UpdateStatusMessage();
            }
            finally
            {
                viewModel.IsDownloading = false;
                viewModel.DownloadProgress = 0;
                viewModel.CancellationTokenSource = null;
                // Button stays enabled - it will show correct text based on state
                OnPropertyChanged(nameof(HasActiveDownloads));
                cts?.Dispose();
            }
        }

        private void ViewModPage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ModPackageViewModel viewModel)
                return;

            try
            {
                var url = viewModel.PackageUrl;
                if (string.IsNullOrEmpty(url))
                {
                    url = $"https://thunderstore.io/c/{_community}/p/{viewModel.Owner}/{viewModel.Name}/";
                }

                OpenUrl(url);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening mod page: {ex.Message}");
            }
        }

        private async void DeleteMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not ModPackageViewModel viewModel)
                return;

            if (!viewModel.IsInstalled)
            {
                await ShowMessageAsync("Not Installed", $"{viewModel.Name} is not currently installed.");
                return;
            }

            // Confirm deletion
            var result = await ShowConfirmAsync(
                "Delete Mod",
                $"Are you sure you want to delete {viewModel.Name}?\n\nThis will remove all mod files and cannot be undone.");

            if (!result)
                return;

            try
            {
                StatusMessage = $"Deleting {viewModel.Name}...";

                var installedMod = _modsManifest.Mods.FirstOrDefault(m =>
                    m.Owner == viewModel.Owner && m.Name == viewModel.Name);

                if (installedMod != null)
                {
                    var settings = AppSettings.Load();
                    var modsPath = GetGameModsPath(settings.IsPortable);
                    int deletedCount = 0;

                    foreach (var file in installedMod.Files)
                    {
                        var filePath = Path.Combine(modsPath, file);
                        if (File.Exists(filePath))
                        {
                            try
                            {
                                File.Delete(filePath);
                                deletedCount++;
                                Debug.WriteLine($"Deleted: {file}");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to delete {file}: {ex.Message}");
                            }
                        }
                    }

                    _modsManifest.Mods.Remove(installedMod);
                    SaveModsManifest();

                    viewModel.UpdateInstalledInfo(null);
                    
                    RefreshModInstallationStatus();
                    
                    StatusMessage = $"Successfully deleted {viewModel.Name}";
                    await Task.Delay(1000);
                    UpdateStatusMessage();
                }
                else
                {
                    await ShowMessageAsync("Error", $"Could not find installation information for {viewModel.Name}.");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to delete {viewModel.Name}";
                Debug.WriteLine($"Error deleting mod: {ex}");
                // Show error dialog and restore normal status
                await ShowMessageAsync("Error", $"Failed to delete mod: {ex.Message}");
                UpdateStatusMessage();
            }
        }

        private void OptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = PlacementMode.BottomEdgeAlignedLeft;
                button.ContextMenu.Open(button);
            }
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OpenUrl(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open URL: {ex.Message}");
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var messageBox = new Window
            {
                Title = title,
                Width = 500,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 20)
                        },
                        new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            MinWidth = 100
                        }
                    }
                }
            };

            var okButton = ((StackPanel)messageBox.Content).Children[1] as Button;
            if (okButton != null)
            {
                okButton.Click += (s, e) => messageBox.Close();
            }

            await messageBox.ShowDialog(this);
        }

        private async Task<bool> ShowConfirmAsync(string title, string message)
        {
            bool result = false;
            var messageBox = new Window
            {
                Title = title,
                Width = 500,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new StackPanel
                {
                    Margin = new Thickness(20),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 20)
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Spacing = 10,
                            Children =
                            {
                                new Button
                                {
                                    Content = "Yes",
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
            var yesButton = buttonPanel?.Children[0] as Button;
            var noButton = buttonPanel?.Children[1] as Button;

            if (yesButton != null)
            {
                yesButton.Click += (s, e) =>
                {
                    result = true;
                    messageBox.Close();
                };
            }

            if (noButton != null)
            {
                noButton.Click += (s, e) =>
                {
                    result = false;
                    messageBox.Close();
                };
            }

            await messageBox.ShowDialog(this);
            return result;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _thunderstoreService?.Dispose();
        }

        public new event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                SearchText = textBox.Text ?? string.Empty;
            }
        }

        private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                SelectedFilter = item.Content?.ToString() ?? "All";
            }
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                SelectedSort = item.Content?.ToString() ?? "Top Rated";
            }
        }
    }
}
