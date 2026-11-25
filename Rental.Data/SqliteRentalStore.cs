using System.Globalization;
using Microsoft.Data.Sqlite;
using Rental.Business;

namespace Rental.Data;

public sealed class SqliteRentalStore : IRentalStore
{
    private readonly string connectionString;

    public SqliteRentalStore(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var c = Open();
        using var version = c.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        long schemaVersion = (long)version.ExecuteScalar()!;
        if (schemaVersion > 1) throw new InvalidOperationException("This database was created by a newer version of the application.");
        if (schemaVersion == 0)
        {
            using var tx = c.BeginTransaction();
            Execute(c, tx, """
                CREATE TABLE properties(id INTEGER PRIMARY KEY, name TEXT NOT NULL, address TEXT NOT NULL);
                CREATE TABLE units(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), name TEXT NOT NULL);
                CREATE TABLE renters(id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL);
                CREATE TABLE leases(id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id), renter_id INTEGER NOT NULL REFERENCES renters(id),
                    start_date TEXT NOT NULL, end_date TEXT, rent_cents INTEGER NOT NULL CHECK(rent_cents>=0), deposit_cents INTEGER NOT NULL CHECK(deposit_cents>=0));
                CREATE TABLE bills(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), description TEXT NOT NULL, due_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>0));
                CREATE TABLE charges(id INTEGER PRIMARY KEY, lease_id INTEGER NOT NULL REFERENCES leases(id), property_id INTEGER NOT NULL REFERENCES properties(id),
                    kind TEXT NOT NULL CHECK(kind IN ('Rent','Bill')), description TEXT NOT NULL, due_date TEXT NOT NULL,
                    amount_cents INTEGER NOT NULL CHECK(amount_cents>=0), paid_date TEXT, rent_month TEXT, bill_id INTEGER REFERENCES bills(id), percentage TEXT,
                    UNIQUE(lease_id, rent_month));
                CREATE TABLE expenses(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), category TEXT NOT NULL, description TEXT NOT NULL,
                    paid_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>0));
                CREATE TABLE receipts(id INTEGER PRIMARY KEY, expense_id INTEGER NOT NULL REFERENCES expenses(id), file_name TEXT NOT NULL, content_type TEXT NOT NULL, data BLOB NOT NULL);
                PRAGMA user_version=1;
                """);
            tx.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Parse(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? NullableDate(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Parse(r.GetString(i));
    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static int? NullableInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        cmd.ExecuteNonQuery();
    }
    private void Save(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        Execute(c, null, sql, args);
    }
    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, null, sql, args);
        using var r = cmd.ExecuteReader();
        var result = new List<T>();
        while (r.Read()) result.Add(map(r));
        return result;
    }

    public IReadOnlyList<Property> Properties() => Query("SELECT id,name,address FROM properties ORDER BY name,id", r => new Property(r.GetInt32(0), r.GetString(1), r.GetString(2)));
    public IReadOnlyList<Unit> Units() => Query("SELECT id,property_id,name FROM units ORDER BY name,id", r => new Unit(r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
    public IReadOnlyList<Renter> Renters() => Query("SELECT id,name,email FROM renters ORDER BY name,id", r => new Renter(r.GetInt32(0), r.GetString(1), r.GetString(2)));
    public IReadOnlyList<Lease> Leases() => Query("SELECT id,unit_id,renter_id,start_date,end_date,rent_cents,deposit_cents FROM leases ORDER BY id", r =>
        new Lease(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), Parse(r.GetString(3)), NullableDate(r, 4), r.GetInt64(5), r.GetInt64(6)));
    public IReadOnlyList<Charge> Charges() => Query("SELECT id,lease_id,property_id,kind,description,due_date,amount_cents,paid_date,rent_month,bill_id,percentage FROM charges ORDER BY due_date,id", r =>
        new Charge(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4), Parse(r.GetString(5)), r.GetInt64(6),
            NullableDate(r, 7), NullableString(r, 8), NullableInt(r, 9), r.IsDBNull(10) ? null : decimal.Parse(r.GetString(10), CultureInfo.InvariantCulture)));
    public IReadOnlyList<Bill> Bills() => Query("SELECT id,property_id,description,due_date,amount_cents FROM bills ORDER BY id", r =>
        new Bill(r.GetInt32(0), r.GetInt32(1), r.GetString(2), Parse(r.GetString(3)), r.GetInt64(4)));
    public IReadOnlyList<Expense> Expenses() => Query("SELECT id,property_id,category,description,paid_date,amount_cents FROM expenses ORDER BY paid_date,id", r =>
        new Expense(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), r.GetInt64(5)));
    public IReadOnlyList<Receipt> Receipts(int expenseId) => Query("SELECT id,expense_id,file_name,content_type,data FROM receipts WHERE expense_id=$expense ORDER BY id", r =>
        new Receipt(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), (byte[])r.GetValue(4)), ("$expense", expenseId));

    public void SaveProperty(Property p) => Save(p.Id == 0 ? "INSERT INTO properties(name,address) VALUES($name,$address)" : "UPDATE properties SET name=$name,address=$address WHERE id=$id",
        ("$id", p.Id), ("$name", p.Name), ("$address", p.Address));
    public void SaveUnit(Unit u) => Save(u.Id == 0 ? "INSERT INTO units(property_id,name) VALUES($property,$name)" : "UPDATE units SET property_id=$property,name=$name WHERE id=$id",
        ("$id", u.Id), ("$property", u.PropertyId), ("$name", u.Name));
    public void SaveRenter(Renter r) => Save(r.Id == 0 ? "INSERT INTO renters(name,email) VALUES($name,$email)" : "UPDATE renters SET name=$name,email=$email WHERE id=$id",
        ("$id", r.Id), ("$name", r.Name), ("$email", r.Email));
    public void SaveLease(Lease l) => Save(l.Id == 0
        ? "INSERT INTO leases(unit_id,renter_id,start_date,end_date,rent_cents,deposit_cents) VALUES($unit,$renter,$start,$end,$rent,$deposit)"
        : "UPDATE leases SET unit_id=$unit,renter_id=$renter,start_date=$start,end_date=$end,rent_cents=$rent,deposit_cents=$deposit WHERE id=$id",
        ("$id", l.Id), ("$unit", l.UnitId), ("$renter", l.RenterId), ("$start", Date(l.Start)), ("$end", l.End is null ? null : Date(l.End.Value)), ("$rent", l.MonthlyRentCents), ("$deposit", l.DepositCents));

    private static void InsertCharge(SqliteConnection c, SqliteTransaction tx, Charge charge, int? billId)
    {
        Execute(c, tx, "INSERT INTO charges(lease_id,property_id,kind,description,due_date,amount_cents,paid_date,rent_month,bill_id,percentage) VALUES($lease,$property,$kind,$description,$due,$amount,$paid,$month,$bill,$percentage)",
            ("$lease", charge.LeaseId), ("$property", charge.PropertyId), ("$kind", charge.Kind), ("$description", charge.Description),
            ("$due", Date(charge.DueDate)), ("$amount", charge.AmountCents), ("$paid", charge.PaidDate is null ? null : Date(charge.PaidDate.Value)),
            ("$month", charge.RentMonth), ("$bill", billId), ("$percentage", charge.Percentage?.ToString(CultureInfo.InvariantCulture)));
    }
    public void AddRentCharges(IReadOnlyList<Charge> charges)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var charge in charges) InsertCharge(c, tx, charge, null);
        tx.Commit();
    }
    public void AddBill(Bill bill, IReadOnlyList<Charge> charges)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "INSERT INTO bills(property_id,description,due_date,amount_cents) VALUES($property,$description,$due,$amount); SELECT last_insert_rowid();",
            ("$property", bill.PropertyId), ("$description", bill.Description), ("$due", Date(bill.DueDate)), ("$amount", bill.AmountCents));
        var id = checked((int)(long)cmd.ExecuteScalar()!);
        foreach (var charge in charges) InsertCharge(c, tx, charge, id);
        tx.Commit();
    }
    public void SetPaidDate(int chargeId, DateOnly? date) => Save("UPDATE charges SET paid_date=$date WHERE id=$id", ("$id", chargeId), ("$date", date is null ? null : Date(date.Value)));
    public int SaveExpense(Expense e)
    {
        using var c = Open();
        using var cmd = Command(c, null, e.Id == 0
            ? "INSERT INTO expenses(property_id,category,description,paid_date,amount_cents) VALUES($property,$category,$description,$date,$amount); SELECT last_insert_rowid();"
            : "UPDATE expenses SET property_id=$property,category=$category,description=$description,paid_date=$date,amount_cents=$amount WHERE id=$id; SELECT $id;",
            ("$id", e.Id), ("$property", e.PropertyId), ("$category", e.Category), ("$description", e.Description), ("$date", Date(e.PaidDate)), ("$amount", e.AmountCents));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    public void AddReceipt(Receipt receipt) => Save("INSERT INTO receipts(expense_id,file_name,content_type,data) VALUES($expense,$name,$type,$data)",
        ("$expense", receipt.ExpenseId), ("$name", receipt.FileName), ("$type", receipt.ContentType), ("$data", receipt.Data));
}
