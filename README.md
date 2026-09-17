# Zapqio Runner - moduł Nexo.Invoices

Metoda **"Add invoice and pdf"**: wystawia fakturę w InsERT nexo (Subiekt), generuje PDF i powiadamia
na Slacku. Połączenie z Nexo bierze ze współdzielonego `NexoClient` z paczki
[Nexo.Connection](https://github.com/zapqio/dotnet-module-nexo-connection).

## Instalacja

`Nexo.Invoices.zip` do `Modules\` runnera obok `Nexo.Connection.zip` i `Nexo.Sdk.zip` (SDK InsERT
w wersji Subiekta, pakuje je `update-nexo-sdk.ps1` z repo Nexo.Connection), restart usługi. Dane połączenia
są w sekcji `Connect` pliku `Config\nexoModule.json` w katalogu runnera (zakłada go Nexo.Connection); ten
moduł dopisuje do tego samego pliku własne klucze (magazyn, pola własne, szablony wydruku, Slack, KSeF)
z wartościami domyślnymi przy pierwszym starcie - lista w `Settings.cs`.

Bez `Nexo.Connection.zip` runner zgłosi w logu `Metoda Nexo.AddInvoice ... nie została utworzona`
i ogłosi się bez tej metody.

## Budowanie

Wymaga SDK InsERT nexo w `C:\nexoSDK_<wersja>\Bin\` (inna ścieżka: `nexoSdkBinPath` w csproj) oraz
paczek NuGet `Zapqio.Runner.Module.Core` i `Zapqio.Nexo.Connection` w źródle widocznym dla
`dotnet restore`. `dotnet publish -c Release` daje `bin\Release\Nexo.Invoices.zip` z samą DLL modułu:
SDK i `Nexo.Connection.dll` przychodzą w runtime z paczki współdzielonej.
