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

## Co umie (beta 8)

- **Czat**: wiadomości, odpowiedzi, edycja, reakcje, przypinanie, wątki (panel z boku), wyszukiwarka (Ctrl+F, `od:@ktoś`, `ma:zdjęcie`), GIF-y, emoji z szukaniem po polsku i podpowiedziami po dwukropku (`:serce`), naklejki, ankiety, mini-gry, pliki, wiadomości głosowe, filmy i dźwięki w czacie, szkice, kolejka wiadomości bez internetu, linki do GibCorda i karty zaproszeń, Skrzynka (Ctrl+I: wzmianki, zapisane, przypomnienia), przeglądarka obrazków z przybliżaniem.
- **Rozmowy**: głos przez LiveKit, kamera, transmisja ekranu (720p–1440p, 30/60 kl/s, dźwięk z komputera bez dźwięków rozmowy), podglądy transmisji, okienko transmisji na wierzchu (obraz w obrazie), soundboard, wspólne oglądanie (YouTube i filmy z czatu), naciśnij-aby-mówić, nakładka w grze (z dymkami nowych wiadomości), skróty Ctrl+Shift+M/D działające w grach.
- **Rozmowy prywatne i grupy**: tworzenie grup, ustawienia grupy, rozmowy wideo.
- **Ustawienia**: konto (profil, e-mail, hasło, 2FA z kodem QR, urządzenia, blokady, Spotify), aplikacja (motywy, rozmiar tekstu, tryb zwarty, nakładka, autostart, zasobnik, ściąga skrótów Ctrl+/), serwer (kanały, kategorie, uprawnienia, role z podglądem „jako rola”, członkowie, bany, emoji/naklejki/dźwięki, automoderacja, zgłoszenia, dziennik, szablon), panel admina (konta, serwery, zgłoszenia, system i wykresy, aktywność osób, poczta, dziennik, ogłoszenia).
- **Windows**: powiadomienia z odpowiedzią w dymku, licznik na ikonie, zasobnik, autostart, linki `gibcord://`, aktualizacje Velopack, paczka do Microsoft Store.

## Testy na żywo

Kopia testowa serwera (nigdy produkcja): `node server.mjs` z `DC_TEST_HOOKS=1 EMAIL_GATE=0 PORT=8893 DATA_DIR=<pusty katalog>`, konta `tester` (pierwsze = admin) i `ola` oraz serwer z kanałem `pogaduchy`. Potem:

```
GC_TEST_URL=http://127.0.0.1:8893 GC_TEST_PASS=… dotnet test tests/GibCord.Core.Tests --filter LiveTests
```

Serwer pozwala na 10 logowań na minutę z jednego adresu.

## Budowanie

- Na Windowsie: `dotnet publish src/GibCord.App -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish`
- Na Linuksie (tylko sprawdzenie kodu): `tools/kompiluj.sh` i `dotnet test tests/GibCord.Core.Tests`
- GitHub Actions (`.github/workflows/build.yml`) buduje przy każdym pushu, robi zrzuty ekranu z trybu pokazowego (`GibCord.exe --demo --view chat --screenshot plik.png`) i instalator. Tag `v6.0.0-beta.N` publikuje wydanie z automatycznymi aktualizacjami.

Dane aplikacji: `%LocalAppData%\GibCord6-dane` (sesja zaszyfrowana kluczem konta Windows, pamięć podręczna, ustawienia, dziennik).
Czcionka Sora: licencja SIL OFL (`src/GibCord.App/Assets/Fonts/OFL.txt`).
