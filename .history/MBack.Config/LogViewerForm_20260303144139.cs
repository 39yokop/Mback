using System;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MBack.Config;

public class LogViewerForm : Form
{
    private ComboBox _dateSelector = new();
    private Label _lblSummary = new();
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();
    private ContextMenuStrip _contextMenu = new();
    private Button _btnRefresh = new(); 

    private SplitContainer _splitMain = new(); 
    private SplitContainer _splitSub = new();  

    private string _dbPath;
    private List<BackupPair> _backupPairs = new(); 

    public LogViewerForm()
    {
        this.Text = "MBack 履歴復元センター (SQLite・完全均等・自己修復版)";
        this.Size = new Size(1200, 750); 
        this.StartPosition = FormStartPosition.CenterParent;

        _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "Database", "history.db");

        // ★追加：UIを開いた瞬間にデータベースのスキーマ(列)が最新かチェックし、不足があれば補う
        EnsureDatabaseSchema();

        LoadSettings();
        SetupLayout();
        this.Shown += (s, e) => FixSplitterLayout();
        LoadDateListFromDb(); 
    }

    /// <summary>
    /// データベースに User カラムが存在するか確認し、無ければ追加する安全装置
    /// </summary>
    private void EnsureDatabaseSchema()
    {
        if (!File.Exists(_dbPath)) return;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            // 既存のDBにUserカラムがない場合、ここで追加する
            using var cmd = new SqliteCommand("ALTER TABLE LogEntries ADD COLUMN User TEXT;", conn);
            cmd.ExecuteNonQuery();
        } catch { 
            // すでにカラムが存在する場合はエラーになるが、握りつぶして正常進行する
        }
    }

    private void SetupLayout()
    {
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10), BackColor = Color.FromArgb(240, 240, 240) };
        var lblDate = new Label { Text = "日付選択:", AutoSize = true, Location = new Point(15, 20), Font = new Font(this.Font, FontStyle.Bold) };
        _dateSelector.Location = new Point(100, 17); _dateSelector.Width = 150; _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChanged;

        _btnRefresh.Text = "最新の状態に更新"; _btnRefresh.Location = new Point(260, 15); _btnRefresh.Size = new Size(120, 30);
        _btnRefresh.Click += (s, e) => {
            LoadDateListFromDb(); 
            if (_dateSelector.SelectedItem?.ToString() is string date) LoadLogFromDb(date);
        };
        
        _lblSummary.Location = new Point(400, 20); _lblSummary.AutoSize = true;
        topPanel.Controls.AddRange(new Control[] { lblDate, _dateSelector, _btnRefresh, _lblSummary });

        _splitMain.Dock = DockStyle.Fill; _splitMain.BorderStyle = BorderStyle.Fixed3D; _splitMain.Panel1MinSize = 50; _splitMain.Panel2MinSize = 100;
        _splitSub.Dock = DockStyle.Fill; _splitSub.BorderStyle = BorderStyle.Fixed3D; _splitSub.Panel1MinSize = 50; _splitSub.Panel2MinSize = 50;

        _splitMain.Panel1.Controls.Add(CreateTreeGroup("コピー済み", _treeCopy, Color.DarkBlue));
        _splitMain.Panel2.Controls.Add(_splitSub);
        _splitSub.Panel1.Controls.Add(CreateTreeGroup("ゴミ箱 (削除)", _treeDelete, Color.DarkRed));
        _splitSub.Panel2.Controls.Add(CreateTreeGroup("エラーログ", _treeError, Color.Red));

        var menuVersion = new ToolStripMenuItem("★ 全世代を表示...", null, OnShowVersionsClick) { Font = new Font(this.Font, FontStyle.Bold) };
        var menuRestore = new ToolStripMenuItem("最新から復元", null, (s, e) => RestoreFile(null));
        var menuOpen = new ToolStripMenuItem("バックアップ先を開く", null, OnOpenBackupFolderClick);
        _contextMenu.Items.AddRange(new ToolStripItem[] { menuVersion, new ToolStripSeparator(), menuRestore, menuOpen });
        _treeCopy.ContextMenuStrip = _contextMenu; _treeDelete.ContextMenuStrip = _contextMenu;

        this.Controls.Add(_splitMain); this.Controls.Add(topPanel);
    }

    private void FixSplitterLayout()
    {
        try {
            int totalW = _splitMain.Width;
            if (totalW > 300) {
                _splitMain.SplitterDistance = totalW / 3;
                _splitSub.SplitterDistance = _splitMain.Panel2.Width / 2;
            }
        } catch { }
    }

    private void LoadDateListFromDb()
    {
        if (!File.Exists(_dbPath)) return;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
            string sql = "SELECT DISTINCT strftime('%Y-%m-%d', Time) as LogDate FROM LogEntries ORDER BY LogDate DESC";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            string? sel = _dateSelector.SelectedItem?.ToString();
            _dateSelector.Items.Clear();
            while (reader.Read()) _dateSelector.Items.Add(reader.GetString(0));
            if (_dateSelector.Items.Count > 0) {
                int idx = sel != null ? _dateSelector.FindStringExact(sel) : 0;
                _dateSelector.SelectedIndex = idx >= 0 ? idx : 0;
            }
        } catch { }
    }

    private void OnDateChanged(object? sender, EventArgs e) { if (_dateSelector.SelectedItem is string d) LoadLogFromDb(d); }

    private void LoadLogFromDb(string dateStr)
    {
        _treeCopy.Nodes.Clear(); _treeDelete.Nodes.Clear(); _treeError.Nodes.Clear();
        _treeCopy.BeginUpdate(); _treeDelete.BeginUpdate(); _treeError.BeginUpdate();
        int countCopy = 0; long sizeTotal = 0;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
            string sql = "SELECT Type, Path, Size, Message, User, Time FROM LogEntries WHERE date(Time) = @date ORDER BY Time ASC";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@date", dateStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                string type = reader.GetString(0); string path = reader.GetString(1);
                long sz = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                string msg = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string user = reader.IsDBNull(4) ? "System" : reader.GetString(4);
                DateTime time = reader.GetDateTime(5);

                string display = $"[{time:HH:mm:ss}] {FormatSize(sz)} ({user})";

                switch (type) {
                    case "Copy": AddPathToTree(_treeCopy, path, display); countCopy++; sizeTotal += sz; break;
                    case "Delete": AddPathToTree(_treeDelete, path, $"[{time:HH:mm:ss}] ({user})"); break;
                    case "Error": AddPathToTree(_treeError, path, $"[{time:HH:mm:ss}] {msg}"); break;
                }
            }
        } catch { } finally {
            _treeCopy.EndUpdate(); _treeDelete.EndUpdate(); _treeError.EndUpdate();
            foreach (TreeView t in new[] { _treeCopy, _treeDelete, _treeError }) foreach (TreeNode n in t.Nodes) n.Expand();
        }
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeTotal)})";
    }

    private void OnShowVersionsClick(object? sender, EventArgs e)
    {
        TreeView t = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (t.SelectedNode?.Tag is not string path) return;
        string? bPath = GetBackupPath(path);
        if (bPath == null) return;
        var vers = new List<string>(); if (File.Exists(bPath)) vers.Add(bPath);
        for (int i = 1; i <= 50; i++) { string v = $"{bPath}.v{i}"; if (File.Exists(v)) vers.Add(v); }
        if (vers.Count == 0) return;
        using var f = new Form { Text = "世代選択", Size = new Size(500, 400), StartPosition = FormStartPosition.CenterParent };
        var lb = new ListBox { Dock = DockStyle.Fill };
        foreach (var v in vers) lb.Items.Add($"{(v == bPath ? "[最新]" : "[履歴]")} {new FileInfo(v).LastWriteTime:yyyy/MM/dd HH:mm:ss}");
        var btn = new Button { Text = "復元", Dock = DockStyle.Bottom, Height = 40 };
        btn.Click += (s, ev) => { if (lb.SelectedIndex >= 0) { RestoreFile(vers[lb.SelectedIndex], path); f.DialogResult = DialogResult.OK; } };
        f.Controls.Add(lb); f.Controls.Add(btn); f.ShowDialog();
    }

    private void RestoreFile(string? vPath, string? tPath = null)
    {
        var node = _treeCopy.SelectedNode ?? _treeDelete.SelectedNode;
        string path = tPath ?? node?.Tag as string ?? "";
        string? bPath = vPath ?? GetBackupPath(path);
        if (string.IsNullOrEmpty(path) || bPath == null || !File.Exists(bPath)) return;
        if (MessageBox.Show($"復元しますか？\n{path}", "復元", MessageBoxButtons.YesNo) == DialogResult.Yes) {
            try {
                string? d = Path.GetDirectoryName(path); if (d != null && !Directory.Exists(d)) Directory.CreateDirectory(d);
                File.Copy(bPath, path, true); MessageBox.Show("完了！");
            } catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
        }
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        TreeView t = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (t.SelectedNode?.Tag is not string p) return;
        string? b = GetBackupPath(p); if (b != null) { string? f = Path.GetDirectoryName(b); if (f != null && Directory.Exists(f)) Process.Start("explorer.exe", f); }
    }

    private string? GetBackupPath(string p)
    {
        foreach (var pair in _backupPairs) {
            if (p.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase)) {
                string r = p.Substring(pair.Source.Length).TrimStart('\\');
                string n = Path.Combine(pair.Destination, r);
                if (File.Exists(n) || (Directory.Exists(Path.GetDirectoryName(n)) && Directory.GetParent(n)?.GetFiles(Path.GetFileName(n) + ".v*").Length > 0)) return n;
                return Path.Combine(pair.Destination, "_TRASH_", r);
            }
        }
        return null;
    }

    private void AddPathToTree(TreeView tree, string fullPath, string info)
    {
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        TreeNodeCollection nodes = tree.Nodes; TreeNode? last = null;
        foreach (var p in parts.Where(s => !string.IsNullOrEmpty(s))) {
            TreeNode? found = null; foreach (TreeNode n in nodes) if (n.Text == p) found = n;
            if (found != null) { nodes = found.Nodes; last = found; } else { last = nodes.Add(p); nodes = last.Nodes; }
        }
        if (last != null) { last.Text += $"  [{info}]"; last.Tag = fullPath; }
    }

    private string FormatSize(long b) { string[] s = { "B", "KB", "MB", "GB" }; double l = b; int i = 0; while (l >= 1024 && i < 3) { i++; l /= 1024; } return $"{l:0.##} {s[i]}"; }

    private GroupBox CreateTreeGroup(string t, TreeView tv, Color c) {
        var g = new GroupBox { Text = t, Dock = DockStyle.Fill, ForeColor = c }; tv.Dock = DockStyle.Fill;
        tv.NodeMouseClick += (s, e) => tv.SelectedNode = e.Node; g.Controls.Add(tv); return g;
    }

    private void LoadSettings() {
        try {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "appsettings.json");
            if (File.Exists(p)) { var s = JsonSerializer.Deserialize<AppSettingsRaw>(File.ReadAllText(p)); if (s != null) _backupPairs = s.BackupSettings; }
        } catch { }
    }
}