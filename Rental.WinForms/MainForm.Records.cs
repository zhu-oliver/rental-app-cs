using Rental.Business;

namespace Rental.WinForms;

internal sealed partial class MainForm
{
    private void EditMaintenance(int propertyId, Maintenance? value)
    {
        ComboBox unit = null!, status = null!, expense = null!;
        TextBox description = null!, contractor = null!;
        using var dialog = new EntryDialog(value is null ? "Add maintenance" : "Edit maintenance", () =>
        {
            int unitId = EntryDialog.Selected(unit), expenseId = EntryDialog.Selected(expense);
            service.Save(new Maintenance(value?.Id ?? 0, propertyId, unitId == 0 ? null : unitId, description.Text,
                ((Choice)status.SelectedItem!).Label, contractor.Text, expenseId == 0 ? null : expenseId));
            return true;
        });
        var units = new[] { new Choice(0, "Entire property") }.Concat(service.Store.Units().Where(u => u.PropertyId == propertyId).Select(u => new Choice(u.Id, u.Name)));
        var expenses = new[] { new Choice(0, "No expense linked") }.Concat(service.Store.Expenses().Where(e => e.PropertyId == propertyId)
            .Select(e => new Choice(e.Id, $"{e.Description} - {Money(e.AmountCents)}")));
        unit = dialog.Select("Unit", units, value?.UnitId ?? 0);
        description = dialog.TextField("Description", value?.Description ?? "");
        var states = new[] { new Choice(1, "Open"), new Choice(2, "In progress"), new Choice(3, "Completed") };
        status = dialog.Select("Status", states, states.FirstOrDefault(s => s.Label == value?.Status)?.Id ?? 1);
        contractor = dialog.TextField("Contractor", value?.Contractor ?? "", required: false);
        expense = dialog.Select("Linked expense", expenses, value?.ExpenseId ?? 0);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }

    private void ManageDocuments(int? propertyId, int? renterId)
    {
        using var form = new Form { Text = "Documents", Width = 650, Height = 450, StartPosition = FormStartPosition.CenterParent };
        var list = new ListBox { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        void Reload()
        {
            list.Items.Clear();
            foreach (var doc in service.Store.Documents(propertyId, renterId)) list.Items.Add(new Choice(doc.Id, doc.FileName));
        }
        bar.Controls.Add(ProfileButton("Import document", () =>
        {
            using var open = new OpenFileDialog { Filter = "All files|*.*" };
            if (open.ShowDialog(form) != DialogResult.OK) return;
            if (new FileInfo(open.FileName).Length > 20 * 1024 * 1024) throw new ArgumentException("Document exceeds the 20 MB limit.");
            service.AddDocument(propertyId, renterId, Path.GetFileName(open.FileName), File.ReadAllBytes(open.FileName));
            Reload();
        }));
        bar.Controls.Add(ProfileButton("Export selected", () =>
        {
            if (list.SelectedItem is not Choice selected) throw new InvalidOperationException("Select a document.");
            var doc = service.Store.Documents(propertyId, renterId).Single(d => d.Id == selected.Id);
            using var save = new SaveFileDialog { FileName = doc.FileName, Filter = "All files|*.*" };
            if (save.ShowDialog(form) == DialogResult.OK) File.WriteAllBytes(save.FileName, doc.Data);
        }));
        form.Controls.Add(list); form.Controls.Add(bar); Reload(); form.ShowDialog(Form.ActiveForm ?? this);
    }

    private void ShowEmailHistory(int renterId)
    {
        using var form = new Form { Text = "Email history", Width = 900, Height = 600, StartPosition = FormStartPosition.CenterParent };
        var leases = service.Store.Leases().Where(l => l.RenterId == renterId).Select(l => l.Id).ToHashSet();
        var charges = service.Store.Charges().Where(c => leases.Contains(c.LeaseId)).Select(c => c.Id).ToHashSet();
        var attempts = service.Store.EmailAttempts().Where(a => charges.Contains(a.ChargeId)).ToList();
        var grid = NewGrid();
        var details = new TextBox { Dock = DockStyle.Bottom, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 200 };
        grid.DataSource = attempts.Select(a => new { a.Id, a.CreatedUtc, a.Type, a.Recipient, a.Subject, a.Status }).ToList();
        void SelectAttempt()
        {
            if (grid.CurrentRow is null) { details.Text = ""; return; }
            var item = attempts.Single(a => a.Id == RowId(grid));
            details.Text = $"{item.Message}\r\n\r\n{item.Error}";
        }
        grid.SelectionChanged += (_, _) => SelectAttempt();
        form.Controls.Add(grid); form.Controls.Add(details); SelectAttempt(); form.ShowDialog(Form.ActiveForm ?? this);
    }
}
