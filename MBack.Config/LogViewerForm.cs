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
using System.Threading;
using System.Threading.Tasks;

namespace MBack.Config;

public class LogViewerForm : Form
{
    // =========================================================
    // UIコントロール - 共通
    // =========================================================

    // モード切替ラジオボタン
    private RadioButton _rdoDateMode   = new() { Text = "● 日付別",    AutoSize = true };
    private RadioButton _rdoSearchMode = new() { Text = "○ 期間・検索", AutoSize = true };

    // 読み込み中・サマリー表示
    private Label _lblLoading = new();
    private Label _lblSummary = new();

    // =========================================================
    // UIコントロール - 日付別モード専用
    // =========================================================
    private Panel    _panelDateMode     = new();
    private ComboBox _dateSelector      = new();
    private Button   _btnRefresh        = new();
    private TextBox  _txtDateSearch     = new(); // ★新機能: リアルタイム絞り込み検索
    private Button   _btnDateSearchClear = new();

    // TreeView（日付別モード）
    private SplitContainer  _splitMain   = new();
    private SplitContainer  _splitSub    = new();
    private TreeView        _treeCopy    = new();
    private TreeView        _treeDelete  = new();
    private TreeView        _treeError   = new();
    private ContextMenuStrip _contextMenu = new();

    // =========================================================
    // UIコントロール - 期間・検索モード専用
    // =========================================================
    private Panel        _panelSearchMode = new();
    private ComboBox     _cboPreset       = new();
    private DateTimePicker _dtpStart      = new() { Format = DateTimePickerFormat.Short };
    private DateTimePicker _dtpEnd        = new() { Format = DateTimePickerFormat.Short };
    private TextBox      _txtKeyword      = new();
    private ComboBox     _cboType         = new();
    private Button       _btnSearch       = new();

    // ★新機能: 期間・検索モードのListView
    private ListView _listView = new();

    // =========================================================
    // データ管理
    // =========================================================
    private string _dbPath;
    private List<BackupPair> _backupPairs = new();

    // TreeViewノード検索をO(1)にするDictionaryキャッシュ
    private Dictionary<string, TreeNode> _copyNodeCache   = new();
    private Dictionary<string, TreeNode> _deleteNodeCache = new();
    private Dictionary<string, TreeNode> _errorNodeCache  = new();

    // 連続操作キャンセル用トークン
    private CancellationTokenSource? _loadCts = null;

    // ★日付別モードで読み込んだ全レコードのメモリキャッシュ（検索フィルタ用）
    private List<(string type, string path, long sz, string msg, string user, DateTime time, int count)>
        _cachedRecords = new();

    // ListViewのソート状態
    private int  _sortColumn    = -1;
    private bool _sortAscending = true;

    // 期間検索の初回上限件数（これを超えると全件確認ダイアログを出す）
    private const int SEARCH_LIMIT = 5000;

    // =========================================================
    // コンストラクタ
    // =========================================================

    public LogViewerForm()
    {
        this.Text          = "MBack 履歴復元センター";
        this.Size          = new Size(1200, 780);
        this.MinimumSize   = new Size(900, 600);
        this.StartPosition = FormStartPosition.CenterParent;

        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MBack", "Database", "history.db");

        EnsureDatabaseSchema();
        LoadSettings();
        SetupLayout();

        // フォームが画面に表示されてからスプリッターを調整して日付一覧を読み込む
        this.Shown += async (s, e) =>
        {
            FixSplitterLayout();
            await LoadDateListFromDbAsync();
        };
    }

    // =========================================================
    // DBスキーマ初期化（既存DBへのマイグレーション）
    // =========================================================

