using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace Rental.Business;

public sealed class RentalService(IRentalStore store)
{
    public IRentalStore Store { get; } = store;

    static void Required(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
    }

    static void Exists(bool exists, string name)
    {
        if (!exists) throw new ArgumentException($"{name} was not found.");
    }

    public static long Cents(decimal amount)
    {
        if (amount < 0 || decimal.Round(amount, 2) != amount) throw new ArgumentException("Amount must be non-negative with at most two decimal places.");
        return checked((long)(amount * 100));
    }

    public void Save(Property p)
    {
        Required(p.Name, "Property name"); Required(p.Address, "Address");
        Store.SaveProperty(p with { Name = p.Name.Trim(), Address = p.Address.Trim() });
    }

    public void Save(Unit u)
    {
        Required(u.Name, "Unit name");
        Exists(Store.Properties().Any(p => p.Id == u.PropertyId), "Property");
        if (u.Id != 0 && Store.Units().Single(x => x.Id == u.Id).PropertyId != u.PropertyId &&
            Store.Leases().Any(l => l.UnitId == u.Id))
            throw new ArgumentException("A unit with assignments cannot be moved to another property.");
        Store.SaveUnit(u with { Name = u.Name.Trim() });
    }

    public void Save(Renter r)
    {
        Required(r.Name, "Renter name"); Required(r.Email, "Email");
        if (!MailAddress.TryCreate(r.Email.Trim(), out _)) throw new ArgumentException("Enter a valid email address.");
        Store.SaveRenter(r with { Name = r.Name.Trim(), Email = r.Email.Trim() });
    }

    public void Save(Lease l)
    {
        Exists(Store.Units().Any(u => u.Id == l.UnitId), "Unit");
        Exists(Store.Renters().Any(r => r.Id == l.RenterId), "Renter");
        if (l.End < l.Start) throw new ArgumentException("End date must not precede the start date.");
        if (l.MonthlyRentCents < 0 || l.DepositCents < 0) throw new ArgumentException("Rent and deposit cannot be negative.");
        if (Store.Leases().Any(x => x.Id != l.Id && x.RenterId == l.RenterId &&
            x.Start <= (l.End ?? DateOnly.MaxValue) && l.Start <= (x.End ?? DateOnly.MaxValue)))
            throw new ArgumentException("This renter already has an overlapping assignment.");
        if (l.Id != 0 && Store.Charges().Any(c => c.LeaseId == l.Id))
        {
            var old = Store.Leases().Single(x => x.Id == l.Id);
            if (old.UnitId != l.UnitId || old.RenterId != l.RenterId)
                throw new ArgumentException("A charged assignment cannot be moved to another renter or unit.");
        }
        Store.SaveLease(l);
    }

