using System;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Forms;
using System.Drawing;
using System.IO;
using System.Collections.Generic;

namespace MBack.Config
{
    public partial class Form1 : Form
    {
        private DataGridView _grid = new();
        private Button _btnAdd = new();
        private Button _btnEdit = new();
        private Button _btnDelete = new();
        private Button _btnSave = new();
        private Button _btnLog = new();
        private Button _btnExclusion = new();
        private Button _btnMail = new(); 
        private Button _btnAdvanced = new(); // ★ここが抜けていたので直しました！
        private Button _btnService = new();
        private FlowLayoutPanel _buttonPanel = new();
        private SplitContainer _splitContainer = new();

        private List<BackupPair> _backupList = new();
        private List<string> _globalExclusions = new();
        private int _logRetentionDays = 60; // ログ保存日数
        private int _ransomwareThreshold = 2000; // ランサム対策の閾値(件/60秒)
        private MailSettings _mailConfig = new();
        private string _jsonPath;

        public Form1()
        {
            this.Text = "MBack 設定ツール";
            this.Size = new Size(800, 500);
            this.StartPosition = FormStartPosition.Manual;

            // 絶対に消えない「ProgramData」フォルダを使用する
            string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack");
            if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
            _jsonPath = Path.Combine(configDir, "appsettings.json");
            
            // ★ここから追加：旧バージョンからの設定データ引き継ぎ
            string oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(_jsonPath) && File.Exists(oldPath))
            {
                File.Copy(oldPath, _jsonPath);
            }
            // ★ここまで
            SetupLayout();
            LoadSettings();
            UpdateGrid();
            LoadWindowState();
        }

        private void SetupLayout()
        {
            _splitContainer.Dock = DockStyle.Fill;
            _splitContainer.Orientation = Orientation.Horizontal;
            _splitContainer.FixedPanel = FixedPanel.Panel2;
            _splitContainer.SplitterDistance = 400;
            _splitContainer.IsSplitterFixed = true;

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

            _buttonPanel.Dock = DockStyle.Fill;
            _buttonPanel.FlowDirection = FlowDirection.LeftToRight;
            _buttonPanel.WrapContents = true;
            _buttonPanel.Padding = new Padding(10);
            _buttonPanel.AutoSize = true;
            _buttonPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;

            InitButton(_btnAdd, "追加", OnAddClick);
            InitButton(_btnEdit, "編集", OnEditClick);
            InitButton(_btnDelete, "削除", OnDeleteClick);
            InitButton(_btnExclusion, "除外設定", OnExclusionClick);
            InitButton(_btnMail, "メール設定", OnMailClick, 100); 
            InitButton(_btnAdvanced, "詳細設定", OnAdvancedClick, 100); 
            InitButton(_btnLog, "ログを見る & 復元", OnLogClick, 140);
            InitButton(_btnService, "サービス管理", OnServiceClick, 140);

            _btnSave.Text = "保存して閉じる";
            _btnSave.Width = 140;
            _btnSave.Height = 30;
            _btnSave.Font = new Font(this.Font, FontStyle.Bold);
            _btnSave.Click += OnSaveClick;

            _buttonPanel.Controls.AddRange(new Control[]
            {
                _btnAdd, _btnEdit, _btnDelete, _btnExclusion, _btnMail, _btnAdvanced, _btnLog, _btnService, _btnSave
            });

            _buttonPanel.SizeChanged += (s, e) => AdjustBottomPanelHeight();
            _splitContainer.Panel2.Controls.Add(_buttonPanel);
            this.Controls.Add(_splitContainer);

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 2000;
            timer.Tick += (s, e) => UpdateServiceButtonState();
            timer.Start();
            UpdateServiceButtonState();
        }

