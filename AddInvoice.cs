using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using InsERT.Moria.Dokumenty.Logistyka;
using InsERT.Moria.Klienci;
using InsERT.Moria.ModelDanych;
using InsERT.Moria.Sfera;
using InsERT.Mox.DataExtensions;
using InsERT.Mox.ObiektyBiznesowe;
using Nexo.Invoice;
using Zapqio.Runner.Core;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Nexo
{
    public class AddInvoice : IRunnerMethod
    {
        private NexoClient _client;
        private readonly Settings _settings;

        public AddInvoice(NexoClient client, Settings settings)
        {           
            _client = client;
            _settings = settings;
            NexoExtensions.Client = _client;
        }
        public Type InData()
        {
            return typeof(InvoiceIn);
        }

        public string NameMethod()
        {
            return "Add invoice and pdf";
        }

        public Type OutData()
        {
            return typeof(InvoiceOut);
        }

        public async Task<string> Run(string data)
        {
            var input = System.Text.Json.JsonSerializer.Deserialize<InvoiceIn>(data);

            // Faktura mogła już zostać wystawiona (np. ponowione wywołanie) - szukamy jej
            // po polu własnym z UniqueId i zamiast duplikatu oddajemy istniejący dokument.
            var existingInvoice = FindInvoiceByUniqueId(input.UniqueId);
            if (existingInvoice != null)
            {
                Console.WriteLine($"Faktura dla UniqueId '{input.UniqueId}' już istnieje: {existingInvoice.NumerWewnetrzny.PelnaSygnatura} - pomijam wystawianie");
                return System.Text.Json.JsonSerializer.Serialize(new InvoiceOut
                {
                    Number = existingInvoice.NumerWewnetrzny.PelnaSygnatura,
                    Pdf = PrintToPdf(existingInvoice, input.TemplatePrintLanguage),
                });
            }

            if (input.Positions == null || input.Positions.Count == 0)
            {
                throw new Exception("Nie uzupełniono pozycji dokumentu");
            }

            using IDokumentSprzedazy invoice = _client.Uchwyt.DokumentySprzedazy().UtworzFaktureSprzedazy();
            var doc = invoice.Dane;
            doc.Magazyn = _client.Uchwyt.Magazyny().Dane.Pierwszy(x => x.Symbol == _settings.Warehouse);
            doc.Uwagi = input.Comment;

            if (input.SaleDate.HasValue)
            {
                doc.DataSprzedazy = input.SaleDate.Value;
            }

            // Nabywca
            doc.Podmiot = GetEntity(input.Buyer);

            // Odbiorca
            if (input.Recipient != null)
            {
                doc.Odbiorca = GetEntity(input.Recipient);
                if (input.Recipient.BindWithBuyer ?? false)
                {
                    doc.Podmiot.BindWithByuer(doc.Odbiorca, input.Recipient);
                }
            }


            if (!string.IsNullOrEmpty(input.Currency))
            {
                doc.Waluta = GetCurrency(input.Currency);
            }


            var taxGreaterThanZero = false;
            foreach (var pos in input.Positions)
            {
                ValidatePosition(pos);
                var ip = invoice.Pozycje.Dodaj(pos.Symbol);
                ip.Ilosc = pos.Quantity ?? 0;
                var tax = GetTax(pos);
                ip.StawkaVat = tax;
                taxGreaterThanZero |= tax.Stawka > 0;
                ip.Cena.NettoPrzedRabatem = pos.NetPrice ?? 0;
            }

            // Dla nabywcy z Polski nie ruszamy transakcji handlowej - dokument ma domyślnie ustawione "S"
            if (doc.Podmiot.AdresPodstawowy.Panstwo.KodISOAlfa2() != "PL")
            {
                Console.WriteLine($"Nabywca spoza Polski (państwo: {doc.Podmiot.AdresPodstawowy.Panstwo.KodISOAlfa2()}), ustalanie transakcji handlowej");
                doc.TransakcjaHandlowa = GetTH(doc.Podmiot, input.Vies, taxGreaterThanZero);
            }

            invoice.Przelicz();

            invoice.SetPayment(GetPaymentType(input.Payment, input.Currency), doc.KwotaDoZaplaty);

            invoice.SetOwnFields(
                OwnField.Text(_settings.ZapqInvoiceIdOwnField, input.UniqueId),
                OwnField.Text(_settings.ViesOwnField, input.Vies),
                OwnField.Date(_settings.StartLicenceDateOwnField, input.StartLicenceDate),
                OwnField.Date(_settings.EndLicenceDateOwnField, input.EndLicenceDate));


            // Licencja na przełomie lat (np. start 2026, koniec 2027) - powiadomienie idzie
            // dopiero po udanym zapisie, żeby było w nim czym się posłużyć: numer dokumentu.
            var crossYearLicence = input.StartLicenceDate.HasValue
                && input.EndLicenceDate.HasValue
                && input.StartLicenceDate.Value.Year != input.EndLicenceDate.Value.Year;

            if (crossYearLicence)
            {
                if (!string.IsNullOrEmpty(_settings.RMPFlagName))
                    invoice.Dane.SetFlag(_settings.RMPFlagName);
                else
                    Console.Error.WriteLine($"Nie ustawiono flagi: Rozliczenia międzyokresowe (RMP)");

            }

            if (input.StartLicenceDate.HasValue && input.EndLicenceDate.HasValue)
            {
                var licencePeriod = $"Okres licencji: {input.StartLicenceDate.Value:dd.MM.yyyy} - {input.EndLicenceDate.Value:dd.MM.yyyy}";
                invoice.Dane.Uwagi = string.IsNullOrWhiteSpace(invoice.Dane.Uwagi)
                    ? licencePeriod
                    : invoice.Dane.Uwagi + "\n" + licencePeriod;
            }

            var saved = invoice.Zapisz();
            if (!saved)
            {
                throw new Exception($"Nie udało się zapisać faktury: {invoice.Error()}");
            }
            else
            {
                Console.WriteLine($"Utworzono fakturę: {invoice.Dane.NumerWewnetrzny.PelnaSygnatura}");
            }

            var invoiceOut = new InvoiceOut
            {
                Number = invoice.Dane.NumerWewnetrzny.PelnaSygnatura
            };
            var documentId = invoice.Dane.Id;

            // Po zapisie czekamy, aż Nexo (Asystent / harmonogram KSeF) wyśle fakturę i dostanie numer KSeF.
            // Wysyłkę robi inny proces, więc dokument czytamy za każdym razem na nowo przez OpenInvoice -
            // obiekt "invoice" trzymany w pamięci nie zobaczy jej wyniku. Sprawdzamy od razu, a czekamy
            // dopiero między próbami: faktura, która do KSeF nie idzie (np. konsument z UE), nie blokuje
            // wywołania na cały limit prób.
            var attempts = _settings.KsefPollAttempts;
            var interval = TimeSpan.FromSeconds(Math.Max(10, _settings.KsefPollIntervalSeconds));
            if (attempts <= 0)
            {
                Console.WriteLine($"KSeF: pomijam oczekiwanie na numer KSeF dla {invoiceOut.Number} (KsefPollAttempts = {attempts})");
            }
            else
            {
                Console.WriteLine($"KSeF: czekam na numer KSeF dla {invoiceOut.Number} - do {attempts} prób co {interval.TotalSeconds:0} s");
                var resolved = false;
                StatusKSeF? lastStatus = null;
                for (var attempt = 1; attempt <= attempts && !resolved; attempt++)
                {
                    if (attempt > 1)
                    {
                        await Task.Delay(interval);
                    }

                    using var current = OpenInvoice(documentId);
                    if (current == null)
                    {
                        Console.Error.WriteLine($"KSeF: próba {attempt}/{attempts} - nie znaleziono dokumentu {invoiceOut.Number} (Id {documentId})");
                        continue;
                    }

                    var status = current.Dane.StatusKSeF();
                    lastStatus = status;
                    switch (status)
                    {
                        case StatusKSeF.PrzyjetoWKsef:
                        case StatusKSeF.PobranoUPO:
                        case StatusKSeF.NumerNadanyRecznie:
                            // Numer KSeF już jest - UPO Nexo dociąga niezależnie, nie ma na co czekać.
                            var ksefNumber = current.Dane.PowiazanieZDokumentemElektronicznym?.DokumentElektroniczny?.NumerKSeF;
                            Console.WriteLine($"KSeF: {invoiceOut.Number} przyjęta - status {status}, numer KSeF: {(string.IsNullOrEmpty(ksefNumber) ? "(brak)" : ksefNumber)} (próba {attempt}/{attempts})");
                            resolved = true;
                            break;
                        case StatusKSeF.BladWysylki:
                        case StatusKSeF.NiezgodneZeSchematem:
                        case StatusKSeF.NieDotyczy:
                        case StatusKSeF.NiePodlegaWysylce:
                            // Stan końcowy - dalsze odpytywanie nic nie zmieni.
                            Console.Error.WriteLine($"KSeF: {invoiceOut.Number} nie trafi do KSeF - status {status}, przerywam oczekiwanie (próba {attempt}/{attempts})");
                            resolved = true;
                            break;
                        default:
                            Console.WriteLine($"KSeF: {invoiceOut.Number} jeszcze bez numeru KSeF - status {status} (próba {attempt}/{attempts})");
                            break;
                    }
                }

                if (!resolved)
                {
                    Console.Error.WriteLine($"KSeF: {invoiceOut.Number} nie dostała numeru KSeF w ciągu {attempts} prób ({(attempts - 1) * interval.TotalSeconds:0} s) - ostatni status: {lastStatus?.ToString() ?? "(brak odczytu)"}");
                }
            }

            // PDF drukujemy z dokumentu odczytanego na nowo - po wysyłce Nexo ma na nim numer KSeF i kod QR,
            // których obiekt "invoice" z pamięci nie zna.
            using var refreshed = OpenInvoice(documentId);
            invoiceOut.Pdf = PrintToPdf(refreshed?.Dane ?? invoice.Dane, input.TemplatePrintLanguage);
            return System.Text.Json.JsonSerializer.Serialize(invoiceOut);
        }

        /// <summary>
        /// Otwiera dokument sprzedaży na nowo - świeży odczyt z bazy do sprawdzania stanu, który zmienia
        /// inny proces (np. wysyłka do KSeF). Zwraca null, gdy dokumentu nie ma. Wynik trzeba zdisposować.
        /// </summary>
        private IObiektBiznesowy<DokumentDS> OpenInvoice(int documentId)
        {
            var entity = _client.Uchwyt.DokumentySprzedazy().Dane.Wszystkie().FirstOrDefault(x => x.Id == documentId);
            return entity == null ? null : _client.Uchwyt.DokumentySprzedazy().Znajdz(entity);
        }

        /// <summary>
        /// Szuka wcześniej wystawionej faktury po polu własnym z identyfikatorem z systemu zewnętrznego.
        /// Brak UniqueId na wejściu albo nieskonfigurowane pole własne = brak kontroli duplikatów.
        /// </summary>
        private DokumentDS FindInvoiceByUniqueId(string uniqueId)
        {
            if (string.IsNullOrWhiteSpace(uniqueId))
            {
                return null;
            }

            var ownFieldName = _settings.ZapqInvoiceIdOwnField;
            if (string.IsNullOrWhiteSpace(ownFieldName))
            {
                Console.Error.WriteLine("Nie ustawiono pola własnego z identyfikatorem (ZapqInvoiceIdOwnField) - pomijam sprawdzenie, czy faktura już istnieje");
                return null;
            }

            return _client.Uchwyt.DokumentySprzedazy().Dane.Wszystkie()
                .Where(a => a.PolaWlasneAdv2.Get<string>(ownFieldName) == uniqueId)
                .ResolveExtensionProperties()
                .FirstOrDefault();
        }

        /// <summary>
        /// Drukuje dokument do PDF-a i zwraca zawartość pliku w base64.
        /// Szablon: MapLaguageToTemplatePrint (po języku) → DefaultTemplatePrint → systemowy.
        /// Plik tymczasowy powstaje w %TEMP%\runner i jest kasowany po odczycie.
        /// </summary>
        private string PrintToPdf(DokumentDS document, string templatePrintLanguage) 
        {
            var namefile = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(document.NumerWewnetrzny.PelnaSygnatura))).Replace("-", "");
            var dir = Path.Combine(Path.GetTempPath(), "runner");
            Directory.CreateDirectory(dir);
            Console.WriteLine(Path.Combine(dir, namefile));
            using var print = _client.Uchwyt.Wydruki().Utworz(InsERT.Moria.Wydruki.Enums.TypWzorcaWydruku.FakturaSprzedazy);
            print.ObiektDoWydruku = document;

            // Wybór szablonu wydruku: MapLaguageToTemplatePrint → DefaultTemplatePrint → systemowy
            bool templateSet = false;

            if (_settings.MapLaguageToTemplatePrint != null
                && !string.IsNullOrEmpty(templatePrintLanguage)
                && _settings.MapLaguageToTemplatePrint.TryGetValue(templatePrintLanguage, out var mappedTemplateName))
            {
                var template = print.ParametryDrukowania.DostepneWzorce.FirstOrDefault(x => x.Nazwa == mappedTemplateName);
                if (template != null)
                {
                    print.ParametryDrukowania.WybranyWzorzec = template;
                    templateSet = true;
                }
                else
                {
                    Console.WriteLine($"Nie znaleziono szablonu '{mappedTemplateName}' dla języka '{templatePrintLanguage}'");
                }
            }

            if (!templateSet && !string.IsNullOrEmpty(_settings.DefaultTemplatePrint))
            {
                var template = print.ParametryDrukowania.DostepneWzorce.FirstOrDefault(x => x.Nazwa == _settings.DefaultTemplatePrint);
                if (template != null)
                {
                    print.ParametryDrukowania.WybranyWzorzec = template;
                    templateSet = true;
                }
                else
                {
                    Console.WriteLine($"Nie znaleziono domyślnego szablonu '{_settings.DefaultTemplatePrint}'");
                }
            }

            if (!templateSet)
            {
                Console.WriteLine($"Użyto szablonu systemowego: {print.ParametryDrukowania.WybranyWzorzec.Nazwa}");
            }

            print.ParametryDrukowania.NazwaDokumentuUzytkownika = namefile;
            print.ParametryDrukowania.SciezkaEksportu = dir;
            print.ParametryDrukowania.FormatEksportu = "pdf";
            print.ParametryDrukowania.ZastapPliki = true;
            print.Eksport();
            var error = print.PobierzListeBledow();
            if (error != null && error.Count() > 0)
            {
                throw new Exception($"Nie udało się utworzyć pliku PDF: {string.Join('|', error)}");
            }
            else
            {
                Console.WriteLine($"Utworzono plik PDF");
            }
            var filePath = Path.Combine(dir, namefile) + ".pdf";
            var pdf = Convert.ToBase64String(File.ReadAllBytes(filePath));
            File.Delete(filePath);
            return pdf;
        }

        private void ValidatePosition(InvoicePosition pos)
        {
            if (string.IsNullOrEmpty(pos.Symbol))
            {
                throw new Exception($"Symbol nie może mieć wartości pustej lub null");
            }
            var asortyment = _client.Uchwyt.Asortymenty().Dane.Wszystkie(t => t.Symbol == pos.Symbol).FirstOrDefault();
            if (asortyment == null)
            {
                throw new Exception($"Nie znaleziono asortymentu o symbolu {pos.Symbol}");
            }

            if (pos.Quantity == null || pos.Quantity == 0)
            {
                throw new Exception($"Ilość na pozycji: {pos.Symbol} nie może być pusta lub zerowa");
            }

            if (pos.NetPrice == null)
            {
                throw new Exception($"Nie uzupełniono ceny netto na pozycji {pos.Symbol}");
            }
        }

        private Waluta GetCurrency(string currency)
        {
            var c = _client.Uchwyt.Waluty().Znajdz(currency);
            if (c == null)
            {
                throw new Exception($"Nie znaleziono waluty: {currency}");
            }
            return c.Dane;
        }

        private Podmiot GetEntity(InvoiceEntity entity)
        {
            if (entity == null) return null;

            if (!string.IsNullOrEmpty(entity.TaxId))
            {
                var taxIdDecoded = DecodeTaxId(entity.TaxId, entity.CountrySymbol);
                if (taxIdDecoded == null)
                {
                    throw new Exception($"Nieprawidłowy NIP: {entity.TaxId}");
                }
                var podmiot = _client.Uchwyt.Podmioty().Dane.Pierwszy(x => (taxIdDecoded.Item1 == "PL" ? x.NIP == taxIdDecoded.Item2 : x.NIPUE == taxIdDecoded.Item1 + taxIdDecoded.Item2));
                if (podmiot != null)
                {
                    Console.WriteLine($"Wyszukano podmiot po NIP-ie: {taxIdDecoded.Item1 + taxIdDecoded.Item2}");
                    return podmiot;
                }
            }
            if (!string.IsNullOrEmpty(entity.Symbol))
            {
                using var podmiot = _client.Uchwyt.Podmioty().Znajdz(entity.Symbol);
                if (podmiot != null)
                {
                    Console.WriteLine($"Wyszukano podmiot {entity.Symbol} po symbolu");
                    return podmiot.Dane;
                }
            }
            return CreateEntity(entity);
        }

        /// <summary>
        /// Kody kraju, ktore oznaczaja to samo panstwo, a roznia sie zapisem: prefiks NIP-u UE
        /// kontra kod ISO. Grecja ma w NIP-ie "EL", a w ISO "GR"; Irlandia Polnocna wystawia NIP-y
        /// z prefiksem "XI", ale w slowniku panstw jest jako Wielka Brytania ("GB").
        /// Mapa jest symetryczna - kazdy kod wskazuje na swoj odpowiednik.
        /// </summary>
        private static readonly Dictionary<string, string> CountryCodeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "GR", "EL" },
            { "EL", "GR" },
            { "XI", "GB" },
            { "GB", "XI" },
        };

        /// <summary>
        /// Odpowiednik kodu kraju z <see cref="CountryCodeAliases"/> albo ten sam kod, gdy aliasu nie ma.
        /// </summary>
        private static string CountryCodeAlias(string code)
        {
            return code != null && CountryCodeAliases.TryGetValue(code, out var alias) ? alias : code;
        }

        /// <summary>
        /// Kod kraju w postaci uzywanej w NIP-ie UE: Grecja ma w numerze "EL", choc jej kod ISO to "GR".
        /// Pozostale kody zostaja bez zmian - w szczegolnosci "GB" nie zamienia sie na "XI", bo z kodu
        /// kraju nie wynika, ze chodzi o Irlandie Polnocna; jesli ma byc "XI", musi przyjsc w NIP-ie.
        /// </summary>
        private static string VatCountryCode(string code)
        {
            return string.Equals(code, "GR", StringComparison.OrdinalIgnoreCase) ? "EL" : code;
        }

        /// <summary>
        /// Sprowadza kod kraju do jednej postaci na potrzeby porownania: "GR" i "EL" to Grecja,
        /// "XI" i "GB" to Wielka Brytania. Dzieki temu NIP "EL123456789" pasuje do CountrySymbol "GR",
        /// a NIP "XI123456789" do "GB".
        /// </summary>
        private static string SameCountryCode(string code)
        {
            if (code == null)
            {
                return null;
            }
            // Za wspolna postac bierzemy alfabetycznie mniejszy kod z pary - wazne tylko to,
            // zeby oba kody z pary daly ten sam wynik.
            var alias = CountryCodeAlias(code);
            return string.CompareOrdinal(code, alias) <= 0 ? code : alias;
        }

        /// <summary>
        /// Rozkłada NIP na kod kraju i sam numer. Kod kraju może przyjść dwiema drogami -
        /// jako prefiks NIP-u ("DE811907980") albo jako CountrySymbol podmiotu - i te dwie drogi
        /// muszą się zgadzać:
        ///   - NIP z prefiksem + ten sam kod kraju ("DE811907980" + "DE") -> ("DE", "811907980"),
        ///   - NIP bez prefiksu + kod kraju ("811907980" + "DE")          -> ("DE", "811907980"),
        ///   - NIP z prefiksem + INNY kod kraju ("DE811907980" + "PL")    -> wyjątek.
        ///
        /// Rozbieżność zgłaszamy błędem, bo nie da się zgadnąć, która strona ma rację:
        /// z prefiksu bierze się NIP UE, a z CountrySymbol państwo adresu i PanstwoRejestracji -
        /// przepuszczony rozjazd daje podmiot z niemieckim NIP-em UE na polskim adresie.
        ///
        /// Brak kodu kraju po obu stronach oznacza NIP krajowy ("PL"). Za zgodne uznajemy też
        /// kody z jednej pary z <see cref="CountryCodeAliases"/> - Grecja ma w NIP-ie "EL",
        /// a w ISO "GR", Irlandia Północna wystawia NIP-y z "XI", a w słowniku państw jest "GB".
        /// Gdy NIP przyjdzie bez prefiksu, kod kraju podmiotu zamieniamy na ten używany
        /// w numerze VAT ("GR" -> "EL"); prefiks podany w NIP-ie zostaje bez zmian.
        /// </summary>
        private Tuple<string, string> DecodeTaxId(string taxId, string countryCode)
        {
            if (string.IsNullOrEmpty(taxId))
            {
                return null;
            }
            var reqex = new System.Text.RegularExpressions.Regex(@"^([A-Za-z]{2})?(.+)");
            var match = reqex.Match(taxId.Replace("-", "").Replace(" ", ""));
            if (match == null || !match.Success)
            {
                return null;
            }

            var entityCode = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpper();
            var taxIdCode = match.Groups[1].Success ? match.Groups[1].Value.ToUpper() : null;

            if (taxIdCode != null && entityCode != null && SameCountryCode(taxIdCode) != SameCountryCode(entityCode))
            {
                throw new Exception(
                    $"Kod kraju w NIP-ie ('{taxIdCode}' w '{taxId}') nie zgadza się z kodem kraju podmiotu " +
                    $"(CountrySymbol: '{countryCode}') - popraw jedno albo drugie. NIP z prefiksem kraju " +
                    $"musi mieć ten sam kod, co podmiot, albo przyjść bez prefiksu (sam numer).");
            }

            // Bez prefiksu w NIP-ie kod kraju bierzemy z podmiotu; gdy nie ma go po żadnej stronie,
            // zostaje NIP krajowy. Kod przepuszczamy przez VatCountryCode, żeby w numerze wylądował
            // prefiks używany w NIP-ie UE ("GR" -> "EL"); "XI" Irlandii Północnej zostaje bez zmian.
            var code = VatCountryCode(taxIdCode ?? entityCode) ?? "PL";
            return new Tuple<string, string>(code, match.Groups[2].Value);
        }

        /// <summary>
        /// Dzieli nazwę osoby fizycznej na imię i nazwisko po pierwszej spacji.
        /// "Jan Kowalski" -> ("Jan", "Kowalski"), "Anna Maria Nowak-Kowalska" -> ("Anna", "Maria Nowak-Kowalska"),
        /// pojedynczy człon trafia w całości do nazwiska.
        /// </summary>
        private static (string FirstName, string LastName) SplitPersonName(string name)
        {
            var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return (string.Empty, string.Empty);
            }
            if (parts.Length == 1)
            {
                return (string.Empty, parts[0]);
            }
            return (parts[0], string.Join(" ", parts.Skip(1)));
        }

        private Podmiot CreateEntity(InvoiceEntity entity)
        {
            Console.WriteLine($"Tworzenie podmiotu - nazwa: '{entity.Name}', pełna nazwa: '{entity.FullName}', symbol: '{entity.Symbol}', NIP: '{entity.TaxId}', kraj: '{entity.CountrySymbol}'");
            if (string.IsNullOrWhiteSpace(entity.CountrySymbol))
            {
                throw new Exception($"Nie uzupełniono kodu kraju (CountrySymbol) dla podmiotu '{entity.Name}' (symbol: '{entity.Symbol}', NIP: '{entity.TaxId}') - bez niego nie da się ustalić państwa adresu");
            }

            var taxIdDecoded = DecodeTaxId(entity.TaxId, entity.CountrySymbol);

            // Państwa szukamy po obu kodach z pary (GR/EL, XI/GB), bo nie wiadomo, którym z nich
            // opisano je w słowniku Nexo - dla kodów bez aliasu oba warunki są takie same.
            var countryCode = entity.CountrySymbol.Trim().ToUpper();
            var countryCodeAlias = CountryCodeAlias(countryCode);
            var country = _client.Uchwyt.Panstwa().Dane.Pierwszy(x => x.KodPanstwaUE == countryCode || x.KodPanstwaUE == countryCodeAlias);
            if (country != null)
            {
                Console.WriteLine($"Znaleziono państwo {country.KodPanstwaUE} (ISO: {country.KodISOAlfa2()}), członek UE: {country.CzlonekUE}");
            }
            else
            {
                Console.WriteLine($"UWAGA: nie znaleziono państwa o kodzie '{entity.CountrySymbol}' - podmiot powstanie bez państwa w adresie, zapis prawdopodobnie się nie powiedzie");
            }

            IPodmiot newEntity;
            if (entity.IsCompany)
            {
                // tworze firme
                newEntity = _client.Uchwyt.Podmioty().UtworzFirme();
                if (taxIdDecoded == null)
                {
                    Console.WriteLine("Tworzenie firmy - NIP nieuzupełniony");
                    newEntity.Dane.NIP = "";
                }
                else
                {
                    if (taxIdDecoded.Item1 == "PL")
                    {
                        newEntity.Dane.NIP = taxIdDecoded.Item2;
                        Console.WriteLine($"Tworzenie firmy - NIP krajowy: {newEntity.Dane.NIP}");
                    }
                    else
                    {
                        newEntity.Dane.PanstwoRejestracji = country;
                        newEntity.Dane.NIPUE = taxIdDecoded.Item1 + taxIdDecoded.Item2;
                        Console.WriteLine($"Tworzenie firmy - NIP UE: {newEntity.Dane.NIPUE}, państwo rejestracji: {entity.CountrySymbol}");
                    }
                }
                newEntity.Dane.Firma.Nazwa = !string.IsNullOrEmpty(entity.FullName) ? entity.FullName : entity.Name;
                newEntity.Dane.NazwaSkrocona = entity.Name;
                Console.WriteLine($"Nazwa firmy: '{newEntity.Dane.Firma.Nazwa}', nazwa skrócona: '{newEntity.Dane.NazwaSkrocona}'");
            }
            else
            {
                // tworze osobe fizyczną

                newEntity = _client.Uchwyt.Podmioty().UtworzOsobe();
                var personName = SplitPersonName(entity.Name);
                Console.WriteLine($"Tworzenie osoby fizycznej - imię: '{personName.FirstName}', nazwisko: '{personName.LastName}'");
                if (taxIdDecoded != null)
                {
                    Console.WriteLine($"UWAGA: podano NIP '{entity.TaxId}', ale IsCompany = false - NIP nie zostanie zapisany na osobie fizycznej");
                }

                newEntity.Dane.Osoba.Imie = personName.FirstName;
                newEntity.Dane.Osoba.Nazwisko = personName.LastName;

            }
            
            if (!string.IsNullOrEmpty(entity.Symbol))
            {
                newEntity.Dane.Sygnatura = new Sygnatura
                {
                    PelnaSygnatura = entity.Symbol
                };
                Console.WriteLine($"Ustawiono symbol podmiotu: {entity.Symbol}");
            }
            else
            {
                Console.WriteLine("Symbol nieuzupełniony - zostanie nadany automatycznie przez Nexo");
            }
            
            var glownyTyp = _client.Uchwyt.TypyAdresu().DaneDomyslne.Glowny;
            var address = newEntity.Dane.AdresPodstawowy ?? newEntity.DodajAdres(glownyTyp);
            address.Szczegoly.Ulica = entity.Street;
            address.Szczegoly.Miejscowosc = entity.City;
            address.Szczegoly.KodPocztowy = entity.PostalCode;
            address.Szczegoly.NrDomu = entity.HomeNumber ?? string.Empty;
            address.Szczegoly.NrLokalu = entity.ApartmentNumber ?? string.Empty;
            address.Panstwo = country;
            Console.WriteLine($"Adres podstawowy - ulica: '{address.Szczegoly.Ulica}', nr domu: '{address.Szczegoly.NrDomu}', nr lokalu: '{address.Szczegoly.NrLokalu}', kod: '{address.Szczegoly.KodPocztowy}', miejscowość: '{address.Szczegoly.Miejscowosc}'");

            if (!string.IsNullOrEmpty(entity.Phone))
            {
                var k = new Kontakt();
                newEntity.Dane.Kontakty.Add(k);
                k.Wartosc = entity.Phone;
                k.Rodzaj = _client.Uchwyt.RodzajeKontaktu().DaneDomyslne.Telefon;
                k.Podstawowy = true;
                Console.WriteLine($"Dodano kontakt podstawowy - telefon: {k.Wartosc}");
            }
            if (!string.IsNullOrEmpty(entity.Email))
            {
                var k = new Kontakt();
                newEntity.Dane.Kontakty.Add(k);
                k.Wartosc = entity.Email.ToLower();
                k.Rodzaj = _client.Uchwyt.RodzajeKontaktu().DaneDomyslne.Email;
                k.Podstawowy = true;
                Console.WriteLine($"Dodano kontakt podstawowy - e-mail: {k.Wartosc}");
            }

            var saved = newEntity.Zapisz();
            if (!saved)
            {
                throw new Exception($"Nie udało się zapisać podmiotu '{entity.Name}' (symbol: '{entity.Symbol}', NIP: '{entity.TaxId}', kraj: '{entity.CountrySymbol}'): {newEntity.Error()}");
            }

            var created = newEntity.Dane;
            var createdTaxId = !string.IsNullOrEmpty(created.NIPUE) ? created.NIPUE : created.NIP;
            Console.WriteLine($"Utworzono podmiot - symbol: {created.Sygnatura?.PelnaSygnatura ?? "(brak)"}, nazwa: '{created.NazwaSkrocona}', NIP: {(string.IsNullOrEmpty(createdTaxId) ? "(brak)" : createdTaxId)}, firma: {created.JestFirma()}");
            return created;
        }

        private StawkaVat GetTax(InvoicePosition position)
        {
            var tax = _client.Uchwyt.StawkiVat().Dane.Pierwszy(x => x.Symbol == position.TaxSymbol);
            if (tax != null)
            {
                Console.WriteLine($"Wyszukano stawkę VAT {position.TaxSymbol} po symbolu");
                return tax;
            }
            tax = _client.Uchwyt.StawkiVat().Dane.Pierwszy(x => x.Stawka == position.TaxPercent / 100M);            
            if (tax == null)
            {
                throw new Exception($"Nie znaleziono stawki VAT: {position.TaxSymbol}-{position.TaxPercent}");
            }
            Console.WriteLine($"Wyszukano stawkę VAT {position.TaxPercent} po wartości");
            return tax;
        }
        /// <summary>
        /// Formy płatności, które w słowniku Nexo mają osobną pozycję na walutę. Klucz to para
        /// (nazwa z wejścia, waluta dokumentu), wartość - nazwa, pod jaką forma naprawdę siedzi
        /// w Nexo. Integrator przysyła nazwę bez waluty, więc podmieniamy ją tutaj.
        ///
        /// Pary spoza mapy zostają nietknięte - dopisujemy je dopiero wtedy, gdy w Nexo naprawdę
        /// powstanie osobna forma płatności, inaczej GetPaymentType wywróciłoby się na nazwie,
        /// której w słowniku nie ma.
        /// </summary>
        private static readonly Dictionary<(string Payment, string Currency), string> PaymentNameByCurrency =
            new Dictionary<(string Payment, string Currency), string>
            {
                [("Stripe RCP", "EUR")] = "Stripe RCP EUR",
            };

        private FormaPlatnosci GetPaymentType(string payment, string currency)
        {
            var name = PaymentName(payment, currency);

            var p = _client.Uchwyt.FormyPlatnosci().Dane.Pierwszy(x => x.Nazwa == name);
            if (p == null)
            {
                throw new Exception($"Nie znaleziono formy płatności: {name}");
            }
            Console.WriteLine($"Wyszukano formę płatności {name} po nazwie");
            return p;
        }

        /// <summary>Nazwa formy płatności do wyszukania - patrz <see cref="PaymentNameByCurrency"/>.</summary>
        private static string PaymentName(string payment, string currency)
        {
            if (payment == null || currency == null)
            {
                return payment;
            }

            if (!PaymentNameByCurrency.TryGetValue((payment, currency), out var mapped))
            {
                return payment;
            }

            Console.WriteLine($"Forma płatności '{payment}' w walucie {currency} - szukam jako '{mapped}'");
            return mapped;
        }

        /// <summary>
        /// 1-transakcja przedsiębiorca z UE - SUPTK
        /// 2-transakcja przedsiębiorca spoza UE - DPTK
        /// 3-Konsument z UE - WSTO-OSS
        /// 4-konsumet spoza UE - DPTK
        /// 5 dla Polski - S
        /// 6-nabywca z UE bez potwierdzenia VIES, a na dokumencie naliczony VAT (stawka > 0
        ///   na co najmniej jednej pozycji) - WSTO-OSS, nawet jeśli podmiot jest oznaczony jako firma
        /// </summary>
        /// <param name="podmiot"></param>
        /// <param name="vies">odpowiedź VIES z wejścia; pusta = nabywca nie jest potwierdzonym podatnikiem VAT UE</param>
        /// <param name="taxGreaterThanZero">czy na co najmniej jednej pozycji naliczono VAT (stawka > 0) - wyliczane przy dodawaniu pozycji</param>
        /// <returns></returns>
        private TransakcjaHandlowa GetTH(Podmiot podmiot, string vies, bool taxGreaterThanZero)
        {
            var country = podmiot?.AdresPodstawowy?.Panstwo;
            if (country == null)
            {
                throw new Exception($"Nie można ustalić transakcji handlowej - podmiot {podmiot?.NazwaSkrocona} nie ma państwa w adresie podstawowym");
            }

            var isCompany = podmiot.JestFirma();
            var viesEmpty = string.IsNullOrWhiteSpace(vies);
            string symbol;
            if (country.KodISOAlfa2() == "PL")
            {
                symbol = "S";
            }
            else if (!country.CzlonekUE)
            {
                symbol = "DPTK";
            }
            else if (viesEmpty && taxGreaterThanZero)
            {
                // Brak potwierdzenia VIES + naliczony VAT = sprzedaż konsumencka w procedurze OSS,
                // niezależnie od tego, czy podmiot w Nexo jest oznaczony jako firma.
                symbol = "WSTO-OSS";
            }
            else if (isCompany)
            {
                symbol = "SUPTK";
            }
            else
            {
                symbol = "WSTO-OSS";
            }

            ITransakcjeHandlowe mgr = _client.Uchwyt.PodajObiektTypu<InsERT.Moria.Dokumenty.Logistyka.ITransakcjeHandlowe>();
            using var th = mgr.Znajdz(x => x.Symbol == symbol);
            if (th == null)
            {
                throw new Exception($"Nie znaleziono transakcji handlowej o symbolu: {symbol}");
            }
            Console.WriteLine($"Wybrano transakcję handlową {symbol} - państwo: {country.KodISOAlfa2()}, członek UE: {country.CzlonekUE}, firma: {isCompany}, VIES: {(viesEmpty ? "(brak)" : vies)}");
            return th.Dane;
        }
    }
}
