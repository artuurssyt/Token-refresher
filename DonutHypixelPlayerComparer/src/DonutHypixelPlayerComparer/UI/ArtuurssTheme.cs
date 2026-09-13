using System.Drawing;

using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.Models;
using DonutHypixelPlayerComparer.Services;
namespace DonutHypixelPlayerComparer.UI;

/// <summary>Palette from https://www.artuurss.com CSS custom properties.</summary>
public static class ArtuurssTheme
{
    public static readonly Color Background = Color.FromArgb(0x08, 0x04, 0x04);
    public static readonly Color Background2 = Color.FromArgb(0x0f, 0x06, 0x06);
    public static readonly Color Background3 = Color.FromArgb(0x15, 0x08, 0x08);
    public static readonly Color Surface = Color.FromArgb(0x1a, 0x0b, 0x0b);
    public static readonly Color Border = Color.FromArgb(0x2a, 0x10, 0x10);
    public static readonly Color BorderRed = Color.FromArgb(0x3d, 0x15, 0x15);
    public static readonly Color Text = Color.FromArgb(0xd4, 0xcc, 0xc9);
    public static readonly Color TextDim = Color.FromArgb(0x7a, 0x6a, 0x68);
    public static readonly Color TextMuted = Color.FromArgb(0x4a, 0x3a, 0x38);
    public static readonly Color Red = Color.FromArgb(0xc0, 0x39, 0x2b);
    public static readonly Color RedBright = Color.FromArgb(0xe8, 0x40, 0x40);
    public static readonly Color RedDim = Color.FromArgb(0x8b, 0x1a, 0x12);
    public static readonly Color TerminalCmd = Color.FromArgb(0xe8, 0xdd, 0xd9);
    public static readonly Color ButtonOnRed = Color.White;

