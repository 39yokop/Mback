using System;
using System.Drawing;
using System.Windows.Forms;

namespace MBack.Config;

public class AdvancedSettingsForm : Form
{
    public int LogRetentionDays { get; private set; }
    public int RansomwareThreshold { get; private set; }

    private NumericUpDown _numLogDays = new();
    private NumericUpDown _numThreshold = new();

    public AdvancedSettingsForm(int currentLogDays, int currentThreshold)
    {
        LogRetentionDays = currentLogDays;
        RansomwareThreshold = currentThreshold;

        this.Text = "詳細設定 (オプション)";
        this.Size = new Size(400, 250);
        this.StartPosition = FormStartPosition.CenterParent;

        SetupLayout();
    }

    private void SetupLayout()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(15) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        // ログ保存日数
        panel.Controls.Add(new Label { Text = "ログの保存日数 (日):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _numLogDays.Minimum = 1; _numLogDays.Maximum = 3650; _numLogDays.Value = LogRetentionDays; _numLogDays.Width = 100;
        panel.Controls.Add(_numLogDays, 1, row++);

        // 異常検知の閾値
        panel.Controls.Add(new Label { Text = "異常検知の閾値 (件/60秒):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _numThreshold.Minimum = 10; _numThreshold.Maximum = 100000; _numThreshold.Value = RansomwareThreshold; _numThreshold.Width = 100;
        panel.Controls.Add(_numThreshold, 1, row++);

        // 説明ラベル
        var lblDesc = new Label 
        { 
            Text = "※ 60秒間に上記件数以上のファイルが変更・削除された場合、ランサムウェア等の異常事態とみなしてバックアップを緊急停止します。", 
            ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(350, 0)
        };
        panel.Controls.Add(lblDesc, 0, row);
        panel.SetColumnSpan(lblDesc, 2);

        // ボタン
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(10) };
        var btnCancel = new Button { Text = "キャンセル", Width = 90 };
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        var btnOk = new Button { Text = "OK", Width = 90 };
        btnOk.Click += (s, e) => { 
            LogRetentionDays = (int)_numLogDays.Value; 
            RansomwareThreshold = (int)_numThreshold.Value; 
            DialogResult = DialogResult.OK; Close(); 
        };
        
        btnPanel.Controls.Add(btnCancel); btnPanel.Controls.Add(btnOk);

        this.Controls.Add(panel); this.Controls.Add(btnPanel);
    }
}