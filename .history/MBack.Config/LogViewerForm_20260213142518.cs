using System.Text.Json;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;

namespace MBack.Config;

// ★ BackupPair と AppSettingsRaw の定義は削除しました（Form1.cs側の定義を使います）

// ログデータ定義 (これはここで定義してOKです)
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
    // --- UI部品 ---
    private ComboBox _dateSelector = new();
    private Label _lblSummary = new();
    
    // 3つのツリービュー
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();

    // 右クリックメニュー
    private ContextMenuStrip _contextMenu = new();

    private string _logDir;
    // BackupPairはForm1.cs等で定義されているものをそのまま使います
    private List<BackupPair> _backupPairs = new(); 

    public LogViewerForm()
    {
        this.Text = "バックアップ ログ表示 & 復元";
        this.Size = new Size(1100, 700);
        this.StartPosition = FormStartPosition.CenterParent;

        // ログの保存場所
        // ※ 本番運用では ProgramData (CommonApplicationData) が推奨ですが、
        //    現状の運用に合わせて LocalApplicationData のままにしています。
        //    もしサービス側を ProgramData に変更した場合はここも修正してください。
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MBack", "Reports");

        LoadSettings(); // 設定ファイル（パスの対応関係）を読み込む
        SetupLayout();
        LoadDateList();
    }

    private void LoadSettings()
    {
        try
        {
            // インストール後は同じフォルダにあるため、自身のパスから探します
            string jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            
            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                // AppSettingsRaw も Form1.cs で定義されているはずなのでそれを使います
                // もし「AppSettingsRawが見つからない」とエラーが出たら、
                // Form1.cs の中にある設定クラスの名前を確認してください（おそらく同じ名前のはずです）
                var settings = JsonSerializer.Deserialize<AppSettingsRaw>(json);
                if (settings != null)
                {
                    _backupPairs = settings.BackupSettings;
                }
            }
        }
        catch 
        { 
            // 読み込めなくてもログ表示は動作させる
        }
    }

    private void SetupLayout()
    {
        // 1. 上部パネル
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

        // 2. メインパネル
        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 3;
        table.RowCount = 1;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));

        table.Controls.Add(CreateTreeGroup("コピー (追加・更新) - 右クリックで復元", _treeCopy, Color.DarkBlue), 0, 0);
        table.Controls.Add(CreateTreeGroup("削除 (ゴミ箱へ移動)", _treeDelete, Color.DarkRed), 1, 0);
        table.Controls.Add(CreateTreeGroup("エラー", _treeError, Color.Red), 2, 0);

        // 3. 右クリックメニューの作成
        var menuItemRestore = new ToolStripMenuItem("このファイルを復元する (上書き)");
        menuItemRestore.Click += OnRestoreClick;
        
        var menuItemOpenFolder = new ToolStripMenuItem("バックアップ先のフォルダを開く");
        menuItemOpenFolder.Click += OnOpenBackupFolderClick;

        _contextMenu.Items.Add(menuItemRestore);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(menuItemOpenFolder);

        // コピーツリーにだけメニューを割り当て
        _treeCopy.ContextMenuStrip = _contextMenu;
        _treeCopy.NodeMouseClick += (s, e) => _treeCopy.SelectedNode = e.Node; // 右クリックでも選択状態にする

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

    // --- 復元機能 ---

    private void OnRestoreClick(object? sender, EventArgs e)
    {
        var node = _treeCopy.SelectedNode;
        if (node == null) return;

        // ノードのTagからフルパスを取得
        if (node.Tag is not string sourcePath) 
        {
            MessageBox.Show("ファイルを選択してください（フォルダは復元できません）");
            return;
        }

        // バックアップ先のパスを計算
        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath == null)
        {
            MessageBox.Show("設定ファイルからバックアップ先が見つかりませんでした。\n設定が変わっている可能性があります。");
            return;
        }

        if (!File.Exists(backupPath))
        {
            MessageBox.Show($"バックアップ先にファイルが見つかりません。\nパス: {backupPath}");
            return;
        }

        // 確認
        var result = MessageBox.Show(
            $"以下のファイルを復元（上書き）しますか？\n\n元: {sourcePath}\nバックアップ: {backupPath}",
            "復元確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result == DialogResult.Yes)
        {
            try
            {
                // フォルダがない場合は作成
                string? dir = Path.GetDirectoryName(sourcePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(backupPath, sourcePath, overwrite: true);
                MessageBox.Show("復元しました！");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"復元に失敗しました: {ex.Message}");
            }
        }
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        var node = _treeCopy.SelectedNode;
        if (node?.Tag is not string sourcePath) return;

        string? backupPath = GetBackupPath(sourcePath);
        if (backupPath != null)
        {
            // ファイルを選択した状態でエクスプローラーを開く
            string arg = "/select, \"" + backupPath + "\"";
            Process.Start("explorer.exe", arg);
        }
    }

    // ソースパスからバックアップ先のパスを計算するロジック
    private string? GetBackupPath(string sourcePath)
    {
        foreach (var pair in _backupPairs)
        {
            // 設定のソースパスと前方一致するか確認
            if (sourcePath.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase))
            {
                // Source部分をDestinationに置換
                string relative = sourcePath.Substring(pair.Source.Length);
                if (relative.StartsWith("\\")) relative = relative.Substring(1); // 先頭の\を取る
                
                return Path.Combine(pair.Destination, relative);
            }
        }
        return null; // 見つからない
    }

    // --- データ読み込みロジック ---

    private void LoadDateList()
    {
        if (!Directory.Exists(_logDir))
        {
            _lblSummary.Text = "ログフォルダが見つかりません";
            return;
        }
        var files = Directory.GetFiles(_logDir, "report-*.jsonl").OrderByDescending(f => f).ToArray();
        _dateSelector.Items.Clear();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("report-", "");
            if (name.Length == 8) 
                name = $"{name.Substring(0, 4)}/{name.Substring(4, 2)}/{name.Substring(6, 2)}";
            _dateSelector.Items.Add(new DateItem { Display = name, FilePath = file });
        }
        if (_dateSelector.Items.Count > 0) _dateSelector.SelectedIndex = 0;
    }

    private void OnDateChanged(object? sender, EventArgs e)
    {
        if (_dateSelector.SelectedItem is not DateItem item) return;
        LoadLogFile(item.FilePath);
    }

    private void LoadLogFile(string path)
    {
        _treeCopy.Nodes.Clear();
        _treeDelete.Nodes.Clear();
        _treeError.Nodes.Clear();
        _treeCopy.BeginUpdate();
        _treeDelete.BeginUpdate();
        _treeError.BeginUpdate();

        int countCopy = 0, countDelete = 0, countError = 0;
        long sizeCopy = 0;

        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                    if (entry == null) continue;

                    switch (entry.Type)
                    {
                        case "Copy":
                            AddPathToTree(_treeCopy, entry.Path, FormatSize(entry.Size));
                            countCopy++;
                            sizeCopy += entry.Size;
                            break;
                        case "Delete":
                            AddPathToTree(_treeDelete, entry.Path, "");
                            countDelete++;
                            break;
                        case "Error":
                            AddPathToTree(_treeError, entry.Path, entry.Message);
                            countError++;
                            break;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("ログ読み込みエラー: " + ex.Message);
        }
        finally
        {
            _treeCopy.EndUpdate();
            _treeDelete.EndUpdate();
            _treeError.EndUpdate();
            ExpandLevel1(_treeCopy);
            ExpandLevel1(_treeDelete);
            ExpandLevel1(_treeError);
        }
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeCopy)})   [削除] {countDelete}件";
    }

    // パスを分解してツリーに追加 (Tagにフルパスを入れる改良版)
    private void AddPathToTree(TreeView tree, string fullPath, string extraInfo)
    {
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        TreeNodeCollection currentNodes = tree.Nodes;
        TreeNode? lastNode = null;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            TreeNode? foundNode = null;
            foreach (TreeNode node in currentNodes)
            {
                if (node.Text == part) { foundNode = node; break; }
            }

            if (foundNode != null)
            {
                currentNodes = foundNode.Nodes;
                lastNode = foundNode;
            }
            else
            {
                lastNode = currentNodes.Add(part);
                currentNodes = lastNode.Nodes;
            }
        }

        if (lastNode != null)
        {
            if (!string.IsNullOrEmpty(extraInfo))
            {
                lastNode.Text += $"  [{extraInfo}]";
                if (tree == _treeError) lastNode.ForeColor = Color.Red;
            }
            // ★右クリック復元用にフルパスを保存
            lastNode.Tag = fullPath;
        }
    }

    private void ExpandLevel1(TreeView tree)
    {
        foreach (TreeNode node in tree.Nodes) node.Expand();
    }

    private string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }

    private class DateItem
    {
        public string Display { get; set; } = "";
        public string FilePath { get; set; } = "";
        public override string ToString() => Display;
    }
}