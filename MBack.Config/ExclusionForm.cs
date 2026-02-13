using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;

namespace MBack.Config;

public class ExclusionForm : Form
{
    public List<string> Exclusions { get; private set; }
    
    private ListBox _listBox = new();
    private TextBox _txtInput = new();
    private TextBox _txtBulkInput = new(); // ★一括入力用
    private Button _btnAdd = new();
    private Button _btnBulkAdd = new();    // ★一括追加ボタン
    private Button _btnRemove = new();
    private Button _btnOk = new();

    public ExclusionForm(List<string> currentExclusions)
    {
        Exclusions = new List<string>(currentExclusions);
        InitializeComponent();
        RefreshList();
    }

    private void InitializeComponent()
    {
        this.Text = "除外設定 (ファイル名パターン)";
        this.Size = new Size(500, 600); // 少し大きく

        // 1. 上部：リスト表示と削除
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 200 };
        _listBox.Dock = DockStyle.Fill;
        
        var removePanel = new Panel { Dock = DockStyle.Right, Width = 80 };
        _btnRemove.Text = "削除";
        _btnRemove.Dock = DockStyle.Top;
        _btnRemove.Click += (s, e) => { 
            if(_listBox.SelectedIndex >= 0) { 
                Exclusions.RemoveAt(_listBox.SelectedIndex); 
                RefreshList(); 
            }
        };
        removePanel.Controls.Add(_btnRemove);
        
        topPanel.Controls.Add(_listBox);
        topPanel.Controls.Add(removePanel);

        // 2. 中部：1件追加
        var singlePanel = new Panel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(5) };
        var lblSingle = new Label { Text = "1件追加:", Dock = DockStyle.Top, Height = 20 };
        _txtInput.PlaceholderText = "*.tmp";
        _txtInput.Dock = DockStyle.Top;
        
        _btnAdd.Text = "追加";
        _btnAdd.Dock = DockStyle.Right;
        _btnAdd.Click += (s, e) => AddItem(_txtInput.Text);

        singlePanel.Controls.Add(_btnAdd); // 先にRightを配置
        singlePanel.Controls.Add(_txtInput);
        singlePanel.Controls.Add(lblSingle);

        // 3. 下部：一括追加（★復活！）
        var bulkPanel = new GroupBox { Text = "一括追加 (改行区切りで入力)", Dock = DockStyle.Fill, Padding = new Padding(5) };
        _txtBulkInput.Multiline = true;
        _txtBulkInput.ScrollBars = ScrollBars.Vertical;
        _txtBulkInput.Dock = DockStyle.Fill;

        _btnBulkAdd.Text = "一括追加を実行";
        _btnBulkAdd.Dock = DockStyle.Bottom;
        _btnBulkAdd.Height = 40;
        _btnBulkAdd.Click += OnBulkAddClick;

        bulkPanel.Controls.Add(_txtBulkInput);
        bulkPanel.Controls.Add(_btnBulkAdd);

        // 4. 最下部：OKボタン
        _btnOk.Text = "設定完了 (OK)";
        _btnOk.Dock = DockStyle.Bottom;
        _btnOk.Height = 40;
        _btnOk.DialogResult = DialogResult.OK;

        // コントロール配置（下から順に追加しないとDockが崩れることがあるため注意）
        this.Controls.Add(bulkPanel);   // Fill
        this.Controls.Add(singlePanel); // Top
        this.Controls.Add(topPanel);    // Top
        this.Controls.Add(_btnOk);      // Bottom
    }

    private void AddItem(string item)
    {
        if(string.IsNullOrWhiteSpace(item)) return;
        if(!Exclusions.Contains(item))
        {
            Exclusions.Add(item.Trim());
            RefreshList();
        }
        _txtInput.Clear();
    }

    private void OnBulkAddClick(object? sender, EventArgs e)
    {
        // 改行コードで分割して追加
        var lines = _txtBulkInput.Lines;
        int count = 0;
        foreach(var line in lines)
        {
            if(!string.IsNullOrWhiteSpace(line) && !Exclusions.Contains(line.Trim()))
            {
                Exclusions.Add(line.Trim());
                count++;
            }
        }
        RefreshList();
        _txtBulkInput.Clear();
        MessageBox.Show($"{count} 件追加しました。", "完了");
    }

    private void RefreshList()
    {
        _listBox.Items.Clear();
        foreach(var item in Exclusions) _listBox.Items.Add(item);
    }
}