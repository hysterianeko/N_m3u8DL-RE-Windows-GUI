using System;
using System.Drawing;
using System.Windows.Forms;

namespace M3u8DownloaderGui
{
    internal sealed class HlsKeyDialog : Form
    {
        private readonly TextBox _keyTextBox;
        private readonly TextBox _ivTextBox;

        public string KeyValue { get; private set; }
        public string IvValue { get; private set; }

        public HlsKeyDialog(string currentKey, string currentIv)
        {
            Text = "手动 HLS 密钥";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 240);
            MinimumSize = new Size(580, 260);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BackColor = Color.FromArgb(246, 248, 249);
            Padding = new Padding(18);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 3;
            layout.RowCount = 5;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            _keyTextBox = CreateTextBox();
            _keyTextBox.UseSystemPasswordChar = true;
            _keyTextBox.Text = currentKey ?? string.Empty;
            Button browseButton = CreateButton("选择文件...");
            browseButton.Click += BrowseButtonClick;
            AddRow(layout, 0, "HLS 密钥", _keyTextBox, browseButton);

            _ivTextBox = CreateTextBox();
            _ivTextBox.UseSystemPasswordChar = true;
            _ivTextBox.Text = currentIv ?? string.Empty;
            Button clearButton = CreateButton("清空");
            clearButton.Click += delegate
            {
                _keyTextBox.Clear();
                _ivTextBox.Clear();
                _keyTextBox.Focus();
            };
            AddRow(layout, 1, "HLS IV", _ivTextBox, clearButton);

            CheckBox showKeyCheckBox = new CheckBox();
            showKeyCheckBox.AutoSize = true;
            showKeyCheckBox.Text = "显示密钥和 IV";
            showKeyCheckBox.Margin = new Padding(0, 6, 0, 0);
            showKeyCheckBox.CheckedChanged += delegate
            {
                bool hide = !showKeyCheckBox.Checked;
                _keyTextBox.UseSystemPasswordChar = hide;
                _ivTextBox.UseSystemPasswordChar = hide;
            };
            layout.Controls.Add(showKeyCheckBox, 1, 2);
            layout.SetColumnSpan(showKeyCheckBox, 2);

            Label formatLabel = new Label();
            formatLabel.Dock = DockStyle.Fill;
            formatLabel.ForeColor = Color.FromArgb(91, 103, 112);
            formatLabel.TextAlign = ContentAlignment.MiddleLeft;
            formatLabel.Text = "密钥支持 32 位 HEX、16 字节 Base64 或密钥文件；IV 可留空。";
            formatLabel.Margin = Padding.Empty;
            layout.Controls.Add(formatLabel, 1, 3);
            layout.SetColumnSpan(formatLabel, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Margin = Padding.Empty;

            Button cancelButton = CreateButton("取消");
            cancelButton.Width = 88;
            cancelButton.DialogResult = DialogResult.Cancel;

            Button confirmButton = CreateButton("确定");
            confirmButton.Width = 88;
            confirmButton.BackColor = Color.FromArgb(20, 122, 88);
            confirmButton.ForeColor = Color.White;
            confirmButton.FlatAppearance.BorderSize = 0;
            confirmButton.Click += ConfirmButtonClick;

            actions.Controls.Add(cancelButton);
            actions.Controls.Add(confirmButton);
            layout.Controls.Add(actions, 0, 4);
            layout.SetColumnSpan(actions, 3);

            Controls.Add(layout);
            AcceptButton = confirmButton;
            CancelButton = cancelButton;
        }

        internal bool RunSmokeTest()
        {
            PerformLayout();
            return _keyTextBox != null &&
                   _ivTextBox != null &&
                   ClientSize.Width >= 560 &&
                   ClientSize.Height >= 220;
        }

        private static TextBox CreateTextBox()
        {
            TextBox textBox = new TextBox();
            textBox.Dock = DockStyle.Fill;
            textBox.Margin = new Padding(0, 7, 10, 7);
            textBox.BorderStyle = BorderStyle.FixedSingle;
            return textBox;
        }

        private static Button CreateButton(string text)
        {
            Button button = new Button();
            button.Height = 32;
            button.Margin = new Padding(0, 5, 0, 5);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 218, 223);
            button.BackColor = Color.White;
            button.Text = text;
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static void AddRow(
            TableLayoutPanel layout,
            int row,
            string labelText,
            TextBox textBox,
            Button button)
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Text = labelText;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(textBox, 1, row);
            layout.Controls.Add(button, 2, row);
        }

        private void BrowseButtonClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 HLS 密钥文件";
                dialog.Filter = "密钥文件|*.key;*.bin;*.txt|所有文件|*.*";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _keyTextBox.Text = dialog.FileName;
                }
            }
        }

        private void ConfirmButtonClick(object sender, EventArgs e)
        {
            string key = HlsKeyValue.Normalize(_keyTextBox.Text);
            string iv = HlsKeyValue.Normalize(_ivTextBox.Text);
            if (key.Length == 0 && iv.Length > 0)
            {
                MessageBox.Show(
                    this,
                    "填写 IV 前必须先填写 HLS 密钥。",
                    "请检查密钥",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _keyTextBox.Focus();
                return;
            }

            if (!HlsKeyValue.IsRecognized(key) || !HlsKeyValue.IsRecognized(iv))
            {
                MessageBox.Show(
                    this,
                    "请输入 16 字节 AES-128 HEX/Base64，或者选择内容有效的密钥文件。",
                    "密钥格式无效",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            KeyValue = key;
            IvValue = iv;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
