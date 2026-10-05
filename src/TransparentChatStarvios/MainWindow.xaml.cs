using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TransparentChatStarvios;

public partial class MainWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly StarviosClient _client = new();
    CancellationTokenSource? _cts;

    StreamInfo? _stream;
    string? _lastCreatedAt;
    bool _realtimeUp;
    readonly Dictionary<string, FrameworkElement> _byId = [];
    Dictionary<string, Emote>? _emotes;
    readonly Dictionary<string, BitmapImage> _emoteImages = [];
    readonly SemaphoreSlim _fetchLock = new(1, 1);

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _autoScroll = true;
    bool _ready;

    static readonly Regex EmoteRx = new(@":([A-Za-z0-9_\-]{1,40}):", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    FontFamily _font = new("Segoe UI, Segoe UI Emoji, Segoe UI Symbol");
    static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)));
    static readonly Brush SystemBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD)));
    static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    static readonly Brush HighlightBg = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xA7, 0x8B, 0xFA)));
    static readonly string[] NamePalette = ["#FF8A5B", "#5BC0FF", "#7CFC9A", "#FFD25B", "#FF6FB5", "#B38BFF", "#5BFFE1", "#FF5B5B"];

    public MainWindow()
    {
        InitializeComponent();
        // Para jugar: el overlay nunca compite con el juego por CPU.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        Left = _s.Left; Top = _s.Top; Width = _s.Width; Height = _s.Height;
        EnsureOnScreen();

        _debounce.Tick += async (_, _) => { _debounce.Stop(); await FetchNewAsync(); };
        _hideTimer.Tick += (_, _) => HideOld();

        SourceInitialized += (_, _) =>
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            // Ventana casi estática y con transparencia por píxel: renderizar por CPU evita que
            // WPF lea de vuelta la GPU en cada fotograma y no roba GPU al juego.
            src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            src.AddHook(WndProc);
            RegisterHotKey(src.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F12);
            ApplySettings();
            SetLocked(_s.Locked);
        };
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => _ready = true, DispatcherPriority.ApplicationIdle);
        Activated += (_, _) =>
        {
            // Con el chat bloqueado no recibe clics; si se activa es porque lo has pulsado en la barra de tareas.
            if (_ready && _s.Locked) SetLocked(false);
        };
        StateChanged += (_, _) =>
        {
            // Bloqueado y en primer plano, un clic en la barra de tareas lo minimizaría: lo devolvemos y desbloqueamos.
            if (_s.Locked && WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; SetLocked(false); Activate(); }
        };
        Closing += (_, _) => SaveWindow();
        Closed += (_, _) => { _cts?.Cancel(); _client.Dispose(); };
        Loaded += (_, _) => Start();
    }

    // ───────────────────────── Conexión ─────────────────────────

    void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Messages.Children.Clear();
        _byId.Clear();
        _lastCreatedAt = null;
        _stream = null;

        var channel = Settings.NormalizeChannel(_s.Channel);
        if (channel.Length == 0)
        {
            SetStatus("Pulsa ⚙ y escribe tu canal", Brushes.Gray);
            AddInfo("Pulsa ⚙ arriba y escribe tu canal de Starvios (por ejemplo: natu).");
            return;
        }
        _ = RunAsync(channel, _cts.Token);
    }

    async Task RunAsync(string channel, CancellationToken ct)
    {
        SetStatus($"@{channel} · conectando…", Brushes.Goldenrod);
        var lastResolve = DateTime.MinValue;
        Task? listener = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_stream?.Id == null || DateTime.UtcNow - lastResolve > TimeSpan.FromMinutes(2))
                {
                    var info = await _client.ResolveAsync(channel, ct);
                    lastResolve = DateTime.UtcNow;
                    if (info == null) { SetStatus($"@{channel} · canal no encontrado", Brushes.IndianRed); }
                    else if (info.Id == null) { SetStatus($"@{channel} · este canal aún no tiene chat", Brushes.Gray); }
                    else
                    {
                        bool changed = _stream?.Id != info.Id || _stream?.ChatClearedAt != info.ChatClearedAt;
                        _stream = info;
                        if (changed)
                        {
                            Messages.Children.Clear(); _byId.Clear(); _lastCreatedAt = null;
                            _emotes ??= await _client.GetEmotesAsync(ct);
                            await FetchNewAsync();
                        }
                        if (listener == null || listener.IsCompleted)
                            listener = _client.ListenAsync(info.Id, OnRealtime, up => Dispatcher.BeginInvoke(() => { _realtimeUp = up; UpdateStatus(channel); }), ct);
                        UpdateStatus(channel);
                    }
                }
                else
                {
                    // Red de seguridad por si se pierde algún aviso en tiempo real. Es una consulta mínima.
                    await FetchNewAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch
            {
                SetStatus($"@{channel} · sin conexión, reintentando…", Brushes.IndianRed);
            }
            try { await Task.Delay(_realtimeUp ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(6), ct); }
            catch { return; }
        }
    }

    void UpdateStatus(string channel)
    {
        if (_stream == null) return;
        var live = _stream.IsLive ? "en directo" : "offline";
        SetStatus($"@{channel} · {live}{(_realtimeUp ? "" : " · sondeo")}",
            _stream.IsLive ? Brushes.LimeGreen : Brushes.Gray);
    }

    void OnRealtime(string type, ChatMessage? rec)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (rec != null && (type == "UPDATE" || type == "DELETE") && (rec.IsDeleted || type == "DELETE"))
            {
                RemoveMessage(rec.Id);
                return;
            }
            // El aviso en tiempo real no trae el perfil (nombre, color…), así que pedimos los nuevos agrupados.
            _debounce.Stop(); _debounce.Start();
        });
    }

    async Task FetchNewAsync()
    {
        var stream = _stream;
        var ct = _cts?.Token ?? CancellationToken.None;
        if (stream?.Id == null) return;
        if (!await _fetchLock.WaitAsync(0)) { _debounce.Stop(); _debounce.Start(); return; }
        try
        {
            // Igual que la web: si el directo acabó hace más de 15 min, no se muestra el chat viejo.
            string? notBefore = stream.ChatClearedAt;
            var endRef = stream.EndedAt ?? stream.StartedAt;
            if (!stream.IsLive && endRef != null && DateTimeOffset.TryParse(endRef, out var end))
            {
                var cutoff = end.AddMinutes(15);
                if (DateTimeOffset.UtcNow > cutoff)
                {
                    var c = cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    if (notBefore == null || string.CompareOrdinal(c, notBefore) > 0) notBefore = c;
                }
            }

            var list = await _client.GetMessagesAsync(stream.Id, notBefore, _lastCreatedAt, Math.Clamp(_s.MaxMessages, 10, 300), ct);
            if (list.Count == 0) return;
            await LoadEmotesFor(list, ct);
            foreach (var m in list)
            {
                _lastCreatedAt = m.CreatedAt;
                if (!m.IsDeleted && !_byId.ContainsKey(m.Id)) AddMessage(m);
            }
            TrimMessages();
            ScheduleTrim();
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { _fetchLock.Release(); }
    }

    async Task LoadEmotesFor(List<ChatMessage> list, CancellationToken ct)
    {
        if (!_s.ShowEmotes || _emotes == null) return;
        var needed = new HashSet<string>();
        foreach (var m in list)
            foreach (Match x in EmoteRx.Matches(m.Body))
                if (_emotes.TryGetValue(x.Groups[1].Value, out var e) && !_emoteImages.ContainsKey(e.StoragePath))
                    needed.Add(e.StoragePath);
        if (needed.Count == 0) return;
        try
        {
            var files = await _client.EnsureImagesAsync(needed, ct);
            foreach (var (path, file) in files)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelHeight = 64; // no guardamos en RAM la imagen original a tamaño completo
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    bmp.Freeze();
                    _emoteImages[path] = bmp;
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ───────────────────────── Pintado ─────────────────────────

    void AddMessage(ChatMessage m)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = _s.FontSize,
            FontFamily = _font,
            Foreground = BodyBrush,
        };
        var kind = m.Kind ?? "chat";
        bool special = kind is "sub" or "gift_sub" or "raid" or "starfall" || m.Coins > 0;

        if (kind == "system")
        {
            tb.Inlines.Add(new Run((m.Profile?.DisplayName ?? "Starvios") + ": ") { FontWeight = FontWeights.SemiBold, Foreground = AccentBrush });
            AppendBody(tb, m.Body, SystemBrush, italic: true);
        }
        else if (kind is "sub" or "gift_sub" or "raid" or "starfall")
        {
            var icon = kind switch { "raid" => "⚑ ", "starfall" => "✦ ", _ => "★ " };
            tb.Inlines.Add(new Run(icon) { Foreground = AccentBrush });
            AppendBody(tb, m.Body, BodyBrush, italic: false, bold: true);
        }
        else
        {
            if (_s.ShowBadges && m.Badges != null)
                foreach (var b in m.Badges) AddBadge(tb, b);
            var p = m.Profile;
            var name = p?.DisplayName ?? p?.Username ?? "—";
            tb.Inlines.Add(new Run(name) { FontWeight = _s.BoldNames ? FontWeights.Bold : FontWeights.SemiBold, Foreground = NameBrush(p) });
            if (p?.IsVerified == true) tb.Inlines.Add(new Run(" ✔") { Foreground = AccentBrush, FontSize = _s.FontSize * 0.8 });
            if (m.Coins > 0) tb.Inlines.Add(new Run($"  ★ {m.Coins} Starvies") { Foreground = AccentBrush, FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new Run(": ") { Foreground = BodyBrush });
            AppendBody(tb, m.Body, BodyBrush, italic: false);
        }

        FrameworkElement el = tb;
        if (special)
            el = new Border
            {
                Child = tb,
                Background = HighlightBg,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(6, 3, 4, 3),
                Margin = new Thickness(0, 2, 0, 2),
            };
        else tb.Margin = new Thickness(0, 1, 0, 1);

        el.Tag = DateTime.UtcNow;
        _byId[m.Id] = el;
        el.Uid = m.Id;
        Messages.Children.Add(el);
    }

    void AppendBody(TextBlock tb, string body, Brush brush, bool italic, bool bold = false)
    {
        int pos = 0;
        if (_s.ShowEmotes && _emotes != null)
        {
            foreach (Match x in EmoteRx.Matches(body))
            {
                if (!_emotes.TryGetValue(x.Groups[1].Value, out var e) || !_emoteImages.TryGetValue(e.StoragePath, out var img))
                    continue;
                if (x.Index > pos) tb.Inlines.Add(MakeRun(body[pos..x.Index], brush, italic, bold));
                var h = Math.Round(_s.FontSize * 1.75);
                tb.Inlines.Add(new InlineUIContainer(new Image { Source = img, Height = h, Stretch = Stretch.Uniform, ToolTip = x.Value })
                { BaselineAlignment = BaselineAlignment.Center });
                pos = x.Index + x.Length;
            }
        }
        if (pos < body.Length) tb.Inlines.Add(MakeRun(body[pos..], brush, italic, bold));
    }

    static Run MakeRun(string text, Brush brush, bool italic, bool bold) => new(text)
    {
        Foreground = brush,
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    void AddBadge(TextBlock tb, string badge)
    {
        (string glyph, string color, string tip) = badge switch
        {
            "owner" => ("👑", "#F5C542", "Dueño del canal"),
            "staff" => ("⚡", "#A78BFA", "Equipo de Starvios"),
            "admin" => ("⚡", "#F87171", "Admin"),
            "moderator" => ("🛡", "#34D399", "Moderador"),
            "vip" => ("♦", "#F472B6", "VIP"),
            "member" => ("♥", "#60A5FA", "Suscriptor"),
            _ => ("", "", ""),
        };
        if (glyph.Length == 0) return;
        tb.Inlines.Add(new Run(glyph + " ") { Foreground = BrushFrom(color), ToolTip = tip });
    }

    static readonly Dictionary<string, Brush> BrushCache = [];
    static Brush BrushFrom(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var b)) return b;
        try { b = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex))); }
        catch { b = BodyBrush; }
        return BrushCache[hex] = b;
    }

    static Brush NameBrush(Profile? p)
    {
        if (!string.IsNullOrWhiteSpace(p?.ChatColor)) return BrushFrom(p!.ChatColor!);
        var key = p?.Username ?? "";
        int h = 0; foreach (var ch in key) h = h * 31 + ch;
        return BrushFrom(NamePalette[(h & 0x7fffffff) % NamePalette.Length]);
    }

    void AddInfo(string text) =>
        Messages.Children.Add(new TextBlock { Text = text, Foreground = Brushes.LightGray, FontSize = _s.FontSize, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyles.Italic });

    void RemoveMessage(string id)
    {
        if (_byId.Remove(id, out var el)) Messages.Children.Remove(el);
    }

    void TrimMessages()
    {
        int max = Math.Clamp(_s.MaxMessages, 10, 300);
        while (Messages.Children.Count > max)
        {
            if (Messages.Children[0] is FrameworkElement el && el.Uid.Length > 0) _byId.Remove(el.Uid);
            Messages.Children.RemoveAt(0);
        }
    }

    void HideOld()
    {
        if (_s.HideAfterSeconds <= 0) return;
        var limit = DateTime.UtcNow.AddSeconds(-_s.HideAfterSeconds);
        while (Messages.Children.Count > 0 && Messages.Children[0] is FrameworkElement el && el.Tag is DateTime t && t < limit)
        {
            if (el.Uid.Length > 0) _byId.Remove(el.Uid);
            Messages.Children.RemoveAt(0);
        }
    }

    void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        Holder.MinHeight = Scroller.ViewportHeight;
        if (e.ExtentHeightChange == 0)
            _autoScroll = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
        else if (_autoScroll)
            Scroller.ScrollToEnd();
    }

    // ───────────────────────── Ventana ─────────────────────────

    void ApplySettings()
    {
        // Si la fuente no tiene emojis o símbolos, Windows tira de Segoe UI Emoji/Symbol.
        _font = new FontFamily($"{_s.FontFamily}, Segoe UI Emoji, Segoe UI Symbol");
        var a = (byte)Math.Round(Math.Clamp(_s.BackgroundOpacity, 0, 1) * 255);
        // Con alfa 0 Windows deja pasar el ratón por el hueco; dejamos 1 para poder hacer scroll si está desbloqueado.
        ChatBg.Background = Freeze(new SolidColorBrush(Color.FromArgb(Math.Max(a, (byte)1), 0, 0, 0)));
        Messages.Effect = _s.TextShadow
            ? new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.95, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
            : null;
        if (_s.HideAfterSeconds > 0) _hideTimer.Start(); else _hideTimer.Stop();
    }

    void SetLocked(bool locked)
    {
        _s.Locked = locked;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = locked ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        Bar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        _s.Save();
    }

    void SetStatus(string text, Brush dot)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot;
        Title = "Chat Starvios — " + text;
    }

    void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } SaveWindow(); }
    }

    void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(true);
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var oldChannel = Settings.NormalizeChannel(_s.Channel);
        var oldEmotes = _s.ShowEmotes;
        var dlg = new SettingsWindow(_s) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _s.Save();
        ApplySettings();
        if (Settings.NormalizeChannel(_s.Channel) != oldChannel || oldEmotes != _s.ShowEmotes) Start();
        else { Messages.Children.Clear(); _byId.Clear(); _lastCreatedAt = null; _ = FetchNewAsync(); }
    }

    void SaveWindow()
    {
        if (WindowState != WindowState.Normal) return;
        _s.Left = Left; _s.Top = Top; _s.Width = Width; _s.Height = Height;
        _s.Save();
    }

    void EnsureOnScreen()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(Left, Top, Width, Height))) { Left = 60; Top = 120; }
    }

    // En reposo el overlay no necesita casi nada en RAM: devolvemos al sistema las páginas que no usa.
    // Si luego hacen falta, Windows las recupera solas. No afecta a la CPU.
    DispatcherTimer? _trim;
    void ScheduleTrim()
    {
        _trim ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _trim!.Stop();
            GC.Collect(2, GCCollectionMode.Optimized, false, true);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }, Dispatcher);
        _trim.Stop(); _trim.Start();
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ───────────────────────── Win32 ─────────────────────────

    const int HotkeyId = 0x5354;
    const int WM_HOTKEY = 0x0312;
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_F12 = 0x7B;
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            SetLocked(!_s.Locked);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint min, nint max);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
