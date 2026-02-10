using System.Text.Json;
using System.Diagnostics;

namespace MBack.Config;

public partial class Form1 : Form
{
    private DataGridView _grid = new();
    private Button _btnAdd = new(), _btnRemove = new(), _btnSave = new();
    private Button _btnViewLog = new(), _btnExclusions = new(), _btnRunNow = new();
    private string _jsonPath = "";
    private AppSettings _currentSettings = new();

    public Form1()
    {
        InitializeComponent();
        FindSettingsFile(); // サービスの場所を探す
        SetupLayout();
        LoadSettings();
    }

    private void SetupLayout()
    {
        this.Text = "MBack 設定ツール";
        this.Size = new Size(700, 500);

        var bottomPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50 };
        
        _btnAdd.Text = "追加 (+)"; _btnAdd.AutoSize = true; _btnAdd.Click += OnAddClick;
        _btnRemove.Text = "削除 (-)"; _btnRemove.AutoSize = true; _btnRemove.Click += OnRemoveClick;
        _btnExclusions.Text = "除外設定..."; _btnExclusions.AutoSize = true; _btnExclusions.Click += OnExclusionsClick;
        
        _btnRunNow.Text = "今すぐバックアップ"; 
        _btnRunNow.AutoSize = true; 
        _btnRunNow.ForeColor = Color.DarkBlue;
        _btnRunNow.Click += OnRunNowClick;

        _btnViewLog.Text = "ログを見る"; _btnViewLog.AutoSize = true; _btnViewLog.Click += OnViewLogClick;
        
        _btnSave.Text = "保存して閉じる"; _btnSave.AutoSize = true; _btnSave.Font = new Font(this.Font, FontStyle.Bold); _btnSave.Click += OnSaveClick;

        bottomPanel.Controls.AddRange(new Control[] { _btnAdd, _btnRemove, _btnExclusions, _btnRunNow, _btnViewLog, _btnSave });

        _grid.Dock = DockStyle.Fill;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.Columns.Add("Source", "監視元");
        _grid.Columns.Add("Dest", "バックアップ先");

        this.Controls.Add(_grid);
        this.Controls.Add(bottomPanel);
    }

    private void FindSettingsFile()
    {
        // プロジェクト構成（開発中）または配置構成（本番）からServiceフォルダを探す
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        string servicePathCandidate = "";

        while (dir != null)
        {
            // MBack.Serviceフォルダを探す
            var sibling = dir.GetDirectories("MBack.Service").FirstOrDefault();
            if (sibling != null)
            {
                 string tryPath = Path.Combine(sibling.FullName, "appsettings.json");
                 if(File.Exists(tryPath)) { servicePathCandidate = tryPath; break; }
                 // まだファイルがなくてもフォルダがあればそこを正とする
                 servicePathCandidate = tryPath; 
                 break;
            }
            dir = dir.Parent;
        }

        if (!string.IsNullOrEmpty(servicePathCandidate))
        {
            _jsonPath = servicePathCandidate;
            // ConfigからServiceのフォルダが見つかった！
        }
        else
        {
            // 見つからなければ自分の直下に置く（非常用）
            _jsonPath = "appsettings.json";
        }
    }

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
        foreach (var pair in _currentSettings.BackupSettings) _grid.Rows.Add(pair.Source, pair.Destination);
    }

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
            _currentSettings.BackupSettings.RemoveAt(_grid.SelectedRows[0].Index);
            RefreshGrid();
        }
    }

    private void OnExclusionsClick(object? s, EventArgs e)
    {
        using var form = new ExclusionForm(_currentSettings.GlobalExclusions);
        if (form.ShowDialog() == DialogResult.OK) _currentSettings.GlobalExclusions = form.Exclusions;
    }

    private void OnSaveClick(object? s, EventArgs e)
    {
        try
        {
            string json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            // もしフォルダがなければ作る（Service側がない場合の保険）
            string? dir = Path.GetDirectoryName(_jsonPath);
            if(dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_jsonPath, json);
            MessageBox.Show("設定を保存しました。サービスに自動反映されます。", "完了");
            this.Close();
        }
        catch (Exception ex) { MessageBox.Show("保存エラー: " + ex.Message); }
    }

    private void OnRunNowClick(object? s, EventArgs e)
    {
        try
        {
            string dir = Path.GetDirectoryName(_jsonPath) ?? "";
            if(string.IsNullOrEmpty(dir)) return;
            
            File.WriteAllText(Path.Combine(dir, "backup.trigger"), DateTime.Now.ToString());
            MessageBox.Show("実行命令を送りました！");
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }

private void OnViewLogClick(object? s, EventArgs e)
    {
        try
        {
            // ★Serviceと同じ場所（AppData/Local/MBack/Logs）を見に行く
            string logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                "MBack", 
                "Logs");

            // 今日の日付のファイル名 (Serilogの仕様: log-yyyyMMdd.txt)
            string logFileName = $"log-{DateTime.Now:yyyyMMdd}.txt";
            string logPath = Path.Combine(logFolder, logFileName);

            if (!File.Exists(logPath))
            {
                MessageBox.Show($"今日のログがまだありません。\n\n探した場所:\n{logPath}", "ログなし");
                return;
            }

            // 新しいログビューア画面を開く
            // (LogViewerFormのコードはそのままでOK)
            var viewer = new LogViewerForm(logPath);
            viewer.ShowDialog();
        }
        catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
    }
}