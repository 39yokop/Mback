using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Mail;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.IO;

namespace MBack.Config;

public class MailSettingsForm : Form
{
    public MailSettings Config { get; private set; }

    private CheckBox _chkEnabled = new() { Text = "メール通知を有効にする" };
    private TextBox _txtTo = new();
    private TextBox _txtFrom = new();
    private TextBox _txtSmtpServer = new();
    private TextBox _txtSmtpPort = new();
    private CheckBox _chkSmtpSsl = new() { Text = "SMTP SSL/TLSを有効にする" };
    private TextBox _txtUser = new();
    private TextBox _txtPass = new() { PasswordChar = '*' }; 
    
    private CheckBox _chkPop = new() { Text = "POP before SMTP を使用する" };
    private TextBox _txtPopServer = new();
    private TextBox _txtPopPort = new();
    private CheckBox _chkPopSsl = new() { Text = "POP3 SSL/TLSを有効にする" };

    // ★ テスト送信ボタンを追加
    private Button _btnTestMail = new();

    public MailSettingsForm(MailSettings current)
    {
        Config = new MailSettings
        {
            Enabled = current.Enabled,
            ToAddress = current.ToAddress,
            FromAddress = current.FromAddress,
            SmtpServer = current.SmtpServer,
            SmtpPort = current.SmtpPort,
            SmtpSsl = current.SmtpSsl,
            UserName = current.UserName,
            Password = current.Password,
            UsePopBeforeSmtp = current.UsePopBeforeSmtp,
            PopServer = current.PopServer,
            PopPort = current.PopPort,
            PopSsl = current.PopSsl
        };

        this.Text = "メール通知設定";
        this.Size = new Size(480, 580);
        this.StartPosition = FormStartPosition.CenterParent;

        SetupLayout();
        DataToUI(); 
    }

    private void SetupLayout()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(15) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        panel.Controls.Add(_chkEnabled, 0, row);
        panel.SetColumnSpan(_chkEnabled, 2);
        row++;
        
        AddRow(panel, ref row, "送信先 (To):", _txtTo);
        AddRow(panel, ref row, "送信元 (From):", _txtFrom);
        
        var lblSmtp = new Label { Text = "--- SMTP設定 ---", ForeColor = Color.Gray, AutoSize = true };
        panel.Controls.Add(lblSmtp, 0, row);
        panel.SetColumnSpan(lblSmtp, 2);
        row++;
        
        AddRow(panel, ref row, "SMTPサーバー:", _txtSmtpServer);
        AddRow(panel, ref row, "SMTPポート:", _txtSmtpPort);
        
        panel.Controls.Add(_chkSmtpSsl, 1, row++);
        
        AddRow(panel, ref row, "ユーザー名:", _txtUser);
        AddRow(panel, ref row, "パスワード:", _txtPass);

        var lblPop = new Label { Text = "--- POP設定 (必要な場合) ---", ForeColor = Color.Gray, AutoSize = true };
        panel.Controls.Add(lblPop, 0, row);
        panel.SetColumnSpan(lblPop, 2);
        row++;

        panel.Controls.Add(_chkPop, 0, row);
        panel.SetColumnSpan(_chkPop, 2);
        row++;

