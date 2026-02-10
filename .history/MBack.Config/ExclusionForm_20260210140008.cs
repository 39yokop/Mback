namespace MBack.Config;

public class ExclusionForm : Form
{
    public List<string> Exclusions { get; private set; }
    private ListBox _listBox = new();
    private TextBox _txtInput = new();
    private Button _btnAdd = new();
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
        this.Size = new Size(300, 400);

        _listBox.Dock = DockStyle.Top;
        _listBox.Height = 250;

        _txtInput.PlaceholderText = "*.tmp など";
        _txtInput.Dock = DockStyle.Top;

        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        _btnAdd.Text = "追加";
        _btnAdd.Click += (s, e) => { if(!string.IsNullOrWhiteSpace(_txtInput.Text)) { Exclusions.Add(_txtInput.Text); _txtInput.Clear(); RefreshList(); }};
        
        _btnRemove.Text = "削除";
        _btnRemove.Click += (s, e) => { if(_listBox.SelectedIndex >= 0) { Exclusions.RemoveAt(_listBox.SelectedIndex); RefreshList(); }};

        panel.Controls.Add(_btnAdd);
        panel.Controls.Add(_btnRemove);

        _btnOk.Text = "OK";
        _btnOk.Dock = DockStyle.Bottom;
        _btnOk.DialogResult = DialogResult.OK;

        this.Controls.Add(_btnOk);
        this.Controls.Add(panel);
        this.Controls.Add(_txtInput);
        this.Controls.Add(_listBox);
    }

    private void RefreshList()
    {
        _listBox.Items.Clear();
        foreach(var item in Exclusions) _listBox.Items.Add(item);
    }
}