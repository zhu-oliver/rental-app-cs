using Rental.Business;

namespace Rental.WinForms;

internal sealed class BillDialog : Form
{
    private readonly RentalService service;
    private readonly ComboBox property = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox description = new() { Width = 260 };
    private readonly DateTimePicker due = new() { Format = DateTimePickerFormat.Short };
    private readonly NumericUpDown amount = new() { DecimalPlaces = 2, Maximum = 1000000000, ThousandsSeparator = true };
    private readonly DataGridView allocations = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
    };

    public BillDialog(RentalService service)
    {
        this.service = service;
        Text = "Create property bill"; Width = 720; Height = 530; StartPosition = FormStartPosition.CenterParent;
        var top = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        void Field(string text, Control control)
        {
            int row = top.RowCount++;
            top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, row);
            top.Controls.Add(control, 1, row);
        }
        Field("Property", property); Field("Description", description); Field("Due date", due); Field("Total amount", amount);
        property.Items.AddRange(service.Store.Properties().Select(p => new Choice(p.Id, p.Name)).Cast<object>().ToArray());
        allocations.Columns.Add(new DataGridViewTextBoxColumn { Name = "LeaseId", HeaderText = "Assignment ID", ReadOnly = true });
        allocations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Renter", HeaderText = "Renter / unit", ReadOnly = true });
        allocations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Percentage", HeaderText = "Percentage (blank = exclude)" });
        var hint = new Label { Text = "Enter percentages totaling 100 for the renters sharing this bill.", Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 4, 0, 0) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var save = new Button { Text = "Create bill", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => SaveBill(); bar.Controls.Add(save); bar.Controls.Add(cancel);
        Controls.Add(allocations); Controls.Add(hint); Controls.Add(top); Controls.Add(bar);
        property.SelectedIndexChanged += (_, _) => Populate();
        due.ValueChanged += (_, _) => Populate();
        if (property.Items.Count > 0) property.SelectedIndex = 0;
        CancelButton = cancel;
    }

    private void Populate()
    {
        allocations.Rows.Clear();
        if (property.SelectedItem is not Choice selected) return;
        var date = DateOnly.FromDateTime(due.Value);
        var units = service.Store.Units().Where(x => x.PropertyId == selected.Id).ToDictionary(x => x.Id);
        var renters = service.Store.Renters().ToDictionary(x => x.Id);
        foreach (var lease in service.Store.Leases().Where(l => units.ContainsKey(l.UnitId) && l.Start <= date && (l.End is null || l.End >= date)))
            allocations.Rows.Add(lease.Id, $"{renters[lease.RenterId].Name} / {units[lease.UnitId].Name}", "");
    }
    private void SaveBill()
    {
        try
        {
            allocations.EndEdit();
            var shares = new List<Allocation>();
            foreach (DataGridViewRow row in allocations.Rows)
            {
                var text = Convert.ToString(row.Cells["Percentage"].Value)?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (!decimal.TryParse(text, out var percentage)) throw new ArgumentException("Enter a valid percentage for each selected renter.");
                shares.Add(new Allocation(Convert.ToInt32(row.Cells["LeaseId"].Value), percentage));
            }
            service.CreateBill(EntryDialog.Selected(property), description.Text, DateOnly.FromDateTime(due.Value), RentalService.Cents(amount.Value), shares);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to create bill", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