        AddRow(panel, ref row, "POPサーバー:", _txtPopServer);
        AddRow(panel, ref row, "POPポート:", _txtPopPort);
        panel.Controls.Add(_chkPopSsl, 1, row++);

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 50, Padding = new Padding(10) };
        
        var btnCancel = new Button { Text = "キャンセル", Width = 90 };
        btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        
        var btnOk = new Button { Text = "OK", Width = 90 };
        btnOk.Click += (s, e) => { UIToData(); DialogResult = DialogResult.OK; Close(); };

        // ★ テストボタンの設定
        _btnTestMail.Text = "テスト送信";
        _btnTestMail.Width = 120;
        _btnTestMail.Click += OnTestMailClick;
        
        // 右から順に配置される (キャンセルが一番右になる)
        btnPanel.Controls.Add(btnCancel);
        btnPanel.Controls.Add(btnOk);
        btnPanel.Controls.Add(_btnTestMail);

        this.Controls.Add(panel);
        this.Controls.Add(btnPanel);
    }

    private void AddRow(TableLayoutPanel panel, ref int row, string label, Control ctrl)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        ctrl.Width = 280;
        panel.Controls.Add(ctrl, 1, row);
        row++;
    }

    private void DataToUI()
    {
        _chkEnabled.Checked = Config.Enabled;
        _txtTo.Text = Config.ToAddress;
        _txtFrom.Text = Config.FromAddress;
        _txtSmtpServer.Text = Config.SmtpServer;
        _txtSmtpPort.Text = Config.SmtpPort.ToString();
        _chkSmtpSsl.Checked = Config.SmtpSsl;
        _txtUser.Text = Config.UserName;
        _txtPass.Text = Config.Password;
        _chkPop.Checked = Config.UsePopBeforeSmtp;
        _txtPopServer.Text = Config.PopServer;
        _txtPopPort.Text = Config.PopPort.ToString();
        _chkPopSsl.Checked = Config.PopSsl;
    }

    private void UIToData()
    {
        Config.Enabled = _chkEnabled.Checked;
        Config.ToAddress = _txtTo.Text.Trim();
        Config.FromAddress = _txtFrom.Text.Trim();
        Config.SmtpServer = _txtSmtpServer.Text.Trim();
        int.TryParse(_txtSmtpPort.Text, out int sp); Config.SmtpPort = sp == 0 ? 587 : sp;
        Config.SmtpSsl = _chkSmtpSsl.Checked;
        Config.UserName = _txtUser.Text.Trim();
        Config.Password = _txtPass.Text; 
        Config.UsePopBeforeSmtp = _chkPop.Checked;
        Config.PopServer = _txtPopServer.Text.Trim();
        int.TryParse(_txtPopPort.Text, out int pp); Config.PopPort = pp == 0 ? 110 : pp;
        Config.PopSsl = _chkPopSsl.Checked;
    }

    // ★ テストメール送信処理
    private void OnTestMailClick(object? sender, EventArgs e)
    {
        // まず画面の入力内容を一時的にConfigへ反映
        UIToData();

        if (string.IsNullOrWhiteSpace(Config.ToAddress) || string.IsNullOrWhiteSpace(Config.SmtpServer))
        {
            MessageBox.Show("テスト送信を行うには、少なくとも「送信先」と「SMTPサーバー」を入力してください。", "入力不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // ボタンを無効化して連打防止
        _btnTestMail.Enabled = false;
        _btnTestMail.Text = "送信中...";
        Application.DoEvents(); // 画面を強制的に描画更新

        try
        {
            // 1. POP before SMTP 認証 (必要な場合のみ)
            if (Config.UsePopBeforeSmtp && !string.IsNullOrEmpty(Config.PopServer))
            {
                DoPopAuth(Config);
            }

            // 2. SMTP送信
            using var client = new SmtpClient(Config.SmtpServer, Config.SmtpPort);
            client.EnableSsl = Config.SmtpSsl;
            if (!string.IsNullOrEmpty(Config.UserName))
            {
                client.Credentials = new NetworkCredential(Config.UserName, Config.Password);
            }

            using var mail = new MailMessage();
            // Fromが空の場合はUserName等から推測できないので、Toと同じにするなどフォールバック
            mail.From = new MailAddress(string.IsNullOrWhiteSpace(Config.FromAddress) ? Config.ToAddress : Config.FromAddress);
            mail.To.Add(Config.ToAddress);
            mail.Subject = "MBack テストメール";
            mail.Body = "これは MBack 設定ツールからのテストメールです。\n\nこのメールが届いていれば、SMTP/POPの設定は正しく行われています。\n以降、バックアップエラー発生時に自動で通知が届きます。";

            client.Send(mail);

            MessageBox.Show("テストメールの送信に成功しました！\n受信トレイを確認してください。", "送信成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"メールの送信に失敗しました。\n設定を見直してください。\n\n【詳細エラー】\n{ex.Message}", "送信エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // ボタンを元に戻す
            _btnTestMail.Enabled = true;
            _btnTestMail.Text = "テスト送信";
        }
    }

    // POP before SMTP 用の生TCP通信ロジック（テスト用）
    private void DoPopAuth(MailSettings config)
    {
        using var client = new TcpClient(config.PopServer, config.PopPort);
        Stream stream = client.GetStream();
        if (config.PopSsl)
        {
            var ssl = new SslStream(stream);
            ssl.AuthenticateAsClient(config.PopServer);
            stream = ssl;
        }
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true };

        reader.ReadLine(); 
        writer.WriteLine($"USER {config.UserName}");
        reader.ReadLine();
        writer.WriteLine($"PASS {config.Password}");
        reader.ReadLine();
        writer.WriteLine("QUIT");
        reader.ReadLine();
    }
}