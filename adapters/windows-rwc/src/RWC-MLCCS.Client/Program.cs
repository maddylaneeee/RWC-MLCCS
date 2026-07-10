using RWC_MLCCS.Common;

namespace RWC_MLCCS.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerWizardForm());
    }
}

internal sealed class InstallerWizardForm : Form
{
    private const string PolicyText =
        "在继续之前，请确认你理解本工具的用途。RWC-MLCCS 建立连接后，远端 MLCCS 服务端可以对这台电脑执行完整的远程管理操作，包括以管理员权限运行命令、读取系统信息以及改变系统配置。MLCCS 不对远端指令、连接环境或执行结果作安全性、可靠性或适用性保证；因使用本工具造成的数据、系统、网络、权限或业务影响，由使用者自行承担。本程序关闭时，由本工具建立的远程控制连接以及相关 PowerShell/WinRM 执行进程会被终止。";

    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(24) };
    private readonly Button _backButton = new() { Text = "上一步", Width = 92, Enabled = false };
    private readonly Button _nextButton = new() { Text = "下一步", Width = 92 };
    private readonly Button _cancelButton = new() { Text = "取消", Width = 92 };
    private readonly Label _titleLabel = new() { Dock = DockStyle.Top, Height = 54, Padding = new Padding(24, 16, 24, 0) };
    private readonly FileLogger _logger = new("client");
    private readonly List<Func<Control>> _pages;

    private int _pageIndex;
    private CheckBox? _acceptCheckBox;
    private ProgressBar? _progressBar;
    private Label? _statusLabel;
    private TextBox? _configUrlTextBox;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public InstallerWizardForm()
    {
        Text = "RWC-MLCCS 安装向导";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 620;
        Height = 440;
        MinimumSize = new Size(620, 440);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        _titleLabel.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 18, 0)
        };
        footer.Controls.Add(_cancelButton);
        footer.Controls.Add(_nextButton);
        footer.Controls.Add(_backButton);

        Controls.Add(_content);
        Controls.Add(_titleLabel);
        Controls.Add(footer);

        _pages = new List<Func<Control>>
        {
            CreateWelcomePage,
            CreatePolicyPage,
            CreateConfigPage,
            CreateInstallPage,
            CreateFinishPage
        };

        _backButton.Click += (_, _) => MovePage(-1);
        _nextButton.Click += async (_, _) => await NextAsync();
        _cancelButton.Click += async (_, _) => await CloseWithCleanupAsync();

        ShowPage(0);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_runTask is null)
        {
            return;
        }

        e.Cancel = true;
        Enabled = false;
        await StopServiceAsync();
        e.Cancel = false;
        Close();
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        _content.Controls.Clear();
        _content.Controls.Add(_pages[index]());
        _backButton.Enabled = index > 0 && index < 4;
        _cancelButton.Enabled = true;
        _cancelButton.Text = index == 4 ? "关闭" : "取消";

        _titleLabel.Text = index switch
        {
            0 => "欢迎使用 RWC-MLCCS",
            1 => "用户政策许可",
            2 => "配置来源",
            3 => "安装与运行服务",
            _ => "等待服务端"
        };

        UpdateNextButton();
    }

    private async Task NextAsync()
    {
        if (_pageIndex == 3)
        {
            await InstallAndRunAsync();
            return;
        }

        if (_pageIndex == 4)
        {
            await CloseWithCleanupAsync();
            return;
        }

        MovePage(1);
    }

    private void MovePage(int delta)
    {
        var next = Math.Clamp(_pageIndex + delta, 0, _pages.Count - 1);
        ShowPage(next);
    }

    private void UpdateNextButton()
    {
        _nextButton.Enabled = _pageIndex != 1 || (_acceptCheckBox?.Checked ?? false) || File.Exists(AppPaths.PolicyAcceptedPath);
        _nextButton.Text = _pageIndex switch
        {
            3 => "安装",
            4 => "关闭",
            _ => "下一步"
        };
    }

    private Control CreateWelcomePage()
    {
        var panel = CreatePagePanel();
        panel.Controls.Add(CreateBodyLabel(
            "此向导将初始化 RWC-MLCCS 客户端，并在完成后连接到 MLCCS 服务端。\r\n\r\n请关闭不必要的管理工具，然后点击“下一步”继续。"));
        return panel;
    }

    private Control CreateConfigPage()
    {
        var panel = CreatePagePanel();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = CreateBodyLabel("请输入客户端初始化配置的 HTTPS 地址。安装时程序会下载该配置，并保存到本机 ProgramData。");
        var label = new Label { Text = "配置 URL", Dock = DockStyle.Fill };
        _configUrlTextBox = new TextBox
        {
            Text = ResolveInitialConfigSourceUrl(),
            Dock = DockStyle.Fill
        };
        _configUrlTextBox.TextChanged += (_, _) => UpdateNextButton();
        var hint = CreateBodyLabel("示例：https://your-server.example/rwc-mlccs/config.json");

        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(label, 0, 1);
        layout.Controls.Add(_configUrlTextBox, 0, 2);
        layout.Controls.Add(hint, 0, 3);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreatePolicyPage()
    {
        var panel = CreatePagePanel();
        var textBox = new TextBox
        {
            Text = PolicyText,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 10),
            BackColor = SystemColors.Window
        };
        _acceptCheckBox = new CheckBox
        {
            Text = "我已阅读并接受上述用户政策许可",
            Dock = DockStyle.Bottom,
            Height = 36,
            Checked = File.Exists(AppPaths.PolicyAcceptedPath)
        };
        _acceptCheckBox.CheckedChanged += (_, _) => UpdateNextButton();

        panel.Controls.Add(textBox);
        panel.Controls.Add(_acceptCheckBox);
        return panel;
    }

    private Control CreateInstallPage()
    {
        var panel = CreatePagePanel();
        _statusLabel = CreateBodyLabel("点击“安装”后，程序会下载初始化配置并启动 RWC-MLCCS 后台连接。");
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Style = ProgressBarStyle.Blocks,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };

        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(_progressBar);
        return panel;
    }

    private Control CreateFinishPage()
    {
        var panel = CreatePagePanel();
        panel.Controls.Add(CreateBodyLabel(
            "RWC-MLCCS 已完成初始化，正在等待服务端连接。\r\n\r\n保持此窗口打开时，后台连接会持续生效。关闭本程序将断开连接并停止本工具创建的执行进程。"));
        return panel;
    }

    private async Task InstallAndRunAsync()
    {
        try
        {
            _nextButton.Enabled = false;
            _backButton.Enabled = false;
            _cancelButton.Enabled = false;
            SetProgress(10, "正在记录用户许可...");
            File.WriteAllText(AppPaths.PolicyAcceptedPath, $"acceptedAt={DateTimeOffset.Now:O}{Environment.NewLine}");

            SetProgress(35, "正在下载初始化配置...");
            var configSourceUrl = _configUrlTextBox?.Text.Trim() ?? "";
            var config = await InstallerBootstrapper.EnsureClientConfigAsync(configSourceUrl, _logger, CancellationToken.None);

            SetProgress(70, "正在启动后台连接...");
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => ReverseClient.RunReconnectLoopAsync(config, _logger, _runCts.Token));

            SetProgress(100, "安装完成，正在等待服务端连接...");
            await Task.Delay(500);
            ShowPage(4);
        }
        catch (Exception ex)
        {
            _logger.Error("Installation failed.", ex);
            SetProgress(0, $"安装失败：{ex.Message}");
            _nextButton.Enabled = true;
            _backButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    private void SetProgress(int value, string status)
    {
        if (_progressBar is not null)
        {
            _progressBar.Value = Math.Clamp(value, _progressBar.Minimum, _progressBar.Maximum);
        }

        if (_statusLabel is not null)
        {
            _statusLabel.Text = status;
        }
    }

    private async Task CloseWithCleanupAsync()
    {
        Enabled = false;
        await StopServiceAsync();
        Close();
    }

    private async Task StopServiceAsync()
    {
        if (_runCts is null || _runTask is null)
        {
            return;
        }

        _logger.Info("Client shutdown requested.");
        _runCts.Cancel();
        try
        {
            await _runTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            _logger.Info("Client shutdown timed out; process will exit.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            _runTask = null;
        }
    }

    private static Panel CreatePagePanel()
    {
        return new Panel { Dock = DockStyle.Fill };
    }

    private static Label CreateBodyLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericSansSerif, 10),
            AutoSize = false
        };
    }

    private static string ResolveInitialConfigSourceUrl()
    {
        if (!File.Exists(AppPaths.DefaultClientConfigPath))
        {
            return AppPaths.DefaultClientConfigUrl;
        }

        try
        {
            var existing = ClientConfig.LoadOrCreate(AppPaths.DefaultClientConfigPath);
            return string.IsNullOrWhiteSpace(existing.ConfigSourceUrl)
                ? AppPaths.DefaultClientConfigUrl
                : existing.ConfigSourceUrl;
        }
        catch
        {
            return AppPaths.DefaultClientConfigUrl;
        }
    }
}

