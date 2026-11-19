using AvailabilityService;

AvailChecker availChecker = new AvailChecker();
Dictionary<string, string> cfg = availChecker.config.config;

List<Uri> uris = new List<Uri>
{
    await availChecker.GetPrinterURIByDevice(cfg["PRINTER_PHOTO"]),
    await availChecker.GetPrinterURIByDevice(cfg["PRINTER_DOC"])
};

while (true)
{
    foreach (Uri uri in uris)
    {
        await availChecker.checkLoop(uri);
    }
    await Task.Delay(10000);
}
