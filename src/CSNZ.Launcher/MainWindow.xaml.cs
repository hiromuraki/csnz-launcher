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

public partial class MainWindow : Window
{
    private const string GameLauncherFileName = "CSOLauncher.exe";

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
    private static readonly Brush ValueBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xE7, 0xEF));
    private static readonly Brush OnlineBrush = new SolidColorBrush(OnlineColor);
    private static readonly Brush OfflineBrush = new SolidColorBrush(OfflineColor);

    private readonly DispatcherTimer _pingTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    private bool _passwordVisible;
    private bool _pinging;
    private bool _dpiToggleRestored;

    public MainWindow()
    {
        InitializeComponent();
        _pingTimer.Tick += (_, _) => _ = ProbeAsync(showProgress: false);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        bool configOk = LoadConfig();

        // launcher.json 里没有记录过这项时，按当前显示器缩放给默认值。
        if (!_dpiToggleRestored)
        {
            FixDpiCheckBox.IsChecked = IsHighDpiDisplay();
        }

        DpiScaleText.Text = CurrentDpiText();

        IpBox.Focus();
        IpBox.CaretIndex = IpBox.Text.Length;
        UpdateTargetReadout();
        CheckClient();

        if (!configOk)
        {
            SetStatus("launcher.json 读取失败，已使用默认配置。", true);
        }

        _pingTimer.Start();
        _ = ProbeAsync(showProgress: false);
    }

    // ================= 启动 =================

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        string ip = IpBox.Text.Trim();
        if (ip.Length == 0)
        {
            ShowValidationError(IpBox, "请填写 IP 地址。");
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1 || port > 65535)
        {
            ShowValidationError(PortBox, "端口必须是 1 到 65535 之间的数字。");
            return;
        }

        string login = LoginBox.Text;
        if (string.IsNullOrWhiteSpace(login))
        {
            ShowValidationError(LoginBox, "请填写用户名。");
            return;
        }

        SyncPasswordFromEditor();
        string password = PasswordBox.Password;
        if (password.Length == 0)
        {
            ShowValidationError(_passwordVisible ? PasswordText : PasswordBox, "请填写密码。");
            return;
        }

        string? gameLauncher = FindGameLauncher();
        if (gameLauncher is null)
        {
            SetStatus("找不到 " + GameLauncherFileName + "，请把启动器放在游戏目录中。", true);
            return;
        }

        try
        {
            ApplyDpiCompatibility(gameLauncher, FixDpiCheckBox.IsChecked == true);

            var startInfo = new ProcessStartInfo(gameLauncher)
            {
                WorkingDirectory = Path.GetDirectoryName(gameLauncher)!,
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add("-ip");
            startInfo.ArgumentList.Add(ip);
            startInfo.ArgumentList.Add("-port");
            startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-login");
            startInfo.ArgumentList.Add(login);
            startInfo.ArgumentList.Add("-password");
            startInfo.ArgumentList.Add(password);

            if (DisableAuthUiCheckBox.IsChecked == true)
            {
                startInfo.ArgumentList.Add("-disableauthui");
            }

            Process.Start(startInfo);

            SaveConfig();
            SetStatus("已启动游戏客户端。", false);
        }
        catch (Exception ex)
        {
            SetStatus("启动失败：" + ex.Message, true);
        }
    }

    // ================= 次操作 =================

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        string? launcher = FindGameLauncher();
        string dir = launcher is not null
            ? Path.GetDirectoryName(launcher)!
            : AppContext.BaseDirectory;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus("打开目录失败：" + ex.Message, true);
        }
    }

    // ================= 连通性检测 =================

    private void Ping_Click(object sender, RoutedEventArgs e)
    {
        // 手动检测：额外显示一次“检测中…”，自动轮询则不打断已有状态。
        _ = ProbeAsync(showProgress: true);
    }

    /// <summary>
    /// 每秒轮询一次服务器连通性；上一轮未结束时跳过本轮，避免请求堆积。
    /// </summary>
    private async Task ProbeAsync(bool showProgress)
    {
        if (_pinging)
        {
            return;
        }

        string ip = IpBox.Text.Trim();
        if (ip.Length == 0)
        {
            SetPingState("待输入 IP", NormalStatusBrush);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1 || port > 65535)
        {
            SetPingState("端口无效", ErrorStatusBrush);
            return;
        }

        _pinging = true;
        if (showProgress)
        {
            SetPingState("检测中…", NormalStatusBrush);
        }

        var watch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromMilliseconds(700));
            watch.Stop();
            SetPingState("在线 · " + watch.ElapsedMilliseconds + " ms", OnlineBrush);
        }
        catch (TimeoutException)
        {
            SetPingState("超时", ErrorStatusBrush);
        }
        catch
        {
            SetPingState("离线", ErrorStatusBrush);
        }
        finally
        {
            _pinging = false;
        }
    }

    private void SetPingState(string text, Brush brush)
    {
        PingText.Text = text;
        PingText.Foreground = brush;
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

    private void SyncPasswordFromEditor()
    {
        if (_passwordVisible)
        {
            PasswordBox.Password = PasswordText.Text;
        }
    }

    private void Endpoint_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTargetReadout();
    }

    private void UpdateTargetReadout()
    {
        // InitializeComponent 设置 Text 时会触发 TextChanged，此时命名字段尚未赋值。
        if (TargetText is null || IpBox is null || PortBox is null)
        {
            return;
        }

        string ip = IpBox.Text.Trim();
        string port = PortBox.Text.Trim();
        TargetText.Text = ip.Length == 0
            ? "—"
            : (port.Length == 0 ? ip : ip + " : " + port);
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
            // 鼠标已松开时 DragMove 会抛异常，忽略即可。
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

    // ================= 配置持久化 =================

    private bool LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return true;
            }

            var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(ConfigPath));
            if (config is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(config.Ip))
            {
                IpBox.Text = config.Ip.Trim();
            }

            if (config.Port is > 0 and <= 65535)
            {
                PortBox.Text = config.Port.ToString(CultureInfo.InvariantCulture);
            }

            LoginBox.Text = config.Login;
            PasswordBox.Password = config.Password;
            PasswordText.Text = config.Password;
            DisableAuthUiCheckBox.IsChecked = config.DisableAuthUi;

            if (config.FixDpi.HasValue)
            {
                FixDpiCheckBox.IsChecked = config.FixDpi.Value;
                _dpiToggleRestored = true;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private void SaveConfig()
    {
        try
        {
            SyncPasswordFromEditor();

            var config = new LauncherConfig
            {
                Ip = IpBox.Text.Trim(),
                Port = int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                       && port is >= 1 and <= 65535
                    ? port
                    : 30002,
                Login = LoginBox.Text,
                Password = PasswordBox.Password,
                DisableAuthUi = DisableAuthUiCheckBox.IsChecked == true,
                FixDpi = FixDpiCheckBox.IsChecked == true,
            };

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus("保存 launcher.json 失败：" + ex.Message, true);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _pingTimer.Stop();
        SaveConfig();
        base.OnClosing(e);
    }

    // ================= DPI 兼容性 =================

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

    /// <summary>
    /// 高 DPI 兼容性设置是「每用户 + 每个 exe」的注册表项（HKCU\...\AppCompatFlags\Layers），
    /// Windows 在创建进程时读取它；进程一旦跑起来就改不了自己的 DPI 感知，
    /// 所以只能在启动游戏之前写入，对紧接着创建的游戏进程立即生效。
    /// </summary>
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
            // 写不进去也不该拦住启动。
        }
    }

    // ================= 状态与查找 =================

    private void CheckClient()
    {
        string? launcher = FindGameLauncher();
        if (launcher is null)
        {
            ClientPathText.Text = "未找到 " + GameLauncherFileName;
            ClientPathText.Foreground = ErrorStatusBrush;
            SetStatus("未找到客户端，请把启动器放进游戏目录。", true);
            return;
        }

        ClientPathText.Text = ShortClientPath(launcher);
        ClientPathText.ToolTip = launcher;
        ClientPathText.Foreground = ValueBrush;
        SetStatus("就绪 · 客户端文件已定位", false);
    }

    private void ShowValidationError(Control control, string message)
    {
        SetStatus(message, true);
        control.Focus();
    }

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

    /// <summary>
    /// 读数条空间有限：位于 Bin 目录时显示 Bin\\xxx，否则只显示文件名，完整路径放在提示里。
    /// </summary>
    private static string ShortClientPath(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && string.Equals(Path.GetFileName(dir), "Bin", StringComparison.OrdinalIgnoreCase))
        {
            return "Bin\\" + Path.GetFileName(path);
        }

        return Path.GetFileName(path);
    }

    /// <summary>
    /// 从启动器所在目录逐级向上查找 Bin\CSOLauncher.exe 或同目录下的 CSOLauncher.exe。
    /// </summary>
    private static string? FindGameLauncher()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            string direct = Path.Combine(dir.FullName, GameLauncherFileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            string inBin = Path.Combine(dir.FullName, "Bin", GameLauncherFileName);
            if (File.Exists(inBin))
            {
                return inBin;
            }
        }

        return null;
    }

    /// <summary>
    /// launcher.json 的结构，与启动器同目录、明文保存。
    /// </summary>
    private sealed class LauncherConfig
    {
        [JsonPropertyName("ip")]
        public string Ip { get; set; } = "127.0.0.1";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 30002;

        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;

        [JsonPropertyName("disableAuthUi")]
        public bool DisableAuthUi { get; set; }

        [JsonPropertyName("fixDpi")]
        public bool? FixDpi { get; set; }
    }

}
