namespace VRCVideoCacher.YTDL;

/// <summary>Mirror of upstream <c>VRCVideoCacher.YTDL.YtdlManager.CookiesPath</c> (L1).</summary>
public static class YtdlManager
{
    public static readonly string CookiesPath = ResolveCookiesPath();

    private static string ResolveCookiesPath()
    {
        var custom = Environment.GetEnvironmentVariable("VRCCB_MOCK_COOKIE_PATH");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom);
            }
            catch
            {
                // fall through
            }
        }

        return Path.Join(Program.DataPath, "youtube_cookies.txt");
    }
}
