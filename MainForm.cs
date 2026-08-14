using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NatanaelPOS;

public class MainForm : Form
{
    // ---- CONFIGURÁ ACÁ TU URL ----
    private const string SiteUrl = "https://evictorhugo4-bit.github.io/Natanael-POS/";

    // Dominio "falso" bajo el cual WebView2 sirve la copia local en modo offline.
    // Se usa esto (en vez de file://) para que el fetch() al Apps Script siga
    // funcionando igual que online, sin las restricciones de CORS de file://.
    private const string OfflineVirtualHost = "natanaelpos.offline";

    private static readonly string CacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NatanaelPOS", "cache");

    private static readonly string CacheFile = Path.Combine(CacheFolder, "index_cache.html");
    private static readonly string CacheStampFile = Path.Combine(CacheFolder, "last_updated.txt");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly WebView2 _webView = new();
    private readonly Panel _errorPanel = new();
    private readonly Label _errorLabel = new();
    private readonly Button _retryButton = new();
    private readonly Label _loadingLabel = new();
    private readonly Panel _offlineBanner = new();
    private readonly Label _offlineLabel = new();

    public MainForm()
    {
        Text = "Natanael POS";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1000, 650);
        Icon = SystemIcons.Application;

        Directory.CreateDirectory(CacheFolder);

        BuildLoadingUi();
        BuildErrorUi();
        BuildOfflineBanner();

        // Orden importante: los Dock=Top se agregan antes que el Dock=Fill,
        // así el webview ocupa el espacio restante debajo del banner.
        Controls.Add(_webView);
        Controls.Add(_errorPanel);
        Controls.Add(_loadingLabel);
        Controls.Add(_offlineBanner);

        _webView.Dock = DockStyle.Fill;
        _webView.Visible = false;

