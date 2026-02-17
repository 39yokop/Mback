using System.Text.Json;
using System.Diagnostics;
using System.ServiceProcess; // サービス操作用
using System.Drawing; // 色やフォント用
using System.Windows.Forms;

namespace MBack.Config;

public partial class Form1 : Form
{
    // --- UI部品の定義 ---
    private DataGridView _grid = new();
    private FlowLayoutPanel _bottomPanel = new(); // ボタンを並べるパネル

    private Button _btnAdd = new();
    private Button _btnRemove = new();
    private Button _btnExclusions = new();
    private Button _btnRunNow = new();
    private Button _btnStopService = new(); 
    private Button _btnViewLog = new();
    private Button _btnSave = new();

    // ログ保存日数の設定用
    private Label _lblLogDays = new();
    private NumericUpDown _numLogDays = new();

    // --- データ関連 ---
    private string _jsonPath = "";
    private AppSettings _currentSettings = new();
    private const string ServiceName = "MBackService"; // Windowsサービス名

    public Form1()
    {
        InitializeComponent();
        FindSettingsFile(); 
        SetupLayout();
        LoadSettings();
    }

    // --- 画面レイアウトの構築 ---
    private void SetupLayout()
    {
        this.Text = "MBack 設定ツール";
        this.Size = new Size(950, 550);

        // 1. ボタンパネルの設定
        _bottomPanel.Dock = DockStyle.Bottom;
        _bottomPanel.Height = 60;
        _bottomPanel.FlowDirection = FlowDirection.LeftToRight;
        _bottomPanel.Padding = new Padding(5);
        _bottomPanel.AutoScroll = true;

        // 2. 各ボタンの設定
        _btnAdd.Text = "追加 (+)";
        _btnAdd.AutoSize = true;
        _btnAdd.Click += OnAddClick;

        _btnRemove.Text = "削除 (-)";
        _btnRemove.AutoSize = true;
        _btnRemove.Click += OnRemoveClick;

        _btnExclusions.Text = "除外設定...";
        _btnExclusions.AutoSize = true;
        _btnExclusions.Click += OnExclusionsClick;

        _btnRunNow.Text = "今すぐバックアップ";
        _btnRunNow.AutoSize = true;
        _btnRunNow.ForeColor = Color.DarkBlue;
        _btnRunNow.Click += OnRunNowClick;

        _btnStopService.Text = "サービス停止";
        _btnStopService.AutoSize = true;
        _btnStopService.ForeColor = Color.Red;
        _btnStopService.Click += OnStopServiceClick;

        _btnViewLog.Text = "ログを見る";
        _btnViewLog.AutoSize = true;
        _btnViewLog.Click += OnViewLogClick;

        _btnSave.Text = "保存して閉じる";
        _btnSave.AutoSize = true;
        _btnSave.Font = new Font(this.Font, FontStyle.Bold);
        _btnSave.Click += OnSaveClick;

        // ログ日数設定
        _lblLogDays.Text = "ログ保存:";
        _lblLogDays.AutoSize = true;
        _lblLogDays.Padding = new Padding(0, 8, 0, 0);

        _numLogDays.Minimum = 1;
        _numLogDays.Maximum = 365;
        _numLogDays.Value = 30;
        _numLogDays.Width = 50;

        // 3. パネルにボタンを追加
        _bottomPanel.Controls.Add(_btnAdd);
        _bottomPanel.Controls.Add(_btnRemove);
        _bottomPanel.Controls.Add(_btnExclusions);
        
        _bottomPanel.Controls.Add(_lblLogDays);
        _bottomPanel.Controls.Add(_numLogDays);
        var lblDaysUnit = new Label { Text = "日", AutoSize = true, Padding = new Padding(0,8,0,0) };
        _bottomPanel.Controls.Add(lblDaysUnit);

        _bottomPanel.Controls.Add(_btnRunNow);
        _bottomPanel.Controls.Add(_btnStopService);
        _bottomPanel.Controls.Add(_btnViewLog);
        _bottomPanel.Controls.Add(_btnSave);

        // 4. グリッド（一覧表）の設定
        _grid.Dock = DockStyle.Fill;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.MultiSelect = false;
        
        _grid.Columns.Add("Source", "監視元フォルダ");
        _grid.Columns.Add("Dest", "バックアップ先");

        // 5. フォームに追加
        this.Controls.Add(_grid);
        this.Controls.Add(_bottomPanel);
    }

