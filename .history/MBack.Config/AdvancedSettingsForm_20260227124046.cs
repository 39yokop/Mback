using System;
using System.Drawing;
using System.Windows.Forms;

namespace MBack.Config;

public class AdvancedSettingsForm : Form
{
    public int LogRetentionDays { get; private set; }
    public int RansomwareThreshold { get; private set; }
    
    // ★新機能のプロパティ
    public string MaintenanceStart { get; private set; }
    public string MaintenanceEnd { get; private set; }
    public bool SendDailySummary { get; private set; }

    private NumericUpDown _numLogDays = new();
    private NumericUpDown _numThreshold = new();
    
    // ★新機能のUIコントロール
    private DateTimePicker _dtpMaintStart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private DateTimePicker _dtpMaintEnd = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private CheckBox _chkSummary = new() { Text = "一日の稼働サマリー（日報）を毎朝8時にメールで送る", AutoSize = true };

    public AdvancedSettingsForm(int currentLogDays, int currentThreshold, string maintStart, string maintEnd, bool sendSummary)
    {
        LogRetentionDays = currentLogDays;
        RansomwareThreshold = currentThreshold;
        MaintenanceStart = string.IsNullOrWhiteSpace(maintStart) ? "00:00" : maintStart;
        MaintenanceEnd = string.IsNullOrWhiteSpace(maintEnd) ? "00:00" : maintEnd;
        SendDailySummary = sendSummary;

        this.Text = "詳細設定 (オプション)";
        this.Size = new Size(420, 360); // 項目が増えたので縦に少し広げました
        this.StartPosition = FormStartPosition.CenterParent;

        // 時間の初期値セット
        if (DateTime.TryParseExact(MaintenanceStart, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var startT)) _dtpMaintStart.Value = startT;
        if (DateTime.TryParseExact(MaintenanceEnd, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var endT)) _dtpMaintEnd.Value = endT;

        SetupLayout();
    }

    private void SetupLayout()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(15) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        // --- ログ・異常検知設定 ---
        panel.Controls.Add(new Label { Text = "ログの保存日数 (日):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _numLogDays.Minimum = 1; _numLogDays.Maximum = 3650; _numLogDays.Value = LogRetentionDays; _numLogDays.Width = 100;
        panel.Controls.Add(_numLogDays, 1, row++);

        panel.Controls.Add(new Label { Text = "異常検知の閾値 (件/60秒):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        _numThreshold.Minimum = 10; _numThreshold.Maximum = 100000; _numThreshold.Value = RansomwareThreshold; _numThreshold.Width = 100;
        panel.Controls.Add(_numThreshold, 1, row++);

        var lblDesc = new Label 
        { 
            Text = "※ 60秒間に上記件数以上のファイルが変更・削除された場合、ランサムウェア等の異常事態とみなしてバックアップを緊急停止します。", 
            ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(350, 0)
        };
        panel.Controls.Add(lblDesc, 0, row);
        panel.SetColumnSpan(lblDesc, 2); row++;

        // --- ★新機能：メンテナンスモード設定 ---
        var lblMaint = new Label { Text = "\n--- メンテナンスモード ---", ForeColor = Color.Gray, AutoSize = true };
        panel.Controls.Add(lblMaint, 0, row); panel.SetColumnSpan(lblMaint, 2); row++;

        panel.Controls.Add(new Label { Text = "監視の一時停止 開始時間:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(_dtpMaintStart, 1, row++);

        panel.Controls.Add(new Label { Text = "監視の一時停止 終了時間:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(_dtpMaintEnd, 1, row++);

        var lblMaintDesc = new Label 
        { 
            Text = "※ 別アプリのバックアップ時など、開始〜終了の時間帯はMBackの監視と異常検知を完全に一時停止します。（00:00〜00:00で無効）", 
            ForeColor = Color.DimGray, AutoSize = true, MaximumSize = new Size(350, 0)
        };
        panel.Controls.Add(lblMaintDesc, 0, row);
        panel.SetColumnSpan(lblMaintDesc, 2); row++;

        // --- ★新機能：日報メール設定 ---
        var lblReport = new Label { Text = "\n--- レポート通知 ---", ForeColor = Color.Gray, AutoSize = true };
        panel.Controls.Add(lblReport, 0, row); panel.SetColumnSpan(lblReport, 2); row++;

        _chkSummary.Checked = SendDailySummary;
        panel.Controls.Add(_chkSummary, 0, row++);
        panel.SetColumnSpan(_chkSummary, 2);

        // --- ボタンエリア ---
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(10) };
        var btnCancel = new Button { Text = "キャンセル", Width = 90 };
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        var btnOk = new Button { Text = "OK", Width = 90 };
        btnOk.Click += (s, e) => { 
            LogRetentionDays = (int)_numLogDays.Value; 
            RansomwareThreshold = (int)_numThreshold.Value; 
            MaintenanceStart = _dtpMaintStart.Value.ToString("HH:mm");
            MaintenanceEnd = _dtpMaintEnd.Value.ToString("HH:mm");
            SendDailySummary = _chkSummary.Checked;
            DialogResult = DialogResult.OK; Close(); 
        };
        
        btnPanel.Controls.Add(btnCancel); btnPanel.Controls.Add(btnOk);

        this.Controls.Add(panel); this.Controls.Add(btnPanel);
    }
}