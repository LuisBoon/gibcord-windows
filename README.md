# GibCord 6.0 na Windows

Nowa, natywna aplikacja GibCorda na komputer: **C# + WinUI 3** (Windows App SDK), bez Electrona.
Łączy się z tym samym serwerem co strona i telefon (`https://gibcord.luisboon.pl`, API `/api/v1` + WebSocket),
więc konta, wiadomości, serwery i znajomi są wspólne.

## Co jest w środku

| Część | Co robi |
|---|---|
| `src/GibCord.Core` | Rdzeń bez okien: REST z tokenem aplikacji, WebSocket z ponownym łączeniem, stan (serwery, kanały, rozmowy, znajomi, nieprzeczytane, wzmianki), pamięć podręczna na dysku, formatowanie wiadomości. Działa też na Linuksie. |
| `src/GibCord.App` | Okno WinUI 3 napisane w samym C# (bez plików XAML): logowanie z 2FA, serwery i kanały, czat, rozmowy prywatne, znajomi, powiadomienia Windows, zasobnik, Ctrl+K, aktualizacje (Velopack). |
| `tests/GibCord.Core.Tests` | Testy rdzenia. Testy „na żywo” uruchamiają się tylko z kopią testową serwera (`GC_TEST_URL`, `GC_TEST_PASS`). |

## Etapy

1. **Fundament** (ta wersja beta): logowanie, serwery, kanały, czat, rozmowy prywatne, znajomi, powiadomienia.
2. Głos: rozmowy przez serwer LiveKit, kamera, transmisje, push-to-talk, redukcja szumów, nakładka w grze, gry i Spotify przy nicku.
3. Reszta: role i uprawnienia, wątki, automoderacja, szablony, wspólne oglądanie, mini-gry, panel admina.
4. Przesiadka z obecnej aplikacji.

## Budowanie

- Na Windowsie: `dotnet publish src/GibCord.App -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish`
- Na Linuksie (tylko sprawdzenie kodu): `tools/kompiluj.sh` i `dotnet test tests/GibCord.Core.Tests`
- GitHub Actions (`.github/workflows/build.yml`) buduje przy każdym pushu, robi zrzuty ekranu z trybu pokazowego (`GibCord.exe --demo --view chat --screenshot plik.png`) i instalator. Tag `v6.0.0-beta.N` publikuje wydanie z automatycznymi aktualizacjami.

Dane aplikacji: `%LocalAppData%\GibCord6-dane` (sesja zaszyfrowana kluczem konta Windows, pamięć podręczna, ustawienia, dziennik).
Czcionka Sora: licencja SIL OFL (`src/GibCord.App/Assets/Fonts/OFL.txt`).