        Load += async (_, _) => await InitializeWebViewAsync();
    }

    private void BuildOfflineBanner()
    {
        _offlineBanner.Dock = DockStyle.Top;
        _offlineBanner.Height = 32;
        _offlineBanner.BackColor = Color.FromArgb(255, 193, 7); // ámbar
        _offlineBanner.Visible = false;

        _offlineLabel.Dock = DockStyle.Fill;
        _offlineLabel.TextAlign = ContentAlignment.MiddleCenter;
        _offlineLabel.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        _offlineLabel.ForeColor = Color.Black;
        _offlineLabel.Text = "SIN CONEXIÓN — mostrando la última versión guardada localmente";

        _offlineBanner.Controls.Add(_offlineLabel);
    }

    private void BuildLoadingUi()
    {
        _loadingLabel.Text = "Cargando Natanael POS...";
        _loadingLabel.Dock = DockStyle.Fill;
        _loadingLabel.TextAlign = ContentAlignment.MiddleCenter;
        _loadingLabel.Font = new Font("Segoe UI", 14F, FontStyle.Regular);
        _loadingLabel.BackColor = Color.White;
    }

    private void BuildErrorUi()
    {
        _errorPanel.Dock = DockStyle.Fill;
        _errorPanel.BackColor = Color.White;
        _errorPanel.Visible = false;

        _errorLabel.AutoSize = false;
        _errorLabel.TextAlign = ContentAlignment.MiddleCenter;
        _errorLabel.Font = new Font("Segoe UI", 12F, FontStyle.Regular);
        _errorLabel.Dock = DockStyle.Top;
        _errorLabel.Height = 120;
        _errorLabel.Text = "No se pudo conectar a Natanael POS.\nVerificá tu conexión a internet e intentá de nuevo.";

        _retryButton.Text = "Reintentar";
        _retryButton.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
        _retryButton.Size = new Size(160, 44);
        _retryButton.Anchor = AnchorStyles.None;
        _retryButton.Click += async (_, _) => await InitializeWebViewAsync();

        _errorPanel.Controls.Add(_retryButton);
        _errorPanel.Controls.Add(_errorLabel);
        _errorPanel.Resize += (_, _) => CenterRetryButton();
        CenterRetryButton();
    }

    private void CenterRetryButton()
    {
        _retryButton.Location = new Point(
            (_errorPanel.ClientSize.Width - _retryButton.Width) / 2,
            160);
    }

    private bool _coreReady;

    private async Task InitializeWebViewAsync()
    {
        ShowLoading();

        try
        {
            if (!_coreReady)
            {
                // Perfil persistente en %LocalAppData% para guardar sesión/caché entre aperturas
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NatanaelPOS", "WebView2");

                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                // Mapea la carpeta de caché local a un "dominio" https falso.
                // Así, si hay que abrir la copia offline, el fetch() al Apps Script
                // funciona igual que si estuviera online (evita las restricciones
                // de seguridad de file://).
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    OfflineVirtualHost, CacheFolder, CoreWebView2HostResourceAccessKind.Allow);

                _coreReady = true;
            }

            await TryLoadOnlineThenFallback();
        }
        catch (Exception ex)
        {
            ShowError($"No se pudo iniciar el navegador embebido.\n{ex.Message}");
        }
    }

    private async Task TryLoadOnlineThenFallback()
    {
        var online = await CheckInternetAsync();

        if (online)
        {
            void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnCompleted;

                if (e.IsSuccess)
                {
                    ShowWebView(offline: false);
                    _ = RefreshLocalCacheInBackground();
                }
                else
                {
                    LoadOfflineCacheOrError();
                }
            }

            _webView.CoreWebView2.NavigationCompleted += OnCompleted;

            // Cache-busting: fuerza a pedir el HTML más nuevo de GitHub Pages
            // en vez de una versión vieja servida desde caché del navegador.
            var freshUrl = SiteUrl + (SiteUrl.Contains('?') ? "&" : "?") + "v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _webView.CoreWebView2.Navigate(freshUrl);
        }
        else
        {
            LoadOfflineCacheOrError();
        }
    }

    /// <summary>Chequeo rápido de conectividad real contra el sitio (no solo "hay wifi").</summary>
    private async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var resp = await _http.GetAsync(SiteUrl, HttpCompletionOption.ResponseHeadersRead);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void LoadOfflineCacheOrError()
    {
        if (File.Exists(CacheFile))
        {
            var stamp = File.Exists(CacheStampFile) ? File.ReadAllText(CacheStampFile) : null;
            _offlineLabel.Text = string.IsNullOrWhiteSpace(stamp)
                ? "SIN CONEXIÓN — mostrando la última versión guardada localmente"
                : $"SIN CONEXIÓN — mostrando versión guardada el {stamp}";

            _webView.CoreWebView2.Navigate($"https://{OfflineVirtualHost}/index_cache.html");
            ShowWebView(offline: true);
        }
        else
        {
            ShowError("No se pudo conectar a Natanael POS y todavía no hay ninguna copia\n" +
                       "guardada localmente. Conectate a internet al menos una vez y volvé a intentar.");
        }
    }

    /// <summary>Descarga el HTML actual y lo guarda como respaldo para la próxima vez que no haya internet.</summary>
    private async Task RefreshLocalCacheInBackground()
    {
        try
        {
            var html = await _http.GetStringAsync(SiteUrl);
            await File.WriteAllTextAsync(CacheFile, html);
            await File.WriteAllTextAsync(CacheStampFile, DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        }
        catch
        {
            // Si falla el guardado de caché no rompemos nada: seguimos con la sesión online activa.
        }
    }

    private void ShowLoading()
    {
        _loadingLabel.Visible = true;
        _errorPanel.Visible = false;
        _offlineBanner.Visible = false;
        _webView.Visible = false;
    }

    private void ShowWebView(bool offline)
    {
        _loadingLabel.Visible = false;
        _errorPanel.Visible = false;
        _offlineBanner.Visible = offline;
        _webView.Visible = true;
    }

    private void ShowError(string message)
    {
        _errorLabel.Text = message;
        _loadingLabel.Visible = false;
        _offlineBanner.Visible = false;
        _webView.Visible = false;
        _errorPanel.Visible = true;
    }
}
