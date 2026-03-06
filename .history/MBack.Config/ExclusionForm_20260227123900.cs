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

    private Button _btnAddPresets = new(); 
    
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
        this.Text = "除外設定 (ファイル名・パスの一部)";
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
        // ★修正: プレースホルダーからアスタリスクを削除
        _txtBulkInput.PlaceholderText = ".log\r\ncache\r\ntemp";
        _txtBulkInput.Dock = DockStyle.Fill;

        bulkGroup.Controls.Add(_txtBulkInput);
        bulkGroup.Controls.Add(bulkBtnPanel); 

        // --- 3. プリセット追加エリア (下から3番目) ---
        var presetPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(5) };
        _btnAddPresets.Text = "★ 一般的な不要ファイル (Office一時ファイル・画像サムネ等) を一発追加";
        _btnAddPresets.Dock = DockStyle.Fill;
        _btnAddPresets.BackColor = Color.LightYellow; 
        _btnAddPresets.Click += OnAddPresetsClick;
        presetPanel.Controls.Add(_btnAddPresets);

        // --- 4. 1件追加エリア (下から4番目) ---
        var singlePanel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(5) };
        
        _btnAdd.Text = "追加";
        _btnAdd.Width = 80;
        _btnAdd.Dock = DockStyle.Right; 
        _btnAdd.Click += (s, e) => AddItem(_txtInput.Text);

        // ★修正: プレースホルダーからアスタリスクを削除
        _txtInput.PlaceholderText = ".tmp";
        _txtInput.Dock = DockStyle.Fill; 
        _txtInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddItem(_txtInput.Text); e.SuppressKeyPress = true; } };

        singlePanel.Controls.Add(_txtInput); 
        singlePanel.Controls.Add(_btnAdd);   

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

        this.Controls.Add(listGroup);    
        this.Controls.Add(singlePanel);  
        this.Controls.Add(presetPanel);  
        this.Controls.Add(bulkGroup);    
        this.Controls.Add(bottomPanel);  
    }

    // --- ロジック ---

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

    private void OnRemoveClick(object? s, EventArgs e)
    {
        if (_listBox.SelectedIndex >= 0)
        {
            Exclusions.RemoveAt(_listBox.SelectedIndex);
            RefreshList();
        }
    }

    private void OnAddPresetsClick(object? s, EventArgs e)
    {
        // ★修正: アスタリスク無しの最強除外リストに差し替え
        var presets = new string[]
        {
            ".tmp",                         // 一般的な一時ファイル
            "~",                            // Office等一時ファイル
            ".laccdb",                      // Accessロック(新)
            ".ldb",                         // Accessロック(旧)
            @"\$",                          // システム隠しフォルダ
            @"\System Volume Information\", // システム復元ポイント
            @"\$RECYCLE.BIN\",              // ゴミ箱
            "Thumbs.db",                    // 画像キャッシュ
            "desktop.ini",                  // フォルダ表示設定
            ".bak",                         // 古いバックアップ
            ".crdownload",                  // Chrome等DL途中ファイル
            ".download",                    // DL途中ファイル
            ".lock"                         // 各種ロックファイル
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