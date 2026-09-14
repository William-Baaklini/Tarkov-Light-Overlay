using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TarkovOverlay;

/// <summary>
/// Hosts a WebView2, created only on first use and destroyed again when the
/// overlay is hidden. That teardown is what keeps the idle footprint near zero
/// while you are actually playing.
/// </summary>
public sealed class WebTab : Panel
{
    private static CoreWebView2Environment? _sharedEnv;
    private static Task<CoreWebView2Environment>? _envTask;

    // Browser flags are fixed when the shared environment is first created,
    // so this is captured before any tab loads.
    private static bool _lowGpu = true;

    /// <summary>Must be set before the first tab loads; changing it needs a restart.</summary>
    public static bool LowGpuMode { get => _lowGpu; set => _lowGpu = value; }


    // Collapses the empty boxes left behind once the ad requests are blocked,
    // plus the site chrome that just wastes room in a small overlay window.
    private const string CosmeticCss = """
        /* Fandom ad slots */
        #top_leaderboard, #top_boxad, #bottom_leaderboard, #incontent_boxad_1,
        #incontent_player, #WikiaAdInContentPlaceHolder, #featured-video__player-container,
        .top-ads-container, .bottom-ads-container, .render-wiki-recirculation-rail,
        .page__right-rail, #mixed-content-footer, #WikiaBar, .wds-global-footer,
        /* generic ad containers across any of the sites */
        [id^='gpt-'], [id*='google_ads'], [class*='ad-slot'], [class*='ad-container'],
        [class*='adsbygoogle'], ins.adsbygoogle, [data-ad-slot], [id^='div-gpt-ad'],
        .gpt-ad, .ad-slot-placeholder, .ad-unit, .adsbox, .advertisement,
        /* consent + nag overlays */
        #onetrust-consent-sdk, .onetrust-pc-dark-filter, .qc-cmp2-container,
        .fandom-sticky-header, .notifications-placeholder
        { display: none !important; }

        /* Reclaim the width the hidden rails were holding */
        .main-container, .resizable-container { margin-left: 0 !important; width: 100% !important; }
        body { overflow-x: hidden !important; }
        """;

    // Every site here leads with a search box, so put the caret in it as soon
    // as the page settles. SPAs render late, hence the short polling loop.
    private const string FocusSearchScript = """
        (function () {
          var selectors = [
            'input[type=search]',
            'input[name=q]', 'input[name=query]', 'input[name=search]',
            '#searchInput', '#search', '.search-input', '.searchInput',
            'input[placeholder*="earch" i]', 'input[aria-label*="earch" i]'
          ];
          function pick() {
            for (var i = 0; i < selectors.length; i++) {
              var list = document.querySelectorAll(selectors[i]);
              for (var j = 0; j < list.length; j++) {
                var el = list[j];
                if (el.disabled || el.readOnly) continue;
                var r = el.getBoundingClientRect();
                if (r.width > 40 && r.height > 8) return el;
              }
            }
            return null;
          }
          var tries = 0;
          var timer = setInterval(function () {
            tries++;
            var a = document.activeElement;
            if (a && (a.tagName === 'INPUT' || a.tagName === 'TEXTAREA')) {
              clearInterval(timer);   // the user already started typing somewhere
              return;
            }
            var el = pick();
            if (el) {
              // eft-ammo puts its search well below the fold, so bring the box
              // into view when it is off screen. Sites with a search box in the
              // header are already visible and do not move.
              var r = el.getBoundingClientRect();
              var h = window.innerHeight || document.documentElement.clientHeight;
              if (r.top < 0 || r.bottom > h) {
                el.scrollIntoView({ block: 'center' });
              }
              el.focus({ preventScroll: true });
              clearInterval(timer);
            }
            if (tries > 25) clearInterval(timer);
          }, 200);
        })();
        """;

    private readonly Config _cfg;
    private readonly Panel _navBar;
    private readonly TextBox _address;
    private WebView2? _web;
    private bool _initialising;

    /// <summary>Focus the search box once per browser lifetime, not on every click-through.</summary>
    private bool _focusSearchPending = true;

    public string HomeUrl { get; }

    /// <summary>Escape inside the page: the browser eats the key, so the page reports it.</summary>
    public event Action? EscapePressed;

    /// <summary>Where the user was when we last tore the browser down.</summary>
    public string LastUrl { get; private set; }

