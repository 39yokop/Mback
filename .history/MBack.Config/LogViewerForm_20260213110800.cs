using System.Text.Json;
using System.Drawing; // UIの色やサイズ指定に必要
using System.Windows.Forms;

namespace MBack.Config;

// 読み込み用にここでも定義します
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

    private string _logDir;

    public LogViewerForm()
    {
        this.Text = "バックアップ ログ表示";
        this.Size = new Size(1100, 600); // ワイド画面に対応
        this.StartPosition = FormStartPosition.CenterParent;

        // ログの保存場所 (Service側と同じ場所)
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MBack", "Reports");

        SetupLayout();
        LoadDateList();
    }

    private void SetupLayout()
    {
        // 1. 上部パネル (日付選択と集計)
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

        // 2. メインパネル (3分割)
        var table = new TableLayoutPanel();
        table.Dock = DockStyle.Fill;
        table.ColumnCount = 3;
        table.RowCount = 1;
        // 3等分 (33.3%)
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));

        // 3つのグループボックスを作成して配置
        table.Controls.Add(CreateTreeGroup("コピー (追加・更新)", _treeCopy, Color.DarkBlue), 0, 0);
        table.Controls.Add(CreateTreeGroup("削除 (ゴミ箱へ移動)", _treeDelete, Color.DarkRed), 1, 0);
        table.Controls.Add(CreateTreeGroup("エラー", _treeError, Color.Red), 2, 0);

        // フォームに追加
        this.Controls.Add(table);
        this.Controls.Add(topPanel);
    }

    // ツリービューを含むグループボックスを作るヘルパー
    private GroupBox CreateTreeGroup(string title, TreeView tree, Color titleColor)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
        group.ForeColor = titleColor; // タイトルの色
        group.Padding = new Padding(5);
        
        tree.Dock = DockStyle.Fill;
        tree.ForeColor = Color.Black; // ツリーの文字は黒
        tree.ShowLines = true;        // 点線を表示
        tree.ShowPlusMinus = true;    // +/-ボタンを表示
        
        group.Controls.Add(tree);
        return group;
    }

    // --- データ読み込みロジック ---

    private void LoadDateList()
    {
        if (!Directory.Exists(_logDir))
        {
            _lblSummary.Text = "ログフォルダが見つかりません (まだ実行されていません)";
            return;
        }

        // report-yyyyMMdd.jsonl を探す
        var files = Directory.GetFiles(_logDir, "report-*.jsonl")
                             .OrderByDescending(f => f) // 新しい順
                             .ToArray();

        _dateSelector.Items.Clear();
        foreach (var file in files)
        {
            // ファイル名から日付表示を作る
            var name = Path.GetFileNameWithoutExtension(file).Replace("report-", "");
            if (name.Length == 8) 
                name = $"{name.Substring(0, 4)}/{name.Substring(4, 2)}/{name.Substring(6, 2)}";
            
            _dateSelector.Items.Add(new DateItem { Display = name, FilePath = file });
        }

        if (_dateSelector.Items.Count > 0)
        {
            _dateSelector.SelectedIndex = 0; // 最新を選択して読み込み開始
        }
        else
        {
            _lblSummary.Text = "ログファイルがありません";
        }
    }

    private void OnDateChanged(object? sender, EventArgs e)
    {
        if (_dateSelector.SelectedItem is not DateItem item) return;
        LoadLogFile(item.FilePath);
    }

    private void LoadLogFile(string path)
    {
        // ツリーをクリア
        _treeCopy.Nodes.Clear();
        _treeDelete.Nodes.Clear();
        _treeError.Nodes.Clear();

        // 描画停止 (高速化)
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
                    // JSONパース
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                    if (entry == null) continue;

                    // 種類ごとに振り分け
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
                catch
                {
                    // パースエラーは無視して次の行へ
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("ログファイルの読み込みに失敗しました:\n" + ex.Message);
        }
        finally
        {
            // 描画再開
            _treeCopy.EndUpdate();
            _treeDelete.EndUpdate();
            _treeError.EndUpdate();
            
            // 第1階層だけ展開しておく (全部展開すると重いので)
            ExpandLevel1(_treeCopy);
            ExpandLevel1(_treeDelete);
            ExpandLevel1(_treeError);
        }

        // 集計表示
        _lblSummary.Text = $"[コピー] {countCopy}件 ({FormatSize(sizeCopy)})   [削除] {countDelete}件   [エラー] {countError}件";
    }

    // パスを分解してツリーに追加する重要ロジック
    private void AddPathToTree(TreeView tree, string fullPath, string extraInfo)
    {
        // "C:\Users\Name\File.txt" -> ["C:", "Users", "Name", "File.txt"]
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        
        TreeNodeCollection currentNodes = tree.Nodes;
        TreeNode? lastNode = null;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            // 今の階層に同じ名前のノードがあるか探す
            TreeNode? foundNode = null;
            foreach (TreeNode node in currentNodes)
            {
                if (node.Text == part) // 完全一致で探す
                {
                    foundNode = node;
                    break;
                }
            }

            if (foundNode != null)
            {
                // あればその下へ
                currentNodes = foundNode.Nodes;
                lastNode = foundNode;
            }
            else
            {
                // なければ作る
                lastNode = currentNodes.Add(part);
                currentNodes = lastNode.Nodes; // 次はその下へ
            }
        }

        // 一番末端（ファイル名）のノードにサイズやエラー情報を追記
        if (lastNode != null && !string.IsNullOrEmpty(extraInfo))
        {
            lastNode.Text += $"  [{extraInfo}]";
            // エラーの場合は赤文字にしてみる
            if (!string.IsNullOrEmpty(extraInfo) && tree == _treeError)
            {
                lastNode.ForeColor = Color.Red;
            }
        }
    }

    private void ExpandLevel1(TreeView tree)
    {
        foreach (TreeNode node in tree.Nodes)
        {
            node.Expand();
        }
    }

    // サイズ表記 (B, KB, MB...)
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

    // コンボボックス用クラス
    private class DateItem
    {
        public string Display { get; set; } = "";
        public string FilePath { get; set; } = "";
        public override string ToString() => Display;
    }
}