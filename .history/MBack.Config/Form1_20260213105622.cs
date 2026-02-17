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
    private Button _btnStopService = new(); // ★サービス停止ボタン
    private Button _btnViewLog = new();
    private Button _btnSave = new();

    // --- データ関連 ---
    private string _jsonPath = "";
    private AppSettings _currentSettings = new();
    private const string ServiceName = "MBackService"; // Windowsサービス名

    //ログ関係のパーツ格納変数
    private NumericUpDown _numLogDays = new();
    private Label _lblLogDays = new();

    public Form1()
    {
        InitializeComponent();
        
        // 設定ファイル(appsettings.json)を探す
        FindSettingsFile(); 
        
        // 画面を作る
        SetupLayout();
        _lblLogDays.Text = "ログ保存日数:";
        _lblLogDays.AutoSize = true;
        _numLogDays.Minimum = 1;
        _numLogDays.Maximum = 365;
        _numLogDays.Value = 10; // 初期値
        _numLogDays.Width = 60;
        
        // レイアウトに追加 (パネルのどこか、例えば保存ボタンの上など)
        // _bottomPanel に追加する場合の例:
        _bottomPanel.Controls.Add(_lblLogDays);
        _bottomPanel.Controls.Add(_numLogDays);

        // LoadSettings() で値をセット
        _numLogDays.Value = _currentSettings.LogRetentionDays > 0 ? _currentSettings.LogRetentionDays : 30;

        // OnSaveClick() で値を保存
        _currentSettings.LogRetentionDays = (int)_numLogDays.Value;

        // ファイルから読み込む
        LoadSettings();
    }

    // --- 画面レイアウトの構築 ---
    private void SetupLayout()
    {
        this.Text = "MBack 設定ツール";
        this.Size = new Size(850, 500); // 少し横長に

        // 1. ボタンパネルの設定
        _bottomPanel.Dock = DockStyle.Bottom;
        _bottomPanel.Height = 50;
        _bottomPanel.FlowDirection = FlowDirection.LeftToRight;
        _bottomPanel.Padding = new Padding(5);

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

        _btnStopService.Text = "サービス停止"; // ★追加
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

        // 3. パネルにボタンを追加（★ここが見当たらなかった部分です）
        // 順番に並べます
        _bottomPanel.Controls.Add(_btnAdd);
        _bottomPanel.Controls.Add(_btnRemove);
        _bottomPanel.Controls.Add(_btnExclusions);
        _bottomPanel.Controls.Add(_btnRunNow);
        _bottomPanel.Controls.Add(_btnStopService); // ★追加
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
        this.Controls.Add(_grid);       // 上いっぱい
        this.Controls.Add(_bottomPanel); // 下にパネル
    }

// 設定ファイル(appsettings.json)を探すロジック（決定版）
    private void FindSettingsFile()
    {
        // 1. 自分が今いる場所（exeがある場所）
        string baseDir = AppContext.BaseDirectory;
        
        // 2. まず「インストール済み環境（同じ場所にServiceがいる）」かチェック
        string flatPath = Path.Combine(baseDir, "MBack.Service.exe");
        if (File.Exists(flatPath))
        {
            // 同じ場所にサービス本体がいるなら、設定ファイルもそこにあります
            // ★重要: 必ず「フルパス（絶対パス）」にします
            _jsonPath = Path.Combine(baseDir, "appsettings.json");
            return;
        }

        // 3. 開発環境（親フォルダを遡るパターン）
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
            // 見つからなければ自分の直下を使う（ここも絶対パスにする！）
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

    // 1. 追加ボタン
    private void OnAddClick(object? s, EventArgs e)
    {
        using var d1 = new FolderBrowserDialog { Description = "監視元を選択" };
        if (d1.ShowDialog() != DialogResult.OK) return;
        
        using var d2 = new FolderBrowserDialog { Description = "バックアップ先を選択" };
        if (d2.ShowDialog() != DialogResult.OK) return;

        _currentSettings.BackupSettings.Add(new BackupPair { Source = d1.SelectedPath, Destination = d2.SelectedPath });
        RefreshGrid();
    }

    // 2. 削除ボタン
    private void OnRemoveClick(object? s, EventArgs e)
    {
        if (_grid.SelectedRows.Count > 0)
        {
            int idx = _grid.SelectedRows[0].Index;
            _currentSettings.BackupSettings.RemoveAt(idx);
            RefreshGrid();
        }
    }

    // 3. 除外設定ボタン
    private void OnExclusionsClick(object? s, EventArgs e)
    {
        using var form = new ExclusionForm(_currentSettings.GlobalExclusions);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _currentSettings.GlobalExclusions = form.Exclusions;
        }
    }

    // 4. 今すぐ実行ボタン
    private void OnRunNowClick(object? s, EventArgs e)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_jsonPath);
            if (string.IsNullOrEmpty(dir)) return;
            
            // backup.trigger ファイルを作成
            File.WriteAllText(Path.Combine(dir, "backup.trigger"), DateTime.Now.ToString());
            MessageBox.Show("実行命令を送りました！\nログを確認してください。");
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }

    // 5. ★サービス停止ボタン
    private void OnStopServiceClick(object? sender, EventArgs e)
    {
        ControlService("Stop");
    }

    // 6. ログを見るボタン (AppData対応版)
    private void OnViewLogClick(object? s, EventArgs e)
    {
        try
        {
            // AppData/Local/MBack/Logs を見る
            string logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                "MBack", 
                "Logs");

            string logFileName = $"log-{DateTime.Now:yyyyMMdd}.txt";
            string logPath = Path.Combine(logFolder, logFileName);

            if (!File.Exists(logPath))
            {
                MessageBox.Show($"今日のログがまだありません。\n場所: {logPath}", "ログなし");
                return;
            }

            // OnViewLogClick() の中身を変更
            // 旧: var viewer = new LogViewerForm(logPath); 
            // 新: var viewer = new LogViewerForm(); // 引数なしでOK
            var viewer = new LogViewerForm(); // 引数なしでOK
            viewer.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }

    // 7. 保存して閉じるボタン
    private void OnSaveClick(object? s, EventArgs e)
    {
        try
        {
            // JSON保存
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_currentSettings, options);

            string? dir = Path.GetDirectoryName(_jsonPath);
            if(dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_jsonPath, json);

            // ★サービス再開（またはリロード通知）
            ControlService("Start");

            MessageBox.Show("設定を保存しました。", "完了");
            this.Close();
        }
        catch (Exception ex) { MessageBox.Show("保存エラー: " + ex.Message); }
    }

    // --- ヘルパーメソッド: サービスの操作 ---
    private void ControlService(string action)
    {
        // 開発中(dotnet run)の場合は動かないので無視する
        // ただし、本番で使うときは管理者権限が必要
        try
        {
            using var sc = new ServiceController(ServiceName);
            
            // サービスが存在するかチェック（例外が出たらサービスがない）
            try { var status = sc.Status; }
            catch 
            {
                // 開発中はここで抜ける
                // MessageBox.Show("サービスが見つかりません(開発モード)"); 
                return; 
            }

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
                // 停止中なら開始する
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
                // 実行中なら何もしない（設定ファイル書き換えで勝手にリロードされるため）
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"サービスの操作に失敗しました。\n管理者権限で実行していますか？\n\n{ex.Message}", "エラー");
        }
    }
}