    public static readonly Font BodyFont = new("Segoe UI", 9F);
    public static readonly Font HeaderFont = new("Segoe UI Semibold", 17F);
    public static readonly Font MonoFont = CreateMonoFont(8.5F);
    public static readonly Font MonoFontLarge = CreateMonoFont(10F);

    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        if (form.Font.Name == "Microsoft Sans Serif") form.Font = BodyFont;
    }

    public static void ApplyHeader(Panel header, Label title, Label? subtitle = null)
    {
        header.BackColor = Surface;
        title.ForeColor = Text;
        title.Font = HeaderFont;
        if (subtitle is not null)
        {
            subtitle.ForeColor = TextDim;
            if (!subtitle.Text.StartsWith("//", StringComparison.Ordinal))
                subtitle.Text = "// " + subtitle.Text;
        }
    }

    public static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = RedBright;
        button.ForeColor = ButtonOnRed;
        button.Font = new Font("Segoe UI Semibold", 9F);
    }

    public static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = BorderRed;
        button.BackColor = Background2;
        button.ForeColor = Text;
    }

    public static void ApplyToolStrip(ToolStrip strip)
    {
        strip.BackColor = Background2;
        strip.ForeColor = Text;
        strip.Renderer = new ArtuurssToolStripRenderer();
        foreach (ToolStripItem item in strip.Items) StyleToolStripItem(item);
    }

    public static void StyleToolStripItem(ToolStripItem item)
    {
        item.ForeColor = Text;
        item.BackColor = Background2;
        if (item is ToolStripDropDownButton dropDown)
        {
            dropDown.DropDown.BackColor = Surface;
            dropDown.DropDown.ForeColor = Text;
            foreach (ToolStripItem child in dropDown.DropDownItems) StyleToolStripItem(child);
        }
    }

    public static void StylePrimaryToolStripButton(ToolStripButton button)
    {
        StyleToolStripItem(button);
        button.BackColor = RedBright;
        button.ForeColor = ButtonOnRed;
        button.Font = new Font("Segoe UI Semibold", 9F);
    }

    public static void ApplyStatusStrip(StatusStrip strip)
    {
        strip.BackColor = Background2;
        strip.ForeColor = TextDim;
        foreach (ToolStripItem item in strip.Items) item.ForeColor = TextDim;
    }

    public static void ApplyGroupBox(GroupBox box)
    {
        box.ForeColor = TextDim;
        box.BackColor = Surface;
    }

    public static void ApplyTextBox(TextBox box, bool mono = false)
    {
        box.BackColor = Background3;
        box.ForeColor = mono ? TerminalCmd : Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        if (mono) box.Font = MonoFont;
    }

    public static void ApplyRichTextBox(RichTextBox box)
    {
        box.BackColor = Background3;
        box.ForeColor = TerminalCmd;
        box.BorderStyle = BorderStyle.None;
        box.Font = MonoFont;
    }

    public static void ApplyDataGridView(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(80, Red);
        grid.DefaultCellStyle.SelectionForeColor = ButtonOnRed;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Background3;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Color.FromArgb(80, Red);
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = ButtonOnRed;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Background2;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextDim;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Background2;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextDim;
        grid.RowHeadersDefaultCellStyle.BackColor = Background2;
        grid.RowHeadersDefaultCellStyle.ForeColor = TextDim;
    }

    public static void ApplyTabControl(TabControl tabs)
    {
        tabs.BackColor = Background;
        tabs.ForeColor = TextDim;
        foreach (TabPage page in tabs.TabPages)
        {
            page.BackColor = Surface;
            page.ForeColor = Text;
        }
    }

    public static void ApplyNumeric(NumericUpDown numeric)
    {
        numeric.BackColor = Background3;
        numeric.ForeColor = Text;
        numeric.BorderStyle = BorderStyle.FixedSingle;
    }

    public static void ApplyComboBox(ComboBox combo)
    {
        combo.BackColor = Background3;
        combo.ForeColor = Text;
        combo.FlatStyle = FlatStyle.Flat;
    }

    public static void ApplyCheckBox(CheckBox box)
    {
        box.ForeColor = Text;
        box.BackColor = Color.Transparent;
    }

    public static void ApplyNoteLabel(Label label) => label.ForeColor = TextMuted;

    public static void StyleProgressBar(ToolStripProgressBar progress)
    {
        progress.BackColor = Border;
        progress.ForeColor = RedBright;
    }

    private static Font CreateMonoFont(float size)
    {
        foreach (var name in new[] { "JetBrains Mono", "Cascadia Mono", "Consolas", "Courier New" })
        {
            if (FontFamily.Families.Any(family => family.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return new Font(name, size);
        }
        return new Font(FontFamily.GenericMonospace, size);
    }

    private sealed class ArtuurssToolStripRenderer : ToolStripProfessionalRenderer
    {
        public ArtuurssToolStripRenderer() : base(new ArtuurssColorTable()) { }
    }

    private sealed class ArtuurssColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Background2;
        public override Color ToolStripGradientMiddle => Background2;
        public override Color ToolStripGradientEnd => Background2;
        public override Color MenuStripGradientBegin => Background2;
        public override Color MenuStripGradientEnd => Background2;
        public override Color MenuItemSelected => Surface;
        public override Color MenuItemBorder => BorderRed;
        public override Color MenuBorder => Border;
        public override Color ImageMarginGradientBegin => Background2;
        public override Color ImageMarginGradientMiddle => Background2;
        public override Color ImageMarginGradientEnd => Background2;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => BorderRed;
        public override Color ButtonSelectedBorder => BorderRed;
        public override Color ButtonSelectedGradientBegin => Surface;
        public override Color ButtonSelectedGradientMiddle => Surface;
        public override Color ButtonSelectedGradientEnd => Surface;
        public override Color ButtonPressedGradientBegin => Background3;
        public override Color ButtonPressedGradientMiddle => Background3;
        public override Color ButtonPressedGradientEnd => Background3;
    }
}
