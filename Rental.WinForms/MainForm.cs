using System.Drawing.Imaging;
using System.Text;
using Rental.Business;

namespace Rental.WinForms;

internal sealed class MainForm : Form
{
    private readonly RentalService service;
    private readonly SendGridSender sender;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, DataGridView> grids = new();
    private ComboBox reportProperty = null!;
    private DateTimePicker reportFrom = null!, reportTo = null!;
    private Label reportSummary = null!;
    private Report? currentReport;
    private bool sending;

    public MainForm(RentalService service, string directory)
    {
        this.service = service;
        sender = new SendGridSender(directory);
        Text = "Rental Property Manager"; Width = 1200; Height = 760; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(tabs);
        AddPage("Properties", ("Add", () => EditProperty(null)), ("Edit", () => EditProperty(SelectedProperty())));
        AddPage("Units", ("Add", () => EditUnit(null)), ("Edit", () => EditUnit(SelectedUnit())));
        AddPage("Renters", ("Add", () => EditRenter(null)), ("Edit", () => EditRenter(SelectedRenter())));
        AddPage("Assignments", ("Add", () => EditLease(null)), ("Edit", () => EditLease(SelectedLease())));
        AddPage("Charges", ("Generate monthly rent", GenerateRent), ("Create split bill", CreateBill),
            ("Mark paid", MarkPaid), ("Mark unpaid", MarkUnpaid), ("Send bill notice", () => Send("Bill")),
            ("Send late notice", () => Send("Late")), ("Send receipt", () => Send("Receipt")), ("Email settings", ConfigureEmail));
        AddPage("Expenses", ("Add", () => EditExpense(null)), ("Edit", () => EditExpense(SelectedExpense())),
            ("Import receipt", ImportReceipt), ("View / export receipts", ViewReceipts));
        AddReportPage();
        RefreshAll();
    }

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Rental Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddPage(string name, params (string Text, Action Action)[] actions)
    {
        var page = new TabPage(name);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        foreach (var (text, action) in actions)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => Run(action);
            bar.Controls.Add(button);
        }
        var grid = NewGrid();
        page.Controls.Add(grid); page.Controls.Add(bar); tabs.TabPages.Add(page); grids[name] = grid;
    }

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, RowHeadersVisible = false
    };

    private void AddReportPage()
    {
        var page = new TabPage("Reports");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        reportProperty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        reportFrom = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 115, Value = new DateTime(DateTime.Today.Year, 1, 1) };
        reportTo = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 115 };
        void InvalidateReport()
        {
            currentReport = null;
            grids["Reports"].DataSource = null;
            reportSummary.Text = "Run a report to see totals.";
        }
        var run = new Button { Text = "Run report", AutoSize = true };
        var export = new Button { Text = "Export CSV", AutoSize = true };
        run.Click += (_, _) => Run(RunReport);
        export.Click += (_, _) => Run(ExportReport);
        bar.Controls.AddRange([new Label { Text = "Property", AutoSize = true }, reportProperty, new Label { Text = "From", AutoSize = true },
            reportFrom, new Label { Text = "To", AutoSize = true }, reportTo, run, export]);
        reportSummary = new Label { Dock = DockStyle.Top, Height = 65, Padding = new Padding(8) };
        var grid = NewGrid(); grids["Reports"] = grid;
        page.Controls.Add(grid); page.Controls.Add(reportSummary); page.Controls.Add(bar); tabs.TabPages.Add(page);
        reportProperty.SelectedIndexChanged += (_, _) => InvalidateReport();
        reportFrom.ValueChanged += (_, _) => InvalidateReport();
        reportTo.ValueChanged += (_, _) => InvalidateReport();
    }

    private int SelectedId(string page)
    {
        var row = grids[page].CurrentRow ?? throw new InvalidOperationException($"Select a row in {page}.");
        return Convert.ToInt32(row.Cells["Id"].Value);
    }
    private Property SelectedProperty() => service.Store.Properties().Single(x => x.Id == SelectedId("Properties"));
    private Unit SelectedUnit() => service.Store.Units().Single(x => x.Id == SelectedId("Units"));
    private Renter SelectedRenter() => service.Store.Renters().Single(x => x.Id == SelectedId("Renters"));
    private Lease SelectedLease() => service.Store.Leases().Single(x => x.Id == SelectedId("Assignments"));
    private Charge SelectedCharge() => service.Store.Charges().Single(x => x.Id == SelectedId("Charges"));
    private Expense SelectedExpense() => service.Store.Expenses().Single(x => x.Id == SelectedId("Expenses"));
    private static string Money(long cents) => (cents / 100m).ToString("C");
    private IEnumerable<Choice> PropertyChoices() => service.Store.Properties().Select(p => new Choice(p.Id, p.Name));
    private IEnumerable<Choice> UnitChoices()
    {
        var properties = service.Store.Properties().ToDictionary(p => p.Id);
        return service.Store.Units().Select(u => new Choice(u.Id, $"{properties[u.PropertyId].Name} / {u.Name}"));
    }
    private IEnumerable<Choice> RenterChoices() => service.Store.Renters().Select(r => new Choice(r.Id, r.Name));

    private void RefreshAll()
    {
        var properties = service.Store.Properties();
        var units = service.Store.Units();
        var renters = service.Store.Renters();
        var leases = service.Store.Leases();
        var p = properties.ToDictionary(x => x.Id);
        var u = units.ToDictionary(x => x.Id);
        var r = renters.ToDictionary(x => x.Id);
        var l = leases.ToDictionary(x => x.Id);
        grids["Properties"].DataSource = properties.ToList();
        grids["Units"].DataSource = units.Select(x => new { x.Id, Property = p[x.PropertyId].Name, Unit = x.Name }).ToList();
        grids["Renters"].DataSource = renters.ToList();
        grids["Assignments"].DataSource = leases.Select(x => new { x.Id, Property = p[u[x.UnitId].PropertyId].Name, Unit = u[x.UnitId].Name,
            Renter = r[x.RenterId].Name, Start = x.Start.ToString("d"), End = x.End?.ToString("d") ?? "", Rent = Money(x.MonthlyRentCents), Deposit = Money(x.DepositCents) }).ToList();
        grids["Charges"].DataSource = service.Store.Charges().Select(x => new { x.Id, Property = p[x.PropertyId].Name, Renter = r[l[x.LeaseId].RenterId].Name,
            x.Kind, x.Description, Due = x.DueDate.ToString("d"), Amount = Money(x.AmountCents), Status = x.PaidDate is not null ? "Paid" : x.DueDate < DateOnly.FromDateTime(DateTime.Today) ? "Overdue" : "Unpaid",
            Paid = x.PaidDate?.ToString("d") ?? "", Allocation = x.Percentage is null ? "" : $"{x.Percentage}%" }).ToList();
        grids["Expenses"].DataSource = service.Store.Expenses().Select(x => new { x.Id, Property = p[x.PropertyId].Name, x.Category, x.Description,
            Paid = x.PaidDate.ToString("d"), Amount = Money(x.AmountCents) }).ToList();
        int selected = (reportProperty.SelectedItem as Choice)?.Id ?? 0;
        reportProperty.Items.Clear(); reportProperty.Items.Add(new Choice(0, "All properties"));
        reportProperty.Items.AddRange(PropertyChoices().Cast<object>().ToArray());
        reportProperty.SelectedItem = reportProperty.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selected) ?? reportProperty.Items[0];
        currentReport = null; grids["Reports"].DataSource = null; reportSummary.Text = "Run a report to see totals.";
    }

    private void EditProperty(Property? value)
    {
        TextBox name = null!, address = null!;
        using var dialog = new EntryDialog(value is null ? "Add property" : "Edit property", () =>
        { service.Save(new Property(value?.Id ?? 0, name.Text, address.Text)); return true; });
        name = dialog.TextField("Name", value?.Name ?? ""); address = dialog.TextField("Address", value?.Address ?? "");
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void EditUnit(Unit? value)
    {
        ComboBox property = null!; TextBox name = null!;
        using var dialog = new EntryDialog(value is null ? "Add unit" : "Edit unit", () =>
        { service.Save(new Unit(value?.Id ?? 0, EntryDialog.Selected(property), name.Text)); return true; });
        property = dialog.Select("Property", PropertyChoices(), value?.PropertyId); name = dialog.TextField("Unit name", value?.Name ?? "");
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void EditRenter(Renter? value)
    {
        TextBox name = null!, email = null!;
        using var dialog = new EntryDialog(value is null ? "Add renter" : "Edit renter", () =>
        { service.Save(new Renter(value?.Id ?? 0, name.Text, email.Text)); return true; });
        name = dialog.TextField("Name", value?.Name ?? ""); email = dialog.TextField("Email", value?.Email ?? "", email: true);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void EditLease(Lease? value)
    {
        ComboBox unit = null!, renter = null!; DateTimePicker start = null!, end = null!; NumericUpDown rent = null!, deposit = null!;
        using var dialog = new EntryDialog(value is null ? "Add assignment" : "Edit assignment", () =>
        {
            service.Save(new Lease(value?.Id ?? 0, EntryDialog.Selected(unit), EntryDialog.Selected(renter), EntryDialog.DateValue(start),
                EntryDialog.OptionalDate(end), RentalService.Cents(rent.Value), RentalService.Cents(deposit.Value))); return true;
        });
        unit = dialog.Select("Unit", UnitChoices(), value?.UnitId); renter = dialog.Select("Renter", RenterChoices(), value?.RenterId);
        start = dialog.Date("Start", value?.Start); end = dialog.Date("End (optional)", value?.End, true);
        rent = dialog.Money("Monthly rent", value?.MonthlyRentCents ?? 0); deposit = dialog.Money("Deposit", value?.DepositCents ?? 0);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void GenerateRent()
    {
        DateTimePicker month = null!, due = null!;
        using var dialog = new EntryDialog("Generate rent", () =>
        {
            int count = service.GenerateRent(EntryDialog.DateValue(month), EntryDialog.DateValue(due));
            MessageBox.Show(this, $"Created {count} rent charge(s).", "Rent"); return true;
        });
        month = dialog.Date("Month (any date)"); due = dialog.Date("Due date");
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void CreateBill()
    {
        using var dialog = new BillDialog(service);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void MarkPaid()
    {
        var charge = SelectedCharge();
        if (charge.PaidDate is not null) throw new InvalidOperationException("This charge is already paid. Mark it unpaid first to correct its payment date.");
        DateTimePicker date = null!;
        using var dialog = new EntryDialog("Record payment", () => { service.SetPayment(charge.Id, EntryDialog.DateValue(date)); return true; });
        date = dialog.Date("Payment date");
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void MarkUnpaid()
    {
        var charge = SelectedCharge();
        if (charge.PaidDate is null) throw new InvalidOperationException("This charge is already unpaid.");
        if (MessageBox.Show(this, "Remove the recorded payment date?", "Mark unpaid", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        service.SetPayment(charge.Id, null); RefreshAll();
    }
    private void EditExpense(Expense? value)
    {
        ComboBox property = null!; TextBox category = null!, description = null!; DateTimePicker date = null!; NumericUpDown amount = null!;
        using var dialog = new EntryDialog(value is null ? "Add expense" : "Edit expense", () =>
        {
            service.Save(new Expense(value?.Id ?? 0, EntryDialog.Selected(property), category.Text, description.Text,
                EntryDialog.DateValue(date), RentalService.Cents(amount.Value))); return true;
        });
        property = dialog.Select("Property", PropertyChoices(), value?.PropertyId); category = dialog.TextField("Category (e.g. Maintenance, Property taxes)", value?.Category ?? "");
        description = dialog.TextField("Description", value?.Description ?? ""); date = dialog.Date("Date paid", value?.PaidDate); amount = dialog.Money("Amount", value?.AmountCents ?? 0);
        if (dialog.ShowDialog(this) == DialogResult.OK) RefreshAll();
    }
    private void ImportReceipt()
    {
        var expense = SelectedExpense();
        using var open = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp", Multiselect = true };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        var images = new List<(string Name, string Type, byte[] Data)>();
        foreach (string file in open.FileNames)
        {
            if (new FileInfo(file).Length > 20 * 1024 * 1024) throw new ArgumentException($"Receipt image {Path.GetFileName(file)} exceeds the 20 MB limit.");
            byte[] data = File.ReadAllBytes(file);
            using var stream = new MemoryStream(data);
            using var image = Image.FromStream(stream, true, true);
            string type = image.RawFormat.Equals(ImageFormat.Jpeg) ? "image/jpeg" : image.RawFormat.Equals(ImageFormat.Png) ? "image/png" :
                image.RawFormat.Equals(ImageFormat.Gif) ? "image/gif" : image.RawFormat.Equals(ImageFormat.Bmp) ? "image/bmp" :
                throw new ArgumentException("Only JPEG, PNG, GIF, and BMP images are supported.");
            images.Add((Path.GetFileName(file), type, data));
        }
        foreach (var image in images) service.AddReceipt(expense.Id, image.Name, image.Type, image.Data);
        MessageBox.Show(this, "Receipt images imported.", "Receipts");
    }
    private void ViewReceipts()
    {
        var receipts = service.Store.Receipts(SelectedExpense().Id);
        if (receipts.Count == 0) { MessageBox.Show(this, "No receipt images for this expense."); return; }
        using var dialog = new ReceiptDialog(receipts);
        dialog.ShowDialog(this);
    }
    private void ConfigureEmail()
    {
        TextBox from = null!, key = null!;
        using var dialog = new EntryDialog("SendGrid settings", () => { sender.Configure(from.Text, key.Text); return true; });
        from = dialog.TextField("Verified sender email", sender.Sender, email: true);
        key = dialog.TextField(sender.Configured ? "API key (blank keeps existing)" : "API key", secret: true, required: !sender.Configured);
        dialog.ShowDialog(this);
    }
    private async void Send(string type)
    {
        if (sending) return;
        try
        {
            var charge = SelectedCharge();
            if (MessageBox.Show(this, $"Send {type.ToLowerInvariant()} email for the selected charge?", "Confirm email", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            sending = true;
            await service.SendNoticeAsync(charge.Id, type, sender, DateOnly.FromDateTime(DateTime.Today));
            MessageBox.Show(this, "SendGrid accepted the message. Delivery is not guaranteed.", "Email sent");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Email failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { sending = false; }
    }
    private void RunReport()
    {
        int id = EntryDialog.Selected(reportProperty);
        currentReport = service.GetReport(id == 0 ? null : id, EntryDialog.DateValue(reportFrom), EntryDialog.DateValue(reportTo));
        grids["Reports"].DataSource = currentReport.Rows.Select(x => new { x.Property, x.Type, x.Description, Date = x.Date.ToString("d"), Amount = Money(x.AmountCents) }).ToList();
        string categories = string.Join("; ", currentReport.ExpensesByCategory.Select(x => $"{x.Key}: {Money(x.Value)}"));
        reportSummary.Text = $"Rent received: {Money(currentReport.RentCents)}   Bill reimbursements: {Money(currentReport.ReimbursementsCents)}   Expenses: {Money(currentReport.ExpenseCents)}   Rent less expenses: {Money(currentReport.NetCents)} (not a tax calculation)\n{categories}";
    }
    private void ExportReport()
    {
        if (currentReport is null) throw new InvalidOperationException("Run the report before exporting.");
        using var save = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = "rental-report.csv" };
        if (save.ShowDialog(this) == DialogResult.OK) File.WriteAllText(save.FileName, RentalService.ReportCsv(currentReport), new UTF8Encoding(true));
    }
}
