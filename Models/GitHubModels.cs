namespace N64RecompLauncher.Models
{
    public class GitHubRelease
    {
        public string tag_name { get; set; }
        public GitHubAsset[] assets { get; set; }
        public bool prerelease { get; set; }
    }

    public class GitHubAsset
    {
        public string name { get; set; }
        public string browser_download_url { get; set; }
    }
}
