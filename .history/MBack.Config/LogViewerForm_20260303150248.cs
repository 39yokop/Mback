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
        this.Text = "MBack 履歴復元センター (SQLite・完全均等・プレビュー対応版)";
        this.Size = new Size(1200, 750); 
        this.StartPosition = FormStartPosition.CenterParent;

        _dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "Database", "history.db");

        EnsureDatabaseSchema();
        LoadSettings();
        SetupLayout();
        this.Shown += (s, e) => FixSplitterLayout();
        LoadDateListFromDb(); 
    }

    private void EnsureDatabaseSchema()
    {
        if (!File.Exists(_dbPath)) return;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = new SqliteCommand("ALTER TABLE LogEntries ADD COLUMN User TEXT;", conn);
            cmd.ExecuteNonQuery();
        } catch { }
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

        var menuVersion = new ToolStripMenuItem("★ 世代選択と復元...", null, OnShowVersionsClick) { Font = new Font(this.Font, FontStyle.Bold) };
        var menuRestore = new ToolStripMenuItem("最新から即時復元", null, (s, e) => RestoreFile(null));
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
        int uniqueCopyCount = 0; long sizeTotal = 0;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}"); conn.Open();
            string sql = @"
                SELECT Type, Path, MAX(Size), MAX(Message), MAX(User), MAX(Time), COUNT(Id) 
                FROM LogEntries 
                WHERE date(Time) = @date 
                GROUP BY Type, Path 
                ORDER BY MAX(Time) ASC";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@date", dateStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                string type = reader.GetString(0); string path = reader.GetString(1);
                long sz = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                string msg = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string user = reader.IsDBNull(4) ? "System" : reader.GetString(4);
                DateTime time = reader.GetDateTime(5);
                int updateCount = reader.GetInt32(6);

                string updateInfo = updateCount > 1 ? $"[計{updateCount}回更新 | 最新 {time:HH:mm:ss}]" : $"[{time:HH:mm:ss}]";
                string display = $"{updateInfo} {FormatSize(sz)} ({user})";

                switch (type) {
                    case "Copy": AddPathToTree(_treeCopy, path, display); uniqueCopyCount++; sizeTotal += sz; break;
                    case "Delete": AddPathToTree(_treeDelete, path, $"{updateInfo} ({user})"); break;
                    case "Error": AddPathToTree(_treeError, path, $"{updateInfo} {msg}"); break;
                }
            }
        } catch { } finally {
            _treeCopy.EndUpdate(); _treeDelete.EndUpdate(); _treeError.EndUpdate();
            foreach (TreeView t in new[] { _treeCopy, _treeDelete, _treeError }) foreach (TreeNode n in t.Nodes) n.Expand();
        }
        _lblSummary.Text = $"[対象ファイル] {uniqueCopyCount}個 (最新合計: {FormatSize(sizeTotal)})";
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

        using var f = new Form { Text = "世代選択: " + Path.GetFileName(path), Size = new Size(650, 450), StartPosition = FormStartPosition.CenterParent };
        
        var grid = new DataGridView {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White
        };
        grid.Columns.Add("Type", "種別"); grid.Columns.Add("Date", "バックアップ日時"); grid.Columns.Add("Size", "サイズ");
        grid.Columns["Type"].Width = 60; grid.Columns["Size"].Width = 80;

        foreach (var v in vers) {
            var info = new FileInfo(v);
            string type = (v == bPath) ? "最新" : "履歴";
            grid.Rows.Add(type, info.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"), FormatSize(info.Length));
        }

        // ★改良：下部のボタンエリアをパネルで2分割し、「確認用保存」ボタンを追加
        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(5) };
        var btnExport = new Button { Text = "🔍 別の場所に保存して確認", Dock = DockStyle.Right, Width = 280, Font = new Font(f.Font, FontStyle.Bold), BackColor = Color.LightYellow };
        var btnRestore = new Button { Text = "⚠️ 元の場所に上書き復元", Dock = DockStyle.Left, Width = 280, Font = new Font(f.Font, FontStyle.Bold), BackColor = Color.LightPink };

        btnRestore.Click += (s, ev) => { 
            if (grid.SelectedRows.Count > 0) { 
                RestoreFile(vers[grid.SelectedRows[0].Index], path); 
                f.DialogResult = DialogResult.OK; 
            } 
        };

        // ★新機能：任意の場所にエクスポートして開く機能
        btnExport.Click += (s, ev) => {
            if (grid.SelectedRows.Count > 0) {
                ExportForPreview(vers[grid.SelectedRows[0].Index], path);
            }
        };

        grid.CellDoubleClick += (s, ev) => {
            if (ev.RowIndex >= 0) {
                RestoreFile(vers[ev.RowIndex], path); // ダブルクリックは引き続き上書き復元（確認画面あり）
                f.DialogResult = DialogResult.OK;
            }
        };

        btnPanel.Controls.Add(btnRestore); btnPanel.Controls.Add(btnExport);
        f.Controls.Add(grid); f.Controls.Add(btnPanel); 
        f.ShowDialog();
    }

    /// <summary>
    /// ★新機能：プレビュー確認用に、選択した世代を任意の場所に保存して開く
    /// </summary>
    private void ExportForPreview(string versionPath, string originalPath)
    {
        using var sfd = new SaveFileDialog();
        sfd.FileName = Path.GetFileName(originalPath);
        sfd.Title = "確認用に保存する場所を選んでください（デスクトップ推奨）";
        sfd.Filter = "すべてのファイル (*.*)|*.*";
        
        if (sfd.ShowDialog() == DialogResult.OK)
        {
            try {
                File.Copy(versionPath, sfd.FileName, true);
                if (MessageBox.Show("保存しました。今すぐ開いて中身を確認しますか？", "プレビュー確認", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    // 関連付けられたアプリケーション（Excel, Access等）で自動的に開く
                    Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
                }
            } catch (Exception ex) {
                MessageBox.Show("保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void RestoreFile(string? vPath, string? tPath = null)
    {
        var node = _treeCopy.SelectedNode ?? _treeDelete.SelectedNode;
        string path = tPath ?? node?.Tag as string ?? "";
        string? bPath = vPath ?? GetBackupPath(path);
        if (string.IsNullOrEmpty(path) || bPath == null || !File.Exists(bPath)) return;
        
        // 誤操作防止の防波堤
        if (MessageBox.Show($"⚠️警告⚠️\n以下のファイルをバックアップデータで【上書き】します。\n本当によろしいですか？\n\n対象: {path}", "上書き復元の最終確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) {
            try {
                string? d = Path.GetDirectoryName(path); if (d != null && !Directory.Exists(d)) Directory.CreateDirectory(d);
                File.Copy(bPath, path, true); MessageBox.Show("正常に復元されました！", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            } catch (Exception ex) { MessageBox.Show("復元エラー: " + ex.Message); }
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
        if (last != null) { last.Text += $"  {info}"; last.Tag = fullPath; } 
    }

    private string FormatSize(long b) { string[] s = { "B", "KB", "MB", "GB", "TB" }; double l = b; int i = 0; while (l >= 1024 && i < 4) { i++; l /= 1024; } return $"{l:0.##} {s[i]}"; }

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