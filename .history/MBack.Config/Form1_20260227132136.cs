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
        private Label _lblEmergency = new();
        private TabControl _tabControl = new();
        private DataGridView _grid = new();
        private Button _btnAdd = new();
        private Button _btnEdit = new();
        private Button _btnDelete = new();
        private Button _btnSave = new();
        private Button _btnLog = new();
        private Button _btnExclusion = new();
        private Button _btnMail = new(); 
        private Button _btnAdvanced = new(); 
        private Button _btnService = new();

        // ステータスバー用ラベル
        private Label _lblStatus = new();

        private List<BackupPair> _backupList = new();
        private List<string> _globalExclusions = new();
        private int _logRetentionDays = 60; 
        private int _ransomwareThreshold = 2000; 
        private string _maintStart = "00:00";
        private string _maintEnd = "00:00";
        private bool _sendSummary = false;
        private MailSettings _mailConfig = new();
        private string _configDir;
        private string _jsonPath;

        public Form1()
        {
            this.Text = "MBack 設定ツール (v2.0)";
            this.Size = new Size(800, 580); 
            this.StartPosition = FormStartPosition.Manual;

            _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack");
            if (!Directory.Exists(_configDir)) Directory.CreateDirectory(_configDir);
            _jsonPath = Path.Combine(_configDir, "appsettings.json");
            
            SetupLayout();
            LoadSettings();
            UpdateGrid();
            LoadWindowState();

            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (s, e) => UpdateServiceState();
            timer.Start();
            UpdateServiceState();
        }

        private void SetupLayout()
        {
            // 緊急停止警告ラベル
            _lblEmergency.Text = "【警告】ランサムウェア検知により緊急停止中！サービスを再起動して復旧してください";
            _lblEmergency.BackColor = Color.Red; _lblEmergency.ForeColor = Color.White;
            _lblEmergency.Dock = DockStyle.Top; _lblEmergency.Height = 35;
            _lblEmergency.TextAlign = ContentAlignment.MiddleCenter;
            _lblEmergency.Font = new Font(this.Font, FontStyle.Bold);
            _lblEmergency.Visible = false;

            // ステータスバー
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.FromArgb(240, 240, 240) };
            statusPanel.BorderStyle = BorderStyle.FixedSingle;
            _lblStatus.Text = "サービス状態を確認中...";
            _lblStatus.Dock = DockStyle.Fill;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Padding = new Padding(10, 0, 0, 0);
            statusPanel.Controls.Add(_lblStatus);

            // 保存ボタンパネル
            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10) };
            _btnSave.Text = "保存して閉じる"; _btnSave.Width = 140; _btnSave.Dock = DockStyle.Right;
            _btnSave.Font = new Font(this.Font, FontStyle.Bold);
            _btnSave.Click += OnSaveClick;
            bottomPanel.Controls.Add(_btnSave);

            _tabControl.Dock = DockStyle.Fill;

            // タブ1：バックアップ
            var tab1 = new TabPage("バックアップ設定");
            _grid.Dock = DockStyle.Fill;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false; _grid.ReadOnly = true; _grid.AllowUserToAddRows = false;
            _grid.Columns.Add("Source", "監視元フォルダ");
            _grid.Columns.Add("Dest", "バックアップ先");
            _grid.Columns.Add("User", "NAS認証(ユーザー)");
            _grid.Columns.Add("Cmd", "事前スクリプト");

            var tab1Btn = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(5) };
            InitButton(_btnAdd, "追加", OnAddClick);
            InitButton(_btnEdit, "編集", OnEditClick);
            InitButton(_btnDelete, "削除", OnDeleteClick);
            tab1Btn.Controls.AddRange(new Control[] { _btnAdd, _btnEdit, _btnDelete });
            tab1.Controls.Add(_grid); tab1.Controls.Add(tab1Btn);

            // タブ2：設定・通知
            var tab2 = new TabPage("除外設定・メール通知");
            var tab2Btn = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown };
            InitButton(_btnExclusion, "グローバル除外設定", OnExclusionClick, 250);
            InitButton(_btnMail, "メール通知設定", OnMailClick, 250);
            tab2Btn.Controls.AddRange(new Control[] { _btnExclusion, _btnMail });
            tab2.Controls.Add(tab2Btn);

            // タブ3：システム・保守
            var tab3 = new TabPage("システム詳細・保守管理");
            var tab3Btn = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown };
            InitButton(_btnAdvanced, "詳細設定 (メンテ時間・日報等)", OnAdvancedClick, 250);
            InitButton(_btnLog, "履歴とログを見る・復元する", OnLogClick, 250);
            InitButton(_btnService, "サービス管理 (停止/開始)", OnServiceClick, 250);
            tab3Btn.Controls.AddRange(new Control[] { _btnAdvanced, _btnLog, _btnService });
            tab3.Controls.Add(tab3Btn);

            _tabControl.TabPages.AddRange(new[] { tab1, tab2, tab3 });

            this.Controls.Add(_tabControl);
            this.Controls.Add(bottomPanel);
            this.Controls.Add(statusPanel); 
            this.Controls.Add(_lblEmergency);

            _lblEmergency.BringToFront();
            statusPanel.BringToFront();
            bottomPanel.BringToFront();
            _tabControl.BringToFront();
        }

        private void InitButton(Button btn, string text, EventHandler handler, int width = 100)
        {
            btn.Text = text; btn.Width = width; btn.Height = 35; btn.Click += handler;
        }

        private void UpdateServiceState()
        {
            string statusText = "";
            Color statusColor = Color.Black;

            try {
                using var sc = new System.ServiceProcess.ServiceController("MBackService");
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) {
                    statusText = "● MBackサービス: 実行中";
                    statusColor = Color.DarkGreen;
                    _btnService.Text = "サービス管理 (停止する)";
                    _btnService.BackColor = Color.LightGreen;
                } else {
                    statusText = "○ MBackサービス: 停止中";
                    statusColor = Color.Red;
                    _btnService.Text = "サービス管理 (開始する)";
                    _btnService.BackColor = Color.LightPink;
                }
            } catch { 
                statusText = "？ MBackサービス: 未インストールまたはエラー";
                statusColor = Color.Gray;
                _btnService.Text = "サービス: 状態不明";
                _btnService.BackColor = Color.LightGray;
            }

            string emergencyFile = Path.Combine(_configDir, "emergency.txt");
            if (File.Exists(emergencyFile)) {
                _lblEmergency.Visible = true;
                statusText += " 【！！緊急停止中！！】";
                statusColor = Color.Red;
            } else {
                _lblEmergency.Visible = false;
            }

            _lblStatus.Text = statusText;
            _lblStatus.ForeColor = statusColor;
        }

        private void LoadWindowState()
        {
            try {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\MBackConfig");
                if (key != null) {
                    int w = (int)(key.GetValue("Width") ?? 800); int h = (int)(key.GetValue("Height") ?? 580);
                    int x = (int)(key.GetValue("X") ?? 100); int y = (int)(key.GetValue("Y") ?? 100);
                    this.Size = new Size(w, h); this.Location = new Point(x, y);
                }
            } catch { }
        }

        private void SaveWindowState()
        {
            try {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\MBackConfig");
                if (key != null) {
                    key.SetValue("Width", this.Width); key.SetValue("Height", this.Height);
                    key.SetValue("X", this.Location.X); key.SetValue("Y", this.Location.Y);
                }
            } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { SaveWindowState(); base.OnFormClosing(e); }

        private void OnAddClick(object? sender, EventArgs e)
        {
            using var form = new BackupSettingForm(new BackupPair());
            if (form.ShowDialog() == DialogResult.OK) { _backupList.Add(form.Pair); UpdateGrid(); SaveSettings(); }
        }

        private void OnEditClick(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            int index = _grid.SelectedRows[0].Index;
            using var form = new BackupSettingForm(_backupList[index]);
            if (form.ShowDialog() == DialogResult.OK) { _backupList[index] = form.Pair; UpdateGrid(); SaveSettings(); }
        }

        private void OnDeleteClick(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            if (MessageBox.Show("設定を削除してもよろしいですか？", "削除確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                _backupList.RemoveAt(_grid.SelectedRows[0].Index); UpdateGrid(); SaveSettings();
            }
        }

        private void OnExclusionClick(object? sender, EventArgs e) { using var form = new ExclusionForm(_globalExclusions); if (form.ShowDialog() == DialogResult.OK) { _globalExclusions = form.Exclusions; SaveSettings(); } }
        private void OnMailClick(object? sender, EventArgs e) { using var form = new MailSettingsForm(_mailConfig); if (form.ShowDialog() == DialogResult.OK) { _mailConfig = form.Config; SaveSettings(); } }
        private void OnAdvancedClick(object? sender, EventArgs e)
        {
            using var form = new AdvancedSettingsForm(_logRetentionDays, _ransomwareThreshold, _maintStart, _maintEnd, _sendSummary);
            if (form.ShowDialog() == DialogResult.OK) {
                _logRetentionDays = form.LogRetentionDays; _ransomwareThreshold = form.RansomwareThreshold;
                _maintStart = form.MaintenanceStart; _maintEnd = form.MaintenanceEnd; _sendSummary = form.SendDailySummary;
                SaveSettings(); 
            }
        }

        private void OnLogClick(object? sender, EventArgs e) { using var logForm = new LogViewerForm(); logForm.ShowDialog(); }

        private void OnServiceClick(object? sender, EventArgs e)
        {
            try {
                var sc = new System.ServiceProcess.ServiceController("MBackService");
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) {
                    if (MessageBox.Show("サービスを停止しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                        sc.Stop(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        string emergencyFile = Path.Combine(_configDir, "emergency.txt");
                        if (File.Exists(emergencyFile)) File.Delete(emergencyFile);
                    }
                } else {
                    if (MessageBox.Show("サービスを開始しますか？", "確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                        sc.Start(); sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    }
                }
                UpdateServiceState();
            } catch (Exception ex) { MessageBox.Show("操作失敗: \n" + ex.Message); }
        }

        private void OnSaveClick(object? sender, EventArgs e) { SaveSettings(); SaveWindowState(); this.Close(); }

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
                    _maintStart = settings.MaintenanceStart ?? "00:00";
                    _maintEnd = settings.MaintenanceEnd ?? "00:00";
                    _sendSummary = settings.SendDailySummary;
                    _mailConfig = settings.MailConfig ?? new(); 
                }
            } catch { }
        }

        private void SaveSettings()
        {
            try {
                var settings = new AppSettingsRaw {
                    BackupSettings = _backupList, GlobalExclusions = _globalExclusions,
                    LogRetentionDays = _logRetentionDays, RansomwareThreshold = _ransomwareThreshold,
                    MaintenanceStart = _maintStart, MaintenanceEnd = _maintEnd, SendDailySummary = _sendSummary,
                    MailConfig = _mailConfig 
                };
                File.WriteAllText(_jsonPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            } catch { }
        }

        private void UpdateGrid()
        {
            _grid.Rows.Clear(); 
            foreach (var p in _backupList) {
                string userDisp = string.IsNullOrEmpty(p.UserName) ? "なし" : p.UserName;
                string cmdDisp = string.IsNullOrEmpty(p.PreCommand) ? "なし" : Path.GetFileName(p.PreCommand);
                _grid.Rows.Add(p.Source, p.Destination, userDisp, cmdDisp);
            }
        }
    }

    // --- ここから下が前回省略してしまった部分です ---

    public class BackupSettingForm : Form
    {
        public BackupPair Pair { get; private set; }
        private TextBox _txtSrc = new();
        private TextBox _txtDest = new();
        private TextBox _txtUser = new();
        private TextBox _txtPass = new() { PasswordChar = '*' };
        private TextBox _txtCmd = new();

        public BackupSettingForm(BackupPair current)
        {
            Pair = new BackupPair { 
                Source = current.Source, Destination = current.Destination, 
                UserName = current.UserName, Password = current.Password, PreCommand = current.PreCommand 
            };
            this.Text = "バックアップ詳細設定";
            this.Size = new Size(500, 350);
            this.StartPosition = FormStartPosition.CenterParent;
            SetupLayout();
        }

        private void SetupLayout()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(15) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));

            int row = 0;
            AddRow(panel, ref row, "監視元フォルダ:", _txtSrc, Pair.Source, true);
            AddRow(panel, ref row, "バックアップ先:", _txtDest, Pair.Destination, true);
            
            var lblAuth = new Label { Text = "\n--- NAS等 ネットワーク認証 (任意) ---", ForeColor = Color.Gray, AutoSize = true };
            panel.Controls.Add(lblAuth, 0, row); panel.SetColumnSpan(lblAuth, 3); row++;
            
            AddRow(panel, ref row, "ユーザー名:", _txtUser, Pair.UserName, false);
            AddRow(panel, ref row, "パスワード:", _txtPass, Pair.Password, false);

            var lblCmd = new Label { Text = "\n--- 事前実行スクリプト (任意) ---", ForeColor = Color.Gray, AutoSize = true };
            panel.Controls.Add(lblCmd, 0, row); panel.SetColumnSpan(lblCmd, 3); row++;
            AddRow(panel, ref row, "バッチ(.bat)パス:", _txtCmd, Pair.PreCommand, true, true);

            var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 45, Padding = new Padding(5) };
            var btnCancel = new Button { Text = "キャンセル", Width = 90 };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            var btnOk = new Button { Text = "OK", Width = 90 };
            btnOk.Click += (s, e) => { 
                if (string.IsNullOrWhiteSpace(_txtSrc.Text) || string.IsNullOrWhiteSpace(_txtDest.Text)) {
                    MessageBox.Show("元と先は必須です。"); return;
                }
                Pair.Source = _txtSrc.Text; Pair.Destination = _txtDest.Text; 
                Pair.UserName = _txtUser.Text; Pair.Password = _txtPass.Text; Pair.PreCommand = _txtCmd.Text;
                DialogResult = DialogResult.OK; Close(); 
            };
            btnPanel.Controls.Add(btnCancel); btnPanel.Controls.Add(btnOk);
            this.Controls.Add(panel); this.Controls.Add(btnPanel);
        }

        private void AddRow(TableLayoutPanel panel, ref int row, string label, TextBox txt, string val, bool showBrowse, bool isFile = false)
        {
            panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            txt.Text = val; txt.Dock = DockStyle.Fill;
            panel.Controls.Add(txt, 1, row);
            if (showBrowse) {
                var btn = new Button { Text = "参照", Dock = DockStyle.Fill };
                btn.Click += (s, e) => {
                    if (isFile) {
                        using var dlg = new OpenFileDialog { Filter = "実行ファイル|*.bat;*.exe;*.cmd|すべて|*.*" };
                        if (dlg.ShowDialog() == DialogResult.OK) txt.Text = dlg.FileName;
                    } else {
                        using var dlg = new FolderBrowserDialog();
                        if (dlg.ShowDialog() == DialogResult.OK) txt.Text = dlg.SelectedPath;
                    }
                };
                panel.Controls.Add(btn, 2, row);
            }
            row++;
        }
    }
}