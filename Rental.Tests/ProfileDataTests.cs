using Microsoft.Data.Sqlite;
using Rental.Business;
using Rental.Data;

namespace Rental.Tests;

public sealed class ProfileDataTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rental-profile-tests-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "test.db");
    public ProfileDataTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Existing_version_one_data_is_preserved_and_can_be_expanded()
    {
        using (var c = new SqliteConnection($"Data Source={Database}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE properties(id INTEGER PRIMARY KEY, name TEXT NOT NULL, address TEXT NOT NULL);
                CREATE TABLE renters(id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL);
                CREATE TABLE units(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), name TEXT NOT NULL);
                CREATE TABLE leases(id INTEGER PRIMARY KEY, unit_id INTEGER NOT NULL REFERENCES units(id), renter_id INTEGER NOT NULL REFERENCES renters(id),
                    start_date TEXT NOT NULL, end_date TEXT, rent_cents INTEGER NOT NULL, deposit_cents INTEGER NOT NULL);
                CREATE TABLE bills(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), description TEXT NOT NULL, due_date TEXT NOT NULL, amount_cents INTEGER NOT NULL);
                CREATE TABLE charges(id INTEGER PRIMARY KEY, lease_id INTEGER NOT NULL REFERENCES leases(id), property_id INTEGER NOT NULL REFERENCES properties(id),
                    kind TEXT NOT NULL, description TEXT NOT NULL, due_date TEXT NOT NULL, amount_cents INTEGER NOT NULL,
                    paid_date TEXT, rent_month TEXT, bill_id INTEGER REFERENCES bills(id), percentage TEXT);
                CREATE TABLE expenses(id INTEGER PRIMARY KEY, property_id INTEGER NOT NULL REFERENCES properties(id), category TEXT NOT NULL, description TEXT NOT NULL, paid_date TEXT NOT NULL, amount_cents INTEGER NOT NULL);
                CREATE TABLE receipts(id INTEGER PRIMARY KEY, expense_id INTEGER NOT NULL REFERENCES expenses(id), file_name TEXT NOT NULL, content_type TEXT NOT NULL, data BLOB NOT NULL);
                INSERT INTO properties VALUES(7,'Old house','123 Main St, Oldtown');
                INSERT INTO renters VALUES(9,'Renter','renter@example.test');
                INSERT INTO units VALUES(11,7,'Unit A');
                INSERT INTO leases VALUES(12,11,9,'2026-06-01',NULL,12000,5000);
                INSERT INTO charges VALUES(44,12,7,'Rent','Rent 2026-06','2026-06-01',12000,'2026-06-05','2026-06',NULL,NULL);
                INSERT INTO expenses VALUES(20,7,'Maintenance','Repair','2026-06-06',3000);
                INSERT INTO receipts VALUES(21,20,'receipt.png','image/png',X'010203');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new SqliteRentalStore(Database);
        var property = store.Properties().Single();
        Assert.Equal("123 Main St, Oldtown", property.Address);
        Assert.Null(property.PurchasePriceCents);
        Assert.Equal("", property.City);
        var renter = store.Renters().Single();
        Assert.Equal("", renter.Phone);
        Assert.Equal(12000, store.Payments().Single().AmountCents);
        Assert.Equal(new DateOnly(2026, 6, 5), store.Payments().Single().Date);
        Assert.Equal(5000, store.Leases().Single().DepositCents);
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Receipts(20).Single().Data);
        var service = new RentalService(store);
        service.Save(property with { City = "Oldtown", PurchasePriceCents = 25000000 });
        service.Save(renter with { Phone = "555-0101", SecondaryContactName = "Contact", SecondaryContactPhone = "555-0102",
            SecondaryContactEmail = "contact@example.test", SecondaryContactRelationship = "Friend" });
        service.SetPhoto(7, new PropertyPhoto("house.png", "image/png", [1, 2, 3]));
        var reopened = new SqliteRentalStore(Database);
        Assert.Equal(25000000, reopened.Properties().Single().PurchasePriceCents);
        Assert.Equal("123 Main St, Oldtown", reopened.Properties().Single().Address);
        Assert.Equal("555-0101", reopened.Renters().Single().Phone);
        Assert.Equal("contact@example.test", reopened.Renters().Single().SecondaryContactEmail);
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.Photo(7)!.Data);
        service.SetPhoto(7, null);
        Assert.Null(store.Photo(7));
    }

    [Fact]
    public void Invalid_profile_data_is_rejected()
    {
        var store = new SqliteRentalStore(Database);
        var service = new RentalService(store);
        Assert.Throws<ArgumentException>(() => service.Save(new Property(0, "Home", "Address", PurchasePriceCents: -1)));
        Assert.Throws<ArgumentException>(() => service.Save(new Renter(0, "Name", "a@example.test", SecondaryContactEmail: "not-an-email")));
        service.Save(new Property(0, "Home", "Address"));
        Assert.Throws<ArgumentException>(() => service.SetPhoto(store.Properties().Single().Id, new PropertyPhoto("bad.txt", "text/plain", [1])));
    }

    [Fact]
    public void Backup_is_a_readable_snapshot_and_never_overwrites_existing_files()
    {
        var store = new SqliteRentalStore(Database);
        var service = new RentalService(store);
        service.Save(new Property(0, "Home", "Address"));
        string destination = Path.Combine(directory, "backup.db");
        store.BackupTo(destination);
        service.Save(new Property(0, "Later", "Address"));
        Assert.Single(new SqliteRentalStore(destination).Properties());
        Assert.Throws<IOException>(() => store.BackupTo(destination));
        Assert.Throws<ArgumentException>(() => store.BackupTo(Database));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }
}
