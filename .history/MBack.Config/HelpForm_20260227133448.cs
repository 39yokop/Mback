using System;
using System.Drawing;
using System.Windows.Forms;

namespace MBack.Config;

public class HelpForm : Form
{
    public HelpForm()
    {
        this.Text = "MBack 2.0 使い方ガイド";
        this.Size = new Size(600, 500);
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;

        SetupLayout();
    }

    private void SetupLayout()
    {
        var tab = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 10) };

        // --- 各ヘルプ項目の定義 ---
        tab.TabPages.Add(CreateHelpPage("NAS認証", 
            "【NASや共有フォルダの認証について】\n\n" +
            "● MBack 2.0 では、Windowsサービスのログオン情報を変更せずに、\n" +
            "   アプリ側からNASへ自動ログイン（マウント）することが可能です。\n\n" +
            "● 設定方法:\n" +
            "   「追加」または「編集」ボタンから、バックアップ先のパスと一緒に\n" +
            "   ユーザー名とパスワードを入力してください。\n\n" +
            "● 注意点:\n" +
            "   ユーザー名は『サーバ名\\ユーザー名』の形式で入力すると確実です。"));

        tab.TabPages.Add(CreateHelpPage("除外設定", 
            "【除外パターンの書き方（重要）】\n\n" +
            "● MBack 2.0 では、アスタリスク（*）を使用しません。\n\n" +
            "● 指定した文字がパスの中に『含まれているか』で判定します。\n" +
            "   ・ .tmp と書けば、大文字小文字問わず .tmp を除外します。\n" +
            "   ・ ~ と書けば、Office等の一時ファイルをすべて除外します。\n\n" +
            "● フォルダごと除外したい場合は、\\System\\ のように\n" +
            "   円マークで囲んで指定すると安全です。"));

        tab.TabPages.Add(CreateHelpPage("メンテモード", 
            "【監視の一時停止について】\n\n" +
            "● 他のバックアップソフトやシステムメンテナンスの時間帯に、\n" +
            "   MBackの監視を休ませる機能です。\n\n" +
            "● 詳細設定から『開始時間』と『終了時間』を指定してください。\n\n" +
            "● この時間帯は、ランサムウェア検知（緊急停止）も無効化されるため、\n" +
            "   大量のファイル移動を伴うバッチ処理等との衝突を回避できます。"));

        tab.TabPages.Add(CreateHelpPage("緊急停止", 
            "【緊急警告が出た場合の復旧】\n\n" +
            "● 短時間に大量の変更を検知すると、NASを保護するために\n" +
            "   処理を強制停止（サーキットブレーカー発動）します。\n\n" +
            "● 復旧手順:\n" +
            "   1. フォルダ内に異常（ウイルス等）がないか確認してください。\n" +
            "   2. 設定ツールの「サービス管理」からサービスを停止します。\n" +
            "   3. 再度「開始」ボタンを押すと、ロックが解除され再開します。"));

        var btnClose = new Button { Text = "閉じる", Dock = DockStyle.Bottom, Height = 40 };
        btnClose.Click += (s, e) => this.Close();

        this.Controls.Add(tab);
        this.Controls.Add(btnClose);
    }

    private TabPage CreateHelpPage(string title, string content)
    {
        var page = new TabPage(title);
        var txt = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = content,
            BorderStyle = BorderStyle.None,
            Padding = new Padding(10),
            Font = new Font("メイリオ", 10),
            BackColor = Color.White
        };
        page.Controls.Add(txt);
        return page;
    }
}