using System.Text;
using Rental.Business;

namespace Rental.WinForms;

internal sealed partial class MainForm : Form
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
        AddHomePage();
        AddPage("Properties", ("Add", () => EditProperty(null)), ("View profile", () => ShowPropertyProfile(SelectedProperty().Id)), ("Edit", () => EditProperty(SelectedProperty())));
        AddPage("Units", ("Add", () => EditUnit(null)), ("Edit", () => EditUnit(SelectedUnit())));
        AddPage("Renters", ("Add", () => EditRenter(null)), ("View profile", () => ShowRenterProfile(SelectedRenter().Id)), ("Edit", () => EditRenter(SelectedRenter())));
        AddPage("Assignments", ("Add", () => EditLease(null)), ("Edit", () => EditLease(SelectedLease())));
        AddPage("Charges", ("Generate monthly rent", GenerateRent), ("Create split bill", () => CreateBill()),
            ("Record payment", () => MarkPaid()), ("Clear payments", () => MarkUnpaid()), ("Send bill notice", () => Send("Bill")),
            ("Send late notice", () => Send("Late")), ("Send receipt", () => Send("Receipt")), ("Email settings", ConfigureEmail));
        AddPage("Expenses", ("Add", () => EditExpense(null)), ("Edit", () => EditExpense(SelectedExpense())),
            ("Import receipt", () => ImportReceipt()), ("View / export receipts", () => ViewReceipts()));
        AddReportPage();
        grids["Properties"].CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Run(() => ShowPropertyProfile(SelectedProperty().Id)); };
        grids["Renters"].CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Run(() => ShowRenterProfile(SelectedRenter().Id)); };
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
        var paidByCharge = service.Store.Payments().GroupBy(x => x.ChargeId).ToDictionary(g => g.Key, g => g.Sum(x => x.AmountCents));
        var p = properties.ToDictionary(x => x.Id);
        var u = units.ToDictionary(x => x.Id);
        var r = renters.ToDictionary(x => x.Id);
        var l = leases.ToDictionary(x => x.Id);
        grids["Properties"].DataSource = properties.Select(x => new { x.Id, x.Name, Address = FormatAddress(x), PurchasePrice = x.PurchasePriceCents is null ? "" : Money(x.PurchasePriceCents.Value) }).ToList();
        grids["Units"].DataSource = units.Select(x => new { x.Id, Property = p[x.PropertyId].Name, Unit = x.Name }).ToList();
        grids["Renters"].DataSource = renters.Select(x => new { x.Id, x.Name, x.Email, x.Phone, SecondaryContact = x.SecondaryContactName }).ToList();
        grids["Assignments"].DataSource = leases.Select(x => new { x.Id, Property = p[u[x.UnitId].PropertyId].Name, Unit = u[x.UnitId].Name,
            Renter = r[x.RenterId].Name, Start = x.Start.ToString("d"), End = x.End?.ToString("d") ?? "", Rent = Money(x.MonthlyRentCents), Deposit = Money(x.DepositCents) }).ToList();
        grids["Charges"].DataSource = service.Store.Charges().Select(x => new { x.Id, Property = p[x.PropertyId].Name, Renter = r[l[x.LeaseId].RenterId].Name,
            x.Kind, x.Description, Due = x.DueDate.ToString("d"), Amount = Money(x.AmountCents), Status = x.PaidDate is not null ? "Paid" : x.DueDate < DateOnly.FromDateTime(DateTime.Today) ? "Overdue" : "Unpaid",
            Balance = Money(x.AmountCents - paidByCharge.GetValueOrDefault(x.Id)), Paid = x.PaidDate?.ToString("d") ?? "", Allocation = x.Percentage is null ? "" : $"{x.Percentage}%" }).ToList();
        grids["Expenses"].DataSource = service.Store.Expenses().Select(x => new { x.Id, Property = p[x.PropertyId].Name, x.Category, x.Description,
            Paid = x.PaidDate.ToString("d"), Amount = Money(x.AmountCents) }).ToList();
        int selected = (reportProperty.SelectedItem as Choice)?.Id ?? 0;
        reportProperty.Items.Clear(); reportProperty.Items.Add(new Choice(0, "All properties"));
        reportProperty.Items.AddRange(PropertyChoices().Cast<object>().ToArray());
        reportProperty.SelectedItem = reportProperty.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selected) ?? reportProperty.Items[0];
        currentReport = null; grids["Reports"].DataSource = null; reportSummary.Text = "Run a report to see totals.";
        RefreshHome();
    }

    private void EditProperty(Property? value)
    {
        TextBox name = null!, address = null!, line2 = null!, city = null!, region = null!, postal = null!, country = null!, price = null!;
        using var dialog = new EntryDialog(value is null ? "Add property" : "Edit property", () =>
        {
            long? cents = null;
            if (!string.IsNullOrWhiteSpace(price.Text))
            {
                if (!decimal.TryParse(price.Text, out var number)) throw new ArgumentException("Enter a valid purchase price or leave it blank.");
                cents = RentalService.Cents(number);
            }
            service.Save(new Property(value?.Id ?? 0, name.Text, address.Text, line2.Text, city.Text, region.Text, postal.Text, country.Text, cents)); return true;
        });
        name = dialog.TextField("Name", value?.Name ?? ""); address = dialog.TextField("Street address", value?.Address ?? "");
        line2 = dialog.TextField("Address line 2", value?.AddressLine2 ?? "", required: false);
        city = dialog.TextField("City", value?.City ?? "", required: false);
        region = dialog.TextField("State / province", value?.Region ?? "", required: false);
        postal = dialog.TextField("Postal code", value?.PostalCode ?? "", required: false);
        country = dialog.TextField("Country", value?.Country ?? "", required: false);
        price = dialog.TextField("Purchase price (optional)", value?.PurchasePriceCents is null ? "" : (value.PurchasePriceCents.Value / 100m).ToString("F2"), required: false);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void EditUnit(Unit? value, int? propertyId = null)
    {
        ComboBox property = null!; TextBox name = null!;
        using var dialog = new EntryDialog(value is null ? "Add unit" : "Edit unit", () =>
        { service.Save(new Unit(value?.Id ?? 0, EntryDialog.Selected(property), name.Text)); return true; });
        property = dialog.Select("Property", PropertyChoices(), value?.PropertyId ?? propertyId); name = dialog.TextField("Unit name", value?.Name ?? "");
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void EditRenter(Renter? value)
    {
        TextBox name = null!, email = null!, phone = null!, secondaryName = null!, secondaryPhone = null!, secondaryEmail = null!, relationship = null!;
        using var dialog = new EntryDialog(value is null ? "Add renter" : "Edit renter", () =>
        { service.Save(new Renter(value?.Id ?? 0, name.Text, email.Text, phone.Text, secondaryName.Text, secondaryPhone.Text, secondaryEmail.Text, relationship.Text)); return true; });
        name = dialog.TextField("Name", value?.Name ?? ""); email = dialog.TextField("Email", value?.Email ?? "", email: true);
        phone = dialog.TextField("Phone", value?.Phone ?? "", required: false);
        secondaryName = dialog.TextField("Secondary contact name", value?.SecondaryContactName ?? "", required: false);
        secondaryPhone = dialog.TextField("Secondary contact phone", value?.SecondaryContactPhone ?? "", required: false);
        secondaryEmail = dialog.TextField("Secondary contact email", value?.SecondaryContactEmail ?? "", required: false);
        relationship = dialog.TextField("Contact relationship", value?.SecondaryContactRelationship ?? "", required: false);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void EditLease(Lease? value, int? renterId = null)
    {
        ComboBox unit = null!, renter = null!; DateTimePicker start = null!, end = null!; NumericUpDown rent = null!, deposit = null!;
        using var dialog = new EntryDialog(value is null ? "Add assignment" : "Edit assignment", () =>
        {
            service.Save(new Lease(value?.Id ?? 0, EntryDialog.Selected(unit), EntryDialog.Selected(renter), EntryDialog.DateValue(start),
                EntryDialog.OptionalDate(end), RentalService.Cents(rent.Value), RentalService.Cents(deposit.Value))); return true;
        });
        unit = dialog.Select("Unit", UnitChoices(), value?.UnitId); renter = dialog.Select("Renter", RenterChoices(), value?.RenterId ?? renterId);
        start = dialog.Date("Start", value?.Start); end = dialog.Date("End (optional)", value?.End, true);
        rent = dialog.Money("Monthly rent", value?.MonthlyRentCents ?? 0); deposit = dialog.Money("Deposit", value?.DepositCents ?? 0);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
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
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void CreateBill(int? propertyId = null)
    {
        using var dialog = new BillDialog(service, propertyId);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void MarkPaid(Charge? selected = null)
    {
        var charge = selected ?? SelectedCharge();
        if (charge.PaidDate is not null) throw new InvalidOperationException("This charge is already paid. Mark it unpaid first to correct its payment date.");
        DateTimePicker date = null!; NumericUpDown amount = null!;
        long balance = service.Balance(charge);
        using var dialog = new EntryDialog("Record payment", () =>
        {
            if (balance == 0) service.SetPayment(charge.Id, EntryDialog.DateValue(date));
            else service.RecordPayment(charge.Id, EntryDialog.DateValue(date), RentalService.Cents(amount.Value));
            return true;
        });
        date = dialog.Date("Payment date");
        amount = dialog.Money("Amount received", balance);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void MarkUnpaid(Charge? selected = null)
    {
        var charge = selected ?? SelectedCharge();
        if (charge.PaidDate is null && !service.Store.Payments().Any(p => p.ChargeId == charge.Id)) throw new InvalidOperationException("This charge has no recorded payments.");
        if (MessageBox.Show(Form.ActiveForm ?? this, "Remove ALL recorded payments for this charge? This cannot be undone.", "Clear payments", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        service.SetPayment(charge.Id, null); RefreshAll();
    }
    private void EditExpense(Expense? value, int? propertyId = null)
    {
        ComboBox property = null!; TextBox category = null!, description = null!; DateTimePicker date = null!; NumericUpDown amount = null!;
        using var dialog = new EntryDialog(value is null ? "Add expense" : "Edit expense", () =>
        {
            service.Save(new Expense(value?.Id ?? 0, EntryDialog.Selected(property), category.Text, description.Text,
                EntryDialog.DateValue(date), RentalService.Cents(amount.Value))); return true;
        });
        property = dialog.Select("Property", PropertyChoices(), value?.PropertyId ?? propertyId); category = dialog.TextField("Category (e.g. Maintenance, Property taxes)", value?.Category ?? "");
        description = dialog.TextField("Description", value?.Description ?? ""); date = dialog.Date("Date paid", value?.PaidDate); amount = dialog.Money("Amount", value?.AmountCents ?? 0);
        if (dialog.ShowDialog(Form.ActiveForm ?? this) == DialogResult.OK) RefreshAll();
    }
    private void ImportReceipt(Expense? selected = null)
    {
        var expense = selected ?? SelectedExpense();
        using var open = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp", Multiselect = true };
        if (open.ShowDialog(Form.ActiveForm ?? this) != DialogResult.OK) return;
        var images = new List<(string Name, string Type, byte[] Data)>();
        foreach (string file in open.FileNames)
        {
            var image = ReadImage(file);
            images.Add((image.FileName, image.ContentType, image.Data));
        }
        foreach (var image in images) service.AddReceipt(expense.Id, image.Name, image.Type, image.Data);
        MessageBox.Show(this, "Receipt images imported.", "Receipts");
    }
    private void ViewReceipts(Expense? selected = null)
    {
        var receipts = service.Store.Receipts((selected ?? SelectedExpense()).Id);
        if (receipts.Count == 0) { MessageBox.Show(this, "No receipt images for this expense."); return; }
        using var dialog = new ReceiptDialog(receipts);
        dialog.ShowDialog(Form.ActiveForm ?? this);
    }
    private void ConfigureEmail()
    {
        TextBox from = null!, key = null!;
        using var dialog = new EntryDialog("SendGrid settings", () => { sender.Configure(from.Text, key.Text); return true; });
        from = dialog.TextField("Verified sender email", sender.Sender, email: true);
        key = dialog.TextField(sender.Configured ? "API key (blank keeps existing)" : "API key", secret: true, required: !sender.Configured);
        dialog.ShowDialog(this);
    }
    private async void Send(string type, Charge? selected = null)
    {
        if (sending) return;
        try
        {
            var charge = selected ?? SelectedCharge();
            var draft = service.PrepareNotice(charge.Id, type, DateOnly.FromDateTime(DateTime.Today));
            TextBox subject = null!, message = null!;
            using (var preview = new EntryDialog("Preview and edit email", () => true))
            {
                var recipient = preview.TextField("To", draft.Recipient); recipient.ReadOnly = true;
                subject = preview.TextField("Subject", draft.Subject);
                message = preview.TextArea("Message", draft.Message);
                if (preview.ShowDialog(Form.ActiveForm ?? this) != DialogResult.OK) return;
                draft = draft with { Subject = subject.Text, Message = message.Text };
            }
            if (MessageBox.Show(Form.ActiveForm ?? this, $"Send {type.ToLowerInvariant()} email for the selected charge?", "Confirm email", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            sending = true;
            await service.SendPreparedAsync(draft, sender, DateOnly.FromDateTime(DateTime.Today));
            MessageBox.Show(Form.ActiveForm ?? this, "SendGrid accepted the message. Delivery is not guaranteed.", "Email sent");
        }
        catch (Exception ex) { MessageBox.Show(Form.ActiveForm ?? this, ex.Message + "\n\nIf a send was attempted, check its status before retrying.", "Email not confirmed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { sending = false; }
    }
    private void RunReport()
    {
        int id = EntryDialog.Selected(reportProperty);
        currentReport = service.GetReport(id == 0 ? null : id, EntryDialog.DateValue(reportFrom), EntryDialog.DateValue(reportTo));
        var summaries = RentalService.PropertySummaries(currentReport).SelectMany(p => new[]
        {
            new { Property = $"{p.Property} (#{p.PropertyId})", Type = "Property summary", Description = "Rent received", Date = "", Amount = Money(p.RentCents) },
            new { Property = $"{p.Property} (#{p.PropertyId})", Type = "Property summary", Description = "Bill reimbursements", Date = "", Amount = Money(p.ReimbursementsCents) },
            new { Property = $"{p.Property} (#{p.PropertyId})", Type = "Property summary", Description = "Expenses", Date = "", Amount = Money(p.ExpenseCents) },
            new { Property = $"{p.Property} (#{p.PropertyId})", Type = "Property summary", Description = "Rent less expenses", Date = "", Amount = Money(p.RentCents - p.ExpenseCents) }
        });
        grids["Reports"].DataSource = summaries.Concat(currentReport.Rows.Select(x => new { x.Property, x.Type, x.Description, Date = x.Date.ToString("d"), Amount = Money(x.AmountCents) })).ToList();
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