        private void OnAdvancedClick(object? sender, EventArgs e)
        {
            using var form = new AdvancedSettingsForm(_logRetentionDays, _ransomwareThreshold);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _logRetentionDays = form.LogRetentionDays;
                _ransomwareThreshold = form.RansomwareThreshold;
            }
        }

        private void InitButton(Button btn, string text, EventHandler handler, int width = 100)
        {
            btn.Text = text; btn.Width = width; btn.Height = 30; btn.Click += handler;
        }

        private void AdjustBottomPanelHeight()
        {
            int preferredHeight = _buttonPanel.PreferredSize.Height;
            int newDistance = this.ClientSize.Height - preferredHeight;
            if (newDistance > 50) { try { _splitContainer.SplitterDistance = newDistance; } catch { } }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); AdjustBottomPanelHeight(); }

        private void LoadWindowState()
        {
            try {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\MBackConfig");
                if (key != null) {
                    int w = (int)(key.GetValue("Width") ?? 800); int h = (int)(key.GetValue("Height") ?? 500);
                    int x = (int)(key.GetValue("X") ?? 100); int y = (int)(key.GetValue("Y") ?? 100);
                    if (x < 0 || y < 0 || x > (Screen.PrimaryScreen?.Bounds.Width ?? 800) || y > (Screen.PrimaryScreen?.Bounds.Height ?? 600)) { x = 100; y = 100; }
                    this.Size = new Size(w, h); this.Location = new Point(x, y);
                }
            } catch { }
        }

        private void SaveWindowState()
        {
            try {
                if (this.WindowState == FormWindowState.Minimized) return;
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\MBackConfig");
                if (key != null) {
                    if (this.WindowState == FormWindowState.Maximized) {
                        key.SetValue("Width", this.RestoreBounds.Width); key.SetValue("Height", this.RestoreBounds.Height);
                        key.SetValue("X", this.RestoreBounds.Location.X); key.SetValue("Y", this.RestoreBounds.Location.Y);
                    } else {
                        key.SetValue("Width", this.Width); key.SetValue("Height", this.Height);
                        key.SetValue("X", this.Location.X); key.SetValue("Y", this.Location.Y);
                    }
                }
            } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { SaveWindowState(); base.OnFormClosing(e); }

        private void OnAddClick(object? sender, EventArgs e)
        {
            string? src = SelectFolder("監視するフォルダを選んでください"); if (src == null) return;
            string? dest = SelectFolder($"[{Path.GetFileName(src)}] のバックアップ先を選んでください"); if (dest == null) return;
            _backupList.Add(new BackupPair { Source = src, Destination = dest }); UpdateGrid();
        }

        private string? SelectFolder(string title)
        {
            using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true };
            return dlg.ShowDialog() == DialogResult.OK ? dlg.SelectedPath : null;
        }

        private void OnEditClick(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            int index = _grid.SelectedRows[0].Index; var pair = _backupList[index];
            if (MessageBox.Show($"設定を編集しますか？\n一旦削除して追加し直す形になります。\n\n現在の設定:\n元: {pair.Source}", "編集", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                _backupList.RemoveAt(index); UpdateGrid(); OnAddClick(null, EventArgs.Empty);
            }
        }

        private void OnDeleteClick(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            int index = _grid.SelectedRows[0].Index;
            if (MessageBox.Show("設定を削除してもよろしいですか？", "削除確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                _backupList.RemoveAt(index); UpdateGrid();
            }
        }

        private void OnExclusionClick(object? sender, EventArgs e)
        {
            using var form = new ExclusionForm(_globalExclusions);
            if (form.ShowDialog() == DialogResult.OK) _globalExclusions = form.Exclusions;
        }

        private void OnMailClick(object? sender, EventArgs e)
        {
            using var form = new MailSettingsForm(_mailConfig);
            if (form.ShowDialog() == DialogResult.OK) _mailConfig = form.Config;
        }

        private void OnLogClick(object? sender, EventArgs e) { using var logForm = new LogViewerForm(); logForm.ShowDialog(); }

        private void OnServiceClick(object? sender, EventArgs e)
        {
            try {
                var sc = new System.ServiceProcess.ServiceController("MBackService");
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) {
                    if (MessageBox.Show("サービスを停止しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                        sc.Stop(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }
                } else {
                    if (MessageBox.Show("サービスを開始しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                        sc.Start(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
                UpdateServiceButtonState();
            } catch (Exception ex) { MessageBox.Show("操作失敗 (管理者として実行してください): \n" + ex.Message); }
        }

        private void UpdateServiceButtonState()
        {
            try {
                using var sc = new System.ServiceProcess.ServiceController("MBackService");
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) {
                    _btnService.Text = "サービス: 実行中"; _btnService.BackColor = Color.LightGreen;
                } else {
                    _btnService.Text = "サービス: 停止中"; _btnService.BackColor = Color.LightPink;
                }
            } catch { _btnService.Text = "サービス: 不明"; _btnService.BackColor = Color.LightGray; }
        }

        private void OnSaveClick(object? sender, EventArgs e)
        {
            SaveSettings(); SaveWindowState(); this.Close();
        }

        private void LoadSettings()
        {
            if (!File.Exists(_jsonPath)) return;
            try {
                var json = File.ReadAllText(_jsonPath);
                var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (settings != null) {
                    _backupList = settings.BackupSettings ?? new();
                    _globalExclusions = settings.GlobalExclusions ?? new();
                    
                    _logRetentionDays = settings.LogRetentionDays > 0 ? settings.LogRetentionDays : 60;
                    _ransomwareThreshold = settings.RansomwareThreshold > 0 ? settings.RansomwareThreshold : 2000;
                    
                    _mailConfig = settings.MailConfig ?? new(); 
                }
            } catch { }
        }

        private void SaveSettings()
        {
            try {
                var settings = new AppSettingsRaw {
                    BackupSettings = _backupList, 
                    GlobalExclusions = _globalExclusions,
                    LogRetentionDays = _logRetentionDays, 
                    RansomwareThreshold = _ransomwareThreshold,
                    MailConfig = _mailConfig 
                };
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_jsonPath, JsonSerializer.Serialize(settings, options));
                MessageBox.Show("設定を保存しました。");
            } catch (Exception ex) { MessageBox.Show("保存に失敗しました: " + ex.Message); }
        }

        private void UpdateGrid()
        {
            _grid.Rows.Clear(); foreach (var pair in _backupList) _grid.Rows.Add(pair.Source, pair.Destination);
        }
    }
}