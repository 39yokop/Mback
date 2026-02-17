using System;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Forms;
using System.Drawing;
namespace MBack.Config
{
    // 設定保存用クラス (appsettings.json用)
    public class AppSettingsRaw
    {
        public List<BackupPair> BackupSettings { get; set; } = new();
        public List<string> GlobalExclusions { get; set; } = new();
        public int LogRetentionDays { get; set; } = 30;
    }

    public class BackupPair
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
    }
}

public partial class Form1 : Form
{
    // UI部品
    private DataGridView _grid = new();
    private Button _btnAdd = new();
    private Button _btnEdit = new();
    private Button _btnDelete = new();
    private Button _btnSave = new();
    private Button _btnLog = new();
    private Button _btnExclusion = new(); // 除外設定ボタン
    private Button _btnService = new();   // サービス操作ボタン
    private FlowLayoutPanel _buttonPanel = new(); // ボタンを並べるパネル(自動折り返し)
    private SplitContainer _splitContainer = new(); // 上下分割用

    // データ
    private List<BackupPair> _backupList = new();
    private List<string> _globalExclusions = new();
    private int _logRetentionDays = 30;
    
    // 設定ファイルパス
    private string _jsonPath;

    public Form1()
    {
        this.Text = "MBack 設定ツール";
        // 初期サイズ（初回起動時のみ有効）
        this.Size = new Size(800, 500);
        this.StartPosition = FormStartPosition.Manual; // 位置復元のためにManualにする

        // 設定ファイルパス (exeと同じ場所)
        _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        SetupLayout();
        LoadSettings();
        UpdateGrid();
        LoadWindowState(); // ★ウィンドウサイズ復元
    }

    // --- レイアウト構築 ---
    private void SetupLayout()
    {
        // 1. 全体を上下に分割するコンテナ
        _splitContainer.Dock = DockStyle.Fill;
        _splitContainer.Orientation = Orientation.Horizontal;
        _splitContainer.FixedPanel = FixedPanel.Panel2; // 下パネル(ボタン)のサイズを優先
        _splitContainer.SplitterDistance = 400; // 初期値
        _splitContainer.IsSplitterFixed = true; // ユーザーによる境界線移動を禁止（自動調整させるため）
        
        // 2. グリッド (上部パネル)
        _grid.Dock = DockStyle.Fill;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Add("Source", "監視元フォルダ");
        _grid.Columns.Add("Dest", "バックアップ先");

        _splitContainer.Panel1.Controls.Add(_grid);

        // 3. ボタンパネル (下部パネル)
        _buttonPanel.Dock = DockStyle.Fill;
        _buttonPanel.FlowDirection = FlowDirection.LeftToRight;
        _buttonPanel.WrapContents = true; // ★折り返し有効
        _buttonPanel.Padding = new Padding(10);
        _buttonPanel.AutoSize = true;        // ★中身に合わせてサイズを変える
        _buttonPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        
        // ボタンの初期化
        _btnAdd.Text = "追加";
        _btnAdd.Width = 100;
        _btnAdd.Height = 30; // 高さを明示
        _btnAdd.Click += OnAddClick;

        _btnEdit.Text = "編集";
        _btnEdit.Width = 100;
        _btnEdit.Height = 30;
        _btnEdit.Click += OnEditClick;

        _btnDelete.Text = "削除";
        _btnDelete.Width = 100;
        _btnDelete.Height = 30;
        _btnDelete.Click += OnDeleteClick;

        _btnExclusion.Text = "除外設定";
        _btnExclusion.Width = 100;
        _btnExclusion.Height = 30;
        _btnExclusion.Click += OnExclusionClick;

        _btnLog.Text = "ログを見る & 復元";
        _btnLog.Width = 140;
        _btnLog.Height = 30;
        _btnLog.Click += OnLogClick;

        _btnService.Text = "サービス管理"; // 後で状態によって書き換わる
        _btnService.Width = 140;
        _btnService.Height = 30;
        _btnService.Click += OnServiceClick;

        _btnSave.Text = "保存して閉じる";
        _btnSave.Width = 140;
        _btnSave.Height = 30;
        _btnSave.Font = new Font(this.Font, FontStyle.Bold);
        _btnSave.Click += OnSaveClick;

        // パネルに追加
        _buttonPanel.Controls.Add(_btnAdd);
        _buttonPanel.Controls.Add(_btnEdit);
        _buttonPanel.Controls.Add(_btnDelete);
        _buttonPanel.Controls.Add(_btnExclusion);
        _buttonPanel.Controls.Add(_btnLog);
        _buttonPanel.Controls.Add(_btnService);
        _buttonPanel.Controls.Add(_btnSave); // 保存ボタンは最後

        // レイアウト変更イベント（リサイズ時に高さ調整）
        _buttonPanel.SizeChanged += (s, e) => AdjustBottomPanelHeight();
        
        _splitContainer.Panel2.Controls.Add(_buttonPanel);
        
        this.Controls.Add(_splitContainer);
        
        // サービス状態の確認用タイマー
        var timer = new System.Windows.Forms.Timer();
        timer.Interval = 2000; // 2秒ごとに確認
        timer.Tick += (s, e) => UpdateServiceButtonState();
        timer.Start();
        UpdateServiceButtonState(); // 初回実行
    }

