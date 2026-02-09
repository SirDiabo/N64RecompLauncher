using N64RecompLauncher.Services;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Linq;
using System.Collections.Generic;

namespace N64RecompLauncher.Models
{
    public class ModPackageViewModel : INotifyPropertyChanged
    {
        private readonly ThunderstorePackage _package;
        private InstalledModInfo? _installedInfo;
        private double _downloadProgress;
        private bool _isDownloading;
        private CancellationTokenSource? _cancellationTokenSource;

        public ThunderstorePackage Package => _package;

        public string Name => _package.Name;
        public string Owner => _package.Owner;
        public string FullName => _package.FullName;
        public string PackageUrl => _package.PackageUrl;
        public ThunderstoreVersion? LatestVersion => _package.LatestVersion;

        public bool IsInstalled => _installedInfo != null;
        public string? InstalledVersion => _installedInfo?.Version;

        public int TotalDownloads => _package.Versions.Sum(v => v.Downloads);
        
        public int RatingScore => _package.RatingScore;

        public bool HasDependencies => LatestVersion?.Dependencies?.Any() == true;

        public string DependenciesText
        {
            get
            {
                if (!HasDependencies)
                    return string.Empty;

                var deps = LatestVersion?.Dependencies ?? new List<string>();
                var count = deps.Count;
                return count == 1 ? "1 dependency" : $"{count} dependencies";
            }
        }

        public string DependenciesTooltip
        {
            get
            {
                if (!HasDependencies)
                    return string.Empty;

                var deps = LatestVersion?.Dependencies ?? new List<string>();
                var dependencyNames = new List<string>();

                foreach (var dep in deps)
                {
                    var parts = dep.Split('-');
                    if (parts.Length >= 2)
                    {
                        var owner = parts[0];
                        var modName = parts[1];
                        dependencyNames.Add($"{owner}/{modName}");
                    }
                    else
                    {
                        dependencyNames.Add(dep);
                    }
                }

                return string.Join("\n", dependencyNames);
            }
        }

        public bool HasUpdate
        {
            get
            {
                if (!IsInstalled || LatestVersion == null || string.IsNullOrEmpty(InstalledVersion))
                    return false;

                return LatestVersion.VersionNumber != InstalledVersion;
            }
        }

        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                if (_downloadProgress != value)
                {
                    _downloadProgress = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            set
            {
                if (_isDownloading != value)
                {
                    _isDownloading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ButtonText));
                    OnPropertyChanged(nameof(ButtonColor));
                    OnPropertyChanged(nameof(IsButtonEnabled));
                }
            }
        }

        public string ButtonText
        {
            get
            {
                if (IsDownloading)
                    return "Cancel";
                if (HasUpdate)
                    return "Update";
                if (IsInstalled)
                    return "Installed";
                return "Download";
            }
        }

        public string ButtonColor
        {
            get
            {
                if (IsDownloading)
                    return "#ff3b30"; // Red for cancel
                if (HasUpdate)
                    return "#ff9500"; // Orange
                if (IsInstalled)
                    return "#34c759"; // Green
                return "#007aff"; // Blue
            }
        }

        public bool IsButtonEnabled
        {
            get
            {
                return IsDownloading || HasUpdate || !IsInstalled;
            }
        }

        public CancellationTokenSource? CancellationTokenSource
        {
            get => _cancellationTokenSource;
            set => _cancellationTokenSource = value;
        }

        public ModPackageViewModel(ThunderstorePackage package, InstalledModInfo? installedInfo)
        {
            _package = package;
            _installedInfo = installedInfo;
        }

        public void CancelDownload()
        {
            _cancellationTokenSource?.Cancel();
        }


        public void UpdateInstalledInfo(InstalledModInfo? installedInfo)
        {
            _installedInfo = installedInfo;
            
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(InstalledVersion));
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(ButtonColor));
            OnPropertyChanged(nameof(IsButtonEnabled));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
