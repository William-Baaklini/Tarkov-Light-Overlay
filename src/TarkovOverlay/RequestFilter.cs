namespace TarkovOverlay;

/// <summary>
/// Host-based request blocking.
///
/// Two deliberate design choices, both learned the hard way against Fandom:
///
/// 1. Blocked requests are answered with an empty <b>200</b>, never a 403 or a
///    connection failure. Anti-adblock scripts detect blocking by watching for
///    requests that error; a clean empty response looks like an ad that simply
///    had nothing to show, so the page keeps working.
/// 2. Anti-adblock circumvention CDNs are only cut off in <b>strict</b> mode.
///    They rotate hostnames constantly and some sites will not render without
///    them, so that trade is the user's to make, not a silent default.
/// </summary>
internal static class RequestFilter
{
    /// <summary>Set TARKOV_OVERLAY_LOG_REQUESTS=1 to dump every request for tuning.</summary>
    public static readonly bool Logging =
        Environment.GetEnvironmentVariable("TARKOV_OVERLAY_LOG_REQUESTS") == "1";

    private static readonly object LogGate = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovOverlay", "requests.log");

    // Hosts we must never break: the sites themselves and their asset CDNs.
    private static readonly string[] Allow =
    {
        "fandom.com",
        "wikia.nocookie.net",
        "tarkov.dev",
        "eft-ammo.com",
        "eft-ammo.b-cdn.net",
        "fonts.gstatic.com",
        "fonts.googleapis.com",
    };

    // Pure telemetry: beacons, analytics, session recording, audience data.
    // Nothing here is used as adblock bait, so blocking it is always safe, and
    // it accounts for a large share of the background chatter.
    private static readonly string[] BlockTrackers =
    {
        "google-analytics.com", "googletagmanager.com", "scorecardresearch.com",
        "quantserve.com", "quantcast.com", "moatads.com", "adsafeprotected.com",
        "imrworldwide.com", "demdex.net", "zeotap.com", "permutive.com",
        "crwdcntrl.net", "agkn.com", "id5-sync.com", "bidswitch.net",
        "sentry.io", "nr-data.net", "newrelic.com", "hotjar.com", "fullstory.com",
        "mouseflow.com", "clarity.ms", "bat.bing.com", "amplitude.com",
        "segment.io", "branch.io", "bounceexchange.com", "btmessage.com",
        "fastly-insights.com", "static.cloudflareinsights.com", "anonm.io",
        "beacon.wikia-services.com", "error-report.com", "ad.gt",
        "facebook.net", "connect.facebook.com",
    };

    // Ad serving proper. Blocking these is where the real memory saving is, but
    // it is also what anti-adblock scripts watch for, hence the empty-200 trick.
    private static readonly string[] BlockAdServing =
    {
        "doubleclick.net", "googlesyndication.com", "googletagservices.com",
        "adservice.google.com", "googleadservices.com", "imasdk.googleapis.com",
        "adnxs.com", "amazon-adsystem.com", "criteo.com", "criteo.net",
        "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com",
        "33across.com", "sharethrough.com", "indexww.com", "smartadserver.com",
        "adsrvr.org", "adform.net", "yieldmo.com", "sonobi.com", "districtm.io",
        "gumgum.com", "media.net", "1rx.io", "3lift.com", "serverbid.com",
        "sekindo.com", "themoneytizer.com", "adtelligent.com", "kargo.com",
        "teads.tv", "connatix.com", "aniview.com", "unrulymedia.com",
        "spotxchange.com", "springserve.com", "playwire.com", "intergient.com",
        "taboola.com", "outbrain.com", "revcontent.com", "mgid.com",
        "nitropay.com", "nitrocdn.com", "browsiprod.com", "smartclip.net",
        "ad-delivery.net", "confiant-integrations.net", "onetrust.com",
        "cookielaw.org",
    };

    // Anti-adblock circumvention CDNs. Over a single test session this one
    // service moved html-load.com -> veto.hencewafer.com -> content-loader.com,
    // so the durable signal is the "stg" hostname label, not the domain.
    // Strict mode only: cutting these off is what makes Fandom refuse to render.
    private static readonly string[] BlockCircumvention =
    {
        "html-load.com", "hencewafer.com", "content-loader.com",
    };

