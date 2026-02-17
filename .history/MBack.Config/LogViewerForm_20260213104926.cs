using System.Text.Json;

namespace MBack.Config;

// 読み込み用データクラス
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
    
    // 3つのツリービュー
    private TreeView _treeCopy = new();
    private TreeView _treeDelete = new();
    private TreeView _treeError = new();

    private string _logDir;

    public LogViewerForm()
    {
        this.Text = "ログ表示";
        this.Size = new Size(1000, 600);

        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MBack", "Reports");

        SetupLayout();
        LoadDateList();
    }

    private void SetupLayout()
    {
        // 上部: 日付選択
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(5) };
        var lblDate = new Label { Text = "日付選択:", AutoSize = true, Location = new Point(10, 12) };
        _dateSelector.Location = new Point(80, 8);
        _dateSelector.Width = 200;
        _dateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChanged;

        _lblSummary.Location = new Point(300, 12);
        _lblSummary.AutoSize = true;

        topPanel.Controls.AddRange(new Control[] { lblDate, _dateSelector, _lblSummary });

        // メイン: 3分割画面 (TableLayoutPanelを使用)
        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 3;
        table.RowCount = 1;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));

        // 3つのパネルを作成
        table.Controls.Add(CreateTreeGroup("コピー (追加・更新)", _treeCopy, Color.DarkBlue), 0, 0);
        table.Controls.Add(CreateTreeGroup("削除", _treeDelete, Color.DarkRed), 1, 0);
        table.Controls.Add(CreateTreeGroup("エラー", _treeError, Color.Red), 2, 0);

        this.Controls.Add(table);
        this.Controls.Add(topPanel);
    }

    private GroupBox CreateTreeGroup(string title, TreeView tree, Color titleColor)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
        group.ForeColor = titleColor; // タイトル色
        
        tree.Dock = DockStyle.Fill;
        tree.ForeColor = Color.Black; // 文字色は黒に戻す
        // 画像のようなアイコン表示などがしたい場合はここでImageListを設定可能
        
        group.Controls.Add(tree);
        return group;
    }

    private void LoadDateList()
    {
        if (!Directory.Exists(_logDir)) return;

        var files = Directory.GetFiles(_logDir, "report-*.jsonl")
                             .OrderByDescending(f => f) // 新しい順
                             .ToArray();

        _dateSelector.Items.Clear();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("report-", "");
            // yyyyMMdd -> yyyy/MM/dd
            if (name.Length == 8) 
                name = $"{name.Substring(0, 4)}/{name.Substring(4, 2)}/{name.Substring(6, 2)}";
            
            _dateSelector.Items.Add(new DateItem { Display = name, FilePath = file });
        }

        if (_dateSelector.Items.Count > 0)
            _dateSelector.SelectedIndex = 0; // 最新を選択
        else
            _lblSummary.Text = "ログファイルがありません";
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

        int countCopy = 0;
        int countDelete = 0;
        int countError = 0;
        long sizeCopy = 0;
        long sizeDelete = 0;

        try
        {
            var lines = File.ReadAllLines(path);
            
            _treeCopy.BeginUpdate();
            _treeDelete.BeginUpdate();
            _treeError.BeginUpdate();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                    if (entry == null) continue;

                    if (entry.Type == "Copy")
                    {
                        AddPathToTree(_treeCopy, entry.Path, $"{FormatSize(entry.Size)}");
                        countCopy++;
                        sizeCopy += entry.Size;
                    }
                    else if (entry.Type == "Delete")
                    {
                        AddPathToTree(_treeDelete, entry.Path, "");
                        countDelete++;
                        sizeDelete += entry.Size; // 削除サイズは0かもしれないが
                    }
                    else if (entry.Type == "Error")
                    {
                        AddPathToTree(_treeError, entry.Path, entry.Message);
                        countError++;
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
            
            // 全展開するかどうか（ファイルが多いと重いので第1階層だけ展開）
            // _treeCopy.ExpandAll(); 
        }

        _lblSummary.Text = $"コピー: {countCopy}個 ({FormatSize(sizeCopy)}) | 削除: {countDelete}個 | エラー: {countError}個";
    }

    // パスを分解してツリーに追加するロジック
    private void AddPathToTree(TreeView tree, string fullPath, string extraInfo)
    {
        // ルートディレクトリ表記をきれいにするため、ドライブレターなどで分割
        // 例: E:\Backup\Data\file.txt -> ["E:", "Backup", "Data", "file.txt"]
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        
        TreeNodeCollection nodes = tree.Nodes;
        TreeNode? lastNode = null;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            // 既存ノードを探す
            bool found = false;
            foreach (TreeNode node in nodes)
            {
                if (node.Text.StartsWith(part)) // 完全一致ではなく前方一致なのは簡易実装のため
                {
                    if (node.Text == part) // 正確にはこれで比較
                    {
                        nodes = node.Nodes;
                        lastNode = node;
                        found = true;
                        break;
                    }
                }
            }

            // なければ作る
            if (!found)
            {
                lastNode = nodes.Add(part);
                nodes = lastNode.Nodes;
            }
        }

        // 最後のノード（ファイル）に追加情報を付与
        if (lastNode != null && !string.IsNullOrEmpty(extraInfo))
        {
            lastNode.Text += $"  [{extraInfo}]";
        }
    }

    private string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private class DateItem
    {
        public string Display { get; set; } = "";
        public string FilePath { get; set; } = "";
        public override string ToString() => Display;
    }
}