using System.Collections.Generic;
using Zapqio.Runner.Core;

namespace Nexo
{
    /// <summary>
    /// Ustawienia modułu faktur: własne klucze w <c>Config\nexoModule.json</c>, tym samym pliku, z którego
    /// Nexo.Connection czyta sekcję Connect (patrz <see cref="NexoConfig"/>). Brakujące klucze dopisują się
    /// z wartościami domyślnymi przy pierwszym starcie modułu.
    /// </summary>
    public class Settings : IRunnerInjection
    {
        public Settings()
        {
            NexoConfig.Populate(this);
        }

        public string Warehouse { get; set; } = "MAG";
        public string ViesOwnField { get; set; }
        public string StartLicenceDateOwnField { get; set; }
        public string EndLicenceDateOwnField { get; set; }
        public string ZapqInvoiceIdOwnField { get; set; }
        public string DefaultTemplatePrint { get; set; }

        public Dictionary<string, string> MapLaguageToTemplatePrint { get; set; }

        /// <summary>
        /// Flaga : "Rozliczenia międzyokresowe (RMP)"
        /// </summary>
        public string RMPFlagName { get; set; }

        /// <summary>Ile razy po zapisie faktury sprawdzać, czy dostała numer KSeF. 0 = nie czekać.</summary>
        public int KsefPollAttempts { get; set; } = 15;

        /// <summary>Odstęp między kolejnymi sprawdzeniami KSeF (sekundy).</summary>
        public int KsefPollIntervalSeconds { get; set; } = 20;

    }
}
