namespace Rental.Business;

public record Property(int Id, string Name, string Address, string AddressLine2 = "", string City = "",
    string Region = "", string PostalCode = "", string Country = "", long? PurchasePriceCents = null);
public record Unit(int Id, int PropertyId, string Name);
public record Renter(int Id, string Name, string Email, string Phone = "", string SecondaryContactName = "",
    string SecondaryContactPhone = "", string SecondaryContactEmail = "", string SecondaryContactRelationship = "");
public record PropertyPhoto(string FileName, string ContentType, byte[] Data);
public record Maintenance(int Id, int PropertyId, int? UnitId, string Description, string Status, string Contractor, int? ExpenseId);
public record Document(int Id, int? PropertyId, int? RenterId, string FileName, byte[] Data);
public record EmailDraft(int ChargeId, string Type, string Recipient, string Subject, string Message);
public record EmailAttempt(int Id, int ChargeId, string Type, string Recipient, string Subject, string Message, string CreatedUtc, string Status, string Error);
public record Lease(int Id, int UnitId, int RenterId, DateOnly Start, DateOnly? End, long MonthlyRentCents, long DepositCents);
public record Charge(int Id, int LeaseId, int PropertyId, string Kind, string Description, DateOnly DueDate,
    long AmountCents, DateOnly? PaidDate, string? RentMonth, int? BillId, decimal? Percentage);
public record Bill(int Id, int PropertyId, string Description, DateOnly DueDate, long AmountCents);
public record Payment(int Id, int ChargeId, DateOnly Date, long AmountCents);
public record Expense(int Id, int PropertyId, string Category, string Description, DateOnly PaidDate, long AmountCents);
public record Receipt(int Id, int ExpenseId, string FileName, string ContentType, byte[] Data);
public record Allocation(int LeaseId, decimal Percentage);
public record ReportRow(string Property, string Type, string Description, DateOnly Date, long AmountCents, int PropertyId = 0);
public record PropertySummary(int PropertyId, string Property, long RentCents, long ReimbursementsCents, long ExpenseCents);
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
    IReadOnlyList<Payment> Payments();
    IReadOnlyList<Bill> Bills();
    IReadOnlyList<Expense> Expenses();
    IReadOnlyList<Receipt> Receipts(int expenseId);
    PropertyPhoto? Photo(int propertyId);
    IReadOnlyList<Maintenance> MaintenanceItems(int propertyId);
    IReadOnlyList<Document> Documents(int? propertyId, int? renterId);
    IReadOnlyList<EmailAttempt> EmailAttempts();
    void SaveProperty(Property value);
    void SaveUnit(Unit value);
    void SaveRenter(Renter value);
    void SaveLease(Lease value);
    void AddRentCharges(IReadOnlyList<Charge> charges);
    void AddBill(Bill bill, IReadOnlyList<Charge> charges);
    void SetPaidDate(int chargeId, DateOnly? paidDate);
    void AddPayment(int chargeId, DateOnly date, long amountCents);
    int SaveExpense(Expense expense);
    void AddReceipt(Receipt receipt);
    void SetPhoto(int propertyId, PropertyPhoto? photo);
    void SaveMaintenance(Maintenance item);
    void AddDocument(Document document);
    int StartEmailAttempt(EmailDraft draft);
    void FinishEmailAttempt(int id, string status, string error);
    void BackupTo(string destination);
}

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, string message, CancellationToken cancellationToken = default);
}
