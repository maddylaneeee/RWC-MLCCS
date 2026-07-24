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
    private TextBox? _publicConfigUrlTextBox;
    private TextBox? _privateConfigSourceTextBox;
    private CheckBox? _showPrivateUrlCheckBox;
    private CancellationTokenSource? _installCts;
    private Task? _installTask;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _closing;
    private bool _reconfiguring;

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
        if (_closing || (_installTask is null && _runTask is null))
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        Enabled = false;
        await StopAllAsync();
        e.Cancel = false;
        Close();
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        _content.Controls.Clear();
        _content.Controls.Add(_pages[index]());
        _backButton.Enabled = index > 0 && index < 4 && !(_reconfiguring && index == 2);
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
            var installTask = InstallAndRunAsync();
            _installTask = installTask;
            try
            {
                await installTask;
            }
            finally
            {
                if (ReferenceEquals(_installTask, installTask)) _installTask = null;
            }
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
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var intro = CreateBodyLabel(
            "输入公开 config.json URL，以及 Codex 提供的单设备加密私有配置 URL；也可在第二栏选择本地 device.private.json。");
        var publicLabel = new Label { Text = "公开 config.json URL", Dock = DockStyle.Fill };
        _publicConfigUrlTextBox = new TextBox
        {
            Text = ClientProvisioning.PublicBootstrapUrl,
            Dock = DockStyle.Fill
        };
        var privateLabel = new Label { Text = "device.private.json URL 或本地路径", Dock = DockStyle.Fill };
        _privateConfigSourceTextBox = new TextBox
        {
            Text = _reconfiguring ? "" : ResolveInitialPrivateConfigSource(),
            Dock = DockStyle.Fill,
            UseSystemPasswordChar = true
        };
        var privateSourcePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        privateSourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        privateSourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        var browseButton = new Button
        {
            Text = "浏览…",
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 0, 0, 0)
        };
        browseButton.Click += (_, _) => SelectLocalPrivateConfig();
        privateSourcePanel.Controls.Add(_privateConfigSourceTextBox, 0, 0);
        privateSourcePanel.Controls.Add(browseButton, 1, 0);
        _showPrivateUrlCheckBox = new CheckBox
        {
            Text = "显示私有配置 URL",
            Dock = DockStyle.Fill
        };
        _showPrivateUrlCheckBox.CheckedChanged += (_, _) =>
        {
            if (_privateConfigSourceTextBox is not null)
                _privateConfigSourceTextBox.UseSystemPasswordChar = !_showPrivateUrlCheckBox.Checked;
        };
        var hint = CreateBodyLabel(
            "远程私有 URL 必须是 FileShare temporary HTTPS 链接，并带 #crc-key=…；链接不会写入日志。");

        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(publicLabel, 0, 1);
        layout.Controls.Add(_publicConfigUrlTextBox, 0, 2);
        layout.Controls.Add(privateLabel, 0, 3);
        layout.Controls.Add(privateSourcePanel, 0, 4);
        layout.Controls.Add(_showPrivateUrlCheckBox, 0, 5);
        layout.Controls.Add(hint, 0, 6);
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
        _statusLabel = CreateBodyLabel("点击“安装”后，程序会验证并安全保存私有配置，然后启动 RWC-MLCCS 后台连接。");
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
        var replaceButton = new Button
        {
            Text = "更换配置…",
            Dock = DockStyle.Bottom,
            Height = 38
        };
        replaceButton.Click += async (_, _) => await BeginReconfigurationAsync(replaceButton);
        panel.Controls.Add(CreateBodyLabel(
            "RWC-MLCCS 已完成初始化，正在等待服务端连接。\r\n\r\n保持此窗口打开时，后台连接会持续生效。关闭本程序将断开连接并停止本工具创建的执行进程。\r\n\r\n如需更换设备身份或密钥，请点击“更换配置”。当前连接会先安全停止。"));
        panel.Controls.Add(replaceButton);
        return panel;
    }

    private async Task InstallAndRunAsync()
    {
        try
        {
            _installCts = new CancellationTokenSource();
            _nextButton.Enabled = false;
            _backButton.Enabled = false;
            _cancelButton.Enabled = true;
            SetProgress(10, "正在记录用户许可...");
            File.WriteAllText(AppPaths.PolicyAcceptedPath, $"acceptedAt={DateTimeOffset.Now:O}{Environment.NewLine}");

            SetProgress(25, "正在验证配置来源...");
            var publicConfigSource = _publicConfigUrlTextBox?.Text.Trim()
                ?? ClientProvisioning.PublicBootstrapUrl;
            var privateConfigSource = _privateConfigSourceTextBox?.Text.Trim() ?? "";
            var config = await InstallerBootstrapper.EnsureClientConfigAsync(
                publicConfigSource, privateConfigSource, _logger, _installCts.Token);
            _installCts.Token.ThrowIfCancellationRequested();
            if (_privateConfigSourceTextBox is not null) _privateConfigSourceTextBox.Clear();

            SetProgress(70, "正在启动后台连接...");
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => ReverseClient.RunReconnectLoopAsync(config, _logger, _runCts.Token));

            SetProgress(100, "安装完成，正在等待服务端连接...");
            await Task.Delay(500, _installCts.Token);
            _installCts.Token.ThrowIfCancellationRequested();
            _reconfiguring = false;
            ShowPage(4);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Installation cancelled.");
            if (!_closing && !IsDisposed)
            {
                SetProgress(0, "安装已取消。");
                _nextButton.Enabled = true;
                _backButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex is ProvisioningException provisioning
                ? $"Installation failed. provisioningCode={provisioning.Code}"
                : $"Installation failed. errorType={ex.GetType().Name}");
            if (!_closing && !IsDisposed)
            {
                SetProgress(0, $"安装失败：{ex.Message}");
                _nextButton.Enabled = true;
                _backButton.Enabled = true;
                _cancelButton.Enabled = true;
            }
        }
        finally
        {
            _installCts?.Dispose();
            _installCts = null;
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
        if (_closing) return;
        _closing = true;
        Enabled = false;
        await StopAllAsync();
        Close();
    }

    private async Task StopAllAsync()
    {
        if (_installCts is not null && _installTask is not null)
        {
            _logger.Info("Installation cancellation requested.");
            _installCts.Cancel();
            try
            {
                await _installTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        await StopServiceAsync(requireCompletion: false);
    }

    private async Task<bool> StopServiceAsync(bool requireCompletion)
    {
        if (_runCts is null || _runTask is null)
        {
            return true;
        }

        var runCts = _runCts;
        var runTask = _runTask;
        _logger.Info("Client shutdown requested.");
        runCts.Cancel();
        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            _logger.Info(requireCompletion
                ? "Client shutdown timed out; configuration replacement was not started."
                : "Client shutdown timed out; process will exit.");
            return false;
        }
        catch (OperationCanceledException)
        {
        }

        if (ReferenceEquals(_runTask, runTask))
        {
            runCts.Dispose();
            _runCts = null;
            _runTask = null;
        }
        return true;
    }

    private void SelectLocalPrivateConfig()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 device.private.json",
            Filter = "device.private.json|device.private.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || _privateConfigSourceTextBox is null) return;
        _privateConfigSourceTextBox.Text = dialog.FileName;
        _privateConfigSourceTextBox.SelectionStart = _privateConfigSourceTextBox.TextLength;
    }

    private async Task BeginReconfigurationAsync(Button sourceButton)
    {
        if (_closing || _installTask is not null) return;
        sourceButton.Enabled = false;
        _logger.Info("Configuration replacement requested.");
        var stopped = await StopServiceAsync(requireCompletion: true);
        if (_closing || IsDisposed) return;
        if (!stopped)
        {
            sourceButton.Enabled = true;
            MessageBox.Show(
                this,
                "当前连接未能在安全时限内完全停止，因此没有开始更换配置。请稍后重试。",
                "无法更换配置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _reconfiguring = true;
        ShowPage(2);
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

    private static string ResolveInitialPrivateConfigSource()
    {
        if (File.Exists(AppPaths.DefaultClientConfigPath)) return AppPaths.DefaultClientConfigPath;
        return AppPaths.ProvisioningClientConfigPath;
    }
}

internal static class InstallerBootstrapper
{
    public static async Task<ClientConfig> EnsureClientConfigAsync(
        string publicConfigSource,
        string privateConfigSource,
        FileLogger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.ClientDataDirectory);

        if (string.IsNullOrWhiteSpace(privateConfigSource))
        {
            if (File.Exists(AppPaths.DefaultClientConfigPath))
                return ClientConfig.LoadOrCreate(AppPaths.DefaultClientConfigPath);
            throw new InvalidDataException("请输入私有配置 URL 或选择本地私有配置文件。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ClientConfig provisioned;
        if (Uri.TryCreate(privateConfigSource, UriKind.Absolute, out var privateUri) &&
            privateUri.Scheme == Uri.UriSchemeHttps)
        {
            using var httpClient = ClientProvisioning.CreateHttpClient();
            provisioned = await ClientProvisioning.DownloadAndDecryptAsync(
                publicConfigSource, privateConfigSource, httpClient, cancellationToken);
            logger.Info("Validated encrypted remote provisioning package.");
        }
        else
        {
            var sourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(privateConfigSource));
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到私有配置文件。", sourcePath);
            provisioned = JsonConfig.Read<ClientConfig>(sourcePath);
            provisioned.Validate();
            logger.Info("Validated local administrator-supplied private configuration.");
        }
        provisioned.Validate();
        var candidatePath = Path.Combine(AppPaths.ClientDataDirectory, $"config.{Guid.NewGuid():N}.tmp");
        try
        {
            JsonConfig.Write(candidatePath, provisioned);
            File.Move(candidatePath, AppPaths.DefaultClientConfigPath, overwrite: true);
            JsonConfig.RestrictPrivateFile(AppPaths.DefaultClientConfigPath);
        }
        finally
        {
            if (File.Exists(candidatePath)) File.Delete(candidatePath);
        }
        logger.Info("Installed private client configuration with restricted permissions.");
        return ClientConfig.LoadOrCreate(AppPaths.DefaultClientConfigPath);
    }
}
