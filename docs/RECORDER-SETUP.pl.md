# Konfiguracja nagrywarki i rozwiązywanie problemów

*English version: [RECORDER-SETUP.md](RECORDER-SETUP.md)*

AMG DIGA Archive znajduje nagrywarkę w sieci domowej i zapisuje nagrania, które nagrywarka udostępnia. Ta strona wyjaśnia, co muszą zapewnić nagrywarka i sieć, jak to włączyć i co zrobić, gdy nagrywarka nie zostaje znaleziona, nie daje się otworzyć albo przerywa w połowie nagrania. Aplikacja otwiera tę stronę z sekcji **Jeśli nagrywarka nie została znaleziona** na stronie **Połącz**.

**Na ile to zostało sprawdzone.** Projekt nie ma własnej nagrywarki. Testy automatyczne sprawdzają kod sieciowy na symulowanych nagrywarkach. Z prawdziwego sprzętu pochodzi jedno zgłoszenie dotyczące aplikacji: właściciel nagrywarki zgłoszonej jako DMR-BS850 potwierdził 2 października 2026 r., że wersja 0.5.2 znalazła nagrywarkę, otworzyła jej foldery i zapisała nagrania, zarówno jako dokładne kopie (`.mpg`), jak i w pliku MKV. W poprzednich dniach ten sam właściciel przysłał raporty [zestawu diagnostycznego](#diagnostics); wspominamy o nich tam, gdzie coś wyjaśniają. Wersje 0.6.0 i 0.6.1 przeszły całą drogę od początku do końca wyłącznie z emulatorem nagrywarki należącym do projektu. Wszystko, co ta strona mówi o menu nagrywarki, pochodzi z instrukcji obsługi Panasonic w brzmieniu z 5 października 2026 r.; projekt sam nie wypróbował żadnego z tych ustawień. To, co mówi o routerach, programach VPN i systemie Windows, to ogólne wskazówki i dokumentacja Microsoftu; w takich sytuacjach projekt również nie testował aplikacji. Jeśli coś tutaj nie zgadza się z tym, co widzisz, [daj nam znać](#reporting).

**Jak czytać tę stronę.** Nazwy **pogrubione** wyświetla aplikacja. Pogrubione słowa na początku akapitu lub punktu listy to tylko śródtytuły. Nazwy w „cudzysłowie” wyświetla coś innego: nagrywarka, system Windows albo formularz zgłoszenia w serwisie GitHub. Nazwy z menu nagrywarki podajemy po angielsku, tak jak drukują je angielskie instrukcje obsługi Panasonic. Polskich wersji tych instrukcji nie znaleźliśmy, a nagrywarka ustawiona na inny język pokazuje nazwy przetłumaczone. Nazwy ustawień systemu Windows podajemy za polskimi stronami pomocy Microsoftu, które nie zawsze nazywają ustawienia dokładnie tak jak sam system. Tam, gdzie komunikat aplikacji zawiera liczbę lub nazwę, piszemy w tym miejscu „…”.

## Spis treści

- [Czego potrzebuje aplikacja](#what-is-needed)
- [Włączanie serwera w nagrywarce](#recorder-settings)
- [Sieć domowa](#home-network)
- [Co nagrywarka udostępnia, a czego nie](#what-the-recorder-offers)
- [Powolne odpowiedzi i przerwane pobieranie](#time-limits)
- [Zestaw diagnostyczny](#diagnostics)
- [Zgłaszanie nagrywarki](#reporting)
- [Rozwiązywanie problemów według objawów](#troubleshooting)

<a name="what-is-needed"></a>
## Czego potrzebuje aplikacja

Aplikacja rozmawia z nagrywarką tak, jak robiłby to telewizor w innym pokoju. Prosi nagrywarkę o listę nagrań, a potem pobiera te, które zaznaczysz. Standard, który to umożliwia, nazywa się DLNA. Instrukcje Panasonic opisują tę funkcję jako odtwarzanie nagrań na innych urządzeniach; o zapisywaniu ich na komputerze nie mówią. Aplikacja korzysta z tej samej funkcji. Nigdy niczego nie zapisuje na nagrywarce i nie potrzebuje do niej hasła.

Muszą być spełnione cztery warunki:

1. Nagrywarka jest włączona.
2. Nagrywarka i komputer są podłączone do tej samej sieci domowej.
3. W nagrywarce jest włączona funkcja serwera. Zależnie od modelu Panasonic nazywa ją „Home Network function” albo „Server ( DLNA ) function”.
4. Nagrywarka pozwala temu komputerowi się połączyć. Niektóre modele dopuszczają każde urządzenie w sieci domowej, inne wymagają wcześniejszego zarejestrowania każdego urządzenia.

To samo mówi sekcja **Jeśli nagrywarka nie została znaleziona** na stronie **Połącz**. Otwiera się sama po wyszukiwaniu, które niczego nie znalazło.

![Strona Połącz w aplikacji AMG DIGA Archive: wyszukiwanie nagrywarek, a pod nim sekcja na wypadek, gdy nagrywarka nie została znaleziona](images/pl/02-connect.png)

| Aplikacja podaje | Więcej |
|---|---|
| **Włącz nagrywarkę. W trybie czuwania nagrywarka nie odpowiada.** | [Tryb czuwania](#standby) |
| **W ustawieniach sieciowych nagrywarki włącz serwer sieciowy (DLNA), a potem wyjdź z menu ustawień.** | [Włączanie serwera w nagrywarce](#recorder-settings) |
| **Podłącz nagrywarkę i ten komputer do tego samego routera. Sieć Wi-Fi dla gości albo VPN na komputerze rozdzielają je.** | [Sieć domowa](#home-network) |
| **Zatrzymaj inne urządzenia odtwarzające z nagrywarki i poczekaj, aż nagrywarka skończy nagrywanie lub kopiowanie.** | [Gdy nagrywarka jest zajęta](#busy) |

O czwartym warunku, czyli rejestracji komputera, aplikacja nie wspomina. Opisujemy go w części [Włączanie serwera w nagrywarce](#recorder-settings).

Po każdej zmianie ponownie wybierz **Znajdź nagrywarki w sieci**. Wyszukiwanie trwa zwykle kilka sekund, a nigdy dłużej niż 23. Możesz je powtarzać dowolnie często.

Gdy wyszukiwanie znajdzie urządzenia, aplikacja pokaże każde z nazwą, modelem i adresem, a przycisk wyszukiwania zmieni nazwę na **Szukaj ponownie**. Na wyszukiwanie odpowiada każdy serwer multimediów w sieci domowej, więc obok nagrywarki na liście może się znaleźć telewizor, dysk sieciowy albo inny komputer. Wybierz nagrywarkę, a potem **Połącz z nagrywarką**. Jeśli na wyszukiwanie odpowie dokładnie jedno urządzenie i przedstawia się ono jako DIGA, aplikacja połączy się z nim od razu, o ile na stronie **Ustawienia** jest zaznaczona opcja **Prowadź mnie do kolejnego kroku**.

<a name="recorder-settings"></a>
## Włączanie serwera w nagrywarce

### Skąd pochodzą te informacje

Projekt nie obsługiwał menu żadnej nagrywarki. Poniższe informacje pochodzą z angielskich instrukcji obsługi Panasonic dla czterech generacji modeli europejskich, odczytanych 5 października 2026 r. Nazwy w menu różnią się między modelami i rocznikami. Korzystaj z instrukcji swojego modelu; podane niżej numery stron pomogą znaleźć w niej odpowiedni rozdział.

| Instrukcja obsługi | Modele wymienione na okładce | Wykorzystane strony |
|---|---|---|
| [RQT9434-L](https://tda.panasonic-europe-service.com/docs/1524838079-6011-FAEB2CDE788885D0490B6F15271A8D97AF771E13/tsn2/data/ALL/DMRBS850/OI/836579/rqt9434-l.pdf) | DMR-BS850, DMR-BS750 (EG) | 18, 79, 94–97, 103 |
| [SQT1119-2](https://tda.panasonic-europe-service.com/GetDoc.aspx?did=248902&lang=en&fmt=pdf) | DMR-BWT850 (EB, model brytyjski) | 16, 19, 20, 35, 60, 70, 76, 78, 79, 90 |
| [TQBS0024](https://tda.panasonic-europe-service.com/docs/2z660fdc4cz3z3e60bz656ez706466z25za9ef229c516e7d7feed6604376a326039e06e3bd/tsn3/data/ALL/DMRBCT765EG/OI/954751/BST_BCT765_760EG_full_eng_TQBS0024.pdf) | DMR-BCT765, DMR-BST765, DMR-BCT760, DMR-BST760 (EG) | 15, 18, 19, 74, 84, 92–95, 101 |
| [TQBS0033](https://tda.panasonic-europe-service.com/docs/2z68442d83z3z3e60fz656ez706466z24z91ffe9d8b044bad009dfc98681991493a8f05dab/tsn3/data/ALL/DMRUBC90EG/OI/994830/UBC_UBS90EG_full_eng_TQBS0033.pdf) | DMR-UBC90, DMR-UBS90 (EG) | 19, 22, 23, 78, 88, 98–101, 109 |

Odnośniki prowadzą do europejskiego serwera dokumentów Panasonic. Jeśli któryś przestał działać, wyszukaj na stronach pomocy Panasonic numer dokumentu z pierwszej kolumny.

### Nagrywarki z ustawieniem „Home Network function”

Instrukcje obsługi Panasonic dla modelu DMR-BWT850, dla rodziny DMR-BCT765 oraz dla modeli DMR-UBC90 i DMR-UBS90 opisują tę samą grupę ustawień.

1. Naciśnij na pilocie przycisk [FUNCTION MENU]. W pozycji „Setup” wybierz „Basic Settings”.
2. W menu „Network” otwórz „Home Network Settings”.
3. Ustaw „Home Network function” na „On”. Według instrukcji to ustawienie włącza i wyłącza serwer DLNA nagrywarki.
4. Sprawdź „Registration type for remote devices”. Przy ustawieniu „Automatic” połączyć się może każde urządzenie w tej samej sieci. Przy ustawieniu „Manual” mogą to zrobić tylko urządzenia zarejestrowane: otwórz „Remote device list”, wybierz ten komputer po nazwie urządzenia albo po adresie MAC i potwierdź, wybierając „Yes”. Instrukcje podają, że zarejestrować można najwyżej 16 urządzeń.
5. Wyjdź z menu. Instrukcje wymieniają wyświetlanie menu „Basic Settings” wśród sytuacji, w których odtwarzanie przez sieć może nie działać.

Te same instrukcje dodają:

- Gdy „Home Network function” jest ustawione na „On”, dostęp do nagrywarki ma każde urządzenie w tej samej sieci. Panasonic prosi, aby upewnić się, że router jest zabezpieczony przed dostępem osób obcych.
- Przy połączeniu bezprzewodowym funkcji nie da się ustawić na „On”, jeśli połączenie z routerem nie jest szyfrowane.
- „Setting device name” zmienia nazwę, pod którą nagrywarka jest widoczna w sieci. Aplikacja pokazuje każde urządzenie pod nazwą, którą ono samo podaje.
- „Conversion Setting for DLNA” ustawione na „On” obniża jakość obrazu przy odtwarzaniu na innych urządzeniach, żeby obraz się nie zacinał. „Resolution Setting for DLNA” wybiera używaną wtedy jakość. Dla aplikacji ma to znaczenie: zobacz [Co nagrywarka udostępnia](#what-the-recorder-offers).
- Instrukcje modeli DMR-BWT850 i DMR-UBC90 podają, że części funkcji sieci domowej nie da się używać, gdy „Audio Description” jest ustawione na „Automatic”, i że należy je wtedy ustawić na „Off”.

Nie wiadomo, czy w tych modelach komputer niezarejestrowany jest odrzucany ani jak wtedy zachowuje się aplikacja.

### DMR-BS850 i DMR-BS750

1. Naciśnij [FUNCTION MENU]. Wybierz „Others”, potem „Setup”, potem „Network Settings”.
2. Instrukcja obsługi Panasonic dla modelu DMR-BS850 opisuje tam dwa ustawienia. „Home Network ( DLNA ) Settings” dotyczy innych urządzeń Panasonic. „Server ( DLNA ) Settings” dotyczy urządzeń innych producentów, a do tej grupy należy komputer.
3. Otwórz „Server ( DLNA ) Settings”. Na ekranie widać „Server ( DLNA ) function” oraz listę zatytułowaną „MAC Address”. Kroki podane w instrukcji: wybierz adres MAC urządzenia, które ma mieć dostęp, naciśnij [OK] i potwierdź, wybierając „Yes”. Zarejestrować można do czterech urządzeń, a lista pokazuje do dwunastu adresów. Instrukcja nie wymienia osobnego kroku, który ustawia „Server ( DLNA ) function” na „On”; na jej ilustracji funkcja ma wartość „Off”. Na koniec sprawdź, czy jest ustawiona na „On”.
4. Wyjdź z menu Setup. Instrukcja podaje, że odtwarzanie przez sieć nie jest możliwe, gdy menu Setup jest wyświetlane.

Co warto wiedzieć o tym modelu:

- Instrukcja nie mówi, kiedy urządzenie pojawia się na liście „MAC Address”. Jeśli komputera tam nie ma, nie wiadomo, co sprawia, że się pojawi. Rozsądnie jest spróbować tak: wybierz raz w aplikacji **Znajdź nagrywarki w sieci**, żeby komputer wysłał coś do sieci, a potem ponownie otwórz listę w nagrywarce. Nie zostało to sprawdzone.
- Instrukcja opisuje wyłącznie połączenie kablem LAN i prosi, aby drugie urządzenie było podłączone do tego samego koncentratora lub routera co nagrywarka.
- Instrukcja podaje, że funkcji „Power Save” nie da się włączyć, gdy włączone jest jedno z dwóch ustawień DLNA.
- W notatkach projektu jest jedna obserwacja dotycząca rejestracji; pochodzi z nagrywarki zgłoszonej jako DMR-BS850. Raport zestawu diagnostycznego z 30 września 2026 r. pokazał, że nagrywarka została znaleziona i przedstawiła się, ale na prośbę o listę odpowiedziała odmową: kodem HTTP 403. Po tym, jak właściciel poinformował o zarejestrowaniu adresu MAC komputera, następny raport, z 1 października 2026 r., nie zawierał już kodu HTTP 403. Zawierał inną odmowę: kod HTTP 412. Drugi raport z tego samego dnia pokazał ponownie kod HTTP 412, a zaraz potem kod HTTP 200 dla tej samej prośby wysłanej w innej postaci, czyli tej, której aplikacja używa od wersji 0.4.3 (zobacz część [What has been seen on real hardware](DLNA-DIAGNOSTICS.md#what-has-been-seen-on-real-hardware) dokumentu o zestawie diagnostycznym, po angielsku). Zgadza się to z przypuszczeniem, że odmowę z kodem HTTP 403 usunęła rejestracja, ale tego nie dowodzi. Pliku pierwszego raportu nie zachowano, więc informacja o kodzie HTTP 403 opiera się wyłącznie na notatkach. Jeśli aplikacja pokaże **Nagrywarka zwróciła kod HTTP 403.**, najpierw sprawdź rejestrację.

### Jak znaleźć adres MAC komputera

Adres MAC to stały numer karty sieciowej, zapisywany jako sześć par znaków, na przykład `00-1A-2B-3C-4D-5E`. Jest potrzebny tylko wtedy, gdy nagrywarka każe wybrać komputer z listy adresów.

1. Naciśnij klawisz Windows, wpisz `cmd` i naciśnij Enter.
2. Wpisz `getmac /v` i naciśnij Enter. Według dokumentacji Microsoftu to polecenie zwraca adres MAC każdej karty sieciowej ([getmac](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/getmac), strona po angielsku).
3. Użyj adresu z wiersza tego połączenia, z którego naprawdę korzystasz: Wi-Fi albo Ethernet.

System Windows może pokazywać sieciom Wi-Fi inny, losowy adres. Microsoft opisuje to ustawienie na stronie o [losowych adresach sprzętowych](https://support.microsoft.com/pl-pl/windows/how-and-why-to-use-random-hardware-addresses-in-windows-060ad2e9-526e-4f1c-9f3d-fe6a842640ed): w Ustawieniach systemu, w części poświęconej sieci i Internetowi, pod „Wi-Fi”, jako „Losowe adresy sprzętowe”, a dla pojedynczej sieci pod „Zarządzaj znanymi sieciami”. Jeśli jest ono włączone dla sieci domowej, nagrywarka widzi adres losowy, a nie własny adres karty. Jeśli rejestracja według adresu MAC działa przez jakiś czas, a potem przestaje, zacznij od sprawdzenia tego ustawienia.

<a name="standby"></a>
### Tryb czuwania

Aplikacja prosi, żeby nagrywarkę włączyć, i to jest pewny sposób. Instrukcje Panasonic mówią więcej, zależnie od modelu:

- **DMR-BS850, DMR-BS750.** Instrukcja nie mówi, czy nagrania można odtwarzać przez sieć, gdy nagrywarka jest w trybie czuwania. Podaje tylko, że „Power Save” nie może być włączone, gdy włączone jest ustawienie DLNA.
- **DMR-BWT850, rodzina DMR-BCT765, DMR-UBC90 i DMR-UBS90.** Ustawienie „Home Network function” na „On” blokuje „Quick Start” w pozycji „On” (w „Standby Settings”, w grupie „Others”). Gdy oba są włączone, nagrywarka, jak podają instrukcje, może działać jako serwer DLNA nawet wtedy, gdy jest wyłączona. Osobne ustawienie, „Networked Standby”, pozwala urządzeniu sieciowemu obudzić nagrywarkę komunikatem budzącym. Instrukcje wiążą jej działanie jako serwera w trybie czuwania z nagrywarkami Panasonic w roli klienta, a instrukcja modelu DMR-BWT850 zaleca pozostawienie tego ustawienia w pozycji „Off”.

Aplikacja nie wysyła komunikatu budzącego. Na żadnym modelu nie sprawdzono, czy nagrywarka w trybie czuwania odpowiada aplikacji. Jeśli nagrywarka nie zostaje znaleziona, włącz ją, odczekaj minutę i wyszukaj ponownie.

Instrukcje opisują też „Automatic Standby”: ustawienie, które przełącza nagrywarkę w tryb czuwania po określonym czasie bez obsługi (w DMR-BS850: po dwóch, czterech albo sześciu godzinach, albo „Off”). Nie mówią, czy udostępnianie nagrań przez sieć liczy się jako obsługa. Jeśli długie sesje zawsze urywają się po tym samym czasie, sprawdź to ustawienie.

<a name="busy"></a>
### Gdy nagrywarka jest zajęta

Sieć nagrywarka obsługuje obok swoich głównych zadań, a instrukcje Panasonic wymieniają sytuacje, w których tego nie robi:

- **Tylko jedno urządzenie sieciowe naraz (DMR-BS850).** Instrukcja podaje, że dwa urządzenia DLNA lub więcej nie mogą odtwarzać z nagrywarki jednocześnie. Zanim użyjesz aplikacji, zatrzymaj odtwarzanie na telewizorach, tabletach i innych odtwarzaczach.
- **Nagrywanie i kopiowanie.** Instrukcja DMR-BS850 wyklucza odtwarzanie przez sieć podczas jednoczesnego nagrywania dwóch programów. Ta instrukcja i instrukcja DMR-BWT850 wymieniają szybkie kopiowanie trwające równocześnie z nagrywaniem. Instrukcje rodziny DMR-BCT765 i modelu DMR-UBC90 wymieniają kopiowanie w trybie „Copy (Keep Picture Quality)” trwające równocześnie z nagrywaniem.
- **Odtwarzanie płyty.** Instrukcja DMR-BS850 wymienia odtwarzanie dowolnej płyty, a nowsze instrukcje odtwarzanie płyty BD-Video.
- **Menu i usługi internetowe.** Odtwarzanie przez sieć może nie działać, gdy na ekranie jest menu Setup (w nowszych modelach: menu „Basic Settings”) albo gdy nagrywarka korzysta z usługi internetowej, takiej jak „VIERA CAST” lub „Network Service”.
- **Trwające nagrywanie.** Instrukcja DMR-BS850 podaje, że programu, który jest właśnie nagrywany, nie da się odtwarzać przez sieć.
- **Nowsze modele.** Instrukcja DMR-UBC90 dodaje, że nagrywarka nie może działać jako serwer podczas oglądania lub odtwarzania programu 4K. Instrukcje rodziny DMR-BCT765 i modelu DMR-UBC90 opisują funkcję „DVB-via-IP Server”, której nie da się używać jednocześnie z pozostałymi funkcjami sieciowymi; o tym, która ma pierwszeństwo, decyduje „Network Function Priority”.

### Inne modele

W instrukcji obsługi swojego modelu szukaj słów „DLNA”, „Home Network”, „Server” i „Remote device” albo ich odpowiedników w języku instrukcji. Rozdział o ustawieniach sieciowych wskazuje przełącznik, a rozdział o odtwarzaniu na innych urządzeniach wymienia ograniczenia. Jeśli Twój model działa albo wymaga kroku, którego tu nie opisano, [zgłoszenie nagrywarki](#reporting) pomoże kolejnym właścicielom tego modelu.

<a name="home-network"></a>
## Sieć domowa

### Ten sam router

Nagrywarka i komputer muszą dostawać adresy sieciowe od tego samego routera. Komunikat wyszukiwania wysyłany przez aplikację nie przechodzi z jednej sieci do drugiej.

Dwie sieci łatwo mieć, nie wiedząc o tym:

- drugi router albo punkt dostępowy Wi-Fi pracujący jako router, podłączony za urządzeniem od dostawcy Internetu, przy czym nagrywarka jest wpięta do jednego, a komputer połączony z drugim;
- komputer połączony z hotspotem w telefonie albo z siecią sąsiada;
- sieć dla gości (o niej niżej).

Żeby porównać adresy, zajrzyj do ustawień sieciowych nagrywarki („IP Address / DNS Settings” w instrukcjach wszystkich czterech generacji) i sprawdź adres komputera. Strona pomocy Microsoftu o [podstawowych ustawieniach sieci w systemie Windows](https://support.microsoft.com/pl-pl/windows/essential-network-settings-and-tasks-in-windows-f21a9bbc-c582-55cd-35e0-73431160a1b9) pokazuje, gdzie go znaleźć: w Ustawieniach systemu, w części poświęconej sieci i Internetowi, po wybraniu połączenia, w pozycji „Adres IPv4”. W większości sieci domowych pierwsze trzy liczby są na wszystkich urządzeniach takie same, na przykład `192.168.1.20` i `192.168.1.37`. W modelu DMR-BS850 to samo menu nagrywarki zawiera „Connection Test”, który pokazuje, czy sama nagrywarka ma połączenie.

### Wi-Fi czy kabel

- **Nagrywarka.** Instrukcja DMR-BS850 opisuje tylko kabel LAN. Nowsze instrukcje opisują i kabel, i Wi-Fi. Przy słabym sygnale Wi-Fi radzą przestawić router albo przejść na kabel, a do odtwarzania przez sieć zalecają sieć domową o szybkości co najmniej 20 Mb/s.
- **Komputer.** Działa jedno i drugie, o ile router przekazuje ruch między siecią Wi-Fi a gniazdami kablowymi. Routery domowe zwykle to robią.

Nagrania to duże pliki, często po kilka gigabajtów; aplikacja pokazuje na liście rozmiar każdego nagrania. Kabel jest szybszy i rzadziej się rozłącza.

Jeśli nagrywarka zostaje znaleziona, gdy komputer jest podłączony kablem, a nie zostaje, gdy korzysta z Wi-Fi, to urządzenia Wi-Fi najpewniej nie przepuszczają komunikatu wyszukiwania. Niektóre punkty dostępowe, wzmacniacze sygnału i systemy mesh mają do tego ustawienie, zwykle ze słowem „multicast” w nazwie. Jeśli nie możesz go zmienić, zapytaj nagrywarkę bezpośrednio: w sekcji **Jeśli nagrywarka nie została znaleziona** na stronie **Połącz** wpisz adres nagrywarki w polu **Adres nagrywarki (opcjonalnie)**, w postaci czterech liczb, na przykład `192.168.1.40`, i wybierz **Odpytaj ten adres**. Adres widać w ustawieniach sieciowych nagrywarki; akapit wyżej mówi, gdzie ich szukać. Przyjmowany jest tylko adres z sieci domowej. Jeśli nagrywarka odpowie, pojawia się na liście i zostaje na niej wskazana, a Ty wybierasz **Połącz z nagrywarką**. Ten sposób pytania wypróbowano wyłącznie na urządzeniach symulowanych, nie na prawdziwej nagrywarce. Pomaga on tylko wtedy, gdy sieć przepuszcza zwykły ruch między komputerem a nagrywarką; sieć dla gości albo izolacja klientów, opisane niżej, blokują także ten ruch.

### Sieci dla gości i izolacja klientów

Sieć Wi-Fi dla gości daje odwiedzającym Internet i ukrywa przed nimi urządzenia domowe. Komputer w sieci dla gości nie znajdzie nagrywarki. Połącz komputer z siecią główną.

Niektóre routery potrafią w ten sam sposób odseparować każde urządzenie Wi-Fi. Ustawienie nazywa się izolacją klientów, izolacją AP albo podobnie. Wyłącz je dla sieci, z której korzysta komputer.

### Programy VPN

VPN na komputerze może kierować do swojego tunelu cały ruch, także ten przeznaczony dla urządzeń domowych, albo blokować sieć domową na czas połączenia. Na czas korzystania z aplikacji rozłącz VPN. Niektóre programy VPN mają zamiast tego ustawienie zezwalające na dostęp do sieci lokalnej. Na laptopie służbowym często decyduje o tym pracodawca.

### Windows: zapora i profil sieci

Aplikacja wyłącznie sama zaczyna rozmowy. Nigdy nie czeka, aż inne urządzenie odezwie się pierwsze. Odpowiedzi na wyszukiwanie przychodzą w ciągu trzech sekund, przez które aplikacja nasłuchuje.

- **Zapora.** Według dokumentacji Microsoftu Zapora systemu Windows blokuje ruch przychodzący, chyba że jest on odpowiedzią na coś, o co komputer prosił, albo zezwala na niego reguła, a ruch wychodzący przepuszcza ([Windows Firewall overview](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/), strona po angielsku). Domyślnie nie blokuje odpowiedzi na komunikat multiemisji (multicast), który komputer właśnie wysłał (ustawienie `DisableUnicastResponsesToMulticastBroadcast` opisane w dokumencie [Firewall CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/firewall-csp), po angielsku). Na komputerze, którego zaporą zarządza organizacja, to ustawienie domyślne mogło zostać zmienione.
- **Pytanie zapory.** System Windows może zapytać, czy aplikacja ma prawo komunikować się w sieci. Projekt nie ustalił, czy pyta o tę aplikację. Jeśli zapyta, zezwól. Według strony Microsoftu o [regułach zapory](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules) (po angielsku) odpowiedź odmowna albo anulowanie pytania sprawia, że system tworzy dla aplikacji reguły blokujące. Tak samo dzieje się bez względu na odpowiedź, gdy konto nie ma uprawnień administratora. Reguły pozostają, dopóki ktoś ich nie usunie. Jeśli podejrzewasz, że tak się stało, otwórz aplikację „Zabezpieczenia Windows”, wybierz „Zapora i ochrona sieci”, a potem odnośnik do listy aplikacji, którym zezwolono na dostęp przez zaporę, i poszukaj wpisu tej aplikacji. Jej plik programu to `Diga.exe`.
- **Profil sieci.** System Windows traktuje każdą sieć jako publiczną albo prywatną. Strona pomocy Microsoftu podaje, że przy pierwszym połączeniu sieć jest publiczna, że jest to ustawienie zalecane także w domu i że komputer jest wtedy ukryty przed innymi urządzeniami; przy ustawieniu prywatnym inne urządzenia w sieci mogą komputer znaleźć ([podstawowe ustawienia sieci w systemie Windows](https://support.microsoft.com/pl-pl/windows/essential-network-settings-and-tasks-in-windows-f21a9bbc-c582-55cd-35e0-73431160a1b9)). Aplikacja nie musi być znajdowana: sama pyta i nasłuchuje odpowiedzi. Według dokumentacji Microsoftu ustawienie publiczne nie powinno więc przeszkadzać w wyszukiwaniu. Projekt nie testował żadnego z tych ustawień. Jeśli wyszukiwanie niczego nie znajduje, choć wszystko inne się zgadza, możesz wypróbować dla sieci domowej ustawienie prywatne: w Ustawieniach systemu, w części poświęconej sieci i Internetowi, we właściwościach połączenia, w pozycji „Typ profilu sieci”. Strona Microsoftu prosi, aby używać go tylko w sieci, w której znasz osoby i urządzenia i masz do nich zaufanie.
- **Inne programy zabezpieczające.** Pakiet zabezpieczający z własną zaporą może blokować odpowiedzi na wyszukiwanie. Poszukaj w nim ustawienia dotyczącego sieci lokalnej lub zaufanej.

Aplikacja nie dodaje reguł zapory i nie zmienia ustawień systemu Windows ani routera.

<a name="network-details"></a>
### Co dokładnie aplikacja robi w sieci

Ta lista jest dla osób, które konfigurują routery lub zapory. Opisuje kod w `src/Diga.Core/Dlna`.

- Nic nie jest wysyłane, dopóki nie wybierzesz **Znajdź nagrywarki w sieci**. Aplikacja sama o tym informuje: **Wyszukiwanie obejmuje tylko sieć domową i rozpoczyna się dopiero po wybraniu przycisku „Znajdź nagrywarki w sieci”.**
- **Wyszukiwanie.** Z każdego aktywnego adresu IPv4 komputera (karty włączone i obsługujące multiemisję, bez karty pętli zwrotnej, najwyżej 16 adresów) aplikacja wysyła po UDP dwa komunikaty wyszukiwania SSDP (`M-SEARCH`) na adres multiemisji `239.255.255.250`, port `1900`. Jeden pyta o `urn:schemas-upnp-org:device:MediaServer:1`, drugi o `urn:schemas-upnp-org:service:ContentDirectory:1`. Mają czas życia (TTL) równy 1, więc nie przechodzą przez router. Potem aplikacja przez trzy sekundy nasłuchuje na porcie, z którego wysyłała; ten port wybiera system Windows. Wyszukiwanie używa wyłącznie IPv4.
- **Odpowiedzi.** Odpowiedź jest brana pod uwagę tylko wtedy, gdy jej pierwszy wiersz to `HTTP/1.1 200 OK` i gdy zawiera dokładnie jeden nagłówek `LOCATION` z adresem `http` lub `https`, którego hostem jest adres IPv4 nadawcy odpowiedzi. Odpowiedź, która wskazuje nadawcę nazwą hosta, jest pomijana. Odczytywanych jest najwyżej 256 odpowiedzi.
- **Opis urządzenia.** Aplikacja pobiera każdy adres `LOCATION` żądaniem HTTP `GET`, dla najwyżej 64 adresów, najwyżej 8 od jednego odpowiadającego adresu, po 16 naraz. Urządzenie trafia na listę, jeśli jego opis zawiera usługę UPnP ContentDirectory, której adres sterowania jest na tym samym hoście.
- **Listy.** Aby otworzyć nagrywarkę lub folder, aplikacja wysyła na ten adres sterowania żądania HTTP `POST` (akcja SOAP `Browse`, po 100 pozycji na żądanie).
- **Nagrania.** Nagranie jest pobierane jednym żądaniem HTTP `GET` z adresu na tym samym hoście co opis; port może być inny. Aplikacja nie prosi o fragmenty pliku (`Range`).
- **Dokąd trafia ruch.** Cały ruch idzie prosto na adres nagrywarki. Aplikacja nie używa serwera proxy i nie wysyła danych logowania ani plików cookie. Przy opisach i listach nie podąża za przekierowaniami; przy pobieraniu podąża najwyżej za trzema, w obrębie tego samego hosta.
- **Tylko adresy prywatne.** Aplikacja łączy się wyłącznie z adresami `10.x.x.x`, od `172.16.x.x` do `172.31.x.x`, `192.168.x.x` i `169.254.x.x`. Sieć domowa używająca innych adresów, na przykład `100.64.x.x`, nie jest obsługiwana.
- **Porty.** Porty opisu, list i nagrań wybiera nagrywarka i podaje je w swoich odpowiedziach. Stały jest tylko port wyszukiwania, UDP 1900.
- Aplikacja nie skanuje adresów ani portów.

<a name="what-the-recorder-offers"></a>
## Co nagrywarka udostępnia, a czego nie

![Strona Nagrania: folder nagrywarki z nagraniami, a przy każdym wiersz informujący, czy można je zapisać](images/pl/03-discover.png)

Przy każdym nagraniu lista nagrywarki podaje, w jakich wersjach nagrywarka udostępnia je urządzeniom sieciowym. Każdą wersję nagrywarka oznacza: chroniona albo nie, przekonwertowana albo nie. Aplikacja odczytuje te oznaczenia i na stronie **Nagrania** pokazuje przy każdym nagraniu jeden wiersz.

| Aplikacja pokazuje | Co zadeklarowała nagrywarka | Co możesz zrobić |
|---|---|---|
| **Można zapisać** | Udostępnia wersję i deklaruje, że nie jest ona przekonwertowana. | Zaznacz i zapisz. |
| **Można zapisać · nagrywarka nie podaje, czy to wersja oryginalna** | Udostępnia wersję, nie podając, czy jest przekonwertowana. | Zaznacz i zapisz. W szczegółach technicznych zapisanego pliku zostanie odnotowane, że nagrywarka tego nie podała. |
| **Chronione przed kopiowaniem · nie można zapisać** | Oznacza nagranie jako chronione i nie udostępnia żadnej wersji bez ochrony. | Nic. |
| **Udostępniane tylko w wersji przekonwertowanej · aplikacja takich nie zapisuje** | Jedyna wersja bez ochrony to taka, którą nagrywarka konwertuje podczas wysyłania. | W modelach, które mają „Conversion Setting for DLNA”, sprawdź, czy jest ustawione na „Off”, a potem wybierz **Odśwież folder**. Nie zostało to sprawdzone. |
| **Nagrywarka nie udostępnia tego nagrania do pobrania** | Pozycja nie ma żadnej wersji, którą da się pobrać z adresu nagrywarki przez sieć domową. | Spróbuj później, na przykład po zakończeniu trwającego nagrywania, i wybierz **Odśwież folder**. To również nie zostało sprawdzone. |

Nagrań, których nie można zapisać, nie da się zaznaczyć. Są wymienione pod listą, pod wierszem kończącym się słowami **w tym folderze nie można zapisać:**, każde z powodem. Na stronie jest też informacja: **Nagrań chronionych przed kopiowaniem oraz nagrań, które nagrywarka udostępnia wyłącznie w wersji przekonwertowanej, nie można zapisać.**

### Dlaczego aplikacja nie może tego zmienić

- **Lista należy do nagrywarki.** Aplikacja może poprosić tylko o to, co jest na liście. Żadne ustawienie aplikacji nie sprawi, że nagrywarka pokaże więcej.
- **Nagrania chronione.** Nagranie chronione nagrywarka wysyła w postaci zaszyfrowanej, przeznaczonej dla urządzeń mających licencję na jego odtwarzanie. Aplikacja takiej licencji nie ma i nie zawiera niczego, co odszyfrowuje. To świadoma decyzja i to się nie zmieni.
- **Wersje przekonwertowane.** Wersja przekonwertowana to nowe kodowanie, które nagrywarka tworzy podczas wysyłania, zwykle w niższej jakości. Aplikacja istnieje po to, żeby zachować obraz i dźwięk w takiej postaci, w jakiej zostały nagrane, dlatego wersji przekonwertowanych nie zapisuje.
- **Instrukcje Panasonic mówią to samo od strony nagrywarki.** Instrukcja DMR-BS850 podaje, że tytułów chronionych prawem autorskim, których kopiowanie jest zabronione, nie da się odtwarzać przez sieć. Nowsze instrukcje podają, że programy nadane przez nadawcę z ograniczeniem dostępu, na przykład z ograniczeniem kopiowania, nie są dostępne dla urządzeń sieciowych, a w rozdziale o rozwiązywaniu problemów dodają tytuły w niezgodnym formacie. Instrukcje rodziny DMR-BCT765 i modelu DMR-UBC90 dodają do tego programy z ochroną treści (opisują ją przy emisjach CI Plus) oraz programy zaszyfrowane. O tym, czy audycję da się zapisać, decydują więc sygnał nadawcy i nagrywarka.

Instrukcja DMR-BWT850 wymienia wśród symboli na liście nagrań samej nagrywarki symbol tytułu, którego nie da się odtworzyć z urządzenia DLNA bez DTCP-IP. DTCP-IP to system ochrony takich nagrań, a aplikacja go nie ma. W modelu, który pokazuje taki symbol, widać więc na telewizorze, których nagrań aplikacja nie pobierze.

Aplikacja sprawdza to jeszcze raz, gdy zaczyna pobieranie. Jeśli odpowiedź nagrywarki mówi wtedy, że zawartość jest chroniona albo przekonwertowana, nagranie nie zostaje zapisane, a komunikat podaje powód, na przykład: **Nagłówki HTTP nagrywarki wskazują chronioną zawartość. Ta aplikacja nie może pobierać nagrań chronionych przez DTCP/DRM.**

Nawet **Można zapisać** opiera się na deklaracji nagrywarki. Mówią o tym szczegóły techniczne każdego zapisanego pliku: **Deklaracji nagrywarki nie da się sprawdzić z zewnątrz: aplikacja nie może porównać pobranego pliku z nagraniem na dysku nagrywarki.**

### Jak aplikacja odczytuje oznaczenia

Ten akapit jest dla osób znających DLNA. Na liście nagrywarki każda wersja nagrania to element `res` z atrybutem `protocolInfo`. Aplikacja uznaje wersję za chronioną, gdy element ma niepusty atrybut `protection` albo gdy `protocolInfo` zawiera `DTCP`, `DRM`, `PLAYREADY` lub `WIDEVINE`. Uznaje wersję za przekonwertowaną, gdy `protocolInfo` zawiera `DLNA.ORG_CI=1`, a za nieprzekonwertowaną, gdy zawiera `DLNA.ORG_CI=0`; znacznik błędnie zapisany albo sprzeczny sam ze sobą liczy się jak konwersja. Brak znacznika oznacza, że nagrywarka „nie podaje”. Zapisać można tylko wersje udostępniane przez `http-get` albo w ogóle bez `protocolInfo`, spod adresu `http` lub `https` na hoście samej nagrywarki. Spośród nich aplikacja wybiera wersję bez ochrony zadeklarowaną jako nieprzekonwertowana przed wersją bez znacznika. Wersja, której adres wskazuje inny host albo nie jest adresem `http` ani `https`, jest pomijana całkowicie. Gdy żadnej wersji nie da się zapisać i żadna nie jest wersją przekonwertowaną bez ochrony, wersja chroniona daje wiersz **Chronione przed kopiowaniem · nie można zapisać**, nawet jeśli jest udostępniana inaczej niż przez `http-get` albo nie ma adresu.

### Nagrania, których nie ma na liście

Aplikacja pokazuje każdą pozycję, którą nagrywarka zwraca dla folderu. Jeśli brakuje nagrania, które widać na telewizorze, to nagrywarka nie umieściła go na liście dla urządzeń sieciowych. Instrukcje Panasonic wymieniają tu programy z ograniczeniem dostępu, a nowsze dodają, że przez sieć nie da się odtwarzać plików, które nie znajdują się na wbudowanym dysku twardym. Otwórz też pozostałe foldery: to nagrywarka decyduje, jak układa nagrania w foldery dla sieci.

<a name="time-limits"></a>
## Powolne odpowiedzi i przerwane pobieranie

Nagrywarka potrafi odpowiadać powoli: tuż po włączeniu, podczas nagrywania albo gdy jest zajęta czymś innym. Nowsze instrukcje Panasonic podają na przykład, że start z trybu czuwania trwa dłużej, gdy „Quick Start” nie jest włączone. Aplikacja czeka, ale nie w nieskończoność, żeby nagrywarka, która zamilkła, nie wstrzymywała pozostałych nagrań.

| Etap | Jak długo aplikacja czeka | Gdy czas minie |
|---|---|---|
| Wyszukiwanie | 3 sekundy na odpowiedzi. Potem 3 sekundy na opis każdego urządzenia. Całe wyszukiwanie kończy się najpóźniej po 23 sekundach. | Urządzenie jest pomijane na liście bez komunikatu. Jeśli nie zostaje żadne: **Nie znaleziono nagrywarki** |
| Otwieranie nagrywarki lub folderu | 3 sekundy na nawiązanie połączenia i 10 sekund na każdą odpowiedź. Nagrywarka przesyła folder w częściach po 100 pozycji; na jeden folder przypadają łącznie najwyżej 2 minuty. | **Nagrywarka nie odpowiedziała w wymaganym czasie. Sprawdź, czy jest włączona i podłączona do sieci, a następnie spróbuj ponownie.** |
| Rozpoczęcie pobierania | Łącznie 30 sekund na to, żeby nagrywarka przyjęła połączenie, na co ma 10 sekund, i zaczęła odpowiadać. | **Nagrywarka nie zwróciła nagłówków HTTP w wymaganym czasie.** |
| W trakcie pobierania | 30 sekund bez żadnych danych. | **Nagrywarka przestała wysyłać dane. Niekompletna kopia została usunięta. Spróbuj ponownie, gdy nagrywarka będzie dostępna.** |

Czas pobierania całego nagrania nie jest ograniczony. Długie nagranie pobiera się tak długo, jak trzeba.

Co się dzieje po przerwaniu:

- Niekompletny plik jest usuwany. W folderze nie zostaje pod nazwą nagrania nic niedokończonego.
- Aplikacja nie wznawia przerwanego pobierania. Następna próba pobiera to nagranie od początku.
- Gdy zaznaczonych jest kilka nagrań, niepowodzenie jednego nie zatrzymuje pozostałych. Po dwóch niepowodzeniach z rzędu aplikacja przestaje, bo wtedy najpewniej zniknęła nagrywarka albo sieć. Komunikat ma nagłówek **Zapisano nagrania: … z …**. Wymienia pierwsze cztery nagrania, których nie udało się zapisać, każde z powodem, i podaje liczbę pozostałych. Dodaje też: **Kolejnych nagrań nie próbowano zapisać (liczba: …), ponieważ dwa z rzędu się nie powiodły.** oraz **Niezapisane nagrania pozostają zaznaczone: wybierz „Zapisz”, aby spróbować ponownie. Zapisane nagrania znajdziesz w Archiwum.**
- **Pobierz i odtwórz podgląd** oraz **Pobierz i pokaż szczegóły** najpierw pobierają całe nagranie, więc obowiązują je te same limity.

Co zrobić: sprawdź na telewizorze, czy nagrywarka jest włączona i czy nie jest otwarte żadne menu, odczekaj minutę lub dwie, na stronie **Nagrania** wybierz **Odśwież folder** i zapisz ponownie.

Podczas pracy aplikacja prosi system Windows, żeby sam nie przechodził w stan uśpienia. Nie dotyczy to uśpienia, które wywołasz samodzielnie, na przykład zamykając pokrywę laptopa.

<a name="diagnostics"></a>
## Zestaw diagnostyczny

Zestaw diagnostyczny to mały skrypt dla systemu Windows. Zadaje nagrywarce te same pierwsze pytania co aplikacja i zapisuje, jak nagrywarka odpowiedziała, w postaci niezawierającej niczego prywatnego. Użyj go, gdy nagrywarka nie zostaje znaleziona mimo spełnienia czterech warunków opisanych wyżej, gdy zostaje znaleziona, ale **Połącz z nagrywarką** kończy się błędem, albo gdy chcesz zgłosić, jak zachowuje się dany model. Pełny opis jest w dokumencie [Diagnostics kit](DLNA-DIAGNOSTICS.md); jest on po angielsku i zawiera krótką instrukcję po polsku.

**Skąd go wziąć.** Każde wydanie ma na [stronie wydań](https://github.com/lukasz-gratkowski/diga-archive/releases) plik o nazwie `DIGA-…-dlna-diagnostics.zip`. Ten sam skrypt jest też częścią aplikacji: w folderze `diagnostics` obok pliku `Diga.exe` (w zainstalowanej kopii to `%LOCALAPPDATA%\Programs\DIGA\diagnostics`, chyba że przy instalacji wybrano inny folder).

**Jak go uruchomić.**

1. Użyj komputera w tej samej sieci domowej co nagrywarka. Plik ZIP pobrany ze strony wydań najpierw rozpakuj w całości do nowego folderu.
2. Włącz nagrywarkę i wyjdź z jej menu.
3. Kliknij dwukrotnie `Run-DlnaDiagnostics.cmd`. Otworzy się okno tekstowe; komunikaty są w nim po angielsku. System Windows może poprosić o potwierdzenie, że chcesz uruchomić pobrany plik.
4. Jeśli skrypt znajdzie kilka urządzeń, wpisz numer nagrywarki i naciśnij Enter. Jeśli znajdzie dokładnie jedno, użyje go bez pytania i bez pokazywania jego nazwy. Jeśli nie znajdzie żadnego, poprosi o `Description URL`. Nie zgaduj go: naciśnij sam Enter.
5. Poczekaj na wiersz zaczynający się od `Share this sanitized ZIP:`. Podaje on plik raportu. Naciśnij dowolny klawisz, żeby zamknąć okno.

**Co robi.** Wysyła wyszukiwanie tego samego rodzaju co aplikacja, odczytuje opisy urządzeń, które odpowiedziały, i raz prosi wybrane urządzenie o najwyższy poziom jego listy (najwyżej 100 pozycji). Jeśli ta prośba się nie powiedzie, wysyła ją jeszcze raz w starszej postaci, dla porównania. Nie otwiera folderów, nie pobiera żadnych nagrań, niczego nie zmienia w nagrywarce ani w systemie Windows i niczego nikomu nie wysyła. Raport zapisuje w folderze `DlnaDiagnostics` obok skryptu.

**Co zawiera raport.** W pliku ZIP są dwa pliki tekstowe: `report.json` i `README.txt`. Zapisują kod HTTP każdego żądania, rozmiary w bajtach, liczbę elementów poszczególnych rodzajów w odpowiedziach, czas trwania każdego żądania i wersję skryptu. Nie zawierają adresów, nazw urządzeń, numerów seryjnych, tytułów nagrań ani samych odpowiedzi nagrywarki. Plik `report.json` możesz przed wysłaniem otworzyć w Notatniku i przeczytać.

**Czego nie powie.** Zagląda tylko na najwyższy poziom listy. Nie pokazuje, czy nagrania w folderach są na liście, czy da się je zapisać ani czy pobieranie doszłoby do końca. Raport nie zawiera nazw, więc nie pokazuje też, które urządzenie odpowiedziało. Jeśli jedynym urządzeniem, które odpowiedziało, był telewizor albo dysk sieciowy, raport opisuje właśnie to urządzenie. Napisz w zgłoszeniu, co pokazała lista w aplikacji.

Raport przydaje się także wtedy, gdy skrypt zakończy się błędem, a nawet gdy nie znajdzie żadnego urządzenia: pokazuje wtedy, czy na wyszukiwanie w ogóle cokolwiek odpowiedziało.

<a name="reporting"></a>
## Zgłaszanie nagrywarki

O tym, które nagrywarki działają, projekt dowiaduje się wyłącznie ze zgłoszeń. Zgłoszenie, że wszystko zadziałało, jest równie cenne jak zgłoszenie błędu.

1. Otwórz [stronę wyboru zgłoszenia](https://github.com/lukasz-gratkowski/diga-archive/issues/new/choose) i wybierz „Recorder report”. Potrzebne jest bezpłatne konto GitHub. Formularz jest po angielsku, ale możesz pisać po polsku.
2. Wypełnij pola „Recorder model and region” (model jest wydrukowany z tyłu nagrywarki, na przykład DMR-BS850EG) i „Application version”.
3. W części „What worked?” zaznacz to, co u Ciebie zadziałało, a resztę opisz w polu „Details”: rodzaj nagrań, to, czego aplikacja odmówiła, wraz z pokazanym komunikatem, oraz sposób sprawdzenia zapisanego pliku.
4. Jeśli coś się nie udało, przeciągnij do pola „Diagnostics” plik ZIP utworzony przez zestaw diagnostyczny. Dołącz go bez zmian.

Nie dołączaj nagrań. Jeśli wolisz nie ujawniać tytułów programów, pomiń je.

Aplikacja prowadzi też na komputerze dziennik błędów: **Ustawienia** > **O aplikacji AMG DIGA Archive** > **Otwórz folder dziennika**. Aplikacja informuje, że w dzienniku mogą się znaleźć nazwy folderów, tytuły nagrań i adres nagrywarki. Przeczytaj go, zanim go dołączysz, albo skopiuj tylko wiersze dotyczące błędu.

<a name="troubleshooting"></a>
## Rozwiązywanie problemów według objawów

### Wyszukiwanie nagrywarki

| Co widzisz | Prawdopodobna przyczyna | Co zrobić |
|---|---|---|
| **Nie znaleziono nagrywarki** oraz **Przejdź przez listę kontrolną poniżej i wyszukaj ponownie.** | Na wyszukiwanie nic nie odpowiedziało: nagrywarka jest wyłączona albo w trybie czuwania, jej funkcja serwera jest wyłączona, jej menu jest otwarte albo komputer jest w innej sieci. | Przejdź przez [cztery warunki](#what-is-needed), a potem ponownie wybierz **Znajdź nagrywarki w sieci**. |
| **Wymagane działanie** z tekstem zaczynającym się od **Ten komputer nie jest połączony z żadną siecią, więc nie można znaleźć nagrywarki.** | Sam komputer nie ma połączenia z siecią. | Połącz komputer z siecią domową kablem lub przez Wi-Fi i wyszukaj ponownie. |
| To samo, mimo spełnienia czterech warunków | Komunikat wyszukiwania albo odpowiedzi na niego nie przechodzą: sieć Wi-Fi dla gości, izolacja klientów, urządzenia Wi-Fi nieprzepuszczające multiemisji, VPN, zapora. | [Sieć domowa](#home-network). Spróbuj podłączyć komputer kablem do tego samego routera. Potem uruchom [zestaw diagnostyczny](#diagnostics). |
| Na liście są urządzenia, ale nie ma nagrywarki | Urządzenia na liście to inne serwery multimediów. Sama nagrywarka nie odpowiedziała albo jej odpowiedzi nie dało się użyć. | Sprawdź ustawienie serwera w nagrywarce ([Włączanie serwera w nagrywarce](#recorder-settings)) i wybierz **Szukaj ponownie**. Zestaw diagnostyczny pokazuje, ile urządzeń odpowiedziało i co zwróciły ich opisy. |
| Nagrywarka była wcześniej znajdowana, a teraz nie jest | Nagrywarka jest w trybie czuwania albo jest zajęta, albo włączony jest VPN. | [Tryb czuwania](#standby), [Gdy nagrywarka jest zajęta](#busy), [Programy VPN](#home-network). |
| Znajdowana, gdy komputer jest podłączony kablem, a przez Wi-Fi nie | Sieć Wi-Fi nie przepuszcza wyszukiwania. | [Wi-Fi czy kabel](#home-network) oraz [Sieci dla gości i izolacja klientów](#home-network). |

### Otwieranie nagrywarki i jej folderów

Gdy czynność się nie powiedzie, aplikacja pokazuje komunikat z nagłówkiem **Wymagane działanie**.

| Co widzisz | Prawdopodobna przyczyna | Co zrobić |
|---|---|---|
| **Nagrywarka nie odpowiedziała w wymaganym czasie. Sprawdź, czy jest włączona i podłączona do sieci, a następnie spróbuj ponownie.** | Nagrywarka została znaleziona, ale potem zamilkła: budzi się, przechodzi w tryb czuwania, pokazuje menu albo jest zajęta. | Odczekaj minutę i spróbuj ponownie. Zobacz [Gdy nagrywarka jest zajęta](#busy). |
| **Nagrywarka zwróciła kod HTTP 403.** | Nagrywarka odmawia temu komputerowi. Projekt zna jeden przypadek: kod HTTP 403 przed zarejestrowaniem komputera w nagrywarce przez właściciela i brak tego kodu po rejestracji. Zgadza się to z przypuszczeniem, że przyczyną był brak rejestracji, ale tego nie dowodzi (zobacz [DMR-BS850 i DMR-BS750](#recorder-settings)). | Zarejestruj komputer w ustawieniach serwera nagrywarki, wyjdź z jej menu i połącz ponownie. |
| Inny komunikat zaczynający się od **Nagrywarka zwróciła kod HTTP** albo komunikat zaczynający się od **Błąd usługi ContentDirectory nagrywarki** lub **Nagrywarka zwróciła pustą odpowiedź** | Nagrywarka odpowiedziała odmową albo odpowiedziała pusto. Może być zajęta albo nie przyjmuje sposobu, w jaki aplikacja pyta. | Zobacz [Gdy nagrywarka jest zajęta](#busy) i spróbuj ponownie. Jeśli to nie pomaga, uruchom [zestaw diagnostyczny](#diagnostics) i [wyślij raport](#reporting). |
| Komunikat zaczynający się od **Nie udało się nawiązać połączenia. Sprawdź, czy komputer ma dostęp do sieci, a w przypadku nagrywarki — czy jest włączona, i spróbuj ponownie. Szczegóły techniczne:**, gdy połączenie zostało zerwane w trakcie żądania, albo **Nie można połączyć się z nagrywarką w sieci lokalnej.** z adresem i portem nagrywarki w nawiasie, gdy połączenia w ogóle nie udało się nawiązać | Nagrywarka odrzuciła połączenie albo była nieosiągalna: po wyszukiwaniu została wyłączona albo zniknęła z sieci, albo komputer stracił połączenie. | Sprawdź oba połączenia i wyszukaj ponownie. |
| **Folder jest pusty lub nagrywarka nie zwróciła żadnych dostępnych pozycji.** | Folder naprawdę jest pusty albo nagrywarka w tej chwili niczego dla niego nie zwróciła. | Otwórz pozostałe foldery. Wybierz **Odśwież folder**, gdy nagrywarka nie jest zajęta. |
| **Zawartość folderu nagrywarki zmieniła się podczas przeglądania. Odśwież folder.** | W trakcie odczytywania listy zaczęło się albo skończyło nagrywanie, albo usunięto nagranie. | Wybierz **Odśwież folder**. |
| Nagranie jest wymienione pod listą z opisem **Chronione przed kopiowaniem · nie można zapisać** | Nagrywarka oznacza je jako chronione. | Nic. Zobacz [Co nagrywarka udostępnia](#what-the-recorder-offers). |
| Nagranie jest wymienione pod listą z opisem **Udostępniane tylko w wersji przekonwertowanej · aplikacja takich nie zapisuje** | Nagrywarka udostępnia tylko wersję przekonwertowaną. | W modelach, które mają „Conversion Setting for DLNA”, sprawdź to ustawienie, a potem wybierz **Odśwież folder**. Niesprawdzone. |
| Nagranie jest wymienione pod listą z opisem **Nagrywarka nie udostępnia tego nagrania do pobrania** | Nagrywarka nie podała adresu, z którego da się je pobrać. Nie wiadomo dlaczego; być może nagrywanie jeszcze trwa. | Spróbuj później i wybierz **Odśwież folder**. Niesprawdzone. |
| Nagrania z nagrywarki w ogóle nie ma na liście | Nagrywarka nie umieściła go na liście dla urządzeń sieciowych. | Zobacz [Nagrania, których nie ma na liście](#what-the-recorder-offers). |

### Zapisywanie

Gdy nagrania nie uda się zapisać, powód jest podany po jego tytule w komunikacie z nagłówkiem **Zapisano nagrania: … z …**. Większość wierszy poniżej przytacza taki powód. **Pobierz i odtwórz podgląd** oraz **Pobierz i pokaż szczegóły** pokazują te same teksty w komunikacie z nagłówkiem **Wymagane działanie**.

| Co widzisz | Prawdopodobna przyczyna | Co zrobić |
|---|---|---|
| **Nagrywarka nie zwróciła nagłówków HTTP w wymaganym czasie.** | Nagrywarka nie zaczęła odpowiadać w ciągu 30 sekund. | Sprawdź, czy jest włączona i niezajęta, i zapisz ponownie. |
| **Nagrywarka przestała wysyłać dane. Niekompletna kopia została usunięta. Spróbuj ponownie, gdy nagrywarka będzie dostępna.** | Przez 30 sekund nie nadeszły żadne dane: nagrywarka przeszła w tryb czuwania albo zajęła się czymś innym, albo połączenie zostało zerwane. | Zobacz [Powolne odpowiedzi i przerwane pobieranie](#time-limits). Jeśli możesz, użyj kabla. |
| **Pobieranie zakończyło się przed odebraniem całego zadeklarowanego nagrania. Niekompletna kopia została usunięta.** | Nagrywarka zakończyła przesyłanie przed czasem. | Wybierz **Odśwież folder** i zapisz ponownie. |
| **Połączenie z nagrywarką zostało przerwane, zanim dotarło całe nagranie. Niekompletna kopia została usunięta. Sprawdź, czy nagrywarka jest włączona i połączona z siecią, a potem zapisz nagranie ponownie.** | Połączenie zostało zerwane w trakcie pobierania: nagrywarka została wyłączona albo przeszła w tryb czuwania, albo sieć przestała działać. | Sprawdź nagrywarkę i sieć, wybierz **Odśwież folder** i zapisz ponownie. |
| Powód **Nie można połączyć się z nagrywarką w sieci lokalnej.** z adresem i portem nagrywarki w nawiasie | Nagrywarka odrzuciła połączenie albo była nieosiągalna: po odczytaniu listy została wyłączona albo zniknęła z sieci. | Sprawdź nagrywarkę i sieć, wybierz **Odśwież folder** i zapisz ponownie. |
| **Nagrywarka nie zwróciła nagrania (HTTP …). Odśwież folder i sprawdź ustawienia dostępu nagrywarki.** | Nagrywarka odmówiła wydania tego nagrania: jest zajęta, obsługuje inne urządzenie albo jest to nagranie, którego nie wysyła. | Zatrzymaj inne odtwarzacze, wybierz **Odśwież folder** i zapisz ponownie. Jeśli zawodzą tylko niektóre nagrania, [zgłoś](#reporting), jakiego są rodzaju. |
| **Długość zawartości HTTP nie odpowiada rozmiarowi nagrania podanemu przez nagrywarkę. Odśwież listę nagrań i spróbuj ponownie.** | Nagranie zmieniło się po odczytaniu listy, na przykład dlatego, że wciąż się wydłużało. | Wybierz **Odśwież folder** i zapisz ponownie. |
| **Nagłówki HTTP nagrywarki wskazują chronioną zawartość. Ta aplikacja nie może pobierać nagrań chronionych przez DTCP/DRM.** | Nagrywarka pokazała nagranie jako dostępne, a potem odpowiedziała zawartością chronioną. | Nic. Zobacz [Co nagrywarka udostępnia](#what-the-recorder-offers). |
| **Zapisano nagrania: … z …** | Części nagrań nie udało się zapisać. Komunikat wymienia pierwsze cztery z nich, przy każdym podając powód, i podaje liczbę pozostałych. | Przeczytaj powody. Niezapisane nagrania pozostają zaznaczone. |
| Zapisywanie zawsze urywa się po tej samej liczbie godzin | „Automatic Standby” w nagrywarce albo komputer przeszedł w stan uśpienia. | [Tryb czuwania](#standby) oraz [Powolne odpowiedzi i przerwane pobieranie](#time-limits). |
| Zapisywanie jest bardzo powolne | Słabe połączenie Wi-Fi albo nagrywarka, która jednocześnie nagrywa. Aplikacja nie ma ustawienia szybkości. | Użyj kabla. Zapisuj, gdy nagrywarka nie jest zajęta. |

Jeśli nic z tego nie pomaga, uruchom [zestaw diagnostyczny](#diagnostics) i [wyślij zgłoszenie nagrywarki](#reporting). Pozostałe części aplikacji opisuje [instrukcja użytkownika](USER-GUIDE.pl.md).
