using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CadDrawingNumber.UI;

public sealed class ModernToolPanel : UserControl
{
    private static readonly Color Background = Color.FromArgb(30, 34, 40);
    private static readonly Color HeaderBg = Color.FromArgb(25, 29, 35);
    private static readonly Color Card = Color.FromArgb(42, 47, 55);
    private static readonly Color TextMain = Color.FromArgb(239, 243, 248);
    private static readonly Color TextDim = Color.FromArgb(140, 152, 165);
    private static readonly Color Blue = Color.FromArgb(67, 147, 245);
    private static readonly Color Btn = Color.FromArgb(57, 64, 73);
    private static readonly Color BtnHover = Color.FromArgb(75, 84, 95);

    private static readonly Color Accent01 = Color.FromArgb(67, 147, 245);
    private static readonly Color Accent02 = Color.FromArgb(114, 190, 87);
    private static readonly Color Accent03 = Color.FromArgb(224, 173, 67);
    private static readonly Color Accent04 = Color.FromArgb(224, 96, 96);

    private const int ButtonHeight = 26;

    private readonly ToolTip _toolTip = new()
    {
        AutoPopDelay = 5000,
        InitialDelay = 200,
        ReshowDelay = 100,
        BackColor = Color.FromArgb(42, 47, 55),
        ForeColor = Color.FromArgb(239, 243, 248),
        UseAnimation = true,
        UseFading = true
    };

    public ModernToolPanel()
    {
        BackColor = Background;
        Dock = DockStyle.Fill;
        Font = new Font("Microsoft YaHei UI", 9f);

        var header = CreateHeader();

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Background,
            Padding = new Padding(5, 5, 5, 3)
        };

        AddGroup(content, "01 图号", Accent01,
            Tuple.Create("批量图号", "THBH", "框选图纸范围，按方向依次替换图号"));

        AddGroup(content, "02 图块", Accent02,
            Tuple.Create("分堆成块", "BBLK", "按聚簇距离自动分组并批量创建图块"),
            Tuple.Create("同名选择", "SELBLK", "点击一个图块，选中所有同名图块并高亮"));

        AddGroup(content, "03 线型", Accent03,
            Tuple.Create("文字线型", "WZXX", "创建带文字的自定义线型"),
            Tuple.Create("图案线型", "TAXX", "创建圆点/方框/轨道/保温棉图案线型"),
            Tuple.Create("线型管理", "LINETYPE", "打开 AutoCAD 线型管理器"));

        AddGroup(content, "04 文字", Accent04,
            Tuple.Create("批量前后缀", "WZQF", "给选定文字批量添加前缀、后缀或前后缀"));

        content.SizeChanged += (_, _) => ResizeGroups(content);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            BackColor = HeaderBg
        };
        footer.Controls.Add(new Label
        {
            Text = "TYPC 工具箱",
            ForeColor = TextDim,
            Font = new Font("Microsoft YaHei UI", 7f),
            AutoSize = true,
            Location = new Point(10, 4)
        });

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private Panel CreateHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = HeaderBg
        };

        header.Controls.Add(new Label
        {
            Text = "T",
            BackColor = Blue,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            Size = new Size(20, 20),
            Location = new Point(8, 7),
            TextAlign = ContentAlignment.MiddleCenter
        });

        header.Controls.Add(new Label
        {
            Text = "TYPC 工具箱",
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
            ForeColor = TextMain,
            AutoSize = true,
            Location = new Point(34, 9)
        });

        header.Controls.Add(new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 2,
            BackColor = Blue
        });

        return header;
    }

    private void AddGroup(
        FlowLayoutPanel content,
        string title,
        Color accentColor,
        params Tuple<string, string, string>[] items)
    {
        const int cols = 2;
        int rows = (items.Length + cols - 1) / cols;
        int height = 24 + rows * ButtonHeight + 6;

        var group = new Panel
        {
            BackColor = Card,
            Height = height,
            Width = 260,
            Margin = new Padding(0, 0, 0, 4),
            Tag = "tool-group"
        };

        // 分类标题条
        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 22,
            BackColor = accentColor
        };
        titleBar.Controls.Add(new Label
        {
            Text = title,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(8, 4)
        });
        group.Controls.Add(titleBar);

        // 按钮网格
        var grid = new TableLayoutPanel
        {
            ColumnCount = cols,
            RowCount = rows,
            Dock = DockStyle.Fill,
            BackColor = Card,
            Padding = new Padding(3, 3, 3, 3),
            Margin = Padding.Empty
        };
        for (int c = 0; c < cols; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        for (int r = 0; r < rows; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, ButtonHeight));

        for (int i = 0; i < items.Length; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Button btn = CreateButton(items[i].Item1, items[i].Item2, items[i].Item3);
            btn.Dock = DockStyle.Fill;
            btn.Margin = col == 0
                ? new Padding(0, 0, 2, 2)
                : new Padding(2, 0, 0, 2);
            grid.Controls.Add(btn, col, row);
        }

        group.Controls.Add(grid);
        grid.BringToFront();

        content.Controls.Add(group);
    }

    private static void ResizeGroups(FlowLayoutPanel content)
    {
        int width = Math.Max(200, content.ClientSize.Width - content.Padding.Horizontal - 2);
        foreach (Control control in content.Controls)
        {
            if (Equals(control.Tag, "tool-group"))
            {
                control.Width = width;
            }
        }
    }

    private Button CreateButton(string label, string command, string? tip)
    {
        var button = new Button
        {
            Text = label,
            BackColor = Btn,
            ForeColor = TextMain,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 8f),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = BtnHover;
        button.FlatAppearance.MouseDownBackColor = BtnHover;
        button.Click += (_, _) =>
        {
            Document? document = AcApplication.DocumentManager.MdiActiveDocument;
            document?.SendStringToExecute(command + " ", true, false, false);
            // 点击执行命令后，焦点还给 CAD
            AcApplication.MainWindow.Focus();
        };

        if (!string.IsNullOrEmpty(tip))
        {
            _toolTip.SetToolTip(button, tip + "\n命令: " + command);
        }
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
