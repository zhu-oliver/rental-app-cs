using Rental.Business;
using Rental.Data;

namespace Rental.Tests;

public sealed class RentalServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rental-tests-" + Guid.NewGuid());
    private readonly string path;
    private readonly SqliteRentalStore store;
    private readonly RentalService service;
    private static readonly DateOnly June = new(2026, 6, 1);

    public RentalServiceTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "test.db");
        store = new SqliteRentalStore(path);
        service = new RentalService(store);
        service.Save(new Property(0, "House", "123 Main"));
        service.Save(new Unit(0, store.Properties().Single().Id, "A"));
        service.Save(new Renter(0, "Alex", "alex@example.test"));
        service.Save(new Lease(0, store.Units().Single().Id, store.Renters().Single().Id, June, null, 123456, 50000));
    }

    [Fact]
    public void Monthly_rent_is_idempotent_and_preserves_charge_amount()
    {
        Assert.Equal(1, service.GenerateRent(June, June.AddDays(4)));
        Assert.Equal(0, service.GenerateRent(June, June.AddDays(4)));
        var lease = store.Leases().Single();
        service.Save(lease with { MonthlyRentCents = 200000 });
        Assert.Equal(123456, store.Charges().Single().AmountCents);
        Assert.Equal(1, service.GenerateRent(June.AddMonths(1), June.AddMonths(1)));
        Assert.Equal(200000, store.Charges().Last().AmountCents);
        Assert.Equal(50000, new SqliteRentalStore(path).Leases().Single().DepositCents);
    }

    [Fact]
    public void Assignments_must_not_overlap_for_the_same_renter()
    {
        var lease = store.Leases().Single();
        Assert.Throws<ArgumentException>(() => service.Save(lease with { Id = 0, Start = June.AddMonths(1) }));
        Assert.Throws<ArgumentException>(() => service.Save(lease with { End = June.AddDays(-1) }));
    }

    [Fact]
    public void Bill_allocations_total_exactly_the_bill_and_reject_invalid_splits()
    {
        service.Save(new Renter(0, "Blair", "blair@example.test"));
        service.Save(new Renter(0, "Casey", "casey@example.test"));
        foreach (var renter in store.Renters().Where(r => r.Name != "Alex"))
            service.Save(new Lease(0, store.Units().Single().Id, renter.Id, June, null, 100, 0));
        var ids = store.Leases().Select(l => l.Id).ToArray();
        var property = store.Properties().Single().Id;
        Assert.Throws<ArgumentException>(() => service.CreateBill(property, "Water", June, 100, [new(ids[0], 99)]));
        Assert.Throws<ArgumentException>(() => service.CreateBill(property, "Water", June, 100, [new(ids[0], 50), new(ids[0], 50)]));
        Assert.Empty(store.Bills());
        service.CreateBill(property, "Water", June, 100, [new(ids[0], 33.33m), new(ids[1], 33.33m), new(ids[2], 33.34m)]);
        var charges = new SqliteRentalStore(path).Charges();
        Assert.Equal(100, charges.Sum(x => x.AmountCents));
        Assert.Equal(new long[] { 33, 33, 34 }, charges.OrderBy(x => x.LeaseId).Select(x => x.AmountCents));
        Assert.All(charges, c => Assert.Equal(store.Bills().Single().Id, c.BillId));
        Assert.Equal(100m, charges.Sum(c => c.Percentage));
    }

    [Fact]
    public void Bills_reject_inactive_or_wrong_property_assignments()
    {
        var lease = store.Leases().Single();
        Assert.Throws<ArgumentException>(() => service.CreateBill(store.Properties().Single().Id, "Water", June.AddDays(-1), 100, [new(lease.Id, 100)]));
        service.Save(new Property(0, "Other", "456 Main"));
        Assert.Throws<ArgumentException>(() => service.CreateBill(store.Properties().Single(p => p.Name == "Other").Id, "Water", June, 100, [new(lease.Id, 100)]));
    }

    [Fact]
    public void Report_uses_payment_dates_separates_reimbursements_and_exports_safely()
    {
        var property = store.Properties().Single().Id;
        service.GenerateRent(June, June);
        service.SetPayment(store.Charges().Single().Id, June.AddDays(5));
        service.CreateBill(property, "Water", June, 1200, [new(store.Leases().Single().Id, 100)]);
        service.SetPayment(store.Charges().Single(c => c.Kind == "Bill").Id, June.AddDays(6));
        service.Save(new Expense(0, property, "Maintenance", "=repair, fixture", June.AddDays(7), 10000));
        service.Save(new Expense(0, property, "Property taxes", "Tax", June.AddMonths(1), 50000));
        var report = service.GetReport(property, June, June.AddDays(29));
        Assert.Equal(123456, report.RentCents);
        Assert.Equal(1200, report.ReimbursementsCents);
        Assert.Equal(10000, report.ExpenseCents);
        Assert.Equal(113456, report.NetCents);
        Assert.Equal(10000, report.ExpensesByCategory["maintenance"]);
        Assert.Contains("\"'=repair, fixture\"", RentalService.ReportCsv(report));
        Assert.Contains("\"Summary\",\"Rent received\",,1234.56", RentalService.ReportCsv(report));
        Assert.Equal(3, report.Rows.Count);
        service.SetPayment(store.Charges().Single(c => c.Kind == "Rent").Id, null);
        Assert.Equal(0, service.GetReport(property, June, June.AddDays(29)).RentCents);
        Assert.Throws<ArgumentException>(() => service.GetReport(property, June.AddDays(1), June));
    }

    [Fact]
    public void Receipt_bytes_survive_database_reopen_and_expense_edit()
    {
        int id = service.Save(new Expense(0, store.Properties().Single().Id, "Maintenance", "Repair", June, 2500));
        byte[] image = [1, 2, 3];
        service.AddReceipt(id, "receipt.png", "image/png", image);
        service.Save(store.Expenses().Single() with { Description = "Updated" });
        var receipt = new SqliteRentalStore(path).Receipts(id).Single();
        Assert.Equal(image, receipt.Data);
        Assert.Equal("receipt.png", receipt.FileName);
        Assert.Throws<ArgumentException>(() => service.AddReceipt(id, "bad.txt", "text/plain", image));
    }

    [Fact]
    public async Task Emails_are_sent_only_for_eligible_charges_and_failures_propagate()
    {
        service.GenerateRent(June, June);
        var charge = store.Charges().Single();
        var sender = new FakeSender();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendNoticeAsync(charge.Id, "Late", sender, June));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendNoticeAsync(charge.Id, "Receipt", sender, June));
        await service.SendNoticeAsync(charge.Id, "Late", sender, June.AddDays(1));
        Assert.Equal("alex@example.test", sender.To);
        service.SetPayment(charge.Id, June.AddDays(2));
        await service.SendNoticeAsync(charge.Id, "Receipt", sender, June.AddDays(2));
        Assert.Contains("received", sender.Body);
        sender.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendNoticeAsync(charge.Id, "Receipt", sender, June.AddDays(2)));
    }

    [Fact]
    public async Task Bill_notices_are_available_only_for_unpaid_bill_charges()
    {
        service.CreateBill(store.Properties().Single().Id, "Electric", June, 100, [new(store.Leases().Single().Id, 100)]);
        var bill = store.Charges().Single();
        var sender = new FakeSender();
        await service.SendNoticeAsync(bill.Id, "Bill", sender, June);
        Assert.Contains("Electric", sender.Body);
        service.SetPayment(bill.Id, June);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendNoticeAsync(bill.Id, "Bill", sender, June));
    }

    [Fact]
    public void Rounding_remainder_is_deterministic_for_equal_fractions()
    {
        service.Save(new Renter(0, "Blair", "blair@example.test"));
        service.Save(new Lease(0, store.Units().Single().Id, store.Renters().Single(r => r.Name == "Blair").Id, June, null, 0, 0));
        var ids = store.Leases().Select(l => l.Id).Order().ToArray();
        service.CreateBill(store.Properties().Single().Id, "Water", June, 1, [new(ids[1], 50), new(ids[0], 50)]);
        Assert.Equal(new long[] { 1, 0 }, store.Charges().OrderBy(c => c.LeaseId).Select(c => c.AmountCents));
    }

    private sealed class FakeSender : IEmailSender
    {
        public string To { get; private set; } = "";
        public string Body { get; private set; } = "";
        public bool Fail { get; set; }
        public Task SendAsync(string recipient, string subject, string message, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Send failed.");
            To = recipient; Body = message;
            return Task.CompletedTask;
        }
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
