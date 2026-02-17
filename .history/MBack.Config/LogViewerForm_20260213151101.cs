using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;

namespace MBack.Config;

// ★ AppSettingsRaw と BackupPair の定義は Form1.cs にあるので、ここでは削除しました。

// ログデータ定義（これは Form1 側にはないので残します）
public class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public string Message { get; set; } = "";
    public long Size { get; set; }
}

public class LogViewerForm : Form
{
    private ComboBox _dateSelector = new();
    private Label _lblSummary = new();
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();
    private ContextMenuStrip _contextMenu = new();

    private string _logDir;
    // BackupPair は Form1.cs 側の定義が自動的に使われます
    private List<BackupPair> _backupPairs = new(); 

    public LogViewerForm()
    {
        this.Text = "バックアップ ログ表示 & 復元";
        this.Size = new Size(1100, 700);
        this.StartPosition = FormStartPosition.CenterParent;

        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MBack", "Reports");

        LoadSettings();
        SetupLayout();
        LoadDateList();
    }

    private void LoadSettings()
    {
        try
        {
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                // AppSettingsRaw も Form1.cs 側の定義が使われます
                var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (settings != null)
                {
                    _backupPairs = settings.BackupSettings;
                }
            }
        }
        catch { }
    }

    private void SetupLayout()
    {
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(10) };
        var lblDate = new Label { Text = "日付選択:", AutoSize = true, Location = new Point(10, 15) };
        _dateSelector.Location = new Point(80, 12);
        _dateSelector.Width = 200;
        _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChanged;
        
        _lblSummary.Location = new Point(300, 15);
        _lblSummary.AutoSize = true;
        _lblSummary.Font = new Font(this.Font, FontStyle.Bold);

        topPanel.Controls.Add(lblDate);
        topPanel.Controls.Add(_dateSelector);
        topPanel.Controls.Add(_lblSummary);

        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 3;
        table.RowCount = 1;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));

        table.Controls.Add(CreateTreeGroup("コピー - 右クリックで復元", _treeCopy, Color.DarkBlue), 0, 0);
        table.Controls.Add(CreateTreeGroup("削除 (ゴミ箱)", _treeDelete, Color.DarkRed), 1, 0);
        table.Controls.Add(CreateTreeGroup("エラー", _treeError, Color.Red), 2, 0);

        var menuItemRestore = new ToolStripMenuItem("このファイルを復元する (上書き)");
        menuItemRestore.Click += OnRestoreClick;
        var menuItemOpenFolder = new ToolStripMenuItem("バックアップ先のフォルダを開く");
        menuItemOpenFolder.Click += OnOpenBackupFolderClick;

        _contextMenu.Items.Add(menuItemRestore);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(menuItemOpenFolder);

        _treeCopy.ContextMenuStrip = _contextMenu;
        _treeCopy.NodeMouseClick += (s, e) => _treeCopy.SelectedNode = e.Node;

        this.Controls.Add(table);
        this.Controls.Add(topPanel);
    }

    private GroupBox CreateTreeGroup(string title, TreeView tree, Color titleColor)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
        group.ForeColor = titleColor;
        tree.Dock = DockStyle.Fill;
        tree.ForeColor = Color.Black;
        tree.ShowLines = true;
        tree.ShowPlusMinus = true;
        group.Controls.Add(tree);
        return group;
    }

    private void OnRestoreClick(object? sender, EventArgs e)
    {
        var node = _treeCopy.SelectedNode;
        if (node?.Tag is not string sourcePath) return;

        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath == null || !File.Exists(backupPath))
        {
            MessageBox.Show("バックアップファイルが見つかりません。");
            return;
        }

        if (MessageBox.Show($"復元しますか？\n{sourcePath}", "復元確認", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            try
            {
                string? dir = Path.GetDirectoryName(sourcePath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.Copy(backupPath, sourcePath, true);
                MessageBox.Show("復元しました！");
            }
            catch (Exception ex) { MessageBox.Show("復元失敗: " + ex.Message); }
        }
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        var node = _treeCopy.SelectedNode;
        if (node?.Tag is not string sourcePath) return;
        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath != null) Process.Start("explorer.exe", $"/select, \"{backupPath}\"");
    }

    private string? GetBackupPath(string sourcePath)
    {
        foreach (var pair in _backupPairs)
        {
            if (sourcePath.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase))
            {
                string relative = sourcePath.Substring(pair.Source.Length).TrimStart(Path.DirectorySeparatorChar);
                return Path.Combine(pair.Destination, relative);
            }
        }
        return null;
    }

    private void LoadDateList()
    {
        if (!Directory.Exists(_logDir)) return;
        var files = Directory.GetFiles(_logDir, "report-*.jsonl").OrderByDescending(f => f).ToArray();
        _dateSelector.Items.Clear();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("report-", "");
            if (name.Length == 8) name = $"{name.Substring(0, 4)}/{name.Substring(4, 2)}/{name.Substring(6, 2)}";
            _dateSelector.Items.Add(new DateItem { Display = name, FilePath = file });
        }
        if (_dateSelector.Items.Count > 0) _dateSelector.SelectedIndex = 0;
    }

    private void OnDateChanged(object? sender, EventArgs e)
    {
        if (_dateSelector.SelectedItem is DateItem item) LoadLogFile(item.FilePath);
    }

    private void LoadLogFile(string path)
    {
        _treeCopy.Nodes.Clear(); _treeDelete.Nodes.Clear(); _treeError.Nodes.Clear();
        _treeCopy.BeginUpdate(); _treeDelete.BeginUpdate(); _treeError.BeginUpdate();
        int countCopy = 0, countDelete = 0; long sizeCopy = 0;
        try
        {
            foreach (var line in File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                if (entry == null) continue;
                switch (entry.Type)
                {
                    case "Copy": AddPathToTree(_treeCopy, entry.Path, FormatSize(entry.Size)); countCopy++; sizeCopy += entry.Size; break;
                    case "Delete": AddPathToTree(_treeDelete, entry.Path, ""); countDelete++; break;
                    case "Error": AddPathToTree(_treeError, entry.Path, entry.Message); break;
                }
            }
        }
        finally
        {
            _treeCopy.EndUpdate(); _treeDelete.EndUpdate(); _treeError.EndUpdate();
            foreach (var t in new[] { _treeCopy, _treeDelete, _treeError }) foreach (TreeNode n in t.Nodes) n.Expand();
        }
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeCopy)})   [削除] {countDelete}件";
    }

    private void AddPathToTree(TreeView tree, string fullPath, string info)
    {
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        TreeNodeCollection nodes = tree.Nodes;
        TreeNode? last = null;
        foreach (var p in parts.Where(s => !string.IsNullOrEmpty(s)))
        {
            TreeNode? found = nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == p);
            if (found != null) { nodes = found.Nodes; last = found; }
            else { last = nodes.Add(p); nodes = last.Nodes; }
        }
        if (last != null) { if (!string.IsNullOrEmpty(info)) last.Text += $"  [{info}]"; last.Tag = fullPath; }
    }

    private string FormatSize(long b)
    {
        string[] s = { "B", "KB", "MB", "GB", "TB" };
        double l = b; int i = 0;
        while (l >= 1024 && i < s.Length - 1) { i++; l /= 1024; }
        return $"{l:0.##} {s[i]}";
    }

    private class DateItem
    {
        public string Display { get; set; } = "";
        public string FilePath { get; set; } = "";
        public override string ToString() => Display;
    }
}