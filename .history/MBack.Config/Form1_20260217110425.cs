using System;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Forms;
using System.Drawing;

namespace MBack.Config
{

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
            this.Size = new Size(800, 500);
            this.StartPosition = FormStartPosition.Manual;

            _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

            SetupLayout();
            LoadSettings();
            UpdateGrid();
            LoadWindowState(); // ★ウィンドウサイズ復元
        }

        // --- レイアウト構築 ---
        private void SetupLayout()
        {
            _splitContainer.Dock = DockStyle.Fill;
            _splitContainer.Orientation = Orientation.Horizontal;
            _splitContainer.FixedPanel = FixedPanel.Panel2;
            _splitContainer.SplitterDistance = 400;
            _splitContainer.IsSplitterFixed = true;

            // グリッド
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

            // ボタンパネル
            _buttonPanel.Dock = DockStyle.Fill;
            _buttonPanel.FlowDirection = FlowDirection.LeftToRight;
            _buttonPanel.WrapContents = true;
            _buttonPanel.Padding = new Padding(10);
            _buttonPanel.AutoSize = true;
            _buttonPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // ボタン設定
            InitButton(_btnAdd, "追加", OnAddClick);
            InitButton(_btnEdit, "編集", OnEditClick);
            InitButton(_btnDelete, "削除", OnDeleteClick);
            InitButton(_btnExclusion, "除外設定", OnExclusionClick);
            InitButton(_btnLog, "ログを見る & 復元", OnLogClick, 140);
            InitButton(_btnService, "サービス管理", OnServiceClick, 140);

            _btnSave.Text = "保存して閉じる";
            _btnSave.Width = 140;
            _btnSave.Height = 30;
            _btnSave.Font = new Font(this.Font, FontStyle.Bold);
            _btnSave.Click += OnSaveClick;

            _buttonPanel.Controls.AddRange(new Control[]
            {
                _btnAdd, _btnEdit, _btnDelete, _btnExclusion, _btnLog, _btnService, _btnSave
            });

            _buttonPanel.SizeChanged += (s, e) => AdjustBottomPanelHeight();
            _splitContainer.Panel2.Controls.Add(_buttonPanel);

            this.Controls.Add(_splitContainer);

            // サービス状態更新タイマー
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += (s, e) => UpdateServiceButtonState();
            timer.Start();
            UpdateServiceButtonState();
        }

        private void InitButton(Button btn, string text, EventHandler handler, int width = 100)
        {
            btn.Text = text;
            btn.Width = width;
            btn.Height = 30;
            btn.Click += handler;
        }

        // --- ボタンエリア高さ調整 ---
        private void AdjustBottomPanelHeight()
        {
            int preferredHeight = _buttonPanel.PreferredSize.Height;
            int newDistance = this.ClientSize.Height - preferredHeight;

            if (newDistance > 50)
            {
                try { _splitContainer.SplitterDistance = newDistance; }
                catch { }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustBottomPanelHeight();
        }

        // --- ウィンドウ位置・サイズ保存 ---
        private void LoadWindowState()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\MBackConfig");
                if (key != null)
                {
                    int w = (int)(key.GetValue("Width") ?? 800);
                    int h = (int)(key.GetValue("Height") ?? 500);
                    int x = (int)(key.GetValue("X") ?? 100);
                    int y = (int)(key.GetValue("Y") ?? 100);

                    if (x < 0 || y < 0 ||
                        x > Screen.PrimaryScreen.Bounds.Width ||
                        y > Screen.PrimaryScreen.Bounds.Height)
                    {
                        x = 100; y = 100;
                    }

                    this.Size = new Size(w, h);
                    this.Location = new Point(x, y);
                }
            }
            catch { }
        }

        private void SaveWindowState()
        {
            try
            {
                if (this.WindowState == FormWindowState.Minimized) return;

                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\MBackConfig");
                if (key != null)
                {
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
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveWindowState();
            base.OnFormClosing(e);
        }

        // --- イベントハンドラ ---
        private void OnAddClick(object? sender, EventArgs e)
        {
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
            return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
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
                OnAddClick(null, EventArgs.Empty);
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
            using var form = new ExclusionForm(_globalExclusions);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _globalExclusions = form.Exclusions;
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
            SaveWindowState();
            this.Close();
        }

        // --- 設定読み書き ---
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
}
