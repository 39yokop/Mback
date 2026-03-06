using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;

namespace MBack.Config;

public class LogViewerForm : Form
{
    private ComboBox _dateSelector = new();
    private Label _lblSummary = new();
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();
    private ContextMenuStrip _contextMenu = new();
    private string _logDir;
    private List<BackupPair> _backupPairs = new(); 

    public LogViewerForm()
    {
        this.Text = "MBack 履歴復元センター";
        this.Size = new Size(1100, 750);
        this.StartPosition = FormStartPosition.CenterParent;

        _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MBack", "Reports");

        LoadSettings();
        SetupLayout();
        LoadDateList();
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

        // メニュー
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

    // --- 世代スキャン機能の核心 ---
    private void OnShowVersionsClick(object? sender, EventArgs e)
    {
        var node = (sender as ToolStripItem)?.Owner?.FocusedChild as TreeView ?? _treeCopy;
        if (node.SelectedNode?.Tag is not string sourcePath) return;

        string? backupBase = GetBackupPath(sourcePath);
        if (backupBase == null) return;

        // バックアップ先にある .v1 ～ .v50 を直接探す
        var versions = new List<string>();
        if (File.Exists(backupBase)) versions.Add(backupBase); // 最新

        for (int i = 1; i <= 50; i++)
        {
            string vPath = $"{backupBase}.v{i}";
            if (File.Exists(vPath)) versions.Add(vPath);
        }

        if (versions.Count == 0) { MessageBox.Show("履歴が見つかりません。"); return; }

        // 簡易的なバージョン選択ダイアログを表示
        using var f = new Form { Text = "世代選択: " + Path.GetFileName(sourcePath), Size = new Size(500, 400), StartPosition = FormStartPosition.CenterParent };
        var lb = new ListBox { Dock = DockStyle.Fill };
        foreach (var v in versions)
        {
            var info = new FileInfo(v);
            string label = v == backupBase ? "[最新] " : $"[履歴] ";
            lb.Items.Add($"{label} {info.LastWriteTime:yyyy/MM/dd HH:mm:ss} ({FormatSize(info.Length)})");
        }
        var btn = new Button { Text = "選択した世代で復元", Dock = DockStyle.Bottom, Height = 40 };
        btn.Click += (s, ev) => {
            if (lb.SelectedIndex >= 0) { RestoreFile(versions[lb.SelectedIndex], sourcePath); f.DialogResult = DialogResult.OK; }
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
        if (backupPath == null || !File.Exists(backupPath)) { MessageBox.Show("ファイルが見つかりません。"); return; }

        if (MessageBox.Show($"復元しますか？\n元: {sourcePath}", "復元確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            try {
                string? dir = Path.GetDirectoryName(sourcePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backupPath, sourcePath, true);
                MessageBox.Show("完了しました。");
            } catch (Exception ex) { MessageBox.Show("失敗: " + ex.Message); }
        }
    }

    // バックアップ先パスを計算（ゴミ箱も考慮）
    private string? GetBackupPath(string sourcePath)
    {
        foreach (var pair in _backupPairs)
        {
            if (sourcePath.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase))
            {
                string relative = sourcePath.Substring(pair.Source.Length).TrimStart(Path.DirectorySeparatorChar);
                // まず普通のバックアップを探す
                string normal = Path.Combine(pair.Destination, relative);
                if (File.Exists(normal) || Directory.GetFiles(pair.Destination, relative + ".v*").Any()) return normal;
                
                // なければゴミ箱内を探す
                string trash = Path.Combine(pair.Destination, "_TRASH_", relative);
                return trash;
            }
        }
        return null;
    }

    // --- 以下、既存のLoadロジックなど ---
    private GroupBox CreateTreeGroup(string t, TreeView tv, Color c) {
        var g = new GroupBox { Text = t, Dock = DockStyle.Fill, ForeColor = c };
        tv.Dock = DockStyle.Fill; tv.ForeColor = Color.Black; g.Controls.Add(tv);
        tv.NodeMouseClick += (s, e) => tv.SelectedNode = e.Node;
        return g;
    }
    private void LoadSettings() { /* appsettings.json読込 */ }
    private void OnDateChanged(object? s, EventArgs e) { /* ログ読込 */ }
    private void LoadLogFile(string p) { /* 以前と同じ */ }
    private void AddPathToTree(TreeView t, string f, string i) { /* 以前と同じ（Tagにフルパス保存） */ }
    private string FormatSize(long b) { /* 以前と同じ */ }
}