    // --- 設定ファイルの探索ロジック ---
    private void FindSettingsFile()
    {
        string baseDir = AppContext.BaseDirectory;
        string flatPath = Path.Combine(baseDir, "MBack.Service.exe");
        if (File.Exists(flatPath))
        {
            _jsonPath = Path.Combine(baseDir, "appsettings.json");
            return;
        }

        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        string servicePathCandidate = "";

        while (dir != null)
        {
            var sibling = dir.GetDirectories("MBack.Service").FirstOrDefault();
            if (sibling != null)
            {
                 string tryPath = Path.Combine(sibling.FullName, "appsettings.json");
                 servicePathCandidate = tryPath; 
                 break;
            }
            dir = dir.Parent;
        }

        if (!string.IsNullOrEmpty(servicePathCandidate))
        {
            _jsonPath = servicePathCandidate;
        }
        else
        {
            _jsonPath = Path.Combine(baseDir, "appsettings.json");
        }
    }

    // --- 設定読み込み ---
    private void LoadSettings()
    {
        if (!File.Exists(_jsonPath)) return;
        try
        {
            string json = File.ReadAllText(_jsonPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _currentSettings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();
            
            RefreshGrid();

            if (_currentSettings.LogRetentionDays < 1) _currentSettings.LogRetentionDays = 30;
            _numLogDays.Value = _currentSettings.LogRetentionDays;
        }
        catch (Exception ex) { MessageBox.Show("読み込みエラー: " + ex.Message); }
    }

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        if (_currentSettings.BackupSettings != null)
        {
            foreach (var pair in _currentSettings.BackupSettings)
            {
                _grid.Rows.Add(pair.Source, pair.Destination);
            }
        }
    }

    // --- イベントハンドラ ---
    private void OnAddClick(object? s, EventArgs e)
    {
        using var d1 = new FolderBrowserDialog { Description = "監視元を選択" };
        if (d1.ShowDialog() != DialogResult.OK) return;
        
        using var d2 = new FolderBrowserDialog { Description = "バックアップ先を選択" };
        if (d2.ShowDialog() != DialogResult.OK) return;

        _currentSettings.BackupSettings.Add(new BackupPair { Source = d1.SelectedPath, Destination = d2.SelectedPath });
        RefreshGrid();
    }

    private void OnRemoveClick(object? s, EventArgs e)
    {
        if (_grid.SelectedRows.Count > 0)
        {
            int idx = _grid.SelectedRows[0].Index;
            _currentSettings.BackupSettings.RemoveAt(idx);
            RefreshGrid();
        }
    }

    private void OnExclusionsClick(object? s, EventArgs e)
    {
        using var form = new ExclusionForm(_currentSettings.GlobalExclusions);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _currentSettings.GlobalExclusions = form.Exclusions;
        }
    }

    private void OnRunNowClick(object? s, EventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_jsonPath);
            if (string.IsNullOrEmpty(dir)) return;
            File.WriteAllText(Path.Combine(dir, "backup.trigger"), DateTime.Now.ToString());
            MessageBox.Show("実行命令を送りました！\nログを確認してください。");
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }

    private void OnStopServiceClick(object? sender, EventArgs e)
    {
        ControlService("Stop");
    }

    private void OnViewLogClick(object? s, EventArgs e)
    {
        try
        {
            var viewer = new LogViewerForm();
            viewer.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }

    private void OnSaveClick(object? s, EventArgs e)
    {
        try
        {
            _currentSettings.LogRetentionDays = (int)_numLogDays.Value;

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_currentSettings, options);

            string? dir = Path.GetDirectoryName(_jsonPath);
            if(dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_jsonPath, json);
            ControlService("Start");
            MessageBox.Show("設定を保存しました。", "完了");
            this.Close();
        }
        catch (Exception ex) { MessageBox.Show("保存エラー: " + ex.Message); }
    }

    private void ControlService(string action)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            try { var status = sc.Status; }
            catch { return; } 

            if (action == "Stop")
            {
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    if (MessageBox.Show("バックアップサービスを停止しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        MessageBox.Show("サービスを停止しました。");
                    }
                }
                else
                {
                    MessageBox.Show("すでに停止しています。");
                }
            }
            else if (action == "Start")
            {
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"サービスの操作に失敗しました。\n管理者権限で実行していますか？\n\n{ex.Message}", "エラー");
        }
    }
}