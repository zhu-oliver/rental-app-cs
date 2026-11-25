namespace Rental.Business;

public record Property(int Id, string Name, string Address);
public record Unit(int Id, int PropertyId, string Name);
public record Renter(int Id, string Name, string Email);
public record Lease(int Id, int UnitId, int RenterId, DateOnly Start, DateOnly? End, long MonthlyRentCents, long DepositCents);
public record Charge(int Id, int LeaseId, int PropertyId, string Kind, string Description, DateOnly DueDate,
    long AmountCents, DateOnly? PaidDate, string? RentMonth, int? BillId, decimal? Percentage);
public record Bill(int Id, int PropertyId, string Description, DateOnly DueDate, long AmountCents);
public record Expense(int Id, int PropertyId, string Category, string Description, DateOnly PaidDate, long AmountCents);
public record Receipt(int Id, int ExpenseId, string FileName, string ContentType, byte[] Data);
public record Allocation(int LeaseId, decimal Percentage);
public record ReportRow(string Property, string Type, string Description, DateOnly Date, long AmountCents);
public record Report(IReadOnlyList<ReportRow> Rows, long RentCents, long ReimbursementsCents,
    long ExpenseCents, IReadOnlyDictionary<string, long> ExpensesByCategory)
{
    public long NetCents => RentCents - ExpenseCents;
}

public interface IRentalStore
{
    IReadOnlyList<Property> Properties();
    IReadOnlyList<Unit> Units();
    IReadOnlyList<Renter> Renters();
    IReadOnlyList<Lease> Leases();
    IReadOnlyList<Charge> Charges();
    IReadOnlyList<Bill> Bills();
    IReadOnlyList<Expense> Expenses();
    IReadOnlyList<Receipt> Receipts(int expenseId);
    void SaveProperty(Property value);
    void SaveUnit(Unit value);
    void SaveRenter(Renter value);
    void SaveLease(Lease value);
    void AddRentCharges(IReadOnlyList<Charge> charges);
    void AddBill(Bill bill, IReadOnlyList<Charge> charges);
    void SetPaidDate(int chargeId, DateOnly? paidDate);
    int SaveExpense(Expense expense);
    void AddReceipt(Receipt receipt);
}

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string message, CancellationToken cancellationToken = default);
}
