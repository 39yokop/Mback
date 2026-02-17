using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;

namespace MBack.Config;

public class ExclusionForm : Form
{
    public List<string> Exclusions { get; private set; }
    
    // UIコントロール
    private ListBox _listBox = new();
    private Button _btnRemove = new();
    
    private TextBox _txtInput = new();
    private Button _btnAdd = new();

    private Button _btnAddPresets = new(); // ★新機能: よくあるゴミを一発追加
    
    private TextBox _txtBulkInput = new();
    private Button _btnBulkAdd = new();
    
    private Button _btnOk = new();

    public ExclusionForm(List<string> currentExclusions)
    {
        // リストのコピーを作成（キャンセル時に元のリストを壊さないため）
        Exclusions = new List<string>(currentExclusions ?? new List<string>());
        InitializeComponent();
        RefreshList();
    }

    private void InitializeComponent()
    {
        this.Text = "除外設定 (ファイル名パターン)";
        this.Size = new Size(500, 650);
        this.StartPosition = FormStartPosition.CenterParent;

        // --- 1. 最下部 (OKボタン) ---
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10) };
        _btnOk.Text = "設定完了 (閉じる)";
        _btnOk.Dock = DockStyle.Fill;
        _btnOk.DialogResult = DialogResult.OK;
        bottomPanel.Controls.Add(_btnOk);

        // --- 2. 一括追加エリア (下から2番目) ---
        var bulkGroup = new GroupBox { Text = "一括追加 (手動入力)", Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(5) };
        
        var bulkBtnPanel = new Panel { Dock = DockStyle.Bottom, Height = 35 };
        _btnBulkAdd.Text = "テキストボックスの内容を追加";
        _btnBulkAdd.Dock = DockStyle.Fill;
        _btnBulkAdd.Click += OnBulkAddClick;
        bulkBtnPanel.Controls.Add(_btnBulkAdd);

        _txtBulkInput.Multiline = true;
        _txtBulkInput.ScrollBars = ScrollBars.Vertical;
        _txtBulkInput.PlaceholderText = "*.log\r\ncache\r\ntemp";
        _txtBulkInput.Dock = DockStyle.Fill;

        bulkGroup.Controls.Add(_txtBulkInput);
        bulkGroup.Controls.Add(bulkBtnPanel); // ボタンを下に

        // --- 3. ★新機能: プリセット追加エリア (下から3番目) ---
        var presetPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(5) };
        _btnAddPresets.Text = "★ 一般的な不要ファイル (Office一時ファイル・画像サムネ等) を一発追加";
        _btnAddPresets.Dock = DockStyle.Fill;
        _btnAddPresets.BackColor = Color.LightYellow; // 目立つように
        _btnAddPresets.Click += OnAddPresetsClick;
        presetPanel.Controls.Add(_btnAddPresets);

        // --- 4. 1件追加エリア (下から4番目) ---
        var singlePanel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(5) };
        
        _btnAdd.Text = "追加";
        _btnAdd.Width = 80;
        _btnAdd.Dock = DockStyle.Right; // 右に固定
        _btnAdd.Click += (s, e) => AddItem(_txtInput.Text);

        _txtInput.PlaceholderText = "*.tmp";
        _txtInput.Dock = DockStyle.Fill; // 残りを埋める
        // テキストボックス内でEnterキーを押したら追加
        _txtInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddItem(_txtInput.Text); e.SuppressKeyPress = true; } };

        singlePanel.Controls.Add(_txtInput); // 先に追加(Fill)
        singlePanel.Controls.Add(_btnAdd);   // 後に追加(Right)

        // --- 5. リスト表示エリア (残り全部) ---
        var listGroup = new GroupBox { Text = "現在の除外リスト", Dock = DockStyle.Fill, Padding = new Padding(5) };
        
        var listBtnPanel = new Panel { Dock = DockStyle.Right, Width = 80 };
        _btnRemove.Text = "選択削除";
        _btnRemove.Dock = DockStyle.Top;
        _btnRemove.Height = 40;
        _btnRemove.Click += OnRemoveClick;
        listBtnPanel.Controls.Add(_btnRemove);

        _listBox.Dock = DockStyle.Fill;

        listGroup.Controls.Add(_listBox);
        listGroup.Controls.Add(listBtnPanel);

        // --- フォームへの配置 (下から順に積み上げるイメージではないが、Dockの性質上順番が大事) ---
        // Dock.Bottom は「先に追加したものが一番下」になります
        this.Controls.Add(listGroup);    // Fill (残り全部)
        this.Controls.Add(singlePanel);  // Bottom
        this.Controls.Add(presetPanel);  // Bottom
        this.Controls.Add(bulkGroup);    // Bottom
        this.Controls.Add(bottomPanel);  // Bottom (一番下)
    }

    // --- ロジック ---

    // 1件追加
    private void AddItem(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        string val = item.Trim();
        if (!Exclusions.Contains(val, StringComparer.OrdinalIgnoreCase))
        {
            Exclusions.Add(val);
            RefreshList();
        }
        _txtInput.Clear();
        _txtInput.Focus();
    }

    // 削除
    private void OnRemoveClick(object? s, EventArgs e)
    {
        if (_listBox.SelectedIndex >= 0)
        {
            Exclusions.RemoveAt(_listBox.SelectedIndex);
            RefreshList();
        }
    }

    // ★新機能: よくある不要ファイルを一括追加
    private void OnAddPresetsClick(object? s, EventArgs e)
    {
        var presets = new string[]
        {
            "*.tmp",                     // 一般的な一時ファイル
            "~$*",                       // Officeの一時ファイル (Word/Excelが開いている時にできるやつ)
            "Thumbs.db",                 // Windowsの画像サムネイル
            "desktop.ini",               // フォルダの表示設定
            "*.bak",                     // バックアップファイル
            "*.log",                     // ログファイル
            "$RECYCLE.BIN",              // ゴミ箱
            "System Volume Information", // システムの復元ポイントなど
            ".DS_Store"                  // Macの管理ファイル(混入した場合用)
        };

        int count = 0;
        foreach (var p in presets)
        {
            if (!Exclusions.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                Exclusions.Add(p);
                count++;
            }
        }
        
        RefreshList();
        MessageBox.Show($"{count} 件のパターンを追加しました。", "完了");
    }

    // 手動一括追加
    private void OnBulkAddClick(object? s, EventArgs e)
    {
        var lines = _txtBulkInput.Lines;
        int count = 0;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                string val = line.Trim();
                if (!Exclusions.Contains(val, StringComparer.OrdinalIgnoreCase))
                {
                    Exclusions.Add(val);
                    count++;
                }
            }
        }
        RefreshList();
        _txtBulkInput.Clear();
        if (count > 0) MessageBox.Show($"{count} 件追加しました。", "完了");
    }

    private void RefreshList()
    {
        _listBox.Items.Clear();
        foreach (var item in Exclusions)
        {
            _listBox.Items.Add(item);
        }
    }
}