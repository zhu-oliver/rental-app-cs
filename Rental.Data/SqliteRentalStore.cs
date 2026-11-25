using System.Globalization;
using Microsoft.Data.Sqlite;
using Rental.Business;

namespace Rental.Data;

public sealed class SqliteRentalStore : IRentalStore
{
    private readonly string connectionString;
    private readonly string databasePath;

    public SqliteRentalStore(string path)
    {
        databasePath = Path.GetFullPath(path);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        using var c = Open();
        using var version = c.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        long schemaVersion = (long)version.ExecuteScalar()!;
        if (schemaVersion > 5) throw new InvalidOperationException("This database was created by a newer version of the application.");
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
        if (schemaVersion <= 1)
        {
            using var tx = c.BeginTransaction();
            Execute(c, tx, """
                ALTER TABLE properties ADD COLUMN address_line2 TEXT NOT NULL DEFAULT '';
                ALTER TABLE properties ADD COLUMN city TEXT NOT NULL DEFAULT '';
                ALTER TABLE properties ADD COLUMN region TEXT NOT NULL DEFAULT '';
                ALTER TABLE properties ADD COLUMN postal_code TEXT NOT NULL DEFAULT '';
                ALTER TABLE properties ADD COLUMN country TEXT NOT NULL DEFAULT '';
                ALTER TABLE properties ADD COLUMN purchase_price_cents INTEGER CHECK(purchase_price_cents IS NULL OR purchase_price_cents>=0);
                ALTER TABLE renters ADD COLUMN phone TEXT NOT NULL DEFAULT '';
                ALTER TABLE renters ADD COLUMN secondary_name TEXT NOT NULL DEFAULT '';
                ALTER TABLE renters ADD COLUMN secondary_phone TEXT NOT NULL DEFAULT '';
                ALTER TABLE renters ADD COLUMN secondary_email TEXT NOT NULL DEFAULT '';
                ALTER TABLE renters ADD COLUMN secondary_relationship TEXT NOT NULL DEFAULT '';
                CREATE TABLE property_photos(property_id INTEGER PRIMARY KEY REFERENCES properties(id), file_name TEXT NOT NULL, content_type TEXT NOT NULL, data BLOB NOT NULL);
                PRAGMA user_version=2;
                """);
            tx.Commit();
        }
        if (schemaVersion <= 2)
        {
            using var tx = c.BeginTransaction();
            Execute(c, tx, """
                CREATE TABLE payments(id INTEGER PRIMARY KEY, charge_id INTEGER NOT NULL REFERENCES charges(id), payment_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents>0));
                INSERT INTO payments(charge_id,payment_date,amount_cents)
                    SELECT id,paid_date,amount_cents FROM charges WHERE paid_date IS NOT NULL AND amount_cents>0;
                PRAGMA user_version=3;
                """);
            tx.Commit();
        }
        if (schemaVersion <= 3)
        {
            using var tx = c.BeginTransaction();
            Execute(c, tx, """
                CREATE TABLE maintenance(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), unit_id INTEGER REFERENCES units(id),
                    description TEXT NOT NULL, status TEXT NOT NULL, contractor TEXT NOT NULL, expense_id INTEGER REFERENCES expenses(id));
                CREATE TABLE documents(id INTEGER PRIMARY KEY, property_id INTEGER REFERENCES properties(id), renter_id INTEGER REFERENCES renters(id),
                    file_name TEXT NOT NULL, data BLOB NOT NULL, CHECK((property_id IS NULL) != (renter_id IS NULL)));
                PRAGMA user_version=4;
                """);
            tx.Commit();
        }
        if (schemaVersion <= 4)
        {
            using var tx = c.BeginTransaction();
            Execute(c, tx, """
                CREATE TABLE email_attempts(id INTEGER PRIMARY KEY, charge_id INTEGER NOT NULL REFERENCES charges(id), type TEXT NOT NULL,
                    recipient TEXT NOT NULL, subject TEXT NOT NULL, message TEXT NOT NULL, created_utc TEXT NOT NULL, status TEXT NOT NULL, error TEXT NOT NULL);
                PRAGMA user_version=5;
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

    public IReadOnlyList<Property> Properties() => Query("SELECT id,name,address,address_line2,city,region,postal_code,country,purchase_price_cents FROM properties ORDER BY name,id", r => new Property(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8)));
    public IReadOnlyList<Unit> Units() => Query("SELECT id,property_id,name FROM units ORDER BY name,id", r => new Unit(r.GetInt32(0), r.GetInt32(1), r.GetString(2)));
    public IReadOnlyList<Renter> Renters() => Query("SELECT id,name,email,phone,secondary_name,secondary_phone,secondary_email,secondary_relationship FROM renters ORDER BY name,id", r => new Renter(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
    public PropertyPhoto? Photo(int propertyId) => Query("SELECT file_name,content_type,data FROM property_photos WHERE property_id=$id", r =>
        new PropertyPhoto(r.GetString(0), r.GetString(1), (byte[])r.GetValue(2)), ("$id", propertyId)).SingleOrDefault();
    public IReadOnlyList<Maintenance> MaintenanceItems(int propertyId) => Query("SELECT id,property_id,unit_id,description,status,contractor,expense_id FROM maintenance WHERE property_id=$id ORDER BY id DESC", r =>
        new Maintenance(r.GetInt32(0), r.GetInt32(1), NullableInt(r, 2), r.GetString(3), r.GetString(4), r.GetString(5), NullableInt(r, 6)), ("$id", propertyId));
    public IReadOnlyList<Document> Documents(int? propertyId, int? renterId) => Query("SELECT id,property_id,renter_id,file_name,data FROM documents WHERE (property_id=$property OR renter_id=$renter) ORDER BY id DESC", r =>
        new Document(r.GetInt32(0), NullableInt(r, 1), NullableInt(r, 2), r.GetString(3), (byte[])r.GetValue(4)), ("$property", propertyId), ("$renter", renterId));
    public IReadOnlyList<EmailAttempt> EmailAttempts() => Query("SELECT id,charge_id,type,recipient,subject,message,created_utc,status,error FROM email_attempts ORDER BY id DESC", r =>
        new EmailAttempt(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8)));
    public IReadOnlyList<Lease> Leases() => Query("SELECT id,unit_id,renter_id,start_date,end_date,rent_cents,deposit_cents FROM leases ORDER BY id", r =>
        new Lease(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), Parse(r.GetString(3)), NullableDate(r, 4), r.GetInt64(5), r.GetInt64(6)));
    public IReadOnlyList<Charge> Charges() => Query("SELECT id,lease_id,property_id,kind,description,due_date,amount_cents,paid_date,rent_month,bill_id,percentage FROM charges ORDER BY due_date,id", r =>
        new Charge(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4), Parse(r.GetString(5)), r.GetInt64(6),
            NullableDate(r, 7), NullableString(r, 8), NullableInt(r, 9), r.IsDBNull(10) ? null : decimal.Parse(r.GetString(10), CultureInfo.InvariantCulture)));
    public IReadOnlyList<Payment> Payments() => Query("SELECT id,charge_id,payment_date,amount_cents FROM payments ORDER BY payment_date,id", r =>
        new Payment(r.GetInt32(0), r.GetInt32(1), Parse(r.GetString(2)), r.GetInt64(3)));
    public IReadOnlyList<Bill> Bills() => Query("SELECT id,property_id,description,due_date,amount_cents FROM bills ORDER BY id", r =>
        new Bill(r.GetInt32(0), r.GetInt32(1), r.GetString(2), Parse(r.GetString(3)), r.GetInt64(4)));
    public IReadOnlyList<Expense> Expenses() => Query("SELECT id,property_id,category,description,paid_date,amount_cents FROM expenses ORDER BY paid_date,id", r =>
        new Expense(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), r.GetInt64(5)));
    public IReadOnlyList<Receipt> Receipts(int expenseId) => Query("SELECT id,expense_id,file_name,content_type,data FROM receipts WHERE expense_id=$expense ORDER BY id", r =>
        new Receipt(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), (byte[])r.GetValue(4)), ("$expense", expenseId));

    public void SaveProperty(Property p) => Save(p.Id == 0
        ? "INSERT INTO properties(name,address,address_line2,city,region,postal_code,country,purchase_price_cents) VALUES($name,$address,$line2,$city,$region,$postal,$country,$price)"
        : "UPDATE properties SET name=$name,address=$address,address_line2=$line2,city=$city,region=$region,postal_code=$postal,country=$country,purchase_price_cents=$price WHERE id=$id",
        ("$id", p.Id), ("$name", p.Name), ("$address", p.Address), ("$line2", p.AddressLine2), ("$city", p.City), ("$region", p.Region),
        ("$postal", p.PostalCode), ("$country", p.Country), ("$price", p.PurchasePriceCents));
    public void SaveUnit(Unit u) => Save(u.Id == 0 ? "INSERT INTO units(property_id,name) VALUES($property,$name)" : "UPDATE units SET property_id=$property,name=$name WHERE id=$id",
        ("$id", u.Id), ("$property", u.PropertyId), ("$name", u.Name));
    public void SaveRenter(Renter r) => Save(r.Id == 0
        ? "INSERT INTO renters(name,email,phone,secondary_name,secondary_phone,secondary_email,secondary_relationship) VALUES($name,$email,$phone,$secondaryName,$secondaryPhone,$secondaryEmail,$relationship)"
        : "UPDATE renters SET name=$name,email=$email,phone=$phone,secondary_name=$secondaryName,secondary_phone=$secondaryPhone,secondary_email=$secondaryEmail,secondary_relationship=$relationship WHERE id=$id",
        ("$id", r.Id), ("$name", r.Name), ("$email", r.Email), ("$phone", r.Phone), ("$secondaryName", r.SecondaryContactName),
        ("$secondaryPhone", r.SecondaryContactPhone), ("$secondaryEmail", r.SecondaryContactEmail), ("$relationship", r.SecondaryContactRelationship));
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
    public void SetPaidDate(int chargeId, DateOnly? date)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Execute(c, tx, "DELETE FROM payments WHERE charge_id=$id", ("$id", chargeId));
        if (date is not null)
            Execute(c, tx, "INSERT INTO payments(charge_id,payment_date,amount_cents) SELECT id,$date,amount_cents FROM charges WHERE id=$id AND amount_cents>0",
                ("$id", chargeId), ("$date", Date(date.Value)));
        Execute(c, tx, "UPDATE charges SET paid_date=$date WHERE id=$id", ("$id", chargeId), ("$date", date is null ? null : Date(date.Value)));
        tx.Commit();
    }
    public void AddPayment(int chargeId, DateOnly date, long amountCents)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "SELECT amount_cents - COALESCE((SELECT SUM(amount_cents) FROM payments WHERE charge_id=$id),0) FROM charges WHERE id=$id AND paid_date IS NULL", ("$id", chargeId));
        var result = cmd.ExecuteScalar();
        if (result is null || amountCents <= 0 || amountCents > (long)result)
            throw new InvalidOperationException("Payment must be positive and no greater than the outstanding balance.");
        Execute(c, tx, "INSERT INTO payments(charge_id,payment_date,amount_cents) VALUES($id,$date,$amount)",
            ("$id", chargeId), ("$date", Date(date)), ("$amount", amountCents));
        if (amountCents == (long)result)
            Execute(c, tx, "UPDATE charges SET paid_date=$date WHERE id=$id", ("$id", chargeId), ("$date", Date(date)));
        tx.Commit();
    }
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
    public void SetPhoto(int propertyId, PropertyPhoto? photo)
    {
        if (photo is null) Save("DELETE FROM property_photos WHERE property_id=$id", ("$id", propertyId));
        else Save("INSERT INTO property_photos(property_id,file_name,content_type,data) VALUES($id,$name,$type,$data) ON CONFLICT(property_id) DO UPDATE SET file_name=$name,content_type=$type,data=$data",
            ("$id", propertyId), ("$name", photo.FileName), ("$type", photo.ContentType), ("$data", photo.Data));
    }
    public void SaveMaintenance(Maintenance m) => Save(m.Id == 0
        ? "INSERT INTO maintenance(property_id,unit_id,description,status,contractor,expense_id) VALUES($property,$unit,$description,$status,$contractor,$expense)"
        : "UPDATE maintenance SET property_id=$property,unit_id=$unit,description=$description,status=$status,contractor=$contractor,expense_id=$expense WHERE id=$id",
        ("$id", m.Id), ("$property", m.PropertyId), ("$unit", m.UnitId), ("$description", m.Description),
        ("$status", m.Status), ("$contractor", m.Contractor), ("$expense", m.ExpenseId));
    public void AddDocument(Document d) => Save("INSERT INTO documents(property_id,renter_id,file_name,data) VALUES($property,$renter,$name,$data)",
        ("$property", d.PropertyId), ("$renter", d.RenterId), ("$name", d.FileName), ("$data", d.Data));
    public int StartEmailAttempt(EmailDraft draft)
    {
        using var c = Open();
        using var cmd = Command(c, null, "INSERT INTO email_attempts(charge_id,type,recipient,subject,message,created_utc,status,error) VALUES($charge,$type,$recipient,$subject,$message,$created,'Attempting',''); SELECT last_insert_rowid();",
            ("$charge", draft.ChargeId), ("$type", draft.Type), ("$recipient", draft.Recipient), ("$subject", draft.Subject), ("$message", draft.Message),
            ("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        return checked((int)(long)cmd.ExecuteScalar()!);
    }
    public void FinishEmailAttempt(int id, string status, string error) => Save("UPDATE email_attempts SET status=$status,error=$error WHERE id=$id",
        ("$id", id), ("$status", status), ("$error", error));
    public void BackupTo(string destination)
    {
        string target = Path.GetFullPath(destination);
        if (string.Equals(databasePath, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The backup must not overwrite the live database.");
        if (File.Exists(target) || File.Exists(target + "-wal") || File.Exists(target + "-shm"))
            throw new IOException("Choose a new backup filename; existing files are not overwritten.");
        string temp = Path.Combine(Path.GetDirectoryName(target)!, $".rental-backup-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = Open())
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            {
                copy.Open();
                source.BackupDatabase(copy);
            }
            File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