    public int GenerateRent(DateOnly month, DateOnly dueDate)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        if (dueDate < first || dueDate > last) throw new ArgumentException("Rent due date must be in the selected month.");
        var key = first.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var units = Store.Units().ToDictionary(u => u.Id);
        var existing = Store.Charges().Where(c => c.Kind == "Rent" && c.RentMonth == key).Select(c => c.LeaseId).ToHashSet();
        var charges = Store.Leases().Where(l => l.Start <= last && (l.End is null || l.End >= first) && !existing.Contains(l.Id))
            .Select(l => new Charge(0, l.Id, units[l.UnitId].PropertyId, "Rent", $"Rent {key}", dueDate, l.MonthlyRentCents, null, key, null, null)).ToList();
        Store.AddRentCharges(charges);
        return charges.Count;
    }

    public void CreateBill(int propertyId, string description, DateOnly dueDate, long amountCents, IReadOnlyList<Allocation> allocations)
    {
        Exists(Store.Properties().Any(p => p.Id == propertyId), "Property");
        Required(description, "Bill description");
        if (amountCents <= 0) throw new ArgumentException("Bill amount must be greater than zero.");
        if (allocations.Count == 0 || allocations.Any(a => a.Percentage <= 0 || decimal.Round(a.Percentage, 2) != a.Percentage) ||
            allocations.Sum(a => a.Percentage) != 100m || allocations.Select(a => a.LeaseId).Distinct().Count() != allocations.Count)
            throw new ArgumentException("Enter unique renters with positive percentages (up to two decimals) totaling 100%.");
        var units = Store.Units().ToDictionary(u => u.Id);
        var leases = Store.Leases().ToDictionary(l => l.Id);
        foreach (var a in allocations)
            if (!leases.TryGetValue(a.LeaseId, out var l) || units[l.UnitId].PropertyId != propertyId ||
                l.Start > dueDate || (l.End is not null && l.End < dueDate))
                throw new ArgumentException("Each allocated renter must have an active assignment at this property on the bill due date.");
        var shares = allocations.Select(a => new
        {
            Allocation = a,
            Exact = amountCents * a.Percentage / 100m,
            Whole = (long)decimal.Floor(amountCents * a.Percentage / 100m)
        }).ToList();
        long remaining = amountCents - shares.Sum(s => s.Whole);
        var extras = shares.OrderByDescending(s => s.Exact - s.Whole).ThenBy(s => s.Allocation.LeaseId)
            .Take(checked((int)remaining)).Select(s => s.Allocation.LeaseId).ToHashSet();
        var charges = shares.Select(s => new Charge(0, s.Allocation.LeaseId, propertyId, "Bill", description.Trim(), dueDate,
            s.Whole + (extras.Contains(s.Allocation.LeaseId) ? 1 : 0), null, null, null, s.Allocation.Percentage)).ToList();
        Store.AddBill(new Bill(0, propertyId, description.Trim(), dueDate, amountCents), charges);
    }

    public void SetPayment(int chargeId, DateOnly? date)
    {
        Exists(Store.Charges().Any(c => c.Id == chargeId), "Charge");
        Store.SetPaidDate(chargeId, date);
    }

    public int Save(Expense expense)
    {
        Exists(Store.Properties().Any(p => p.Id == expense.PropertyId), "Property");
        Required(expense.Category, "Category"); Required(expense.Description, "Description");
        if (expense.AmountCents <= 0) throw new ArgumentException("Expense amount must be greater than zero.");
        return Store.SaveExpense(expense with { Category = expense.Category.Trim(), Description = expense.Description.Trim() });
    }

    public void AddReceipt(int expenseId, string fileName, string contentType, byte[] data)
    {
        Exists(Store.Expenses().Any(e => e.Id == expenseId), "Expense");
        Required(fileName, "Filename");
        if (!new[] { "image/jpeg", "image/png", "image/gif", "image/bmp" }.Contains(contentType) || data.Length == 0)
            throw new ArgumentException("Receipt must be a non-empty JPEG, PNG, GIF, or BMP image.");
        Store.AddReceipt(new Receipt(0, expenseId, fileName, contentType, data));
    }

    public Report GetReport(int? propertyId, DateOnly from, DateOnly to)
    {
        if (from > to) throw new ArgumentException("Start date must not be after end date.");
        if (propertyId is not null) Exists(Store.Properties().Any(p => p.Id == propertyId), "Property");
        var properties = Store.Properties().ToDictionary(p => p.Id);
        var charges = Store.Charges().Where(c => c.PaidDate >= from && c.PaidDate <= to && (propertyId is null || c.PropertyId == propertyId)).ToList();
        var expenses = Store.Expenses().Where(e => e.PaidDate >= from && e.PaidDate <= to && (propertyId is null || e.PropertyId == propertyId)).ToList();
        var rows = charges.Select(c => new ReportRow(properties[c.PropertyId].Name, c.Kind == "Rent" ? "Rent received" : "Bill reimbursement", c.Description, c.PaidDate!.Value, c.AmountCents))
            .Concat(expenses.Select(e => new ReportRow(properties[e.PropertyId].Name, $"Expense: {e.Category}", e.Description, e.PaidDate, -e.AmountCents)))
            .OrderBy(r => r.Date).ThenBy(r => r.Property).ToList();
        return new Report(rows, charges.Where(c => c.Kind == "Rent").Sum(c => c.AmountCents),
            charges.Where(c => c.Kind == "Bill").Sum(c => c.AmountCents), expenses.Sum(e => e.AmountCents),
            expenses.GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(e => e.AmountCents), StringComparer.OrdinalIgnoreCase));
    }

    public static string ReportCsv(Report report)
    {
        static string Escape(string value)
        {
            string trimmed = value.TrimStart(' ', '\t', '\r', '\n');
            if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) || value.StartsWith('\t') || value.StartsWith('\r'))
                value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        static string Amount(long cents) => (cents / 100m).ToString("F2", CultureInfo.InvariantCulture);
        var result = new StringBuilder("Property,Type,Description,Payment date,Amount\r\n");
        void Summary(string description, long amount) =>
            result.AppendJoin(",", Escape(""), Escape("Summary"), Escape(description), "", Amount(amount)).Append("\r\n");
        Summary("Rent received", report.RentCents);
        Summary("Bill reimbursements", report.ReimbursementsCents);
        Summary("Expenses", report.ExpenseCents);
        Summary("Rent less expenses (not a tax calculation)", report.NetCents);
        foreach (var category in report.ExpensesByCategory.OrderBy(x => x.Key))
            Summary($"Expense: {category.Key}", category.Value);
        foreach (var r in report.Rows)
            result.AppendJoin(",", Escape(r.Property), Escape(r.Type), Escape(r.Description),
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Amount(r.AmountCents)).Append("\r\n");
        return result.ToString();
    }

    public async Task SendNoticeAsync(int chargeId, string type, IEmailSender sender, DateOnly today)
    {
        var charge = Store.Charges().SingleOrDefault(c => c.Id == chargeId) ?? throw new ArgumentException("Charge not found.");
        if (type == "Bill" && (charge.Kind != "Bill" || charge.PaidDate is not null)) throw new ArgumentException("Bill notices require an unpaid bill charge.");
        if (type == "Late" && (charge.PaidDate is not null || charge.DueDate >= today)) throw new ArgumentException("Late notices require an overdue unpaid charge.");
        if (type == "Receipt" && charge.PaidDate is null) throw new ArgumentException("A receipt requires a paid charge.");
        if (type is not ("Bill" or "Late" or "Receipt")) throw new ArgumentException("Unknown notice type.");
        var lease = Store.Leases().Single(l => l.Id == charge.LeaseId);
        var renter = Store.Renters().Single(r => r.Id == lease.RenterId);
        var property = Store.Properties().Single(p => p.Id == charge.PropertyId);
        var amount = (charge.AmountCents / 100m).ToString("C", CultureInfo.CurrentCulture);
        string subject = type switch { "Bill" => "Your property bill", "Late" => "Payment overdue", _ => "Payment receipt" };
        string message = type switch
        {
            "Bill" => $"Hello {renter.Name},\n\nYour share of {charge.Description} for {property.Name} is {amount}, due {charge.DueDate:d}.",
            "Late" => $"Hello {renter.Name},\n\nYour payment of {amount} for {charge.Description} at {property.Name} was due {charge.DueDate:d} and is overdue.",
            _ => $"Hello {renter.Name},\n\nWe received your payment of {amount} for {charge.Description} at {property.Name} on {charge.PaidDate:d}. Thank you."
        };
        await sender.SendAsync(renter.Email, subject, message);
    }
}
