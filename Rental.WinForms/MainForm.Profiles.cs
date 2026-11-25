using System.Drawing.Imaging;
using Rental.Business;

namespace Rental.WinForms;

internal sealed partial class MainForm
{
    private TextBox search = null!;
    private Label dashboard = null!;
    private DataGridView searchResults = null!;

    private static string FormatAddress(Property p) => string.Join(", ",
        new[] { p.Address, p.AddressLine2, p.City, p.Region, p.PostalCode, p.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private void AddHomePage()
    {
        var page = new TabPage("Home");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        search = new TextBox { Width = 350, PlaceholderText = "Name, address, email or phone" };
        var open = new Button { Text = "Open profile", AutoSize = true };
        var backup = new Button { Text = "Back up database", AutoSize = true };
        var restore = new Button { Text = "Restore instructions", AutoSize = true };
        top.Controls.AddRange([new Label { Text = "Search properties and renters:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, search, open, backup, restore]);
        dashboard = new Label { Dock = DockStyle.Top, Height = 85, Padding = new Padding(12), AutoEllipsis = true };
        searchResults = NewGrid();
        page.Controls.Add(searchResults); page.Controls.Add(dashboard); page.Controls.Add(top);
        tabs.TabPages.Add(page);
        search.TextChanged += (_, _) => RefreshHome();
        open.Click += (_, _) => Run(OpenSearchResult);
        backup.Click += (_, _) => Run(() =>
        {
            using var save = new SaveFileDialog { Filter = "SQLite database|*.db", FileName = "rental-backup.db", OverwritePrompt = true };
            if (save.ShowDialog(this) != DialogResult.OK) return;
            service.Store.BackupTo(save.FileName);
            MessageBox.Show(this, "Backup created. Store it securely; it contains private information.", "Backup");
        });
        restore.Click += (_, _) => MessageBox.Show(this,
            "1. Close every Rental Property Manager window.\n2. Back up the current rentals.db first.\n3. Copy the chosen backup to %LOCALAPPDATA%\\RentalApplication\\rentals.db, replacing the existing file.\n4. Restart the app.\n\nIf RENTAL_APPLICATION_DATA is set, use that folder instead.",
            "Restore instructions", MessageBoxButtons.OK, MessageBoxIcon.Information);
        searchResults.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Run(OpenSearchResult); };
    }

    private void RefreshHome()
    {
        if (search is null) return;
        var query = search.Text.Trim();
        bool Match(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
        var properties = service.Store.Properties();
        var renters = service.Store.Renters();
        var rows = properties.Where(p => Match(p.Name) || Match(FormatAddress(p)))
            .Select(p => new SearchResult(p.Id, "Property", p.Name, FormatAddress(p)))
            .Concat(renters.Where(r => Match(r.Name) || Match(r.Email) || Match(r.Phone))
                .Select(r => new SearchResult(r.Id, "Renter", r.Name, $"{r.Email}  {r.Phone}"))).ToList();
        searchResults.DataSource = rows;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var charges = service.Store.Charges();
        var units = service.Store.Units();
        var occupied = service.Store.Leases().Where(l => l.Start <= today && (l.End is null || l.End >= today)).Select(l => l.UnitId).ToHashSet();
        var overdue = charges.Where(c => c.PaidDate is null && c.DueDate < today).ToList();
        var upcoming = charges.Where(c => c.Kind == "Rent" && c.PaidDate is null && c.DueDate >= today && c.DueDate <= today.AddDays(30)).ToList();
        var paid = service.Store.Payments().GroupBy(p => p.ChargeId).ToDictionary(g => g.Key, g => g.Sum(p => p.AmountCents));
        long Balance(Charge c) => c.AmountCents - paid.GetValueOrDefault(c.Id);
        var recent = service.Store.Expenses().OrderByDescending(e => e.PaidDate).Take(3).ToList();
        dashboard.Text = $"Properties: {properties.Count}   Renters: {renters.Count}   Vacant units: {units.Count(u => !occupied.Contains(u.Id))}\n" +
            $"Overdue: {overdue.Count} ({Money(overdue.Sum(Balance))})   Upcoming rent (30 days): {upcoming.Count} ({Money(upcoming.Sum(Balance))})\n" +
            $"Recent expenses: {string.Join("; ", recent.Select(e => $"{e.Description} {Money(e.AmountCents)}"))}";
    }
    private record SearchResult(int Id, string Type, string Name, string Details);
    private void OpenSearchResult()
    {
        if (searchResults.CurrentRow?.DataBoundItem is not SearchResult result) throw new InvalidOperationException("Select a search result.");
        if (result.Type == "Property") ShowPropertyProfile(result.Id);
        else ShowRenterProfile(result.Id);
    }

    private static int RowId(DataGridView grid)
    {
        if (grid.CurrentRow is null) throw new InvalidOperationException("Select a row first.");
        return Convert.ToInt32(grid.CurrentRow.Cells["Id"].Value);
    }
    private static Button ProfileButton(string label, Action action)
    {
        var button = new Button { Text = label, AutoSize = true };
        button.Click += (_, _) => Run(action);
        return button;
    }
    private static DataGridView ProfileGrid() { var grid = NewGrid(); grid.Height = 170; grid.Dock = DockStyle.Top; return grid; }
    private static Label Heading(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 27, Padding = new Padding(4), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
    private static Form ProfileWindow(string title) => new() { Text = title, Width = 1050, Height = 820, StartPosition = FormStartPosition.CenterParent };

    private void ShowRenterProfile(int renterId)
    {
        using var form = ProfileWindow("Renter profile");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var details = new Label { Dock = DockStyle.Top, Height = 105, Padding = new Padding(8) };
        var assignments = ProfileGrid(); var charges = ProfileGrid(); var paymentHistory = ProfileGrid();
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var store = service.Store;
        void Reload()
        {
            var renter = store.Renters().Single(r => r.Id == renterId);
            var units = store.Units().ToDictionary(u => u.Id);
            var properties = store.Properties().ToDictionary(p => p.Id);
            var leases = store.Leases().Where(l => l.RenterId == renterId).ToList();
            var ids = leases.Select(l => l.Id).ToHashSet();
            var chargeRows = store.Charges().Where(c => ids.Contains(c.LeaseId)).ToList();
            var payments = store.Payments().GroupBy(p => p.ChargeId).ToDictionary(g => g.Key, g => g.Sum(p => p.AmountCents));
            details.Text = $"{renter.Name}    {renter.Email}    {renter.Phone}\nSecondary contact: {renter.SecondaryContactName}  {renter.SecondaryContactRelationship}  {renter.SecondaryContactPhone}  {renter.SecondaryContactEmail}\n" +
                $"Outstanding balance: {Money(chargeRows.Sum(c => c.AmountCents - payments.GetValueOrDefault(c.Id)))}    Deposit on assignments: {Money(leases.Sum(l => l.DepositCents))}";
            assignments.DataSource = leases.Select(l => new { l.Id, Property = properties[units[l.UnitId].PropertyId].Name, Unit = units[l.UnitId].Name,
                Start = l.Start.ToString("d"), End = l.End?.ToString("d") ?? "", Rent = Money(l.MonthlyRentCents), Deposit = Money(l.DepositCents) }).ToList();
            charges.DataSource = chargeRows.Select(c => new { c.Id, Property = properties[c.PropertyId].Name, c.Kind, c.Description,
                Due = c.DueDate.ToString("d"), Amount = Money(c.AmountCents), Balance = Money(c.AmountCents - payments.GetValueOrDefault(c.Id)), Status = c.PaidDate is null ? c.DueDate < DateOnly.FromDateTime(DateTime.Today) ? "Overdue" : "Unpaid" : "Paid",
                Paid = c.PaidDate?.ToString("d") ?? "" }).ToList();
            var byId = chargeRows.ToDictionary(c => c.Id);
            paymentHistory.DataSource = store.Payments().Where(p => byId.ContainsKey(p.ChargeId))
                .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
                .Select(p => new { p.Id, Charge = byId[p.ChargeId].Description, Date = p.Date.ToString("d"), Amount = Money(p.AmountCents) }).ToList();
            RefreshAll();
        }
        Charge Selected() => store.Charges().Single(c => c.Id == RowId(charges));
        bar.Controls.AddRange([
            ProfileButton("Edit renter", () => { EditRenter(store.Renters().Single(r => r.Id == renterId)); Reload(); }),
            ProfileButton("Documents", () => ManageDocuments(null, renterId)),
            ProfileButton("Email history", () => ShowEmailHistory(renterId)),
            ProfileButton("Add assignment", () => { EditLease(null, renterId); Reload(); }),
            ProfileButton("Edit assignment", () => { EditLease(store.Leases().Single(l => l.Id == RowId(assignments))); Reload(); }),
            ProfileButton("Open property", () => { var lease = store.Leases().Single(l => l.Id == RowId(assignments)); ShowPropertyProfile(store.Units().Single(u => u.Id == lease.UnitId).PropertyId); Reload(); }),
            ProfileButton("Record payment", () => { MarkPaid(Selected()); Reload(); }),
            ProfileButton("Clear payments", () => { MarkUnpaid(Selected()); Reload(); }),
            ProfileButton("Send bill", () => Send("Bill", Selected())),
            ProfileButton("Send late notice", () => Send("Late", Selected())),
            ProfileButton("Send receipt", () => Send("Receipt", Selected()))
        ]);
        body.Controls.Add(paymentHistory); body.Controls.Add(Heading("Payment history"));
        body.Controls.Add(charges); body.Controls.Add(Heading("Charges (select a charge for payment or email)"));
        body.Controls.Add(assignments); body.Controls.Add(Heading("Unit assignments (select one to edit or open its property)"));
        form.Controls.Add(body); form.Controls.Add(details); form.Controls.Add(bar);
        Reload(); form.ShowDialog(Form.ActiveForm ?? this);
    }

    private void ShowPropertyProfile(int propertyId)
    {
        using var form = ProfileWindow("Property profile");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var header = new Panel { Dock = DockStyle.Top, Height = 155 };
        var details = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var picture = new PictureBox { Dock = DockStyle.Right, Width = 210, SizeMode = PictureBoxSizeMode.Zoom };
        header.Controls.Add(details); header.Controls.Add(picture);
        var unitsGrid = ProfileGrid(); var rentersGrid = ProfileGrid(); var expensesGrid = ProfileGrid(); var billsGrid = ProfileGrid(); var maintenanceGrid = ProfileGrid();
        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var store = service.Store;
        void Reload()
        {
            var property = store.Properties().Single(p => p.Id == propertyId);
            var units = store.Units().Where(u => u.PropertyId == propertyId).ToList();
            var ids = units.Select(u => u.Id).ToHashSet();
            var renters = store.Renters().ToDictionary(r => r.Id);
            var now = DateOnly.FromDateTime(DateTime.Today);
            var assignments = store.Leases().Where(l => ids.Contains(l.UnitId)).ToList();
            var current = assignments.Where(l => l.Start <= now && (l.End is null || l.End >= now)).ToList();
            var expenses = store.Expenses().Where(e => e.PropertyId == propertyId).ToList();
            var rentIds = store.Charges().Where(c => c.PropertyId == propertyId && c.Kind == "Rent").Select(c => c.Id).ToHashSet();
            var paidRent = store.Payments().Where(p => rentIds.Contains(p.ChargeId)).Sum(p => p.AmountCents);
            details.Text = $"{property.Name}\n{FormatAddress(property)}\nPurchase price: {(property.PurchasePriceCents is null ? "Not recorded" : Money(property.PurchasePriceCents.Value))}\n" +
                $"Rent received (all time): {Money(paidRent)}   Expenses (all time): {Money(expenses.Sum(e => e.AmountCents))}\nSelect a unit, renter or expense below to act on it.";
            unitsGrid.DataSource = units.Select(u => new { u.Id, Unit = u.Name, Occupants = current.Count(l => l.UnitId == u.Id) }).ToList();
            rentersGrid.DataSource = assignments.Select(l => new { Id = l.RenterId, Renter = renters[l.RenterId].Name,
                Unit = units.Single(u => u.Id == l.UnitId).Name, Start = l.Start.ToString("d"), End = l.End?.ToString("d") ?? "" }).ToList();
            expensesGrid.DataSource = expenses.Select(e => new { e.Id, e.Category, e.Description, Paid = e.PaidDate.ToString("d"), Amount = Money(e.AmountCents) }).ToList();
            billsGrid.DataSource = store.Bills().Where(b => b.PropertyId == propertyId).Select(b => new { b.Id, b.Description, Due = b.DueDate.ToString("d"), Amount = Money(b.AmountCents) }).ToList();
            maintenanceGrid.DataSource = store.MaintenanceItems(propertyId).Select(m => new { m.Id, m.Description, m.Status, m.Contractor,
                Unit = m.UnitId is null ? "Property" : units.Single(u => u.Id == m.UnitId).Name,
                Cost = m.ExpenseId is null ? "" : Money(expenses.Single(e => e.Id == m.ExpenseId).AmountCents) }).ToList();
            var old = picture.Image; picture.Image = null; old?.Dispose();
            var photo = store.Photo(propertyId);
            if (photo is not null) { using var stream = new MemoryStream(photo.Data); using var source = Image.FromStream(stream); picture.Image = new Bitmap(source); }
            RefreshAll();
        }
        bar.Controls.AddRange([
            ProfileButton("Edit property", () => { EditProperty(store.Properties().Single(p => p.Id == propertyId)); Reload(); }),
            ProfileButton("Documents", () => ManageDocuments(propertyId, null)),
            ProfileButton("Add / replace photo", () => { ImportPropertyPhoto(propertyId); Reload(); }),
            ProfileButton("Remove photo", () => { if (MessageBox.Show(form, "Remove this photo?", "Photo", MessageBoxButtons.YesNo) == DialogResult.Yes) { service.SetPhoto(propertyId, null); Reload(); } }),
            ProfileButton("Add unit", () => { EditUnit(null, propertyId); Reload(); }),
            ProfileButton("Edit unit", () => { EditUnit(store.Units().Single(u => u.Id == RowId(unitsGrid))); Reload(); }),
            ProfileButton("Open renter", () => { ShowRenterProfile(RowId(rentersGrid)); Reload(); }),
            ProfileButton("Create bill", () => { CreateBill(propertyId); Reload(); }),
            ProfileButton("Add maintenance", () => { EditMaintenance(propertyId, null); Reload(); }),
            ProfileButton("Edit maintenance", () => { EditMaintenance(propertyId, store.MaintenanceItems(propertyId).Single(m => m.Id == RowId(maintenanceGrid))); Reload(); }),
            ProfileButton("Add expense", () => { EditExpense(null, propertyId); Reload(); }),
            ProfileButton("Edit expense", () => { EditExpense(store.Expenses().Single(e => e.Id == RowId(expensesGrid))); Reload(); }),
            ProfileButton("Import receipt", () => ImportReceipt(store.Expenses().Single(e => e.Id == RowId(expensesGrid)))),
            ProfileButton("View receipts", () => ViewReceipts(store.Expenses().Single(e => e.Id == RowId(expensesGrid))))
        ]);
        body.Controls.Add(maintenanceGrid); body.Controls.Add(Heading("Maintenance requests"));
        body.Controls.Add(billsGrid); body.Controls.Add(Heading("Property bills"));
        body.Controls.Add(expensesGrid); body.Controls.Add(Heading("Property expenses"));
        body.Controls.Add(rentersGrid); body.Controls.Add(Heading("Occupants and assignment history"));
        body.Controls.Add(unitsGrid); body.Controls.Add(Heading("Units"));
        form.Controls.Add(body); form.Controls.Add(header); form.Controls.Add(bar);
        try { Reload(); form.ShowDialog(Form.ActiveForm ?? this); }
        finally { picture.Image?.Dispose(); }
    }

    private void ImportPropertyPhoto(int propertyId)
    {
        using var open = new OpenFileDialog { Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp" };
        if (open.ShowDialog(Form.ActiveForm ?? this) != DialogResult.OK) return;
        service.SetPhoto(propertyId, ReadImage(open.FileName));
    }

    private static PropertyPhoto ReadImage(string fileName)
    {
        if (new FileInfo(fileName).Length > 20 * 1024 * 1024) throw new ArgumentException("Image exceeds the 20 MB limit.");
        var data = File.ReadAllBytes(fileName);
        using var stream = new MemoryStream(data);
        using var image = Image.FromStream(stream, true, true);
        string type = image.RawFormat.Equals(ImageFormat.Jpeg) ? "image/jpeg" : image.RawFormat.Equals(ImageFormat.Png) ? "image/png" :
            image.RawFormat.Equals(ImageFormat.Gif) ? "image/gif" : image.RawFormat.Equals(ImageFormat.Bmp) ? "image/bmp" :
            throw new ArgumentException("Only JPEG, PNG, GIF, and BMP images are supported.");
        return new PropertyPhoto(Path.GetFileName(fileName), type, data);
    }
}
