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
    // --- UI部品 ---
    private ComboBox _dateSelector = new();
    private Label _lblSummary = new();
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();
    private ContextMenuStrip _contextMenu = new();

    private string _dbPath;
    private List<BackupPair> _backupPairs = new(); 

    public LogViewerForm()
    {
        this.Text = "MBack 履歴復元センター (SQLite版)";
        this.Size = new Size(1100, 750);
        this.StartPosition = FormStartPosition.CenterParent;

        // サービス側と共通のデータベースパス（ProgramData）
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MBack", "Database", "history.db");

        LoadSettings();
        SetupLayout();
        LoadDateListFromDb(); // ファイルではなくDBから日付一覧を取得
    }

    private void SetupLayout()
    {
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10), BackColor = Color.FromArgb(240, 240, 240) };
        var lblDate = new Label { Text = "1. 日付で探す:", AutoSize = true, Location = new Point(15, 20), Font = new Font(this.Font, FontStyle.Bold) };
        
        _dateSelector.Location = new Point(110, 17);
        _dateSelector.Width = 200;
        _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChanged;
        
        _lblSummary.Location = new Point(330, 20);
        _lblSummary.AutoSize = true;

        topPanel.Controls.Add(lblDate);
        topPanel.Controls.Add(_dateSelector);
        topPanel.Controls.Add(_lblSummary);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(5) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        table.Controls.Add(CreateTreeGroup("コピー済み (右クリックで全世代表示)", _treeCopy, Color.DarkBlue), 0, 0);
        table.Controls.Add(CreateTreeGroup("ゴミ箱 (削除されたファイル)", _treeDelete, Color.DarkRed), 1, 0);
        table.Controls.Add(CreateTreeGroup("エラーログ", _treeError, Color.Red), 2, 0);

        // コンテキストメニュー（既存ロジックを継承）
        var menuVersion = new ToolStripMenuItem("★ このファイルの全世代(最大50件)を表示...", null, OnShowVersionsClick);
        menuVersion.Font = new Font(menuVersion.Font, FontStyle.Bold);
        var menuRestore = new ToolStripMenuItem("最新のバックアップから復元", null, (s, e) => RestoreFile(null));
        var menuOpen = new ToolStripMenuItem("バックアップ先を開く", null, OnOpenBackupFolderClick);

        _contextMenu.Items.AddRange(new ToolStripItem[] { menuVersion, new ToolStripSeparator(), menuRestore, menuOpen });
        _treeCopy.ContextMenuStrip = _contextMenu;
        _treeDelete.ContextMenuStrip = _contextMenu;

        this.Controls.Add(table);
        this.Controls.Add(topPanel);
    }

    // --- データベース連携ロジック ---

    /// <summary>
    /// データベースに存在するログの日付一覧を取得してコンボボックスに詰める
    /// </summary>
    private void LoadDateListFromDb()
    {
        if (!File.Exists(_dbPath)) {
            _lblSummary.Text = "データベースが見つかりません。";
            return;
        }

        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            // 重複を除いた日付（yyyy-MM-dd形式）を取得
            string sql = "SELECT DISTINCT strftime('%Y-%m-%d', Time) as LogDate FROM LogEntries ORDER BY LogDate DESC";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();

            _dateSelector.Items.Clear();
            while (reader.Read()) {
                string dateStr = reader.GetString(0);
                _dateSelector.Items.Add(dateStr);
            }

            if (_dateSelector.Items.Count > 0) _dateSelector.SelectedIndex = 0;
        } catch (Exception ex) {
            _lblSummary.Text = "日付読込エラー";
            Debug.WriteLine(ex.Message);
        }
    }

    private void OnDateChanged(object? sender, EventArgs e)
    {
        if (_dateSelector.SelectedItem is string dateStr) LoadLogFromDb(dateStr);
    }

    /// <summary>
    /// 指定された日付のログをDBから取得し、Tree表示を更新する
    /// </summary>
    private void LoadLogFromDb(string dateStr)
    {
        _treeCopy.Nodes.Clear(); _treeDelete.Nodes.Clear(); _treeError.Nodes.Clear();
        _treeCopy.BeginUpdate(); _treeDelete.BeginUpdate(); _treeError.BeginUpdate();
        
        int countCopy = 0, countDelete = 0; 
        long sizeCopy = 0;

        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            // 指定された日付に一致するログを抽出
            string sql = "SELECT Type, Path, Size, Message FROM LogEntries WHERE date(Time) = @date";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@date", dateStr);

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                string type = reader.GetString(0);
                string path = reader.GetString(1);
                long size = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                string msg = reader.IsDBNull(3) ? "" : reader.GetString(3);

                switch (type) {
                    case "Copy": 
                        AddPathToTree(_treeCopy, path, FormatSize(size)); 
                        countCopy++; sizeCopy += size; break;
                    case "Delete": 
                        AddPathToTree(_treeDelete, path, ""); 
                        countDelete++; break;
                    case "Error": 
                        AddPathToTree(_treeError, path, msg); break;
                }
            }
        } catch (Exception ex) {
            MessageBox.Show("DB読み込みエラー: " + ex.Message);
        } finally {
            _treeCopy.EndUpdate(); _treeDelete.EndUpdate(); _treeError.EndUpdate();
            ExpandLevel1(_treeCopy); ExpandLevel1(_treeDelete); ExpandLevel1(_treeError);
        }
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeCopy)})   [削除] {countDelete}件";
    }

    // --- 既存の復元・ユーティリティロジック (変更なし) ---

    private void OnShowVersionsClick(object? sender, EventArgs e)
    {
        TreeView tree = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (tree.SelectedNode?.Tag is not string sourcePath) return;

        string? backupBase = GetBackupPath(sourcePath);
        if (backupBase == null) return;

        var versions = new List<string>();
        if (File.Exists(backupBase)) versions.Add(backupBase); 

        for (int i = 1; i <= 50; i++) {
            string vPath = $"{backupBase}.v{i}";
            if (File.Exists(vPath)) versions.Add(vPath);
        }

        if (versions.Count == 0) { MessageBox.Show("履歴が見つかりません。"); return; }

        using var f = new Form { Text = "世代選択: " + Path.GetFileName(sourcePath), Size = new Size(500, 400), StartPosition = FormStartPosition.CenterParent };
        var lb = new ListBox { Dock = DockStyle.Fill };
        
        foreach (var v in versions) {
            var info = new FileInfo(v);
            string label = v == backupBase ? "[最新] " : $"[履歴] ";
            lb.Items.Add($"{label} {info.LastWriteTime:yyyy/MM/dd HH:mm:ss} ({FormatSize(info.Length)})");
        }

        var btn = new Button { Text = "選択した世代で復元", Dock = DockStyle.Bottom, Height = 40 };
        btn.Click += (s, ev) => {
            if (lb.SelectedIndex >= 0) { 
                RestoreFile(versions[lb.SelectedIndex], sourcePath); 
                f.DialogResult = DialogResult.OK; 
            }
        };
        f.Controls.Add(lb); f.Controls.Add(btn);
        f.ShowDialog();
    }

    private void RestoreFile(string? specificVersionPath, string? targetSourcePath = null)
    {
        var node = _treeCopy.SelectedNode ?? _treeDelete.SelectedNode;
        string sourcePath = targetSourcePath ?? node?.Tag as string ?? "";
        if (string.IsNullOrEmpty(sourcePath)) return;

        string? backupPath = specificVersionPath ?? GetBackupPath(sourcePath);
        if (backupPath == null || !File.Exists(backupPath)) { 
            MessageBox.Show("バックアップファイルが見つかりません。"); 
            return; 
        }

        if (MessageBox.Show($"以下の場所に復元（上書き）しますか？\n先: {sourcePath}", "復元確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
            try {
                string? dir = Path.GetDirectoryName(sourcePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backupPath, sourcePath, true);
                MessageBox.Show("復元しました！");
            } catch (Exception ex) { MessageBox.Show("復元失敗: " + ex.Message); }
        }
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        TreeView tree = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (tree.SelectedNode?.Tag is not string sourcePath) return;

        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath != null) {
            string? folder = Path.GetDirectoryName(backupPath);
            if (folder != null && Directory.Exists(folder)) {
                Process.Start("explorer.exe", folder);
            } else {
                MessageBox.Show("バックアップフォルダが見つかりません。");
            }
        }
    }

    private string? GetBackupPath(string sourcePath)
    {
        foreach (var pair in _backupPairs) {
            if (sourcePath.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase)) {
                string relative = sourcePath.Substring(pair.Source.Length);
                if (relative.StartsWith("\\")) relative = relative.Substring(1);

                string normal = Path.Combine(pair.Destination, relative);
                if (File.Exists(normal) || (Directory.Exists(Path.GetDirectoryName(normal)) && Directory.GetParent(normal)?.GetFiles(Path.GetFileName(normal) + ".v*").Length > 0)) 
                    return normal;
                
                string trash = Path.Combine(pair.Destination, "_TRASH_", relative);
                return trash;
            }
        }
        return null;
    }

    private void AddPathToTree(TreeView tree, string fullPath, string info)
    {
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        TreeNodeCollection nodes = tree.Nodes;
        TreeNode? last = null;
        foreach (var p in parts.Where(s => !string.IsNullOrEmpty(s))) {
            TreeNode? found = null;
            foreach(TreeNode n in nodes) { if (n.Text == p) { found = n; break; } }
            if (found != null) { nodes = found.Nodes; last = found; }
            else { last = nodes.Add(p); nodes = last.Nodes; }
        }
        if (last != null) { 
            if (!string.IsNullOrEmpty(info)) last.Text += $"  [{info}]"; 
            last.Tag = fullPath; 
        }
    }

    private void ExpandLevel1(TreeView tree) { foreach (TreeNode node in tree.Nodes) node.Expand(); }

    private string FormatSize(long b)
    {
        string[] s = { "B", "KB", "MB", "GB", "TB" };
        double l = b; int i = 0;
        while (l >= 1024 && i < s.Length - 1) { i++; l /= 1024; }
        return $"{l:0.##} {s[i]}";
    }

    private GroupBox CreateTreeGroup(string t, TreeView tv, Color c) 
    {
        var g = new GroupBox { Text = t, Dock = DockStyle.Fill, ForeColor = c };
        tv.Dock = DockStyle.Fill; tv.ForeColor = Color.Black; g.Controls.Add(tv);
        tv.NodeMouseClick += (s, e) => tv.SelectedNode = e.Node;
        return g;
    }

    private void LoadSettings()
    {
        try {
            string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack");
            string jsonPath = Path.Combine(configDir, "appsettings.json");
            if (File.Exists(jsonPath)) {
                var json = File.ReadAllText(jsonPath);
                var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (settings != null) _backupPairs = settings.BackupSettings;
            }
        } catch { }
    }
}