    /// <summary>Telemetry on the sites' own domains. Fire-and-forget, safe to drop.</summary>
    private static readonly string[] TelemetryPaths =
    {
        "/adengine/meter/", "/adeng/api/adengine/meter",
        "__track/special/trackingevent", "__track/special/pageload",
    };

    /// <summary>First-party ad plumbing. Strict only: pages may await these scripts.</summary>
    private static readonly string[] StrictFirstPartyPaths =
    {
        "prebid", "gpt/pubads", "/adproduct", "slot-tracker",
    };

    /// <summary>
    /// Extra hosts from %APPDATA%\TarkovOverlay\blocklist.txt, one per line, so
    /// a newly appeared tracker can be shut out without rebuilding the app.
    /// </summary>
    private static readonly string[] UserBlock = LoadUserBlock();

    private static string[] LoadUserBlock()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TarkovOverlay", "blocklist.txt");
            if (!File.Exists(path)) return Array.Empty<string>();

            return File.ReadAllLines(path)
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>True when <paramref name="label"/> is a whole dot-separated label of the host.</summary>
    private static bool HasLabel(string host, string label) =>
        host.StartsWith(label + ".", StringComparison.Ordinal) ||
        host.Contains("." + label + ".", StringComparison.Ordinal);

    private static bool Matches(string host, string[] domains)
    {
        foreach (var d in domains)
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal))
                return true;
        return false;
    }

    public static bool ShouldBlock(string url, bool strict)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        var host = uri.Host.ToLowerInvariant();
        var path = (uri.AbsolutePath + uri.Query).ToLowerInvariant();

        // Trusted host: drop only its telemetry, never its page code.
        if (Matches(host, Allow))
        {
            foreach (var f in TelemetryPaths)
                if (path.Contains(f, StringComparison.Ordinal)) return true;

            if (strict)
                foreach (var f in StrictFirstPartyPaths)
                    if (path.Contains(f, StringComparison.Ordinal)) return true;

            return false;
        }

        if (Matches(host, UserBlock)) return true;
        if (Matches(host, BlockTrackers)) return true;
        if (Matches(host, BlockAdServing)) return true;

        if (strict)
        {
            if (Matches(host, BlockCircumvention)) return true;
            if (HasLabel(host, "stg")) return true;
        }

        // Unknown third party: allowed, but never its ad-shaped subdomains.
        return HasLabel(host, "ads")
            || HasLabel(host, "ad")
            || HasLabel(host, "adserver")
            || HasLabel(host, "adservice");
    }

    // A 1x1 transparent GIF, so a blocked image decodes cleanly instead of
    // leaving a broken-image box in the layout.
    private static readonly byte[] PixelGif = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    /// <summary>
    /// Body and content type for a blocked request: empty but well-formed, so
    /// the page sees a successful response rather than a blocking signal.
    /// </summary>
    public static (Stream Body, string ContentType) EmptyResponseFor(string url)
    {
        var path = url.Split('?')[0].ToLowerInvariant();

        if (path.EndsWith(".js", StringComparison.Ordinal))
            return (new MemoryStream(), "application/javascript");
        if (path.EndsWith(".css", StringComparison.Ordinal))
            return (new MemoryStream(), "text/css");
        if (path.EndsWith(".json", StringComparison.Ordinal))
            return (new MemoryStream("{}"u8.ToArray()), "application/json");
        if (path.EndsWith(".gif", StringComparison.Ordinal)
            || path.EndsWith(".png", StringComparison.Ordinal)
            || path.EndsWith(".jpg", StringComparison.Ordinal)
            || path.EndsWith(".jpeg", StringComparison.Ordinal)
            || path.EndsWith(".webp", StringComparison.Ordinal))
            return (new MemoryStream(PixelGif), "image/gif");

        return (new MemoryStream(), "text/plain");
    }

    public static void Log(string url, bool blocked)
    {
        if (!Logging) return;
        try
        {
            lock (LogGate)
                File.AppendAllText(LogPath, $"{(blocked ? "BLOCK" : "allow")}\t{url}\n");
        }
        catch { }
    }
}
