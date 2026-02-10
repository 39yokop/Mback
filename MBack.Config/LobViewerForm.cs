using System.Text.RegularExpressions;

namespace MBack.Config;

public class LogViewerForm : Form
{
    private DataGridView _grid = new();
    private Button _btnRefresh = new();
    private Button _btnClose = new();
    private string _logPath;

    public LogViewerForm(string logPath)
    {
        _logPath = logPath;
        InitializeComponent();
        LoadLogs();
    }

    private void InitializeComponent()
    {
        this.Text = "バックアップ履歴ログ";
        this.Size = new Size(800, 600);

        // ボタンパネル
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        _btnClose.Text = "閉じる";
        _btnClose.Click += (s, e) => this.Close();
        
        _btnRefresh.Text = "最新の情報に更新";
        _btnRefresh.AutoSize = true;
        _btnRefresh.Click += (s, e) => LoadLogs();

        panel.Controls.Add(_btnClose);
        panel.Controls.Add(_btnRefresh);

        // グリッド設定
        _grid.Dock = DockStyle.Fill;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.RowHeadersVisible = false;

        // カラム定義
        _grid.Columns.Add("Time", "時刻");
        _grid.Columns.Add("Type", "種類");
        _grid.Columns.Add("Message", "内容");

        // 列の幅調整
        _grid.Columns[0].Width = 150; // 時刻
        _grid.Columns[1].Width = 100; // 種類

        this.Controls.Add(_grid);
        this.Controls.Add(panel);
    }

    private void LoadLogs()
    {
        _grid.Rows.Clear();

        if (!File.Exists(_logPath))
        {
            MessageBox.Show("ログファイルが見つかりません: " + _logPath);
            return;
        }

        try
        {
            // ファイルを読み込む（ロックされていても読めるように FileShare を指定）
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // ログの解析 (Serilogの標準フォーマットを想定)
                // 例: 2026-02-10 14:00:00 [INF] [初期同期] コピー: C:\test.txt
                ParseAndAddRow(line);
            }

            // 一番下（最新）までスクロール
            if (_grid.Rows.Count > 0)
                _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show("ログ読み込みエラー: " + ex.Message);
        }
    }

    private void ParseAndAddRow(string line)
    {
        // 簡易的なパース処理
        string time = "";
        string type = "その他";
        string msg = line;
        Color rowColor = Color.Black;

        // 日時を取得 (先頭の20文字くらい)
        if (line.Length > 20)
        {
            time = line.Substring(0, 19); // "yyyy-MM-dd HH:mm:ss"
            msg = line.Substring(20).Trim();
        }

        // メッセージから種類を判定して色を変える
        if (msg.Contains("[ERR]") || msg.Contains("エラー") || msg.Contains("失敗"))
        {
            type = "★エラー";
            rowColor = Color.Red;
        }
        else if (msg.Contains("[ゴミ箱]"))
        {
            type = "削除・移動";
            rowColor = Color.Gray;
        }
        else if (msg.Contains("[初期同期]") || msg.Contains("[リアルタイム同期]"))
        {
            type = "コピー成功";
            rowColor = Color.Blue;
        }
        else if (msg.Contains("[INF]"))
        {
            type = "情報";
            rowColor = Color.Black;
        }

        // 行を追加
        int index = _grid.Rows.Add(time, type, msg);
        _grid.Rows[index].DefaultCellStyle.ForeColor = rowColor;
    }
}