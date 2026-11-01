using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CSNZ.Launcher;

public enum GameKind
{
    Csnz,
    Cso2,
}

public partial class MainWindow : Window
{
    private const string CsnzGameId = "csnz";
    private const string Cso2GameId = "cso2";

    private const string CompatLayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string HighDpiFlag = "HIGHDPIAWARE";

    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "launcher.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Color OnlineColor = Color.FromRgb(0x3E, 0xCF, 0x8E);
    private static readonly Color OfflineColor = Color.FromRgb(0xFF, 0x5C, 0x4D);

    private static readonly Brush NormalStatusBrush = new SolidColorBrush(Color.FromRgb(0x87, 0x92, 0x9D));
    private static readonly Brush ErrorStatusBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x7A));
    private static readonly Brush OnlineBrush = new SolidColorBrush(OnlineColor);
    private static readonly Brush InactiveTabBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0x9F));
    private static readonly Brush ActiveTabBrush = Brushes.White;
    private static readonly Brush TabDotMissingBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x8B, 0x95, 0x9F));
    private static readonly Brush TabBackgroundActive = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0x6A, 0x00));
    private static readonly Brush BadgeBorderActive = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x00));
    private static readonly Brush BadgeBorderIdle = new SolidColorBrush(Color.FromRgb(0x33, 0x3D, 0x47));
    private static readonly Brush BadgeTextActive = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D));
    private static readonly Brush BadgeTextIdle = new SolidColorBrush(Color.FromRgb(0x7B, 0x87, 0x94));
    private static readonly Brush TabSubActive = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0x9F));
    private static readonly Brush TabSubIdle = new SolidColorBrush(Color.FromRgb(0x5D, 0x68, 0x73));

    private readonly DispatcherTimer _pingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private LauncherConfig _config = new();
    private GameKind _activeGame = GameKind.Csnz;
    private bool _passwordVisible;
    private bool _pinging;
    private bool _applying;

    public MainWindow()
    {
        InitializeComponent();
        _pingTimer.Tick += (_, _) => _ = ProbeAsync(false);
        Loaded += OnLoaded;
    }

    // ================= 启动流程 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        bool configOk = LoadConfig();
        EnsureProfiles();
        DetectGames();
        ApplyActiveProfile();

        DpiScaleText.Text = CurrentDpiText();

        _pingTimer.Start();
        _ = ProbeAsync(false);

        IpBox.Focus();
        IpBox.CaretIndex = IpBox.Text.Length;

        if (!configOk)
        {
            SetStatus("launcher.json 读取失败，已使用默认配置。", true);
        }
    }

    private bool LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _config = new LauncherConfig();
                return true;
            }

            var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath));
            if (config is null)
            {
                _config = new LauncherConfig();
                return false;
            }

            if (config.Csnz is null && config.LegacyIp is not null)
            {
                config.Csnz = new CsnzProfile
                {
                    Ip = config.LegacyIp,
                    Port = config.LegacyPort ?? 30002,
                    Login = config.LegacyLogin ?? string.Empty,
                    Password = config.LegacyPassword ?? string.Empty,
                    DisableAuthUi = config.LegacyDisableAuthUi ?? true,
                    FixDpi = config.LegacyFixDpi ?? true,
                };
            }

            _config = config;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _config = new LauncherConfig();
            return false;
        }
    }

    private void EnsureProfiles()
    {
        bool highDpi = IsHighDpiDisplay();

        _config.Csnz ??= new CsnzProfile { FixDpi = highDpi };
        _config.Cso2 ??= new Cso2Profile { FixDpi = highDpi };

        Normalize(_config.Csnz, 30002, @"Bin\CSOLauncher.exe");
        Normalize(_config.Cso2, 30001, @"Bin\launcher.exe");

        _activeGame = string.Equals(_config.ActiveGame, Cso2GameId, StringComparison.OrdinalIgnoreCase)
            ? GameKind.Cso2
            : GameKind.Csnz;
    }

    private static void Normalize(ProfileBase profile, int defaultPort, string defaultFeature)
    {
        if (profile.Port is < 1 or > 65535)
        {
            profile.Port = defaultPort;
        }

        if (profile.FeatureFiles.Count == 0)
        {
            profile.FeatureFiles.Add(defaultFeature);
        }

        profile.GameDir = NormalizeDir(profile.GameDir);
        profile.Ip = string.IsNullOrWhiteSpace(profile.Ip) ? "127.0.0.1" : profile.Ip.Trim();
        profile.Login ??= string.Empty;
        profile.Password ??= string.Empty;
    }

    private void DetectGames()
    {
        var csnz = _config.Csnz!;
        var cso2 = _config.Cso2!;

        string? csnzDir = DetectGameDir(csnz.FeatureFiles);
        string? cso2Dir = DetectGameDir(cso2.FeatureFiles);

        if (!HasFeature(csnz.GameDir, csnz.FeatureFiles) && csnzDir is not null)
        {
            csnz.GameDir = csnzDir;
        }

        if (!HasFeature(cso2.GameDir, cso2.FeatureFiles) && cso2Dir is not null)
        {
            cso2.GameDir = cso2Dir;
        }

        if (!IsProfileUsable(CurrentProfile()) && IsProfileUsable(OtherProfile()))
        {
            _activeGame = _activeGame == GameKind.Csnz ? GameKind.Cso2 : GameKind.Csnz;
        }

        _config.ActiveGame = _activeGame == GameKind.Csnz ? CsnzGameId : Cso2GameId;
    }

    private static string? DetectGameDir(IReadOnlyList<string> featureFiles)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 3; depth++, dir = dir.Parent)
        {
            if (HasFeature(dir.FullName, featureFiles))
            {
                return NormalizeDir(dir.FullName);
            }
        }

        return null;
    }

    private static string NormalizeDir(string dir)
    {
        string trimmed = (dir ?? string.Empty).Trim();
        return trimmed.Length > 3 ? trimmed.TrimEnd('\\', '/') : trimmed;
    }

    private static bool HasFeature(string? dir, IReadOnlyList<string> files)
    {
        if (string.IsNullOrWhiteSpace(dir) || files.Count == 0)
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(dir))
            {
                return false;
            }

            foreach (string file in files)
            {
                if (!string.IsNullOrWhiteSpace(file) && File.Exists(Path.Combine(dir, file)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return false;
    }

    // ================= 面板 =================

    private ProfileBase CurrentProfile()
    {
        return _activeGame == GameKind.Csnz ? _config.Csnz! : _config.Cso2!;
    }

    private ProfileBase OtherProfile()
    {
        return _activeGame == GameKind.Csnz ? _config.Cso2! : _config.Csnz!;
    }

    private bool IsProfileUsable(ProfileBase profile)
    {
        return HasFeature(profile.GameDir, profile.FeatureFiles);
    }

    private void ApplyActiveProfile()
    {
        _applying = true;
        var profile = CurrentProfile();
        DirBox.Text = profile.GameDir;
        IpBox.Text = profile.Ip;
        PortBox.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
        LoginBox.Text = profile.Login;
        PasswordBox.Password = profile.Password;
        PasswordText.Text = profile.Password;
        OptionToggle.IsChecked = profile.Option;
        FixDpiCheckBox.IsChecked = profile.FixDpi;
        _applying = false;

        bool cso2 = _activeGame == GameKind.Cso2;
        CsnzBrand.Visibility = cso2 ? Visibility.Collapsed : Visibility.Visible;
        Cso2Brand.Visibility = cso2 ? Visibility.Visible : Visibility.Collapsed;
        CsnzPoster.Visibility = cso2 ? Visibility.Collapsed : Visibility.Visible;
        Cso2Poster.Visibility = cso2 ? Visibility.Visible : Visibility.Collapsed;
        LaunchText.Text = cso2 ? "启动 CSO2" : "启动游戏";
        Title = cso2 ? "CSO2 启动器" : "CSNZ 启动器";

        UpdateOptionLabel();
        UpdateTabVisuals();
        UpdateFeatureHint();
        UpdateTargetReadout();
        SetLink("待检测", NormalStatusBrush);
        SetStatusReady();
    }

    private void CommitProfile()
    {
        var profile = CurrentProfile();
        profile.GameDir = NormalizeDir(DirBox.Text);
        profile.Ip = IpBox.Text.Trim();
        if (int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
        {
            profile.Port = port;
        }

        profile.Login = LoginBox.Text;
        profile.Password = _passwordVisible ? PasswordText.Text : PasswordBox.Password;
        profile.Option = OptionToggle.IsChecked == true;
        profile.FixDpi = FixDpiCheckBox.IsChecked == true;
    }

    private void SwitchTo(GameKind kind)
    {
        if (kind == _activeGame)
        {
            return;
        }

        CommitProfile();
        _activeGame = kind;
        _config.ActiveGame = kind == GameKind.Csnz ? CsnzGameId : Cso2GameId;
        ApplyActiveProfile();
        _ = ProbeAsync(false);
    }

    private void CsnzTab_Click(object sender, RoutedEventArgs e)
    {
        SwitchTo(GameKind.Csnz);
    }

    private void Cso2Tab_Click(object sender, RoutedEventArgs e)
    {
        SwitchTo(GameKind.Cso2);
    }

    private void UpdateTabVisuals()
    {
        bool cso2 = _activeGame == GameKind.Cso2;
        ApplyTab(CsnzTab, CsnzTabBadge, CsnzTabBadgeText, CsnzTabName, CsnzTabSub, !cso2);
        ApplyTab(Cso2Tab, Cso2TabBadge, Cso2TabBadgeText, Cso2TabName, Cso2TabSub, cso2);

        CsnzTabDot.Fill = IsProfileUsable(_config.Csnz!) ? OnlineBrush : TabDotMissingBrush;
        Cso2TabDot.Fill = IsProfileUsable(_config.Cso2!) ? OnlineBrush : TabDotMissingBrush;
    }

    private static void ApplyTab(Button tab, Border badge, TextBlock badgeText, TextBlock name, TextBlock sub, bool active)
    {
        tab.Background = active ? TabBackgroundActive : Brushes.Transparent;
        tab.BorderBrush = active ? BadgeBorderActive : Brushes.Transparent;
        badge.BorderBrush = active ? BadgeBorderActive : BadgeBorderIdle;
        badge.Background = active ? TabBackgroundActive : Brushes.Transparent;
        badgeText.Foreground = active ? BadgeTextActive : BadgeTextIdle;
        name.Foreground = active ? ActiveTabBrush : InactiveTabBrush;
        sub.Foreground = active ? TabSubActive : TabSubIdle;
    }

    private void UpdateOptionLabel()
    {
        OptionToggle.Content = _activeGame == GameKind.Cso2 ? "启用自定义内容" : "跳过游戏内登录框";
        UpdateOptionHint();
    }

    private void UpdateOptionHint()
    {
        bool on = OptionToggle.IsChecked == true;
        bool cso2 = _activeGame == GameKind.Cso2;
        OptionHint.Text = cso2
            ? (on ? "CUSTOM ON" : "CUSTOM OFF")
            : (on ? "AUTH UI OFF" : "AUTH UI ON");
    }

    private void OptionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_applying)
        {
            UpdateOptionHint();
        }
    }

    private void UpdateFeatureHint()
    {
        var profile = CurrentProfile();
        string relative = profile.FeatureFiles.Count > 0 ? profile.FeatureFiles[0] : string.Empty;
        bool ok = HasFeature(profile.GameDir, profile.FeatureFiles);

        FeatureHint.Text = (ok ? "✓ 已定位 " : "✗ 未找到 ") + relative;
        FeatureHint.Foreground = ok ? OnlineBrush : ErrorStatusBrush;

        if (_activeGame == GameKind.Csnz)
        {
            CsnzClient.Text = relative;
        }
        else
        {
            Cso2Client.Text = relative;
        }
    }

    private void UpdateTargetReadout()
    {
        if (IpBox is null || PortBox is null || CsnzTarget is null || Cso2Target is null)
        {
            return;
        }

        string ip = IpBox.Text.Trim();
        string port = PortBox.Text.Trim();
        string text = ip.Length == 0 ? "—" : (port.Length == 0 ? ip : ip + " : " + port);

        if (_activeGame == GameKind.Csnz)
        {
            CsnzTarget.Text = text;
        }
        else
        {
            Cso2Target.Text = text;
        }
    }

    private void DirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applying)
        {
            return;
        }

        CurrentProfile().GameDir = DirBox.Text.Trim();
        UpdateFeatureHint();
        UpdateTabVisuals();
        SetStatusReady();
    }

    private void Endpoint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applying)
        {
            return;
        }

        UpdateTargetReadout();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择游戏目录",
            InitialDirectory = Directory.Exists(DirBox.Text) ? DirBox.Text : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            DirBox.Text = dialog.FolderName;
            DirBox.CaretIndex = DirBox.Text.Length;
        }
    }

    // ================= 启动游戏 =================

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        CommitProfile();
        var profile = CurrentProfile();

        if (!ValidateBeforeLaunch(profile))
        {
            return;
        }

        string relative = profile.FeatureFiles[0];
        string exePath = Path.Combine(profile.GameDir, relative);

        try
        {
            ApplyDpiCompatibility(exePath, profile.FixDpi);

            var startInfo = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = profile.GameDir,
                UseShellExecute = true,
            };

            BuildArguments(startInfo, profile);
            Process.Start(startInfo);

            SaveConfig();
            SetStatus("已启动游戏客户端。", false);
        }
        catch (Exception ex)
        {
            SetStatus("启动失败：" + ex.Message, true);
        }
    }

    private bool ValidateBeforeLaunch(ProfileBase profile)
    {
        if (!HasFeature(profile.GameDir, profile.FeatureFiles))
        {
            string relative = profile.FeatureFiles.Count > 0 ? profile.FeatureFiles[0] : "客户端";
            SetStatus("游戏目录不正确，找不到 " + relative + "。", true);
            DirBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.Ip))
        {
            SetStatus("请填写服务器 IP。", true);
            IpBox.Focus();
            return false;
        }

        if (profile.Port is < 1 or > 65535)
        {
            SetStatus("端口必须是 1 到 65535 之间的数字。", true);
            PortBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.Login))
        {
            SetStatus("请填写用户名。", true);
            LoginBox.Focus();
            return false;
        }

        if (string.IsNullOrEmpty(profile.Password))
        {
            SetStatus("请填写密码。", true);
            (_passwordVisible ? (Control)PasswordText : PasswordBox).Focus();
            return false;
        }

        return true;
    }

    private static void BuildArguments(ProcessStartInfo info, ProfileBase profile)
    {
        if (profile is CsnzProfile csnz)
        {
            info.ArgumentList.Add("-ip");
            info.ArgumentList.Add(profile.Ip);
            info.ArgumentList.Add("-port");
            info.ArgumentList.Add(profile.Port.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("-login");
            info.ArgumentList.Add(profile.Login);
            info.ArgumentList.Add("-password");
            info.ArgumentList.Add(profile.Password);

            if (csnz.DisableAuthUi)
            {
                info.ArgumentList.Add("-disableauthui");
            }
        }
        else if (profile is Cso2Profile cso2)
        {
            info.ArgumentList.Add("-masterip");
            info.ArgumentList.Add(profile.Ip);

            if (cso2.EnableCustom)
            {
                info.ArgumentList.Add("-enablecustom");
            }

            info.ArgumentList.Add("-decryptedfiles");
            info.ArgumentList.Add("-username");
            info.ArgumentList.Add(profile.Login);
            info.ArgumentList.Add("-password");
            info.ArgumentList.Add(profile.Password);
        }
    }

    // ================= 连通性检测 =================

    private async Task ProbeAsync(bool showProgress)
    {
        if (_pinging)
        {
            return;
        }

        string ip = IpBox.Text.Trim();
        if (ip.Length == 0)
        {
            SetLink("待输入 IP", NormalStatusBrush);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            SetLink("端口无效", ErrorStatusBrush);
            return;
        }

        _pinging = true;
        if (showProgress)
        {
            SetLink("检测中…", NormalStatusBrush);
        }

        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromMilliseconds(700));
            watch.Stop();
            SetLink("在线 · " + watch.ElapsedMilliseconds + " ms", OnlineBrush);
        }
        catch (TimeoutException)
        {
            SetLink("超时", ErrorStatusBrush);
        }
        catch
        {
            SetLink("离线", ErrorStatusBrush);
        }
        finally
        {
            _pinging = false;
        }
    }

    private void SetLink(string text, Brush brush)
    {
        var target = _activeGame == GameKind.Csnz ? CsnzLink : Cso2Link;
        target.Text = text;
        target.Foreground = brush;
    }

    // ================= 配置持久化 =================

    private void SaveConfig()
    {
        try
        {
            CommitProfile();
            _config.ActiveGame = _activeGame == GameKind.Csnz ? CsnzGameId : Cso2GameId;
            ClearLegacy();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus("保存 launcher.json 失败：" + ex.Message, true);
        }
    }

    private void ClearLegacy()
    {
        _config.LegacyIp = null;
        _config.LegacyPort = null;
        _config.LegacyLogin = null;
        _config.LegacyPassword = null;
        _config.LegacyDisableAuthUi = null;
        _config.LegacyFixDpi = null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _pingTimer.Stop();
        SaveConfig();
        base.OnClosing(e);
    }

    // ================= 界面交互 =================

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;

        if (_passwordVisible)
        {
            PasswordText.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordText.Visibility = Visibility.Visible;
            PasswordText.Focus();
            PasswordText.CaretIndex = PasswordText.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordText.Text;
            PasswordText.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    private void PasswordText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_passwordVisible)
        {
            PasswordBox.Password = PasswordText.Text;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    // ================= 状态 =================

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? ErrorStatusBrush : NormalStatusBrush;

        Color color = error ? OfflineColor : OnlineColor;
        StatusDot.Fill = new SolidColorBrush(color);
        if (StatusDot.Effect is DropShadowEffect glow)
        {
            glow.Color = color;
        }
    }

    private void SetStatusReady()
    {
        bool ok = IsProfileUsable(CurrentProfile());
        SetStatus(ok ? "就绪 · 客户端已定位" : "未定位 · 请选择游戏目录", !ok);
    }

    // ================= DPI =================

    private bool IsHighDpiDisplay()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Abs(dpi.DpiScaleX - 1.0) > 0.01 || Math.Abs(dpi.DpiScaleY - 1.0) > 0.01;
    }

    private string CurrentDpiText()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Round(dpi.DpiScaleX * 100).ToString(CultureInfo.InvariantCulture) + "%";
    }

    private static void ApplyDpiCompatibility(string exePath, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(CompatLayersKey, writable: true);
            if (key is null)
            {
                return;
            }

            string? current = key.GetValue(exePath) as string;
            var flags = (current ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token != "~" && !token.Equals(HighDpiFlag, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (enabled)
            {
                flags.Add(HighDpiFlag);
            }

            if (flags.Count == 0)
            {
                key.DeleteValue(exePath, throwOnMissingValue: false);
                return;
            }

            key.SetValue(exePath, "~ " + string.Join(' ', flags), RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
        }
    }

    // ================= 配置模型 =================

    private sealed class LauncherConfig
    {
        [JsonPropertyName("activeGame")]
        public string ActiveGame { get; set; } = CsnzGameId;

        [JsonPropertyName("csnz")]
        public CsnzProfile? Csnz { get; set; }

        [JsonPropertyName("cso2")]
        public Cso2Profile? Cso2 { get; set; }

        [JsonPropertyName("ip"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyIp { get; set; }

        [JsonPropertyName("port"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? LegacyPort { get; set; }

        [JsonPropertyName("login"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyLogin { get; set; }

        [JsonPropertyName("password"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LegacyPassword { get; set; }

        [JsonPropertyName("disableAuthUi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? LegacyDisableAuthUi { get; set; }

        [JsonPropertyName("fixDpi"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? LegacyFixDpi { get; set; }
    }

    private abstract class ProfileBase
    {
        [JsonPropertyName("gameDir")]
        public string GameDir { get; set; } = string.Empty;

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = "127.0.0.1";

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("fixDpi")]
        public bool FixDpi { get; set; } = true;

        [JsonPropertyName("featureFiles")]
        public List<string> FeatureFiles { get; set; } = new();

        [JsonIgnore]
        public abstract bool Option { get; set; }
    }

    private sealed class CsnzProfile : ProfileBase
    {
        [JsonPropertyName("disableAuthUi")]
        public bool DisableAuthUi { get; set; } = true;

        [JsonIgnore]
        public override bool Option
        {
            get => DisableAuthUi;
            set => DisableAuthUi = value;
        }
    }

    private sealed class Cso2Profile : ProfileBase
    {
        [JsonPropertyName("enableCustom")]
        public bool EnableCustom { get; set; } = true;

        [JsonIgnore]
        public override bool Option
        {
            get => EnableCustom;
            set => EnableCustom = value;
        }
    }
}
