# Rental Property Manager

A single-user Windows Forms application for managing properties, renter assignments, rent, shared bills, payments, deposits, and property expenses. It runs locally as a three-tier monolith: `Rental.WinForms` provides the UI and SendGrid integration, `Rental.Business` contains business rules and contracts, and `Rental.Data` implements SQLite persistence. `Rental.Tests` exercises business rules and SQLite.

## Run

Install the .NET 10 SDK on Windows, then from this folder:

```powershell
dotnet run --project Rental.WinForms
```

Run tests with `dotnet test RentalApplication.sln`. To build a distributable, use `dotnet publish Rental.WinForms -c Release`; a framework-dependent publish requires the matching .NET Desktop Runtime on the target machine.

## Getting started

1. Add a property, then its units, then renters. Add an assignment for each renter with its start date, optional end date, monthly rent, and deposit. Assignments for the same renter cannot overlap. Multiple renters can occupy the same unit.
2. On **Charges**, generate rent for a month and choose its due date within that month. The app creates one charge per assignment active at any point in that month; it does not prorate partial months. Repeating the operation does not duplicate rent. Changes to assignment rent affect future generated charges, not existing ones.
3. Create a property bill on **Charges**, choose its due date, and enter positive percentages for active renters at that property totaling 100%. The app distributes rounding cents deterministically, and the shares always sum to the bill. Bill details and charge amounts are preserved after creation.
4. Select a charge and mark it paid with its payment date or mark it unpaid to correct a mistake. Payments are all-or-nothing; partial payments, late fees, and automatic reminders are not supported. Deposits are recorded on assignments, but are not treated as rent or tracked as separate deposit transactions.
5. On **Expenses**, add a paid expense, including its property, category (for example `Maintenance` or `Property taxes`), description, date paid, and amount. Select it to import one or more JPEG, PNG, GIF, or BMP receipt images (up to 20 MB each); view or export them from the receipt dialog.
6. On **Reports**, select a property or all properties and an inclusive payment-date range. Run the report and optionally export its summary and detail rows to CSV. The summary separates rent received from bill reimbursements and groups expenses by category; rent less expenses excludes reimbursements and deposits and is not a tax calculation. Use consistent category names to group similar expenses.

## SendGrid

On **Charges**, open **Email settings** and enter a verified SendGrid sender address and API key with mail-send permission. The API key is protected using Windows DPAPI for the current Windows user; it is not stored in the source code. Blank out the key field when editing settings to retain the existing key. Windows user/profile changes can make the key unreadable, in which case configure it again.

Emails are sent only after an explicit button click and confirmation. A bill notice requires an unpaid bill charge; a late notice requires a past-due unpaid charge; a receipt requires a paid charge. SendGrid acceptance is not a guarantee of delivery. The app does not schedule messages or track delivery status. Configure a real sender and API key before sending; no email is sent by the test suite.

## Storage and backups

The database and encrypted email settings are stored under `%LOCALAPPDATA%\RentalApplication`. Receipt image binaries are stored in the SQLite database, so they increase its size and are included in database backups. The database and receipt images themselves are **not encrypted**; protect the Windows account and backups appropriately.

Close the app before copying `rentals.db` to a secure backup location. Restore by closing the app and replacing that file with the backup. The encrypted settings file can be backed up separately, but its API key is tied to the original Windows user. For an isolated instance, set `RENTAL_APPLICATION_DATA` to a writable directory before starting the application.
