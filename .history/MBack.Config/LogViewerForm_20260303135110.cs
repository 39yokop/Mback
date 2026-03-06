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
        this.Text = "MBack 履歴復元センター (SQLite・鉄壁レイアウト版)";
        this.Size = new Size(1200, 750); 
        this.StartPosition = FormStartPosition.CenterParent;

        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MBack", "Database", "history.db");

        LoadSettings();
        SetupLayout();
        
        // ★修正ポイント1: 「Shown(表示完了後)」イベントを使用し、サイズが確定してから計算する
        // これにより、初期化時のゼロ計算によるクラッシュを防ぐ
        this.Shown += (s, e) => FixSplitterLayout();
        
        LoadDateListFromDb(); 
    }

    private void SetupLayout()
    {
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10), BackColor = Color.FromArgb(240, 240, 240) };
        
        var lblDate = new Label { Text = "1. 日付で探す:", AutoSize = true, Location = new Point(15, 20), Font = new Font(this.Font, FontStyle.Bold) };
        _dateSelector.Location = new Point(110, 17);
        _dateSelector.Width = 150;
        _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChanged;

        _btnRefresh.Text = "最新の状態に更新";
        _btnRefresh.Location = new Point(270, 15);
        _btnRefresh.Size = new Size(120, 30);
        _btnRefresh.Click += (s, e) => {
            LoadDateListFromDb(); 
            if (_dateSelector.SelectedItem?.ToString() is string selectedDate) 
            {
                LoadLogFromDb(selectedDate);
            }
        };
        
        _lblSummary.Location = new Point(410, 20);
        _lblSummary.AutoSize = true;

        topPanel.Controls.Add(lblDate);
        topPanel.Controls.Add(_dateSelector);
        topPanel.Controls.Add(_btnRefresh);
        topPanel.Controls.Add(_lblSummary);

        // --- レイアウト構築 ---
        _splitMain.Dock = DockStyle.Fill;
        _splitMain.BorderStyle = BorderStyle.Fixed3D;
        // ★修正ポイント2: MinSizeを初期化時は小さめ(50)に設定し、計算時の衝突を避ける
        _splitMain.Panel1MinSize = 50;
        _splitMain.Panel2MinSize = 100;

        _splitSub.Dock = DockStyle.Fill;
        _splitSub.BorderStyle = BorderStyle.Fixed3D;
        _splitSub.Panel1MinSize = 50;
        _splitSub.Panel2MinSize = 50;

        _splitMain.Panel1.Controls.Add(CreateTreeGroup("コピー済み (右クリックで全世代表示)", _treeCopy, Color.DarkBlue));
        _splitMain.Panel2.Controls.Add(_splitSub);

        _splitSub.Panel1.Controls.Add(CreateTreeGroup("ゴミ箱 (削除されたファイル)", _treeDelete, Color.DarkRed));
        _splitSub.Panel2.Controls.Add(CreateTreeGroup("エラーログ", _treeError, Color.Red));

        // メニュー等
        var menuVersion = new ToolStripMenuItem("★ このファイルの全世代を表示...", null, OnShowVersionsClick);
        menuVersion.Font = new Font(menuVersion.Font, FontStyle.Bold);
        var menuRestore = new ToolStripMenuItem("最新のバックアップから復元", null, (s, e) => RestoreFile(null));
        var menuOpen = new ToolStripMenuItem("バックアップ先を開く", null, OnOpenBackupFolderClick);
        _contextMenu.Items.AddRange(new ToolStripItem[] { menuVersion, new ToolStripSeparator(), menuRestore, menuOpen });
        _treeCopy.ContextMenuStrip = _contextMenu;
        _treeDelete.ContextMenuStrip = _contextMenu;

        this.Controls.Add(_splitMain);
        this.Controls.Add(topPanel);
    }

    /// <summary>
    /// レイアウトを3等分にする。例外が出ないようにガードレールを設けた。
    /// </summary>
    private void FixSplitterLayout()
    {
        try {
            int totalW = _splitMain.Width;
            if (totalW < 300) return; // あまりに幅が狭い場合は調整をスキップ

            // 1/3 の位置を計算
            int dist1 = totalW / 3;
            // ガード: 許容範囲内[MinSize, Total - MinSize]に値を収める (Math.Clampは.NET 6+ / net10.0で使用可能)
            _splitMain.SplitterDistance = Math.Clamp(dist1, _splitMain.Panel1MinSize, totalW - _splitMain.Panel2MinSize);
            
            int subW = _splitSub.Width;
            if (subW < 100) return;

            int dist2 = subW / 2;
            _splitSub.SplitterDistance = Math.Clamp(dist2, _splitSub.Panel1MinSize, subW - _splitSub.Panel2MinSize);
            
            // レイアウト確定後に、本来の使い勝手のための最低サイズを再設定（任意）
            _splitMain.Panel1MinSize = 150;
            _splitSub.Panel1MinSize = 150;
            _splitSub.Panel2MinSize = 150;
        } catch {
            // 万が一の計算ミスでも、ここでのエラーで画面が開けない事態を防ぐ
        }
    }

    // --- 以下、DB連携・復元ロジック (既存の警告解消版を継承) ---

    private void LoadDateListFromDb()
    {
        if (!File.Exists(_dbPath)) return;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            string sql = "SELECT DISTINCT strftime('%Y-%m-%d', Time) as LogDate FROM LogEntries ORDER BY LogDate DESC";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            string? currentSelection = _dateSelector.SelectedItem?.ToString();
            _dateSelector.Items.Clear();
            while (reader.Read()) { _dateSelector.Items.Add(reader.GetString(0)); }
            if (_dateSelector.Items.Count > 0) {
                int index = (currentSelection != null) ? _dateSelector.FindStringExact(currentSelection) : 0;
                _dateSelector.SelectedIndex = (index >= 0) ? index : 0;
            }
        } catch { }
    }

    private void OnDateChanged(object? sender, EventArgs e) { if (_dateSelector.SelectedItem is string dateStr) LoadLogFromDb(dateStr); }

    private void LoadLogFromDb(string dateStr)
    {
        _treeCopy.Nodes.Clear(); _treeDelete.Nodes.Clear(); _treeError.Nodes.Clear();
        _treeCopy.BeginUpdate(); _treeDelete.BeginUpdate(); _treeError.BeginUpdate();
        int countCopy = 0, countDelete = 0; long sizeCopy = 0;
        try {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            string sql = "SELECT Type, Path, Size, Message FROM LogEntries WHERE date(Time) = @date";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@date", dateStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                string type = reader.GetString(0); string path = reader.GetString(1);
                long size = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                string msg = reader.IsDBNull(3) ? "" : reader.GetString(3);
                switch (type) {
                    case "Copy": AddPathToTree(_treeCopy, path, FormatSize(size)); countCopy++; sizeCopy += size; break;
                    case "Delete": AddPathToTree(_treeDelete, path, ""); countDelete++; break;
                    case "Error": AddPathToTree(_treeError, path, msg); break;
                }
            }
        } catch { } finally {
            _treeCopy.EndUpdate(); _treeDelete.EndUpdate(); _treeError.EndUpdate();
            ExpandLevel1(_treeCopy); ExpandLevel1(_treeDelete); ExpandLevel1(_treeError);
        }
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeCopy)})   [削除] {countDelete}件";
    }

    private void OnShowVersionsClick(object? sender, EventArgs e)
    {
        TreeView tree = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (tree.SelectedNode?.Tag is not string sourcePath) return;
        string? backupBase = GetBackupPath(sourcePath);
        if (backupBase == null) return;
        var versions = new List<string>();
        if (File.Exists(backupBase)) versions.Add(backupBase); 
        for (int i = 1; i <= 50; i++) { string vPath = $"{backupBase}.v{i}"; if (File.Exists(vPath)) versions.Add(vPath); }
        if (versions.Count == 0) return;
        using var f = new Form { Text = "世代選択: " + Path.GetFileName(sourcePath), Size = new Size(500, 400), StartPosition = FormStartPosition.CenterParent };
        var lb = new ListBox { Dock = DockStyle.Fill };
        foreach (var v in versions) {
            var info = new FileInfo(v);
            lb.Items.Add($"{(v == backupBase ? "[最新]" : "[履歴]")} {info.LastWriteTime:yyyy/MM/dd HH:mm:ss} ({FormatSize(info.Length)})");
        }
        var btn = new Button { Text = "復元する", Dock = DockStyle.Bottom, Height = 40 };
        btn.Click += (s, ev) => { if (lb.SelectedIndex >= 0) { RestoreFile(versions[lb.SelectedIndex], sourcePath); f.DialogResult = DialogResult.OK; } };
        f.Controls.Add(lb); f.Controls.Add(btn); f.ShowDialog();
    }

    private void RestoreFile(string? specificVersionPath, string? targetSourcePath = null)
    {
        var node = _treeCopy.SelectedNode ?? _treeDelete.SelectedNode;
        string sourcePath = targetSourcePath ?? node?.Tag as string ?? "";
        string? backupPath = specificVersionPath ?? GetBackupPath(sourcePath);
        if (string.IsNullOrEmpty(sourcePath) || backupPath == null || !File.Exists(backupPath)) return;
        if (MessageBox.Show($"復元しますか？\n{sourcePath}", "復元確認", MessageBoxButtons.YesNo) == DialogResult.Yes) {
            try {
                string? dir = Path.GetDirectoryName(sourcePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backupPath, sourcePath, true);
                MessageBox.Show("完了！");
            } catch (Exception ex) { MessageBox.Show("エラー: " + ex.Message); }
        }
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        TreeView tree = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (tree.SelectedNode?.Tag is not string sourcePath) return;
        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath != null) {
            string? folder = Path.GetDirectoryName(backupPath);
            if (folder != null && Directory.Exists(folder)) Process.Start("explorer.exe", folder);
        }
    }

    private string? GetBackupPath(string sourcePath)
    {
        foreach (var pair in _backupPairs) {
            if (sourcePath.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase)) {
                string relative = sourcePath.Substring(pair.Source.Length).TrimStart('\\');
                string normal = Path.Combine(pair.Destination, relative);
                if (File.Exists(normal) || (Directory.Exists(Path.GetDirectoryName(normal)) && Directory.GetParent(normal)?.GetFiles(Path.GetFileName(normal) + ".v*").Length > 0)) return normal;
                return Path.Combine(pair.Destination, "_TRASH_", relative);
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
        if (last != null) { if (!string.IsNullOrEmpty(info)) last.Text += $"  [{info}]"; last.Tag = fullPath; }
    }

    private void ExpandLevel1(TreeView tree) { foreach (TreeNode node in tree.Nodes) node.Expand(); }
    private string FormatSize(long b) { string[] s = { "B", "KB", "MB", "GB", "TB" }; double l = b; int i = 0; while (l >= 1024 && i < s.Length - 1) { i++; l /= 1024; } return $"{l:0.##} {s[i]}"; }
    private GroupBox CreateTreeGroup(string t, TreeView tv, Color c) {
        var g = new GroupBox { Text = t, Dock = DockStyle.Fill, ForeColor = c };
        tv.Dock = DockStyle.Fill; tv.ForeColor = Color.Black; g.Controls.Add(tv);
        tv.NodeMouseClick += (s, e) => tv.SelectedNode = e.Node; return g;
    }
    private void LoadSettings() {
        try {
            string jsonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MBack", "appsettings.json");
            if (File.Exists(jsonPath)) {
                var json = File.ReadAllText(jsonPath);
                var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (settings != null) _backupPairs = settings.BackupSettings;
            }
        } catch { }
    }
}