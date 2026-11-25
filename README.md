# Rental Property Manager

A single-user Windows Forms application for managing properties, renter assignments, rent, shared bills, payments, deposits, and property expenses. It runs locally as a three-tier monolith: `Rental.WinForms` provides the UI and SendGrid integration, `Rental.Business` contains business rules and contracts, and `Rental.Data` implements SQLite persistence. `Rental.Tests` exercises business rules and SQLite.

The Home screen searches property names and addresses and renter names, emails, and phone numbers. Open a result to see its profile. Property profiles show structured addresses, purchase price, an optional image, units, occupants, bills, and expenses. Renter profiles show contact and secondary-contact details, assignments, deposits, charges, and payment status; use the profile buttons to perform actions without navigating between the main tabs.

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
4. Select a charge and record one or more payments with their dates and amounts, up to the outstanding balance. Clear payments removes **all** payments for that charge after confirmation. A receipt refers to the most recently recorded payment. Late fees and automatic reminders are not supported. Deposits are recorded on assignments, but are not treated as rent or tracked as separate deposit transactions.
5. On **Expenses**, add a paid expense, including its property, category (for example `Maintenance` or `Property taxes`), description, date paid, and amount. Select it to import one or more JPEG, PNG, GIF, or BMP receipt images (up to 20 MB each); view or export them from the receipt dialog.
6. On **Reports**, select a property or all properties and an inclusive payment-date range. Run the report and optionally export its summary and detail rows to CSV. The summary separates rent received from bill reimbursements and groups expenses by category; rent less expenses excludes reimbursements and deposits and is not a tax calculation. Use consistent category names to group similar expenses.
7. From a property profile, log maintenance and link an existing property expense to record its cost and receipts without double-counting it in reports. Import and export documents from property or renter profiles. Documents and images are limited to 20 MB each and stored in the database.

## SendGrid

On **Charges**, open **Email settings** and enter a verified SendGrid sender address and API key with mail-send permission. The API key is protected using Windows DPAPI for the current Windows user; it is not stored in the source code. Blank out the key field when editing settings to retain the existing key. Windows user/profile changes can make the key unreadable, in which case configure it again.

Emails are sent only after a preview, optional edits, and explicit confirmation. A bill notice requires an unpaid bill charge; a late notice requires a past-due unpaid charge; a receipt requires a recorded payment. Email attempts, including message contents, are visible in the renter profile and stored in the database. `Accepted` means SendGrid accepted the request, not that it was delivered; `Attempting` or `Not confirmed` may mean the outcome is unknown, so check before resending. The app does not schedule messages or track delivery status. Configure a real sender and API key before sending; no email is sent by the test suite.

## Storage and backups

The database and encrypted email settings are stored under `%LOCALAPPDATA%\RentalApplication`. Receipt image binaries are stored in the SQLite database, so they increase its size and are included in database backups. The database and receipt images themselves are **not encrypted**; protect the Windows account and backups appropriately.

Existing databases are upgraded on first launch. The former address is retained as the street-address field without guessing its components; edit each property to fill in the remaining fields. Purchase price is left blank when unknown. Property photos are also stored in the database. Back up the database before installing an update, and close older versions of the application before opening the updated version.

Use **Back up database** on Home to create a consistent snapshot with a new filename; it contains private information and should be stored securely. Follow **Restore instructions** on Home: close all app windows, back up the existing database, and replace `rentals.db` with the chosen backup. The encrypted settings file can be backed up separately, but its API key is tied to the original Windows user. For an isolated instance, set `RENTAL_APPLICATION_DATA` to a writable directory before starting the application.
