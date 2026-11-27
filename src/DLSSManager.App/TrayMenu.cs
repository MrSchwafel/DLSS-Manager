using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DLSSManager.Core;
using Forms = System.Windows.Forms;

namespace DLSSManager.App;

internal sealed class TrayMenu : Forms.ContextMenuStrip
{
    private const string ScanKey = "Tray.RescanLibrary";
    private static readonly Color Ink = Color.FromArgb(0xF1, 0xF5, 0xF9);
    private static readonly Color Muted = Color.FromArgb(0x94, 0xA3, 0xB8);
    private readonly LibraryViewModel model;
    private readonly Font primaryFont;
    private readonly List<Image> icons = [];
    private readonly Dictionary<Forms.ToolStripMenuItem, string> labels = [];
    private readonly Forms.ToolStripMenuItem scanItem;
    private readonly Forms.ToolStripMenuItem syncItem;
    private readonly Forms.ToolStripMenuItem updateItem;
    private readonly Forms.ToolStripMenuItem folderItem;

    public TrayMenu(LibraryViewModel model, Action open, Action scan, Action sync, Action update,
        Action openFolder, Action openSettings, Action exit)
    {
        this.model = model;
        ShowImageMargin = true;
        ShowCheckMargin = false;
        primaryFont = new Font(Font, FontStyle.Bold);

        AddItem("Tray.OpenManager", '\uE737', open).Font = primaryFont;
        Items.Add(new Forms.ToolStripSeparator());
        scanItem = AddItem(ScanKey, '\uE721', scan);
        syncItem = AddItem("Tray.SyncCatalog", '\uE895', sync);
        updateItem = AddItem("Tray.CheckUpdates", '\uE896', update);
        Items.Add(new Forms.ToolStripSeparator());
        folderItem = AddItem("Tray.OpenDataFolder", '\uE8B7', openFolder);
        AddItem("Tray.OpenSettings", '\uE713', openSettings);
        Items.Add(new Forms.ToolStripSeparator());
        var exitItem = AddItem("Tray.Exit", '\uE711', exit);
        Renderer = new DarkRenderer(exitItem);
        model.PropertyChanged += OnModelChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private Forms.ToolStripMenuItem AddItem(string key, char glyph, Action action)
    {
        var icon = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(icon);
        using var font = new Font("Segoe MDL2 Assets", 29, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Ink);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, icon.Width, icon.Height), format);
        icons.Add(icon);

        var item = new Forms.ToolStripMenuItem(MenuText(key), icon, (_, _) => action())
        {
            Padding = new Forms.Padding(0, 7, 8, 7)
        };
        labels.Add(item, key);
        Items.Add(item);
        return item;
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        RefreshActions();
        base.OnOpening(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Visible && e.PropertyName == nameof(LibraryViewModel.IsBusy)) RefreshActions();
    }

    private static string MenuText(string key) => LocalizationService.GetText(key).Replace("&", "&&");

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var (item, key) in labels) item.Text = MenuText(key);
        RefreshActions();
    }

    private void RefreshActions()
    {
        scanItem.Text = MenuText(model.IsScanning ? "Tray.Scanning" : ScanKey);
        scanItem.Enabled = syncItem.Enabled = updateItem.Enabled = !model.IsBusy;
        folderItem.Enabled = model.OpenDataFolder.CanExecute(null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            model.PropertyChanged -= OnModelChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        }
        base.Dispose(disposing);
        if (!disposing) return;
        primaryFont.Dispose();
        foreach (var icon in icons) icon.Dispose();
    }

    private sealed class DarkRenderer(Forms.ToolStripMenuItem exitItem) : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(Color.FromArgb(0x1E, 0x22, 0x29));

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(31, Color.White));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var inset = (e.ToolStrip?.DeviceDpi ?? 96) * 4 / 96;
            var bounds = Rectangle.Inflate(new Rectangle(Point.Empty, e.Item.Size), -inset, 0);
            using var shape = new GraphicsPath();
            shape.AddRoundedRectangle(bounds, new Size(inset * 2, inset * 2));
            using var brush = new SolidBrush(e.Item == exitItem
                ? Color.FromArgb(36, 239, 68, 68) : Color.FromArgb(20, Color.White));
            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, shape);
            e.Graphics.SmoothingMode = smoothing;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var color = !e.Item.Enabled ? Muted :
                e.Item == exitItem && e.Item.Selected ? Color.FromArgb(0xFC, 0xA5, 0xA5) : Ink;
            var bounds = e.TextRectangle;
            bounds.Y = 0;
            bounds.Height = e.Item.Height;
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, bounds, color,
                e.TextFormat | Forms.TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var inset = (e.ToolStrip?.DeviceDpi ?? 96) * 10 / 96;
            var midpoint = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(20, Color.White));
            e.Graphics.DrawLine(pen, inset, midpoint, e.Item.Width - inset, midpoint);
        }
    }
}