    // ★ ボタンエリアの高さを自動調整する魔法のメソッド
    private void AdjustBottomPanelHeight()
    {
        // FlowLayoutPanelの推奨サイズ（ボタンが並んだ後の高さ）を取得
        int preferredHeight = _buttonPanel.PreferredSize.Height;
        
        // スプリッターの下パネルの高さを設定（少し余裕を持たせる）
        if (_splitContainer.SplitterDistance != this.ClientSize.Height - preferredHeight)
        {
            try 
            {
                // ウィンドウ全体 - ボタンエリアの高さ = スプリッターの位置
                int newDistance = this.ClientSize.Height - preferredHeight;
                if (newDistance > 50) // 最小限のグリッドエリアは確保
                {
                    _splitContainer.SplitterDistance = newDistance;
                }
            }
            catch { /* ウィンドウが小さすぎる場合は無視 */ }
        }
    }
    
    // ウィンドウリサイズ時にも調整
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        AdjustBottomPanelHeight();
    }

    // --- ウィンドウサイズ保存・復元ロジック (レジストリを使用) ---
    // ※ ユーザー別の設定保存場所 (HKCU) に保存するのが一般的で簡単です

    private void LoadWindowState()
    {
        try
        {
            // レジストリから読み込む (MBackConfigというキーを作る)
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\MBackConfig");
            if (key != null)
            {
                int w = (int)(key.GetValue("Width") ?? 800);
                int h = (int)(key.GetValue("Height") ?? 500);
                int x = (int)(key.GetValue("X") ?? 100);
                int y = (int)(key.GetValue("Y") ?? 100);
                
                // 画面外にいってしまった場合の補正（マルチモニタ環境対策）
                if (x < 0 || y < 0 || x > Screen.PrimaryScreen.Bounds.Width || y > Screen.PrimaryScreen.Bounds.Height)
                {
                    x = 100; y = 100;
                }

                this.Size = new Size(w, h);
                this.Location = new Point(x, y);
            }
        }
        catch { /* 初回などで失敗しても気にしない */ }
    }

    private void SaveWindowState()
    {
        try
        {
            // 最小化されていたら保存しない
            if (this.WindowState == FormWindowState.Minimized) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\MBackConfig");
            if (key != null)
            {
                // 最大化されていたら、復元時のサイズ（RestoreBounds）を保存する
                if (this.WindowState == FormWindowState.Maximized)
                {
                    key.SetValue("Width", this.RestoreBounds.Width);
                    key.SetValue("Height", this.RestoreBounds.Height);
                    key.SetValue("X", this.RestoreBounds.Location.X);
                    key.SetValue("Y", this.RestoreBounds.Location.Y);
                }
                else
                {
                    key.SetValue("Width", this.Width);
                    key.SetValue("Height", this.Height);
                    key.SetValue("X", this.Location.X);
                    key.SetValue("Y", this.Location.Y);
                }
            }
        }
        catch { /* 保存失敗は無視 */ }
    }

    // フォームが閉じる時に保存
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveWindowState();
        base.OnFormClosing(e);
    }

    // --- イベントハンドラ ---

    private void OnAddClick(object? sender, EventArgs e)
    {
        // フォルダ選択ダイアログ
        string? src = SelectFolder("監視するフォルダを選んでください (NASも可)");
        if (src == null) return;

        string? dest = SelectFolder($"[{Path.GetFileName(src)}] のバックアップ先を選んでください");
        if (dest == null) return;

        _backupList.Add(new BackupPair { Source = src, Destination = dest });
        UpdateGrid();
    }

    private string? SelectFolder(string title)
    {
        using var dlg = new FolderBrowserDialog();
        dlg.Description = title;
        dlg.UseDescriptionForTitle = true;
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            return dlg.SelectedPath;
        }
        return null;
    }

    private void OnEditClick(object? sender, EventArgs e)
    {
        if (_grid.SelectedRows.Count == 0) return;
        int index = _grid.SelectedRows[0].Index;

        var pair = _backupList[index];
        
        var result = MessageBox.Show(
            $"設定を編集しますか？\n一旦削除して追加し直す形になります。\n\n現在の設定:\n元: {pair.Source}",
            "編集",
            MessageBoxButtons.YesNo);

        if (result == DialogResult.Yes)
        {
            _backupList.RemoveAt(index);
            UpdateGrid();
            OnAddClick(null, null); // 追加処理を呼ぶ
        }
    }

    private void OnDeleteClick(object? sender, EventArgs e)
    {
        if (_grid.SelectedRows.Count == 0) return;
        int index = _grid.SelectedRows[0].Index;
        
        var pair = _backupList[index];
        var result = MessageBox.Show(
            $"以下の設定を削除してもよろしいですか？\n(バックアップ済みのファイルは消えません)\n\n元: {pair.Source}\n先: {pair.Destination}", 
            "削除確認", 
            MessageBoxButtons.YesNo, 
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            _backupList.RemoveAt(index);
            UpdateGrid();
        }
    }

    private void OnExclusionClick(object? sender, EventArgs e)
    {
        // 除外設定フォームを開く
        using var form = new ExclusionForm(_globalExclusions);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _globalExclusions = form.Exclusions; // 更新されたリストを受け取る
        }
    }

    private void OnLogClick(object? sender, EventArgs e)
    {
        using var logForm = new LogViewerForm();
        logForm.ShowDialog();
    }

    private void OnServiceClick(object? sender, EventArgs e)
    {
        string serviceName = "MBackService";
        try 
        {
            // 管理者権限で sc コマンドを実行して制御する
            // .NETのServiceControllerクラスは管理者権限がないと例外が出るため、
            // 簡易的にプロセス起動で代用する手もありますが、
            // ここでは簡易的にメッセージだけ出しておきます。
            // 本格的にやるなら System.ServiceProcess.ServiceController を使います。
            
            var sc = new System.ServiceProcess.ServiceController(serviceName);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                if (MessageBox.Show("サービスを停止しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
            }
            else
            {
                if (MessageBox.Show("サービスを開始しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    sc.Start();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }
            }
            UpdateServiceButtonState();
        }
        catch (Exception ex)
        {
            MessageBox.Show("サービスの操作に失敗しました。\n(管理者として実行していない可能性があります)\n" + ex.Message);
        }
    }

    private void UpdateServiceButtonState()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("MBackService");
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                _btnService.Text = "サービス: 実行中 (停止)";
                _btnService.BackColor = Color.LightGreen;
            }
            else
            {
                _btnService.Text = "サービス: 停止中 (開始)";
                _btnService.BackColor = Color.LightPink;
            }
        }
        catch 
        {
            _btnService.Text = "サービス: 不明";
            _btnService.BackColor = Color.LightGray;
        }
    }

    private void OnSaveClick(object? sender, EventArgs e)
    {
        SaveSettings();
        SaveWindowState(); // 閉じる時にも位置保存
        
        // サービスが動いていなかったら開始を試みる
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("MBackService");
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                 // 自動開始はあえてしない（ユーザーの意図を尊重）
                 // 必要ならここに sc.Start() を書く
            }
        }
        catch {}

        this.Close();
    }

    // --- データ読み書き ---

    private void LoadSettings()
    {
        if (!File.Exists(_jsonPath)) return;
        try
        {
            var json = File.ReadAllText(_jsonPath);
            var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
            if (settings != null)
            {
                _backupList = settings.BackupSettings ?? new();
                _globalExclusions = settings.GlobalExclusions ?? new();
                _logRetentionDays = settings.LogRetentionDays;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("設定の読み込みに失敗しました: " + ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppSettingsRaw
            {
                BackupSettings = _backupList,
                GlobalExclusions = _globalExclusions,
                LogRetentionDays = _logRetentionDays
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(_jsonPath, json);

            MessageBox.Show("設定を保存しました。\nサービスが自動的に新しい設定を読み込みます。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存に失敗しました: " + ex.Message);
        }
    }

    private void UpdateGrid()
    {
        _grid.Rows.Clear();
        foreach (var pair in _backupList)
        {
            _grid.Rows.Add(pair.Source, pair.Destination);
        }
    }
}