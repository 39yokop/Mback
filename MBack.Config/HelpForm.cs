using System;
using System.Drawing;
using System.Windows.Forms;

namespace MBack.Config;

public class HelpForm : Form
{
    public HelpForm()
    {
        this.Text = "MBack 2.0 使い方ガイド (最強防衛仕様)";
        this.Size = new Size(650, 580); // ★タブが増えたので縦を少し広げた
        this.StartPosition = FormStartPosition.CenterParent;
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;

        SetupLayout();
    }

    private void SetupLayout()
    {
        var tab = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(10, 10),
            Font = new Font(this.Font.FontFamily, 10, FontStyle.Regular)
        };

        // --- 1. 基本操作（変更なし） ---
        tab.TabPages.Add(CreateHelpPage("基本と便利機能",
            "【MBack 2.0 の便利なUI機能】\n\n" +
            "● ドラッグ＆ドロップ対応\n" +
            "   設定の「監視元フォルダ」や「バックアップ先」の入力欄には、\n" +
            "   エクスプローラーからフォルダを直接ドラッグ＆ドロップできます。\n\n" +
            "● 複製（コピー）追加機能\n" +
            "   NASのパスワード入力等を省略するため、既存の設定行を選択して\n" +
            "   『📋 複製』ボタンを押すだけで簡単に設定を増やせます。\n\n" +
            "● NAS認証の自動化\n" +
            "   Windowsサービスのログオン情報を変更せずに、アプリ側から\n" +
            "   NASへ自動ログインが可能です。ユーザー名は『サーバ名\\ユーザー名』\n" +
            "   の形式で入力してください。"));

        // --- 2. 除外設定（変更なし） ---
        tab.TabPages.Add(CreateHelpPage("除外設定",
            "【除外パターンの書き方（重要）】\n\n" +
            "● MBack 2.0 では、アスタリスク（*）を使用しません。\n" +
            "● 指定した文字がパスの中に『含まれているか』で判定します。\n\n" +
            "   例1: .tmp と書けば、大文字小文字問わず .tmp を除外します。\n" +
            "   例2: ~ と書けば、Office等の一時ファイルをすべて除外します。\n\n" +
            "● フォルダごと除外したい場合は、\\System\\ のように\n" +
            "   円マークで囲んで指定すると安全です。"));

        // --- 3. ランサムウェア対策（変更なし） ---
        tab.TabPages.Add(CreateHelpPage("ランサムウェア対策",
            "【最強ハイブリッド検知・絶対防衛システム】\n\n" +
            "MBack 2.0は、以下の3段構えでファイル破壊からシステムを守ります。\n\n" +
            "① 囮（ハニーポット）検知\n" +
            "   監視元フォルダに「!000_MBack_Trap.txt」という隠しファイルを作ります。\n" +
            "   ランサムウェアがこのファイルに触れた瞬間、即座に緊急停止します。\n\n" +
            "② 60秒の遅延（上書き防止）バックアップ\n" +
            "   ファイルを変更後、バックアップ先にコピーされるまで『60秒間』\n" +
            "   安全確認の待機を行います。この間に異常を検知すれば、破壊された\n" +
            "   ファイルがバックアップ先に上書きされることはありません。\n\n" +
            "③ 工事写真リサイズ・スルー機能\n" +
            "   画像ファイル（.jpg等）の操作や、ファイルの新規作成は危険判定から\n" +
            "   除外されます。大量の写真コピー等で誤爆停止することはありません。"));

        // --- 4. メンテと復旧（変更なし） ---
        tab.TabPages.Add(CreateHelpPage("メンテと復旧",
            "【緊急停止からのワンクリック復旧】\n\n" +
            "● 緊急停止が発生すると、メイン画面の下部にオレンジ色の\n" +
            "   『⚠️ 緊急停止を解除して再開』ボタンが出現します。\n" +
            "● フォルダ内にウイルス等の異常がないことを確認してから\n" +
            "   このボタンを1回押すだけで、ロックを解除して安全に再開します。\n\n" +
            "【監視の一時停止（メンテモード）】\n" +
            "● 詳細設定から『開始時間』と『終了時間』を指定すると、その時間は\n" +
            "   監視と異常検知が一時停止します。他ソフトとの衝突回避に使えます。"));

        // --- 5. ★新規追加: 履歴復元センターの使い方 ---
        tab.TabPages.Add(CreateHelpPage("履歴・ログの確認と復元",
            "【履歴復元センターの2つのモード】\n\n" +
            "「🔍 履歴とログを見る・復元する」ボタンで開く画面には、\n" +
            "目的に応じて2つのモードがあります。\n\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "● 日付別モード（ファイルの復元に使う）\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "特定の1日のバックアップ履歴をフォルダ階層で確認できます。\n\n" +
            "  ・日付ドロップダウンで確認したい日を選択する\n" +
            "  ・「絞り込み」欄に文字を入力すると、その文字を含む\n" +
            "    ファイルだけがリアルタイムで絞り込まれます\n" +
            "  ・ファイルを右クリック →『★ 世代選択と復元』で\n" +
            "    過去50世代から好きな版を選んで復元できます\n" +
            "  ・復元前に『🔍 別の場所に保存して確認』で中身を\n" +
            "    確認してから復元するのが安全です\n\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "● 期間・検索モード（ファイルを追跡するときに使う）\n" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            "「いつ・誰が・このファイルを触ったか」を横断検索できます。\n\n" +
            "  ・期間：「過去30日」またはカレンダーで期間を自由指定\n" +
            "  ・キーワード：ファイル名やフォルダ名の一部で絞り込み\n" +
            "  ・種別：コピー(Copy)・削除(Delete)・エラー(Error)で絞込\n" +
            "  ・「🔍 検索」ボタンで実行。結果は最大 5,000 件を表示\n" +
            "  ・5,000件を超えた場合は確認ダイアログが出て、\n" +
            "    「はい」を選ぶと全件検索に切り替わります\n" +
            "  ・列のヘッダーをクリックすると、その列で並び替えできます\n\n" +
            "※ ファイルの復元操作は「日付別モード」からのみ行えます。"));

        var btnClose = new Button
        {
            Text = "閉じる",
            Dock = DockStyle.Bottom,
            Height = 40,
            Font = new Font(this.Font, FontStyle.Bold)
        };
        btnClose.Click += (s, e) => this.Close();

        this.Controls.Add(tab);
        this.Controls.Add(btnClose);
    }

    private TabPage CreateHelpPage(string title, string content)
    {
        var page = new TabPage(title);
        var txt = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            Text        = content,
            BorderStyle = BorderStyle.None,
            Padding     = new Padding(15),
            Font        = new Font("メイリオ", 10),
            BackColor   = Color.White
        };
        page.Controls.Add(txt);
        return page;
    }
}
