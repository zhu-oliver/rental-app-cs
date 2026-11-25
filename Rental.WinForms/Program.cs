using Rental.Business;
using Rental.Data;

namespace Rental.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            string directory = Environment.GetEnvironmentVariable("RENTAL_APPLICATION_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RentalApplication");
            Directory.CreateDirectory(directory);
            var service = new RentalService(new SqliteRentalStore(Path.Combine(directory, "rentals.db")));
            Application.Run(new MainForm(service, directory));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unable to start Rental Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
