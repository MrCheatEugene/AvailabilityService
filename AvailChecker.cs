using MasterConfig;
using SharpIpp;
using SharpIpp.Models;
using System;
using System.Diagnostics;
using System.Runtime.Caching;
using System.Security.Policy;
using static MasterConfig.MasterConfig;
using static Org.BouncyCastle.Math.EC.ECCurve;
namespace AvailabilityService
{
    public record PrinterInfo ( bool Ok, string StatusReadable, string Status, string Reasons, string ReasonsReadable );
    public class AvailChecker
    {
        
        MemoryCache cache = MemoryCache.Default; // <Uri, PrinterInfo>
        SharpIppClient client = new SharpIppClient();
        public MasterConfig.MasterConfig config = new MasterConfig.MasterConfig();
        SQL sql;
        Dictionary<String, string> info = new Dictionary<String, String> {
            {"other", "Неизвестная ошибка" },
            {"media-empty-warning; media-needed-warning", "Лоток бумаги пуст" },
            {"media-empty-warning", "Лоток бумаги пуст" },
            {"none", "Нет проблем" },
            {"media-needed", "Недостаточно бумаги в лотке" },
            {"media-jam", "Обнаружена зажёванная бумага" },
            {"toner-low", "Низкий обьём чернил" },
            {"toner-empty", "Чернила закончились" },
            {"cover-open", "Открыта крышка" },
            {"interlock-open", "Открыт замок" },
            {"door-open", "Открыта дверца" },
            {"input-tray-missing", "Отсутствует лоток для бумаги" },
            {"media-low", "Низкое кол-во бумаги" },
            {"media-empty", "Лоток бумаги пуст" },
            {"output-tray-missing", "Отсутствует выходной лоток (как?)" },
            {"output-area-almost-full", "Выходной лоток почти заполнен" },
            {"output-area-full", "Выходной лоток заполнен" },
            {"marker-supply-low", "Низкое кол-во чернил" },
            {"marker-supply-empty", "Чернила закончились" },
            {"marker-waste-almost-full", "Абсорбер (памперс) почти заполнен" },
            {"marker-waste-full", "Абсорбер (памперс) заполнен" },
            {"fuser-over-temp", "Температура фьюзера выше нормы" },
            {"fuser-under-temp", "Температура фьюзера ниже нормы" },
            {"opc-near-eol", "Оптический фотопроводник почти отработал свой жизненный цикл" },
            {"opc-life-over", "Оптический фотопроводник больше не работает" },
            {"developer-low", "Низкий обьём проявителя" },
            {"developer-empty", "Проявитель закончился" },
            {"interpreter-resource-unavailable", "Ресурс для интерпретации недоступен (шрифт/форма)" }
        };
        Dictionary<String, String> statuses = new Dictionary<string, string>
        {
            {"idle", "Простаивает" },
            {"stopped", "Остановлен" },
            {"paused", "Приостановлен" },
            {"processing", "В работе" }
        };

        public AvailChecker()
        {
            sql = config.GetSQL();
        }
        public async Task<PrinterInfo> checkPrinter(Uri printer)
        {
            try
            {
                var request = new GetPrinterAttributesRequest
                {
                    OperationAttributes = new GetPrinterAttributesOperationAttributes
                    {
                        PrinterUri = printer,
                        AttributesCharset = "utf-8",
                        AttributesNaturalLanguage = "en"
                    }
                };

                var response = await client.GetPrinterAttributesAsync(request);
                string errors = response.PrinterStateReasons != null ? String.Join("; ", response.PrinterStateReasons.Select(x => info.ContainsKey(x.Trim()) ? info[x.Trim()] : x)): "N/A";
                string errorsUnreadable = response.PrinterStateReasons != null ? String.Join("; ", response.PrinterStateReasons) : "N/A";
                string status = (response.PrinterState.ToString() ?? "N/A").ToLower();
                bool ok = errorsUnreadable.Trim() == "none" && status != "stopped";
                string SR = (statuses.ContainsKey(status) ? statuses[status] : status) ?? "N/A";
                PrinterInfo pI = new(ok, SR, status, errorsUnreadable, errors);
                return pI;
            }catch(Exception e)
            {
                Console.WriteLine(e.StackTrace);
                return new PrinterInfo(false, "Не удается установить связь с принтером", "unknown", e.Message, $"Отладочные данные: {e.Message}\n{e.StackTrace}");
            }
        }

        private async Task<String> GetPrinterDeviceByName(Uri uri)
        {
            Process p = Process.Start("/bin/bash", $"-c \"lpstat -v | grep {uri.ToString()}\"");
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            string r = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return r.Split(" for ").Last().Split(":").First().Trim();
        }

        public async Task<Uri> GetPrinterURIByDevice(String name)
        {
            Process p = Process.Start("/bin/bash", $"-c \"lpstat -v | grep {name}\"");
            p.StartInfo.RedirectStandardOutput = true;
            p.Start();
            string r = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return new Uri(r.Split(": ").Last().Trim());
        }
        private async Task<String> GetPrinterTypeByName(Uri uri)
        {
            String dev = await GetPrinterDeviceByName(uri);
            return config.config.Keys.Where(x => config.config[x].Equals(dev)).First();
        }

        public async Task processNewPI(PrinterInfo pI, Uri printer)
        {
            string Emoji = pI.Ok ? "✅" : "❌";
            string PrinterType = await GetPrinterTypeByName(printer);
            string ReadablePRType = PrinterType == "PRINTER_DOC" ? "Документы" : "Фотографии";
            string pIOk = pI.Ok ? "\n\nВведите /resetErrors чтобы сбросить ошибки" : "";
            string Message = $"{Emoji} Обновлен статус принтера {ReadablePRType} ({PrinterType}): \nСтатус: {pI.StatusReadable}\n<code>{pI.Status}</code>\n\nДоп. информация: {pI.ReasonsReadable}\n<code>{pI.Reasons}</code>{pIOk}";
            if (!pI.Ok)
            {
                await config.BackReport(Message);

                await sql.Query("INSERT INTO errors (printer, error) VALUES (@p, @e);", new Dictionary<string, string> { { "@p", config.config[PrinterType] }, { "@e", Message } });
            }
            else
            {
                await sql.Query("DELETE FROM errors WHERE printer = @p;", new Dictionary<string, string> { { "@p", config.config[PrinterType] } });
            }
        }

        public async Task checkLoop(Uri printer)
        {
            String url = printer.ToString();
            PrinterInfo? knownPI = (PrinterInfo?)cache.Get(url);
            PrinterInfo pI = await checkPrinter(printer);
            if (knownPI == null || pI != knownPI){
                cache.Set(url, pI, DateTimeOffset.Now.AddMinutes(30));
                if(knownPI != null)
                {
                    Console.WriteLine($"KnownInfo: {knownPI}");
                    await processNewPI(pI, printer);
                }
            }
        }
    }
}
