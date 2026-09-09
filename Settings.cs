using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Zapqio.Runner.Core;

namespace Nexo
{
    public class Settings : IRunnerInjection
    {
        private static readonly object _lock = new();
        private static bool _isLoading = false;

        public Settings()
        {
            lock (_lock)
            {
                if (_isLoading) return; // przerwij rekurencję

                _isLoading = true;
                try
                {
                    var data = ReadFromFile();
                    if (data != null)
                    {
                        foreach (var item in typeof(Settings).GetProperties())
                        {
                            if (item.CanWrite)
                            {
                                item.SetValue(this, item.GetValue(data));
                            }
                        }
                    }
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }

        private static Settings ReadFromFile()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDirectory, "nexoModule.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
            }
            else
            {
                var d = new Settings();
                File.WriteAllText(path, JsonSerializer.Serialize(d, new JsonSerializerOptions() { WriteIndented = true }));
                return null;
            }
        }

        public class NexoConnect
        {
            public string DatabaseServer { get; set; } = "172.24.43.98,1433";
            public string DatabaseUser { get; set; } = "sa";
            public string DatabasePassword { get; set; } = "sa";
            public string DatabaseName { get; set; } = "Nexo_Demo";
            public string UserName { get; set; } = "Szef";
            public string UserPassword { get; set; } = "robocze";
            public bool WindowsLogin { get; set; } = false;
        }

        public NexoConnect Connect { get; set; } = new();
        public string Warehouse { get; set; } = "MAG";
        public string ViesOwnField { get; set; }
        public string StartLicenceDateOwnField { get; set; }
        public string EndLicenceDateOwnField { get; set; }
        public string ZapqInvoiceIdOwnField { get; set; }
        public string DefaultTemplatePrint { get; set; }

        /// <summary>Token bota Slacka (xoxb-...) - uzywany przez <see cref="SlackClient"/>.</summary>
        public string SlackToken { get; set; }

        /// <summary>Domyslny kanal powiadomien, np. "#erp-alerty" albo ID kanalu.</summary>
        public string SlackChannel { get; set; }

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