    private void EnsureDatabaseSchema()
    {
        if (!File.Exists(_dbPath)) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = new SqliteCommand("ALTER TABLE LogEntries ADD COLUMN User TEXT;", conn);
            cmd.ExecuteNonQuery();
        }
        catch { /* すでにカラムがある場合はエラーを無視 */ }
    }

    // =========================================================
    // レイアウト構築
    // =========================================================

    private void SetupLayout()
    {
        // ---------------------------------------------------------
        // トップパネル（2行構成: 1行目=モード切替、2行目=各モードの操作）
        // ---------------------------------------------------------
        var topPanel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 95,
            BackColor = Color.FromArgb(235, 240, 248),
            Padding   = new Padding(10, 6, 10, 4)
        };

        // --- 1行目: モード切替ラジオボタン ---
        _rdoDateMode.Location  = new Point(15, 8);
        _rdoDateMode.Font      = new Font(this.Font.FontFamily, 10, FontStyle.Bold);
        _rdoDateMode.ForeColor = Color.DarkBlue;
        _rdoDateMode.Checked   = true;
        _rdoDateMode.CheckedChanged += OnModeChanged;

        _rdoSearchMode.Location  = new Point(135, 8);
        _rdoSearchMode.Font      = new Font(this.Font.FontFamily, 10, FontStyle.Bold);
        _rdoSearchMode.ForeColor = Color.DarkGreen;
        _rdoSearchMode.CheckedChanged += OnModeChanged;

        // ローディング表示（読み込み中だけ表示）
        _lblLoading.Text      = "⏳ 読み込み中...";
        _lblLoading.AutoSize  = true;
        _lblLoading.ForeColor = Color.DarkOrange;
        _lblLoading.Font      = new Font(this.Font, FontStyle.Bold);
        _lblLoading.Visible   = false;
        _lblLoading.Location  = new Point(870, 10);

        // サマリー表示
        _lblSummary.AutoSize = true;
        _lblSummary.Location = new Point(620, 10);

        topPanel.Controls.AddRange(new Control[]
            { _rdoDateMode, _rdoSearchMode, _lblLoading, _lblSummary });

        // --- 2行目: 各モードのコントロール（切替で表示/非表示） ---
        BuildDateModePanel(topPanel);
        BuildSearchModePanel(topPanel);

        // ---------------------------------------------------------
        // 右クリックメニュー（日付別モードのTreeView専用）
        // ---------------------------------------------------------
        var menuVersion = new ToolStripMenuItem(
            "★ 世代選択と復元...", null, OnShowVersionsClick)
            { Font = new Font(this.Font, FontStyle.Bold) };
        var menuRestore = new ToolStripMenuItem(
            "最新から即時復元", null, (s, e) => RestoreFile(null));
        var menuOpen = new ToolStripMenuItem(
            "バックアップ先を開く", null, OnOpenBackupFolderClick);
        _contextMenu.Items.AddRange(new ToolStripItem[]
            { menuVersion, new ToolStripSeparator(), menuRestore, menuOpen });
        _treeCopy.ContextMenuStrip  = _contextMenu;
        _treeDelete.ContextMenuStrip = _contextMenu;

        // ---------------------------------------------------------
        // TreeView 3分割（日付別モード用）
        // ---------------------------------------------------------
        _splitMain.Dock           = DockStyle.Fill;
        _splitMain.BorderStyle    = BorderStyle.Fixed3D;
        _splitMain.Panel1MinSize  = 50;
        _splitMain.Panel2MinSize  = 100;

        _splitSub.Dock           = DockStyle.Fill;
        _splitSub.BorderStyle    = BorderStyle.Fixed3D;
        _splitSub.Panel1MinSize  = 50;
        _splitSub.Panel2MinSize  = 50;

        _splitMain.Panel1.Controls.Add(CreateTreeGroup("コピー済み",    _treeCopy,   Color.DarkBlue));
        _splitMain.Panel2.Controls.Add(_splitSub);
        _splitSub.Panel1.Controls.Add(CreateTreeGroup("ゴミ箱 (削除)", _treeDelete, Color.DarkRed));
        _splitSub.Panel2.Controls.Add(CreateTreeGroup("エラーログ",     _treeError,  Color.Red));

        // ---------------------------------------------------------
        // ListView（期間・検索モード用）
        // ---------------------------------------------------------
        BuildListView();

        // ---------------------------------------------------------
        // フォームへの追加（追加順序がDockレイアウトに影響するため注意）
        // ---------------------------------------------------------
        this.Controls.Add(_splitMain);  // Dock=Fill: 日付別モード（初期表示）
        this.Controls.Add(_listView);   // Dock=Fill: 期間検索モード（初期非表示）
        this.Controls.Add(topPanel);    // Dock=Top

        _listView.Visible  = false;
        _splitMain.Visible = true;
    }

    /// <summary>
    /// 日付別モードの2行目コントロール群をトップパネルに追加する。
    /// </summary>
    private void BuildDateModePanel(Panel topPanel)
    {
        _panelDateMode.Location  = new Point(10, 36);
        _panelDateMode.Size      = new Size(1100, 50);
        _panelDateMode.BackColor = Color.Transparent;

        var lblDate = new Label
        {
            Text     = "日付:",
            AutoSize = true,
            Location = new Point(0, 13),
            Font     = new Font(this.Font, FontStyle.Bold)
        };

        _dateSelector.Location        = new Point(45, 9);
        _dateSelector.Width           = 140;
        _dateSelector.DropDownStyle   = ComboBoxStyle.DropDownList;
        _dateSelector.SelectedIndexChanged += OnDateChangedAsync;

        _btnRefresh.Text     = "🔄 更新";
        _btnRefresh.Location = new Point(195, 8);
        _btnRefresh.Size     = new Size(75, 28);
        _btnRefresh.Click   += async (s, e) =>
        {
            await LoadDateListFromDbAsync();
            if (_dateSelector.SelectedItem?.ToString() is string d)
                await LoadLogFromDbAsync(d);
        };

        // ★新機能: リアルタイム絞り込み検索テキストボックス
        var lblSearch = new Label
        {
            Text     = "🔍 絞り込み:",
            AutoSize = true,
            Location = new Point(285, 13),
            Font     = new Font(this.Font, FontStyle.Bold)
        };

        _txtDateSearch.Location        = new Point(385, 9);
        _txtDateSearch.Size            = new Size(290, 26);
        _txtDateSearch.PlaceholderText = "ファイル名・パスで絞り込み（リアルタイム）";
        // テキスト変更のたびに即座にフィルタを適用する（DBへの再クエリは不要）
        _txtDateSearch.TextChanged += (s, e) => ApplyDateModeFilter();

        _btnDateSearchClear.Text     = "✕";
        _btnDateSearchClear.Location = new Point(680, 8);
        _btnDateSearchClear.Size     = new Size(30, 28);
        _btnDateSearchClear.Click   += (s, e) => { _txtDateSearch.Clear(); _txtDateSearch.Focus(); };

        _panelDateMode.Controls.AddRange(new Control[]
            { lblDate, _dateSelector, _btnRefresh, lblSearch, _txtDateSearch, _btnDateSearchClear });

        topPanel.Controls.Add(_panelDateMode);
    }

    /// <summary>
    /// 期間・検索モードの2行目コントロール群をトップパネルに追加する。
    /// </summary>
    private void BuildSearchModePanel(Panel topPanel)
    {
        _panelSearchMode.Location  = new Point(10, 36);
        _panelSearchMode.Size      = new Size(1150, 55);
        _panelSearchMode.BackColor = Color.Transparent;
        _panelSearchMode.Visible   = false; // 初期非表示

        // 期間プリセット選択
        var lblPreset = new Label
            { Text = "期間:", AutoSize = true, Location = new Point(0, 13),
              Font = new Font(this.Font, FontStyle.Bold) };

        _cboPreset.Location        = new Point(48, 9);
        _cboPreset.Width           = 100;
        _cboPreset.DropDownStyle   = ComboBoxStyle.DropDownList;
        _cboPreset.Items.AddRange(new object[] { "過去30日", "カスタム" });
        _cboPreset.SelectedIndex   = 0;
        _cboPreset.SelectedIndexChanged += OnPresetChanged;

        // カスタム期間 DateTimePicker
        var lblFrom = new Label
            { Text = "開始:", AutoSize = true, Location = new Point(158, 13) };
        _dtpStart.Location = new Point(193, 9);
        _dtpStart.Size     = new Size(112, 26);
        _dtpStart.Value    = DateTime.Today.AddDays(-30);
        _dtpStart.Enabled  = false; // 「過去30日」選択中は無効

        var lblTo = new Label
            { Text = "〜", AutoSize = true, Location = new Point(310, 13) };
        _dtpEnd.Location = new Point(325, 9);
        _dtpEnd.Size     = new Size(112, 26);
        _dtpEnd.Value    = DateTime.Today;
        _dtpEnd.Enabled  = false;

        // キーワード入力
        var lblKeyword = new Label
            { Text = "🔍 キーワード:", AutoSize = true, Location = new Point(450, 13),
              Font = new Font(this.Font, FontStyle.Bold) };
        _txtKeyword.Location        = new Point(555, 9);
        _txtKeyword.Size            = new Size(240, 26);
        _txtKeyword.PlaceholderText = "ファイル名・パスで絞り込み";

        // 種別フィルタ
        var lblType = new Label
            { Text = "種別:", AutoSize = true, Location = new Point(808, 13) };
        _cboType.Location      = new Point(845, 9);
        _cboType.Width         = 90;
        _cboType.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboType.Items.AddRange(new object[] { "すべて", "Copy", "Delete", "Error" });
        _cboType.SelectedIndex = 0;

        // 検索実行ボタン
        _btnSearch.Text      = "🔍 検索";
        _btnSearch.Location  = new Point(945, 8);
        _btnSearch.Size      = new Size(90, 30);
        _btnSearch.BackColor = Color.LightGreen;
        _btnSearch.Font      = new Font(this.Font, FontStyle.Bold);
        _btnSearch.Click    += async (s, e) => await ExecuteSearchAsync(false);

        _panelSearchMode.Controls.AddRange(new Control[]
        {
            lblPreset, _cboPreset, lblFrom, _dtpStart, lblTo, _dtpEnd,
            lblKeyword, _txtKeyword, lblType, _cboType, _btnSearch
        });

        topPanel.Controls.Add(_panelSearchMode);
    }

    /// <summary>
    /// 期間・検索モード用のListViewを構築する。
    /// </summary>
    private void BuildListView()
    {
        _listView.Dock         = DockStyle.Fill;
        _listView.View         = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines    = true;
        _listView.MultiSelect  = false;
        _listView.Font         = new Font("メイリオ", 9);

        // 列定義（ヘッダークリックでソート可能）
        _listView.Columns.Add("種別",       60);
        _listView.Columns.Add("日時",       145);
        _listView.Columns.Add("ファイル名",  260);
        _listView.Columns.Add("サイズ",      80);
        _listView.Columns.Add("ユーザー",   110);
        _listView.Columns.Add("フルパス",   450);

        // 列ヘッダークリックでソートする
        _listView.ColumnClick += OnListViewColumnClick;
    }

    private void FixSplitterLayout()
    {
        try
        {
            int totalW = _splitMain.Width;
            if (totalW > 300)
            {
                _splitMain.SplitterDistance = totalW / 3;
                _splitSub.SplitterDistance  = _splitMain.Panel2.Width / 2;
            }
        }
        catch { }
    }

    // =========================================================
    // モード切替
    // =========================================================

    /// <summary>
    /// ラジオボタンでモードを切り替える。
    /// 日付別モード ↔ 期間・検索モードでUIを入れ替える。
    /// </summary>
    private void OnModeChanged(object? sender, EventArgs e)
    {
        bool isDateMode = _rdoDateMode.Checked;

        _panelDateMode.Visible   = isDateMode;
        _panelSearchMode.Visible = !isDateMode;
        _splitMain.Visible       = isDateMode;
        _listView.Visible        = !isDateMode;

        _lblSummary.Text = "";

        if (isDateMode)
        {
            // 日付別モードに戻ったとき、現在選択中の日付で再表示する
            if (_dateSelector.SelectedItem?.ToString() is string d)
                _ = LoadLogFromDbAsync(d);
        }
        else
        {
            // 期間・検索モードに切り替えたとき、ListViewをクリアして案内を表示する
            _listView.Items.Clear();
            _lblSummary.Text = "期間とキーワードを指定して「検索」ボタンを押してください";
        }
    }

    // =========================================================
    // 日付別モード - データ読み込みロジック
    // =========================================================

    /// <summary>
    /// DBから日付の一覧を非同期で読み込んでコンボボックスに反映する。
    /// </summary>
    private async Task LoadDateListFromDbAsync()
    {
        if (!File.Exists(_dbPath)) return;

        List<string> dates = await Task.Run(() =>
        {
            var result = new List<string>();
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                string sql = "SELECT DISTINCT strftime('%Y-%m-%d', Time) as LogDate " +
                             "FROM LogEntries ORDER BY LogDate DESC";
                using var cmd    = new SqliteCommand(sql, conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(reader.GetString(0));
            }
            catch { }
            return result;
        });

        // UIの更新はメインスレッドで行う
        string? sel = _dateSelector.SelectedItem?.ToString();
        _dateSelector.SelectedIndexChanged -= OnDateChangedAsync; // イベントを一時停止
        _dateSelector.Items.Clear();
        foreach (var d in dates) _dateSelector.Items.Add(d);

        if (_dateSelector.Items.Count > 0)
        {
            int idx = sel != null ? _dateSelector.FindStringExact(sel) : 0;
            _dateSelector.SelectedIndex = idx >= 0 ? idx : 0;
        }
        _dateSelector.SelectedIndexChanged += OnDateChangedAsync; // イベントを再開

        if (_dateSelector.SelectedItem?.ToString() is string date)
            await LoadLogFromDbAsync(date);
    }

    private async void OnDateChangedAsync(object? sender, EventArgs e)
    {
        if (_dateSelector.SelectedItem is string d)
            await LoadLogFromDbAsync(d);
    }

    /// <summary>
    /// 指定日付のログをDBから非同期で読み込んでTreeViewに反映する。
    /// 読み込んだ全レコードを _cachedRecords にキャッシュして、
    /// 検索フィルタ時にDBへの再クエリを不要にする。
    /// </summary>
    private async Task LoadLogFromDbAsync(string dateStr)
    {
        // 日付切替の連打に対応: 前の読み込みをキャンセルする
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        SetLoadingState(true);
        ClearTreeViews();

        try
        {
            var records = await Task.Run(() =>
            {
                var list = new List<(string type, string path, long sz, string msg,
                    string user, DateTime time, int count)>();
                try
                {
                    using var conn = new SqliteConnection($"Data Source={_dbPath}");
                    conn.Open();
                    // GROUP BYで同一パスの複数回更新を1行に集約する
                    string sql = @"
                        SELECT Type, Path, MAX(Size), MAX(Message), MAX(User), MAX(Time), COUNT(Id)
                        FROM LogEntries
                        WHERE date(Time) = @date
                        GROUP BY Type, Path
                        ORDER BY MAX(Time) ASC";
                    using var cmd    = new SqliteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@date", dateStr);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (token.IsCancellationRequested) break;
                        list.Add((
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? 0    : reader.GetInt64(2),
                            reader.IsDBNull(3) ? ""   : reader.GetString(3),
                            reader.IsDBNull(4) ? "System" : reader.GetString(4),
                            reader.GetDateTime(5),
                            reader.GetInt32(6)
                        ));
                    }
                }
                catch { }
                return list;
            }, token);

            if (token.IsCancellationRequested) return;

            // ★全レコードをメモリにキャッシュする（検索フィルタで再利用）
            _cachedRecords = records;

            // 現在の検索テキストを引き継いでTreeViewを構築する
            BuildTreeViewFromCache(_txtDateSearch.Text.Trim());
        }
        catch (OperationCanceledException) { /* キャンセルは正常動作 */ }
        catch (Exception ex)
        {
            MessageBox.Show($"ログの読み込みに失敗しました。\n{ex.Message}",
                "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    /// <summary>
    /// ★新機能: テキスト変更のたびに呼ばれるリアルタイム絞り込みフィルタ。
    /// DBに再クエリせず、メモリ上の _cachedRecords を絞り込んでTreeViewを再構築する。
    /// </summary>
    private void ApplyDateModeFilter()
    {
        ClearTreeViews();
        BuildTreeViewFromCache(_txtDateSearch.Text.Trim());
    }

    /// <summary>
    /// _cachedRecords からTreeViewを構築するメインロジック。
    /// keyword が空なら全件、指定した場合はパスに含む行のみ表示する。
    /// </summary>
    private void BuildTreeViewFromCache(string keyword)
    {
        int  uniqueCopyCount = 0;
        long sizeTotal       = 0;

        _treeCopy.BeginUpdate();
        _treeDelete.BeginUpdate();
        _treeError.BeginUpdate();

        foreach (var (type, path, sz, msg, user, time, updateCount) in _cachedRecords)
        {
            // キーワードフィルタ（大文字小文字を区別しない部分一致）
            if (!string.IsNullOrEmpty(keyword) &&
                !path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            string updateInfo = updateCount > 1
                ? $"[計{updateCount}回更新 | 最新 {time:HH:mm:ss}]"
                : $"[{time:HH:mm:ss}]";

            switch (type)
            {
                case "Copy":
                    AddPathToTreeDict(_treeCopy, _copyNodeCache, path,
                        $"{updateInfo} {FormatSize(sz)} ({user})");
                    uniqueCopyCount++;
                    sizeTotal += sz;
                    break;
                case "Delete":
                    AddPathToTreeDict(_treeDelete, _deleteNodeCache, path,
                        $"{updateInfo} ({user})");
                    break;
                case "Error":
                    AddPathToTreeDict(_treeError, _errorNodeCache, path,
                        $"{updateInfo} {msg}");
                    break;
            }
        }

        // BeginUpdate内でExpand()も実行して描画コストを削減する
        foreach (TreeNode n in _treeCopy.Nodes)   n?.Expand();
        foreach (TreeNode n in _treeDelete.Nodes) n?.Expand();
        foreach (TreeNode n in _treeError.Nodes)  n?.Expand();

        _treeCopy.EndUpdate();
        _treeDelete.EndUpdate();
        _treeError.EndUpdate();

        // 絞り込み中であれば件数に「(絞り込み中)」を付記する
        string filterMark = string.IsNullOrEmpty(keyword) ? "" : " 🔍(絞り込み中)";
        _lblSummary.Text =
            $"[対象ファイル] {uniqueCopyCount}個 (最新合計: {FormatSize(sizeTotal)}){filterMark}";
    }

    // =========================================================
    // 期間・検索モード - データ読み込みロジック
    // =========================================================

    /// <summary>
    /// プリセット変更時にDateTimePickerの有効/無効を切り替える。
    /// </summary>
    private void OnPresetChanged(object? sender, EventArgs e)
    {
        bool isCustom = _cboPreset.SelectedItem?.ToString() == "カスタム";
        _dtpStart.Enabled = isCustom;
        _dtpEnd.Enabled   = isCustom;

        if (!isCustom)
        {
            _dtpStart.Value = DateTime.Today.AddDays(-30);
            _dtpEnd.Value   = DateTime.Today;
        }
    }

    /// <summary>
    /// ★新機能: 期間・キーワード・種別を組み合わせてDBを非同期検索する。
    /// isFullSearch=false の場合は LIMIT 5000 で打ち切り、
    /// 5000件に達したらユーザーに全件検索を問い合わせるダイアログを表示する。
    /// </summary>
    private async Task ExecuteSearchAsync(bool isFullSearch)
    {
        if (!File.Exists(_dbPath)) return;

        // 検索条件を確定する
        DateTime startDate, endDate;
        if (_cboPreset.SelectedItem?.ToString() == "過去30日")
        {
            startDate = DateTime.Today.AddDays(-30);
            endDate   = DateTime.Today.AddDays(1); // 今日の分も含める
        }
        else
        {
            startDate = _dtpStart.Value.Date;
            endDate   = _dtpEnd.Value.Date.AddDays(1);
        }

        string keyword    = _txtKeyword.Text.Trim();
        string typeFilter = _cboType.SelectedItem?.ToString() ?? "すべて";

        // 上限+1件取得することで「上限を超えた」を判定する
        int limit = isFullSearch ? int.MaxValue : SEARCH_LIMIT + 1;

        SetLoadingState(true);
        _listView.Items.Clear();
        _lblSummary.Text = "⏳ 検索中...";

        try
        {
            var records = await Task.Run(() =>
            {
                var list = new List<(string type, string path, long sz,
                    string msg, string user, DateTime time)>();
                try
                {
                    using var conn = new SqliteConnection($"Data Source={_dbPath}");
                    conn.Open();

                    // 検索条件に応じてWHERE句を動的に組み立てる
                    var conditions = new List<string> { "Time >= @start AND Time < @end" };
                    if (typeFilter != "すべて")
                        conditions.Add("Type = @type");
                    if (!string.IsNullOrEmpty(keyword))
                        conditions.Add("Path LIKE @keyword");

                    string limitClause = limit == int.MaxValue ? "" : $"LIMIT {limit}";
                    string sql = $@"
                        SELECT Type, Path, Size, Message, User, Time
                        FROM LogEntries
                        WHERE {string.Join(" AND ", conditions)}
                        ORDER BY Time DESC
                        {limitClause}";

                    using var cmd = new SqliteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@start", startDate);
                    cmd.Parameters.AddWithValue("@end",   endDate);
                    if (typeFilter != "すべて")
                        cmd.Parameters.AddWithValue("@type", typeFilter);
                    if (!string.IsNullOrEmpty(keyword))
                        cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        list.Add((
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? 0    : reader.GetInt64(2),
                            reader.IsDBNull(3) ? ""   : reader.GetString(3),
                            reader.IsDBNull(4) ? "System" : reader.GetString(4),
                            reader.GetDateTime(5)
                        ));
                    }
                }
                catch { }
                return list;
            });

            // 上限超過の判定（LIMIT+1件取れていれば超過）
            bool hitLimit = !isFullSearch && records.Count > SEARCH_LIMIT;
            if (hitLimit) records = records.Take(SEARCH_LIMIT).ToList();

            // ListViewに結果を反映する
            _listView.BeginUpdate();
            _listView.Items.Clear();

            foreach (var (type, path, sz, msg, user, time) in records)
            {
                var item = new ListViewItem(type);
                item.SubItems.Add(time.ToString("yyyy/MM/dd HH:mm:ss"));
                item.SubItems.Add(Path.GetFileName(path));
                item.SubItems.Add(FormatSize(sz));
                item.SubItems.Add(user);
                item.SubItems.Add(path);

                // 種別によって行の背景色を変えて視認性を高める
                item.BackColor = type switch
                {
                    "Copy"   => Color.FromArgb(240, 248, 255), // 薄い青
                    "Delete" => Color.FromArgb(255, 245, 245), // 薄い赤
                    "Error"  => Color.FromArgb(255, 250, 220), // 薄い黄
                    _        => Color.White
                };

                _listView.Items.Add(item);
            }

            _listView.EndUpdate();

            _lblSummary.Text = hitLimit
                ? $"[検索結果] 上位 {SEARCH_LIMIT:N0} 件を表示中"
                : $"[検索結果] {records.Count:N0} 件";

            // ★5,000件超過時: ユーザーに全件検索を促すダイアログを表示する
            if (hitLimit)
            {
                var answer = MessageBox.Show(
                    $"検索結果が {SEARCH_LIMIT:N0} 件を超えています。\n\n" +
                    "全件を検索しますか？\n" +
                    "（件数によっては時間がかかる場合があります）",
                    "全件検索の確認",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer == DialogResult.Yes)
                    await ExecuteSearchAsync(true); // 全件で再実行
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"検索に失敗しました。\n{ex.Message}",
                "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    /// <summary>
    /// ListViewの列ヘッダークリックでソートする。
    /// 同じ列を再クリックすると昇順/降順が切り替わる。
    /// </summary>
    private void OnListViewColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (_sortColumn == e.Column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn    = e.Column;
            _sortAscending = true;
        }

        _listView.ListViewItemSorter = new ListViewItemComparer(e.Column, _sortAscending);
        _listView.Sort();
    }

    // =========================================================
    // 共通ユーティリティ
    // =========================================================

    /// <summary>
    /// TreeViewを全クリアしてDictionaryキャッシュもリセットする。
    /// </summary>
    private void ClearTreeViews()
    {
        _treeCopy.BeginUpdate();
        _treeDelete.BeginUpdate();
        _treeError.BeginUpdate();
        _treeCopy.Nodes.Clear();
        _treeDelete.Nodes.Clear();
        _treeError.Nodes.Clear();
        _copyNodeCache.Clear();
        _deleteNodeCache.Clear();
        _errorNodeCache.Clear();
        _treeCopy.EndUpdate();
        _treeDelete.EndUpdate();
        _treeError.EndUpdate();
    }

    /// <summary>
    /// 読み込み中のUI状態を切り替える。
    /// 操作系コントロールを無効にして誤操作を防ぐ。
    /// </summary>
    private void SetLoadingState(bool isLoading)
    {
        _lblLoading.Visible   = isLoading;
        _dateSelector.Enabled = !isLoading;
        _btnRefresh.Enabled   = !isLoading;
        _btnSearch.Enabled    = !isLoading;
        _rdoDateMode.Enabled  = !isLoading;
        _rdoSearchMode.Enabled = !isLoading;
    }

    /// <summary>
    /// ファイルパスをTreeViewに階層表示する。
    /// Dictionaryキャッシュでノード検索をO(1)にしている。
    /// </summary>
    private void AddPathToTreeDict(
        TreeView tree,
        Dictionary<string, TreeNode> cache,
        string fullPath,
        string info)
    {
        string[] parts = fullPath.Split(Path.DirectorySeparatorChar);
        TreeNodeCollection nodes = tree.Nodes;
        TreeNode? last = null;
        string builtPath = "";

        foreach (var part in parts.Where(s => !string.IsNullOrEmpty(s)))
        {
            builtPath = builtPath.Length == 0 ? part : builtPath + @"\" + part;

            if (!cache.TryGetValue(builtPath, out TreeNode? found))
            {
                found = nodes.Add(part);
                cache[builtPath] = found;
            }

            nodes = found.Nodes;
            last  = found;
        }

        if (last != null)
        {
            last.Text += $"  {info}";
            last.Tag   = fullPath; // 右クリックの復元処理で使うフルパスを保持する
        }
    }

    private string FormatSize(long b)
    {
        string[] s = { "B", "KB", "MB", "GB", "TB" };
        double l = b; int i = 0;
        while (l >= 1024 && i < 4) { i++; l /= 1024; }
        return $"{l:0.##} {s[i]}";
    }

    private GroupBox CreateTreeGroup(string title, TreeView tv, Color color)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, ForeColor = color };
        tv.Dock = DockStyle.Fill;
        tv.NodeMouseClick += (s, e) => tv.SelectedNode = e.Node;
        g.Controls.Add(tv);
        return g;
    }

    private void LoadSettings()
    {
        try
        {
            string p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MBack", "appsettings.json");
            if (File.Exists(p))
            {
                var s = JsonSerializer.Deserialize<AppSettingsRaw>(File.ReadAllText(p));
                if (s?.BackupSettings != null) _backupPairs = s.BackupSettings;
            }
        }
        catch { }
    }

    // =========================================================
    // 復元・操作ロジック（既存から完全維持）
    // =========================================================

    private void OnShowVersionsClick(object? sender, EventArgs e)
    {
        TreeView? t = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (t?.SelectedNode?.Tag is not string path) return;

        string? bPath = GetBackupPath(path);
        if (bPath == null) return;

        var vers = new List<string>();
        if (File.Exists(bPath)) vers.Add(bPath);
        for (int i = 1; i <= 50; i++)
        {
            string v = $"{bPath}.v{i}";
            if (File.Exists(v)) vers.Add(v);
        }
        if (vers.Count == 0) return;

        using var f = new Form
        {
            Text          = "世代選択: " + Path.GetFileName(path),
            Size          = new Size(650, 450),
            StartPosition = FormStartPosition.CenterParent
        };

        var grid = new DataGridView
        {
            Dock             = DockStyle.Fill,
            ReadOnly         = true,
            AllowUserToAddRows = false,
            SelectionMode    = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor  = Color.White
        };
        grid.Columns.Add("Type", "種別");
        grid.Columns.Add("Date", "バックアップ日時");
        grid.Columns.Add("Size", "サイズ");
        grid.Columns[0].Width = 60;
        grid.Columns[2].Width = 80;

        foreach (var v in vers)
        {
            var info = new FileInfo(v);
            grid.Rows.Add(
                v == bPath ? "最新" : "履歴",
                info.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"),
                FormatSize(info.Length));
        }

        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(5) };
        var btnExport = new Button
        {
            Text      = "🔍 別の場所に保存して確認",
            Dock      = DockStyle.Right,
            Width     = 280,
            Font      = new Font(f.Font, FontStyle.Bold),
            BackColor = Color.LightYellow
        };
        var btnRestore = new Button
        {
            Text      = "⚠️ 元の場所に上書き復元",
            Dock      = DockStyle.Left,
            Width     = 280,
            Font      = new Font(f.Font, FontStyle.Bold),
            BackColor = Color.LightPink
        };

        btnRestore.Click += (s, ev) =>
        {
            if (grid.SelectedRows.Count > 0)
            { RestoreFile(vers[grid.SelectedRows[0].Index], path); f.DialogResult = DialogResult.OK; }
        };
        btnExport.Click += (s, ev) =>
        {
            if (grid.SelectedRows.Count > 0)
                ExportForPreview(vers[grid.SelectedRows[0].Index], path);
        };
        grid.CellDoubleClick += (s, ev) =>
        {
            if (ev.RowIndex >= 0)
            { RestoreFile(vers[ev.RowIndex], path); f.DialogResult = DialogResult.OK; }
        };

        btnPanel.Controls.Add(btnRestore);
        btnPanel.Controls.Add(btnExport);
        f.Controls.Add(grid);
        f.Controls.Add(btnPanel);
        f.ShowDialog();
    }

    private void OnOpenBackupFolderClick(object? sender, EventArgs e)
    {
        TreeView? t = (_contextMenu.SourceControl as TreeView) ?? _treeCopy;
        if (t?.SelectedNode?.Tag is not string p) return;
        string? b = GetBackupPath(p);
        if (b != null)
        {
            string? folder = Path.GetDirectoryName(b);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                Process.Start("explorer.exe", folder);
        }
    }

    private void ExportForPreview(string versionPath, string originalPath)
    {
        using var sfd = new SaveFileDialog();
        sfd.FileName = Path.GetFileName(originalPath);
        sfd.Title    = "確認用に保存する場所を選んでください（デスクトップ推奨）";
        sfd.Filter   = "すべてのファイル (*.*)|*.*";

        if (sfd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                File.Copy(versionPath, sfd.FileName, true);
                if (MessageBox.Show("保存しました。今すぐ開いて中身を確認しますか？",
                    "プレビュー確認", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存に失敗しました: {ex.Message}",
                    "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void RestoreFile(string? vPath, string? tPath = null)
    {
        var node  = _treeCopy.SelectedNode ?? _treeDelete.SelectedNode;
        string path = tPath ?? node?.Tag as string ?? "";
        string? bPath = vPath ?? GetBackupPath(path);

        if (string.IsNullOrEmpty(path) || bPath == null || !File.Exists(bPath)) return;

        if (MessageBox.Show(
            $"⚠️警告⚠️\n以下のファイルをバックアップデータで【上書き】します。\n" +
            $"本当によろしいですか？\n\n対象: {path}",
            "上書き復元の最終確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            try
            {
                string? d = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                File.Copy(bPath, path, true);
                MessageBox.Show("正常に復元されました！", "完了",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show("復元エラー: " + ex.Message); }
        }
    }

    private string? GetBackupPath(string p)
    {
        foreach (var pair in _backupPairs)
        {
            if (p.StartsWith(pair.Source, StringComparison.OrdinalIgnoreCase))
            {
                string r = p.Substring(pair.Source.Length).TrimStart('\\');
                string n = Path.Combine(pair.Destination, r);

                string? dirName   = Path.GetDirectoryName(n);
                var     parentDir = Directory.GetParent(n);
                bool hasHistory   = false;

                if (parentDir != null && dirName != null && Directory.Exists(dirName))
                    hasHistory = parentDir.GetFiles(Path.GetFileName(n) + ".v*").Length > 0;

                if (File.Exists(n) || hasHistory) return n;
                return Path.Combine(pair.Destination, "_TRASH_", r);
            }
        }
        return null;
    }
}

// =========================================================
// ListViewのソート用ヘルパークラス
// =========================================================

/// <summary>
/// ListViewの列ヘッダークリックでソートするための比較クラス。
/// 日時列はDateTimeとして比較し、それ以外は文字列として比較する。
/// </summary>
internal class ListViewItemComparer : System.Collections.IComparer
{
    private readonly int  _col;
    private readonly bool _asc;

    public ListViewItemComparer(int column, bool ascending)
    {
        _col = column;
        _asc = ascending;
    }

    public int Compare(object? x, object? y)
    {
        if (x is not ListViewItem lx || y is not ListViewItem ly) return 0;

        string sx = lx.SubItems.Count > _col ? lx.SubItems[_col].Text : "";
        string sy = ly.SubItems.Count > _col ? ly.SubItems[_col].Text : "";

        int result;

        // 日時列（1列目）はDateTimeとして比較してソート精度を上げる
        if (_col == 1 &&
            DateTime.TryParse(sx, out var dx) &&
            DateTime.TryParse(sy, out var dy))
            result = dx.CompareTo(dy);
        else
            result = string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);

        return _asc ? result : -result;
    }
}