internal static class InstallerBootstrapper
{
    public static async Task<ClientConfig> EnsureClientConfigAsync(string configSourceUrl, FileLogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.ClientDataDirectory);

        if (string.IsNullOrWhiteSpace(configSourceUrl) ||
            !Uri.TryCreate(configSourceUrl, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("请输入有效的 HTTPS 配置地址。");
        }

        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var json = await httpClient.GetStringAsync(sourceUri, cancellationToken);
            var candidatePath = Path.Combine(AppPaths.ClientDataDirectory, "config.download");
            await File.WriteAllTextAsync(candidatePath, json, cancellationToken);

            var downloaded = ClientConfig.LoadOrCreate(candidatePath);
            downloaded.ConfigSourceUrl = sourceUri.ToString();
            JsonConfig.Write(candidatePath, downloaded);
            if (downloaded.SharedSecret == "change-this-shared-secret")
            {
                throw new InvalidDataException("Downloaded config still uses the placeholder shared secret.");
            }

            File.Copy(candidatePath, AppPaths.DefaultClientConfigPath, overwrite: true);
            File.Delete(candidatePath);
            logger.Info($"Downloaded client config from {sourceUri}.");
        }
        catch (Exception ex) when (File.Exists(AppPaths.DefaultClientConfigPath))
        {
            logger.Error("Config download failed; using existing local config.", ex);
        }

        return ClientConfig.LoadOrCreate(AppPaths.DefaultClientConfigPath);
    }
}