    public WebTab(Config cfg, string homeUrl)
    {
        _cfg = cfg;
        HomeUrl = homeUrl;
        LastUrl = homeUrl;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;

        _address = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            Font = Theme.UiFont,
        };
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            Navigate(_address.Text.Trim());
        };

        _navBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Theme.Chrome, Padding = new Padding(4) };
        var strip = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Chrome,
            Padding = new Padding(4, 2, 2, 2),
        };
        strip.Controls.Add(_address);

        _navBar.Controls.Add(strip);
        foreach (var (glyph, tip, action) in new (string, string, Action)[]
                 {
                     ("←", "Back", () => _web?.CoreWebView2?.GoBack()),
                     ("→", "Forward", () => _web?.CoreWebView2?.GoForward()),
                     ("↻", "Reload", () => _web?.CoreWebView2?.Reload()),
                     ("⌂", "Home", () => Navigate(HomeUrl)),
                 })
        {
            var b = Theme.FlatButton(glyph, 30);
            b.Dock = DockStyle.Left;
            var captured = action;
            b.Click += (_, _) => captured();
            new ToolTip().SetToolTip(b, tip);
            _navBar.Controls.Add(b);
            b.BringToFront();
        }

        Controls.Add(_navBar);
    }

    private static Task<CoreWebView2Environment> EnvAsync()
    {
        if (_sharedEnv is not null) return Task.FromResult(_sharedEnv);
        return _envTask ??= CreateEnvAsync();
    }

    private static async Task<CoreWebView2Environment> CreateEnvAsync()
    {
        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovOverlay", "WebView2");
        Directory.CreateDirectory(dataFolder);

        // Collapse everything into as few processes as WebView2 allows: site
        // isolation off, one renderer shared by both tabs, a capped JS heap.
        // The on-disk cache is deliberately kept so reloads after unhiding are quick.
        var flags = new List<string>
        {
            "--process-per-site",
            "--disable-site-isolation-trials",
            "--disable-background-networking",
            "--disable-backgrounding-occluded-windows",
            "--disable-gpu-shader-disk-cache",
            "--no-pings",
            // A ceiling, not a diet: tarkov.dev and Fandom are real SPAs and
            // will hard-fail with "not enough memory" if this is set too low.
            "--js-flags=--max-old-space-size=512",
            "--disable-features=Translate,OptimizationHints,MediaRouter,AutofillServerCommunication," +
            "CalculateNativeWinOcclusion,IsolateOrigins,site-per-process,BackForwardCache",
        };

        // Software rendering costs little on a text page, shrinks the GPU helper
        // process to a stub, and keeps the overlay off the GPU the game is using.
        if (_lowGpu) flags.Add("--disable-gpu");

        var opts = new CoreWebView2EnvironmentOptions(string.Join(' ', flags)) { Language = "en-US" };
        _sharedEnv = await CoreWebView2Environment.CreateAsync(null, dataFolder, opts);
        return _sharedEnv;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_web is not null || _initialising) return;
        _initialising = true;
        try
        {
            var env = await EnvAsync();

            var web = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Theme.Background,
            };
            Controls.Add(web);
            // WinForms docks in reverse z-order: the Fill control has to be at
            // index 0 so the Top-docked nav bar reserves its strip first.
            // Bringing the nav bar forward here would overlap the page's header.
            web.BringToFront();

            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            try
            {
                core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
            catch { /* older runtimes simply lack these */ }

            if (_cfg.BlockAds || RequestFilter.Logging)
            {
                // One catch-all filter and a host test in managed code: far easier
                // to reason about than a pile of WebView2 wildcard patterns, and
                // it lets the same hook log traffic when tuning the list.
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var url = e.Request.Uri;
                    bool blocked = _cfg.BlockAds
                        && RequestFilter.ShouldBlock(url, _cfg.BlockAdsStrict);
                    RequestFilter.Log(url, blocked);
                    if (!blocked) return;

                    // 200 with an empty body, not 403: a failed request is the
                    // signal anti-adblock scripts look for.
                    var (body, contentType) = RequestFilter.EmptyResponseFor(url);
                    e.Response = core.Environment.CreateWebResourceResponse(
                        body, 200, "OK",
                        string.Join("\r\n",
                        "Content-Type: " + contentType,
                        "Access-Control-Allow-Origin: *"));
                };

                // Kill ad iframes at navigation time: cheaper and more reliable
                // than filtering each resource they would go on to request.
                core.FrameNavigationStarting += (_, fe) =>
                {
                    if (!_cfg.BlockAds) return;
                    if (!RequestFilter.ShouldBlock(fe.Uri, _cfg.BlockAdsStrict)) return;
                    RequestFilter.Log("[frame] " + fe.Uri, blocked: true);
                    fe.Cancel = true;
                };
            }

            if (_cfg.BlockAds)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    $$"""
                      (function () {
                        var s = document.createElement('style');
                        s.textContent = {{System.Text.Json.JsonSerializer.Serialize(CosmeticCss)}};
                        (document.head || document.documentElement).appendChild(s);
                      })();
                      """);
            }

            // Keep every link inside the overlay instead of spawning browser windows.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
            };

            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                """
                document.addEventListener('keydown', function (e) {
                  if (e.key === 'Escape') { window.chrome.webview.postMessage('escape'); }
                }, true);
                """);
            core.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg == "escape") EscapePressed?.Invoke();
                else if (msg is not null && msg.StartsWith("dom:", StringComparison.Ordinal))
                    RequestFilter.Log(msg, blocked: false);
            };

            if (RequestFilter.Logging)
            {
                // Dump the page's structural containers so the cosmetic CSS can
                // be aimed at whatever markup Fandom is shipping this month.
                core.NavigationCompleted += async (_, _) =>
                {
                    try
                    {
                        await core.ExecuteScriptAsync(
                            """
                            (function () {
                              var out = [];
                              document.querySelectorAll('body *').forEach(function (el) {
                                var r = el.getBoundingClientRect();
                                if (r.width < 40 || r.height < 40) return;
                                if (el.children.length > 40) return;
                                out.push(el.tagName + '#' + (el.id || '') + '.' +
                                         (typeof el.className === 'string' ? el.className : '') +
                                         ' [' + Math.round(r.x) + ',' + Math.round(r.y) + ' ' +
                                         Math.round(r.width) + 'x' + Math.round(r.height) + ']');
                              });
                              window.chrome.webview.postMessage('dom:' + out.slice(0, 120).join('\n'));
                            })();
                            """);
                    }
                    catch { }
                };
            }

            core.SourceChanged += (_, _) => SyncAddress(core.Source);
            core.NavigationCompleted += async (_, e) =>
            {
                SyncAddress(core.Source);
                if (!_focusSearchPending || !e.IsSuccess) return;
                _focusSearchPending = false;
                if (Visible) web.Focus();
                try { await core.ExecuteScriptAsync(FocusSearchScript); }
                catch { /* the page may have navigated away already */ }
            };

            _web = web;
            SyncAddress(LastUrl);
            core.Navigate(LastUrl);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _initialising = false;
        }
    }

    private void SyncAddress(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;
        LastUrl = url;
        if (!_address.Focused) _address.Text = url;
    }

    public void Navigate(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var url = input;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Looks like a bare domain? Treat it as one. Otherwise search the wiki.
            url = input.Contains(' ') || !input.Contains('.')
                ? "https://escapefromtarkov.fandom.com/wiki/Special:Search?query=" + Uri.EscapeDataString(input)
                : "https://" + input;
        }

        LastUrl = url;
        if (_web?.CoreWebView2 is { } core) core.Navigate(url);
        else _ = EnsureLoadedAsync();
    }

    /// <summary>
    /// Freezes the renderer for a tab you switched away from: its timers stop
    /// and Chromium releases a chunk of its heap, but page state survives.
    /// </summary>
    public async Task SuspendAsync()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        // WebView2 only allows suspending a hidden view, which is exactly when
        // we call this. Failures here are never worth surfacing.
        try { if (!core.IsSuspended) await core.TrySuspendAsync(); }
        catch { }
    }

    public void Resume()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        try { if (core.IsSuspended) core.Resume(); }
        catch { /* ditto */ }
    }

    /// <summary>Destroys the browser entirely, returning its memory to the system.</summary>
    public void Unload()
    {
        if (_web is null) return;
        var web = _web;
        _web = null;
        _focusSearchPending = true;
        Controls.Remove(web);
        web.Dispose();
    }

    public void FocusContent()
    {
        if (_web is not null) _web.Focus();
        else _address.Focus();
    }

    public void FocusAddress()
    {
        _address.Focus();
        _address.SelectAll();
    }

    private void ShowError(Exception ex)
    {
        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Theme.Background,
            Font = Theme.UiFont,
            Padding = new Padding(16),
            Text = "Could not start WebView2.\r\n\r\n" + ex.Message +
                   "\r\n\r\nInstall the WebView2 Runtime from:\r\n" +
                   "https://developer.microsoft.com/microsoft-edge/webview2/",
        };
        Controls.Add(lbl);
        lbl.BringToFront();
    }
}
