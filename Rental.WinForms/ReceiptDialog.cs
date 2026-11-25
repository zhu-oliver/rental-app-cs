using Rental.Business;

namespace Rental.WinForms;

internal sealed class ReceiptDialog : Form
{
    private readonly IReadOnlyList<Receipt> receipts;
    private readonly ListBox list = new() { Dock = DockStyle.Left, Width = 220 };
    private readonly PictureBox image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };

    public ReceiptDialog(IReadOnlyList<Receipt> receipts)
    {
        this.receipts = receipts;
        Text = "Receipt images"; Width = 900; Height = 650; StartPosition = FormStartPosition.CenterParent;
        list.Items.AddRange(receipts.Select(r => r.FileName).Cast<object>().ToArray());
        var export = new Button { Text = "Export selected image", Dock = DockStyle.Bottom, Height = 40 };
        list.SelectedIndexChanged += (_, _) =>
        {
            try
            {
                using var stream = new MemoryStream(receipts[list.SelectedIndex].Data);
                using var source = Image.FromStream(stream);
                var old = image.Image;
                image.Image = new Bitmap(source);
                old?.Dispose();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to view image", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        export.Click += (_, _) =>
        {
            try
            {
                if (list.SelectedIndex < 0) throw new InvalidOperationException("Select a receipt.");
                var receipt = receipts[list.SelectedIndex];
                using var save = new SaveFileDialog { FileName = receipt.FileName, Filter = "All files|*.*" };
                if (save.ShowDialog(this) == DialogResult.OK) File.WriteAllBytes(save.FileName, receipt.Data);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to export image", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        Controls.Add(image); Controls.Add(list); Controls.Add(export);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) image.Image?.Dispose();
        base.Dispose(disposing);
    }
}
