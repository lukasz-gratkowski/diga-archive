# Konfiguracja chmury: OneDrive lub Dysk Google

*English version: [CLOUD-SETUP.md](CLOUD-SETUP.md)*

AMG DIGA Archive potrafi przesłać zapisane nagrania na **Twój własny** OneDrive lub Dysk Google i pokazać, co już się tam znajduje. Przesyłanie jest opcjonalne i nigdy nie zaczyna się samo. Ten przewodnik prowadzi przez konfigurację krok po kroku. Pozostałe części aplikacji opisuje [instrukcja użytkownika](USER-GUIDE.pl.md).

| | OneDrive, wbudowany identyfikator | OneDrive, własna rejestracja | Dysk Google |
|---|---|---|---|
| Przygotowanie przed pierwszym logowaniem | żadne | około 20 minut, jednorazowo | około 15 minut, jednorazowo |
| Co jest potrzebne | konto Microsoft | konto Microsoft, a przy koncie osobistym bezpłatna rejestracja w Azure (telefon i karta płatnicza do potwierdzenia tożsamości) | konto Google z weryfikacją dwuetapową |
| Do czego aplikacja ma dostęp | do plików w Twoim OneDrive; przy ustawionym [udostępnionym folderze](#shared-folder) do wszystkich plików, do których ma dostęp Twoje konto | tak samo | tylko do plików, które sama przesłała |
| Jak długo działa logowanie | do rozłączenia albo do długiej przerwy w używaniu | tak samo | 7 dni, chyba że opublikujesz swój projekt Google |
| Dla kogo | prawie dla każdego | dla organizacji blokujących wbudowany identyfikator i dla osób, które wolą własny | dla osób trzymających archiwum na Dysku Google |

**Na ile ten przewodnik został sprawdzony.** Właściciel projektu poinformował 2 października 2026 r., że połączenie z OneDrive przy użyciu wbudowanego identyfikatora i przesłanie pliku zadziałały na jego koncie Microsoft. Zgłoszenie dotyczyło wersji 0.5.2 i rejestracji Microsoft, która była wtedy wbudowana. 5 października 2026 r. aplikacja otrzymała nową wbudowaną rejestrację (identyfikator aplikacji `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`). Nikt jeszcze nie zgłosił połączenia ani przesłania pliku za pośrednictwem nowej rejestracji. Sprawdzono dla niej tylko to, że usługa logowania Microsoft zna ten identyfikator i przyjmuje adres przekierowania `http://localhost`; sprawdzono to bez logowania się.

Kroki po stronie Microsoft i Google opierają się na dokumentacji Microsoft i Google w brzmieniu z 3 października 2026 r.: projekt nie przeszedł sam ani rejestracji własnej aplikacji Microsoft, ani tworzenia klienta Google, nie sprawdzał konta służbowego ani szkolnego, a ścieżka Dysku Google była dotąd uruchamiana wyłącznie w testach automatycznych, z symulowanymi serwerami Google. To, co przewodnik mówi o samej aplikacji (jej strony i komunikaty, czekanie i ponawianie prób przy przesyłaniu, działanie przycisku **Rozłącz**), pochodzi z kodu aplikacji. Kod logowania, przesyłania i odczytywania listy plików jest sprawdzany testami automatycznymi z symulowanymi serwerami Microsoft i Google; poza opisanym wyżej zgłoszeniem projekt nie wykonał żadnego logowania ani przesyłania z prawdziwymi usługami.

Nazwy w portalach się zmieniają; tam, gdzie strony Microsoft pokazują dwie generacje nazw, podano obie. Jeśli któryś krok nie zgadza się już z tym, co widzisz, [zgłoś to](https://github.com/lukasz-gratkowski/diga-archive/issues).

Nazwy elementów w portalach Microsoft i Google podano po angielsku. Oba portale tłumaczą swoje nazwy, a tłumaczenia bywają niekonsekwentne, dlatego najłatwiej przejść te kroki po przełączeniu portalu na język angielski.

## Spis treści

- [Wybór miejsca docelowego w aplikacji](#wybor)
- [OneDrive, najprostsza droga](#onedrive-szybko)
- [OneDrive z własną rejestracją](#onedrive-with-your-own-registration)
- [Konta służbowe i szkolne](#konta-sluzbowe)
- [Udostępniony folder na przesyłane pliki](#shared-folder)
- [Gdy logowanie do OneDrive się nie udaje](#onedrive-bledy)
- [Dysk Google](#google-drive)
- [Gdy logowanie do Google się nie udaje](#google-bledy)
- [Przesyłanie i podgląd chmury](#przesylanie)
- [Logowanie w szczegółach](#logowanie)
- [Co aplikacja przechowuje i jak cofnąć dostęp](#dane)

<a name="wybor"></a>
## Wybór miejsca docelowego w aplikacji

![Sekcja chmury na stronie Ustawienia: lista Miejsce docelowe w chmurze nad kartami Microsoft OneDrive i Dysk Google](images/pl/10-settings-cloud.png)

Otwórz stronę **Ustawienia** i przewiń do sekcji **Twoje połączenia z chmurą**. Lista **Miejsce docelowe w chmurze** decyduje, dokąd przesyła pliki strona **Archiwum** i co pokazuje strona **Chmura**. Przy każdej usłudze lista podaje jej stan: **połączono**, **brak połączenia** albo **logowanie wygasło**. Ta sama lista jest na stronach **Archiwum** i **Chmura**; zmiana w jednym miejscu obowiązuje wszędzie i zostaje od razu zapamiętana. Możesz połączyć obie usługi i przełączać się między nimi w dowolnej chwili.

Gdy łączysz usługę, a wybrane miejsce docelowe nie ma działającego logowania, aplikacja ustawia jako miejsce docelowe usługę, którą właśnie połączono. W przeciwnym razie miejsce docelowe zostaje bez zmian, a komunikat po zalogowaniu podaje, dokąd trafiają przesyłane pliki.

Wszystko inne na stronie **Ustawienia** zapisuje przycisk **Zapisz preferencje** na pasku na dole okna. Przyciski łączenia i przycisk **Rozłącz** również zapisują tę stronę. Jeśli czegoś na stronie nie da się zapisać, na przykład pole folderu jest puste albo lokalizacja narzędzia multimedialnego nie wskazuje istniejącego pliku, aplikacja informuje, co jest nie tak, i nie rozpoczyna logowania.

<a name="onedrive-szybko"></a>
## OneDrive, najprostsza droga

Aplikacja ma wbudowaną rejestrację Microsoft, więc niczego nie trzeba rejestrować ani wpisywać.

1. Otwórz stronę **Ustawienia** i przewiń do sekcji **Twoje połączenia z chmurą**. Na karcie **Microsoft OneDrive** wybierz **Połącz z OneDrive**.
2. W przeglądarce otworzy się strona logowania Microsoft. Aplikacja pokazuje komunikat **Dokończ logowanie do usługi OneDrive w przeglądarce** i czeka najwyżej dziesięć minut.
3. Wskaż konto Microsoft, na którego OneDrive mają trafiać nagrania. Microsoft zawsze pokazuje tu listę kont, bo aplikacja o to prosi: dzięki temu przez przypadek nie zostanie użyte konto służbowe, na które akurat jesteś zalogowany.
4. Microsoft pyta, czy aplikacja może mieć pełny dostęp do Twoich plików i zachować dostęp do danych, do których go udzielono (w angielskiej wersji strony: **Have full access to your files** oraz **Maintain access to data you have given it access to**). Zaakceptuj. Jeśli strona określa wydawcę jako niezweryfikowanego (**unverified**), przeczytaj uwagę poniżej.
5. Przeglądarka pokaże jeden wiersz tekstu z informacją, że aplikacja otrzymała dane logowania. Zamknij kartę przeglądarki i wróć do aplikacji. Karta w aplikacji pokazuje teraz **Połączono · OneDrive (osobisty)** albo **Połączono · OneDrive (służbowy lub szkolny)** i nazwę właściciela dysku.

Aplikacja używa tej zgody do dodawania nowych plików do głównego folderu Twojego OneDrive, do wyświetlania zawartości tego folderu oraz do zapytania o rodzaj dysku, nazwę jego właściciela i ilość wolnego miejsca. Nigdy nie zmienia ani nie usuwa plików, które już tam są: jeśli plik o takiej nazwie już istnieje, OneDrive zapisuje nowy pod zmienioną nazwą.

Pliki mogą trafiać do jednego udostępnionego folderu zamiast do folderu głównego; zob. [Udostępniony folder na przesyłane pliki](#shared-folder). Gdy taki folder jest ustawiony, strona Microsoft w kroku 4 pyta o wszystkie pliki, do których masz dostęp, a nie tylko o własne.

Aby później użyć innego konta Microsoft, ponownie wybierz **Połącz z OneDrive** i wskaż inne konto na stronie Microsoft. Nie trzeba się wcześniej rozłączać.

**Jeśli połączono OneDrive w wersji 0.5.2 przy użyciu wbudowanego identyfikatora.** Tamta wersja miała inny wbudowany identyfikator aplikacji, a logowanie jest związane z identyfikatorem, z którym je wykonano. Po aktualizacji karta informuje, że nie ma połączenia z OneDrive: wybierz **Połącz z OneDrive** jeszcze raz. Logowanie zapisane przez wersję 0.5.2 pozostaje na komputerze nieużywane; przycisk **Rozłącz** usuwa je razem z nowym. Zgoda udzielona wcześniejszej rejestracji pozostaje na Twoim koncie Microsoft, dopóki jej tam nie usuniesz; zob. [ostatnią część przewodnika](#dane).

> **Dlaczego „niezweryfikowany”?** Microsoft pokazuje nazwę wydawcy tylko dla aplikacji zarejestrowanych przez firmę należącą do jego programu partnerskiego. Sama zgoda jest dokładnie taka, jak na stronie, i dotyczy wyłącznie konta, na które się logujesz. Identyfikator aplikacji nie jest hasłem: mówi firmie Microsoft tylko, która aplikacja prosi o dostęp.

Przy koncie **służbowym lub szkolnym** przeczytaj najpierw [Konta służbowe i szkolne](#konta-sluzbowe).

<a name="onedrive-with-your-own-registration"></a>
## OneDrive z własną rejestracją

Jest potrzebna tylko wtedy, gdy nie chcesz używać wbudowanego identyfikatora albo gdy Twoja organizacja go blokuje. Rejestracja przedstawia aplikację firmie Microsoft; tworzy się ją raz, nic nie kosztuje i nie zawiera żadnego hasła.

Portal tłumaczy swoje nazwy, gdy działa w innym języku. Aby iść za tym przewodnikiem słowo w słowo, przełącz portal na angielski ikoną koła zębatego u góry (**Language + region**).

### Część A: katalog (tylko konta osobiste Microsoft)

Od czerwca 2024 r. Microsoft pozwala rejestrować aplikacje wyłącznie w *katalogu* (inaczej *dzierżawie*, ang. *tenant*). Konto służbowe lub szkolne już do jakiegoś należy. Konto osobiste (`outlook.com`, `hotmail.com`, `live.com` i podobne) zwykle nie.

1. Otwórz <https://entra.microsoft.com> i zaloguj się. Jeśli wejdziesz i pod nazwą konta w prawym górnym rogu zobaczysz nazwę katalogu, masz już katalog: przejdź do części B. Jeśli zobaczysz błąd mówiący, że konto nie istnieje w dzierżawie (na przykład `AADSTS16000` lub `AADSTS50020`), długi identyfikator zamiast nazwy albo komunikat, że aplikacji nie można już tworzyć poza katalogiem, czytaj dalej.
2. Otwórz <https://azure.microsoft.com/free> i wybierz **Try Azure for free**. Zaloguj się tym samym kontem Microsoft.
3. Wypełnij sekcję **About you**; kraj musi być krajem adresu rozliczeniowego karty. Potwierdź numer telefonu SMS-em lub połączeniem (numery telefonii internetowej nie są przyjmowane), a potem kartę kredytową lub debetową (karty przedpłacone i wirtualne są odrzucane). Zaakceptuj umowę. Jeśli krok z kartą się nie wczytuje, zezwól stronie rejestracji na pliki cookie innych firm.
4. Koszty: Microsoft podaje, że przy rejestracji nie obciąża karty i pobiera opłaty tylko wtedy, gdy później samodzielnie przejdziesz na płatność za użycie (pay-as-you-go). Może pojawić się tymczasowa blokada około jednego dolara, która potem znika. Nie twórz żadnych usług Azure. Po 30 dniach Microsoft wyłącza subskrypcję próbną; według dokumentacji Microsoft katalog pozostaje. Nieużywany katalog może zostać później usunięty: zob. uwagę o nieaktywności po części C.
5. Strony administracyjne Microsoft wymagają logowania dwuetapowego, więc spodziewaj się prośby o jego ustawienie.

Bezpłatne konto jest oferowane raz, osobom, które wcześniej nie korzystały z Azure. Jeśli korzystałeś już z Azure, Microsoft proponuje pay-as-you-go; projekt nie sprawdzał tej drogi. Studenci ze szkolnym adresem e-mail mogą skorzystać z *Azure for Students*, które nie wymaga karty; nie ustalono, czy daje ono kontu osobistemu katalog.

### Część B: rejestracja aplikacji

6. W <https://entra.microsoft.com> przejdź do **Entra ID → App registrations → New registration**. (Starsze menu: **Identity → Applications → App registrations**. W `portal.azure.com`: otwórz **Microsoft Entra ID**, potem **App registrations**.) Jeśli masz kilka katalogów, wybierz właściwy ikoną koła zębatego u góry.
7. **Name**: dowolna nazwa, którą rozpoznasz, na przykład `Moje archiwum DIGA`.
8. **Supported account types**: wybierz **Any Entra ID Tenant + Personal Microsoft accounts** (starsza nazwa: **Accounts in any organizational directory and personal Microsoft accounts**). Formularz podpowiada **Single tenant only**; nie zostawiaj tej opcji i nie wybieraj **Personal accounts only**. Z żadną z nich aplikacja nie działa.
9. Jeśli formularz zawiera pole **Redirect URI**, zostaw je puste. Wybierz **Register**.
10. Na stronie **Overview** skopiuj **Application (client) ID**. Nie Object ID i nie Directory (tenant) ID.
11. W sekcji **Manage** otwórz **Authentication** (może się nazywać **Authentication (Preview)**). Na karcie **Redirect URI configuration** wybierz **Add Redirect URI**, wskaż **Mobile and desktop applications**, wpisz `http://localhost` jako własny adres przekierowania i wybierz **Configure**. (Starszy układ: **Platform configurations → Add a platform**.)
    - Wpisz dokładnie tak: `http`, nie `https`; bez portu, bez ukośnika, bez ścieżki. Nie używaj tu `127.0.0.1`, choć jedna ze stron Microsoft to zaleca: aplikacja wysyła `localhost`.
    - Nie dodawaj adresu w sekcjach **Web** ani **Single-page application**.
12. Opcję **Allow public client flows** zostaw wyłączoną (w nowym układzie jest na karcie **Settings** strony Authentication, w starym w sekcji **Advanced settings**). Niczego nie twórz w **Certificates & secrets**: dla OneDrive aplikacja nie używa klucza tajnego.
13. Otwórz **API permissions → Add a permission → Microsoft Graph → Delegated permissions**, zaznacz **Files.ReadWrite** oraz **offline_access** i wybierz **Add permissions**. Jeśli pliki mają trafiać do [udostępnionego folderu](#shared-folder), zaznacz też **Files.ReadWrite.All**. Wpis **User.Read**, który dodał Microsoft, zostaw. Przy koncie osobistym ten krok jest opcjonalny, bo aplikacja i tak prosi o potrzebne uprawnienia podczas logowania; ma znaczenie, gdy aplikację musi zatwierdzić administrator organizacji.
14. Odczekaj około pięciu minut. Zmiany w Microsoft nie działają natychmiast.

### Część C: połączenie w aplikacji

15. W aplikacji otwórz stronę **Ustawienia** i przewiń do sekcji **Twoje połączenia z chmurą**. Na karcie **Microsoft OneDrive** rozwiń sekcję **Własna rejestracja aplikacji w Microsoft** i wklej identyfikator w pole **Własny identyfikator aplikacji**. Następnie wybierz **Połącz z OneDrive**. Połączenie zapisuje również ustawienia z tej strony.
16. W przeglądarce wskaż konto, do którego należy OneDrive, i zaakceptuj prośbę. Wydawca jest oznaczony jako niezweryfikowany; przy własnej rejestracji tak ma być.
17. Karta powinna teraz pokazywać **Połączono · OneDrive (osobisty)** albo **Połączono · OneDrive (służbowy lub szkolny)** i nazwę właściciela, tak jak przy najprostszej drodze. Wypróbuj na jednym małym pliku ze strony **Archiwum**.

Pole przyjmuje identyfikator w postaci, w jakiej pokazuje go Microsoft: 36 znaków z czterema łącznikami. Innej wartości aplikacja nie przyjmie: zgłosi to, zanim otworzy przeglądarkę. Dopóki pole jest puste, widać w nim tekst **Puste: używany jest wbudowany identyfikator**, a wiersz pod polem podaje wbudowany identyfikator.

Logowanie jest związane z identyfikatorem aplikacji, z którym je wykonano. Po zmianie identyfikatora karta informuje, że nie ma połączenia z OneDrive, i trzeba połączyć się ponownie. Zmiana identyfikatora nie usuwa logowania zapisanego dla poprzedniego: zostaje ono na tym komputerze nieużywane, wraca do użycia, gdy przywrócisz poprzedni identyfikator, a usuwa je przycisk **Rozłącz**, który kasuje wszystkie logowania do OneDrive zapisane na tym komputerze.

Aby wrócić do wbudowanego identyfikatora, wyczyść pole i wybierz **Zapisz preferencje**. Jeśli na tym komputerze nadal jest zapisane wcześniejsze logowanie z wbudowanym identyfikatorem, zostanie użyte ponownie; w przeciwnym razie wybierz **Połącz z OneDrive**.

**Katalog można stracić z powodu nieaktywności.** Według dokumentacji Microsoft katalog, który nie jest już używany, zostaje zablokowany, a jeśli blokada trwa dłużej niż 20 dni, zostaje usunięty. Rejestracja znika razem z katalogiem. Dokumentacja nie podaje, co liczy się jako używanie. Osoby piszące w serwisie Microsoft Q&A cytują wiadomość e-mail, w której Microsoft uznaje katalog za nieaktywny po ponad 200 dniach i wzywa do dokonania zakupu przed podaną datą; odpowiedzi w tym serwisie różnią się co do tego, czy do zachowania katalogu wystarczy logować się od czasu do czasu. Projekt tego nie wie. Zwracaj uwagę na taką wiadomość od firmy Microsoft.

Jeśli własna rejestracja przestanie działać z błędem `AADSTS5000225`, Microsoft zablokował katalog z powodu nieaktywności. Według dokumentacji Microsoft administrator może w ciągu 20 dni poprosić Microsoft o przywrócenie katalogu; potem katalog jest usuwany i rejestrację trzeba wykonać od nowa, w nowym katalogu.

**Aby wszystko cofnąć:** wybierz w aplikacji **Rozłącz**, usuń aplikację na stronie <https://account.microsoft.com/privacy/app-access> (konta osobiste) i skasuj rejestrację w centrum administracyjnym.

<a name="konta-sluzbowe"></a>
## Konta służbowe i szkolne

Ta część opiera się na dokumentacji Microsoft; projekt nie sprawdzał konta służbowego ani szkolnego.

Wiele organizacji nie pozwala użytkownikom samodzielnie zatwierdzać aplikacji albo pozwala tylko na aplikacje zweryfikowanych wydawców. Microsoft uznaje też za ryzykowną niedawno zarejestrowaną aplikację, która prosi o więcej niż samo logowanie, nie ma zweryfikowanego wydawcy i pochodzi z innej organizacji. Wtedy zamiast strony ze zgodą pojawia się **Need admin approval** (albo błąd taki jak `AADSTS90094`, `AADSTS90093` lub `AADSTS900941`). Co można zrobić:

- **Poproś administratora** o zatwierdzenie aplikacji. Może to zrobić w centrum administracyjnym Entra w sekcji **Enterprise apps** albo za pomocą adresu zgody administratora; zob. [Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent) w dokumentacji Microsoft. Podaj mu identyfikator aplikacji. Wbudowany identyfikator tej wersji to `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`; aplikacja pokazuje go na stronie **Ustawienia**, w sekcji **Własna rejestracja aplikacji w Microsoft**. Uprawnienia to delegowane uprawnienia Microsoft Graph **Files.ReadWrite** i **offline_access**, a przy ustawionym [udostępnionym folderze](#shared-folder) **Files.ReadWrite.All** w miejsce pierwszego.
- **Zarejestruj własną aplikację w katalogu swojej organizacji** (część B powyżej, o ile organizacja pozwala użytkownikom rejestrować aplikacje). Zasady dotyczące niezweryfikowanych wydawców nie obejmują aplikacji zarejestrowanych we własnej organizacji.
- Jeśli organizacja stosuje obieg zatwierdzania, zobaczysz **Approval required** z polem tekstowym: wyślij prośbę i poczekaj na wiadomość e-mail.

Błędy wspominające o dostępie warunkowym lub logowaniu wieloskładnikowym (`AADSTS53003`, `AADSTS50076`) wynikają z zasad Twojej organizacji; zmienić je może tylko jej dział IT.

<a name="shared-folder"></a>
## Udostępniony folder na przesyłane pliki

Bez dodatkowych ustawień pliki trafiają do głównego folderu OneDrive połączonego konta. Można je zamiast tego kierować do jednego udostępnionego folderu: folderu w czyimś OneDrive albo w bibliotece dokumentów SharePoint, do którego ma dostęp kilka osób. Każda z nich przesyła wtedy pliki ze swojego konta Microsoft, a wszystkie nagrania trafiają w to samo miejsce. Dysk Google nie ma takiego ustawienia.

**Na ile to zostało sprawdzone.** Udostępniony folder był dotąd uruchamiany wyłącznie w testach automatycznych projektu, z symulowanymi serwerami Microsoft. Nikt jeszcze nie zgłosił użycia go z prawdziwym folderem OneDrive lub SharePoint. To, co ta część mówi o stronie Microsoft, opiera się na dokumentacji Microsoft w brzmieniu z 8 października 2026 r.; to, co mówi o aplikacji, pochodzi z kodu aplikacji.

### Konfiguracja

1. **Wybierz folder.** Na stronie OneDrive albo w bibliotece dokumentów SharePoint utwórz lub wskaż folder, do którego mają trafiać nagrania.
2. **Udostępnij go i skopiuj link.** Zaznacz folder i wybierz **Share**. Ustaw, dla kogo link działa (Microsoft daje do wyboru **Anyone**, **People in** Twoja organizacja **with the link**, **People with existing access** oraz **Specific people**; organizacja może część z nich wyłączyć), i wybierz **Copy Link**. Każdy, kto ma przesyłać pliki, potrzebuje prawa *edycji*, bo dodanie pliku jest edycją. Osobom, które mają tylko oglądać nagrania, wystarczy prawo wyświetlania.
3. **Wklej link.** W aplikacji otwórz **Ustawienia** i przewiń do karty **Microsoft OneDrive**. Wklej link w polu **Folder na przesyłane pliki (opcjonalnie)**. Musi to być cały adres, zaczynający się od `https://`.
4. **Połącz.** Wybierz **Połącz z OneDrive**, także wtedy, gdy OneDrive był już połączony: gdy folder jest ustawiony, aplikacja prosi Microsoft o dostęp do plików udostępnionych Twojemu kontu, a nie tylko do własnych, i strona Microsoft o tym informuje. O tym, kiedy ma to znaczenie, mówi część [Które konta mogą korzystać z udostępnionego folderu](#ktore-konta).
5. **Sprawdź.** Wybierz **Zapisz i sprawdź folder**. Aplikacja pyta OneDrive, jako połączone konto, do jakiego folderu prowadzi link, i odpowiada **Folder znaleziony** wraz z nazwą folderu. Następnie prześlij jeden mały plik ze strony **Archiwum**: to, czy konto może dodawać pliki do folderu, okazuje się dopiero przy pierwszym przesyłaniu.

Jeśli w chwili wybrania **Zapisz i sprawdź folder** OneDrive nie jest połączony, aplikacja zapisuje link i prosi o połączenie OneDrive, bo folder jest sprawdzany z użyciem połączonego konta. Po połączeniu wybierz przycisk ponownie.

Aby wrócić do folderu głównego, wyczyść pole i wybierz **Zapisz preferencje**.

<a name="ktore-konta"></a>
### Które konta mogą korzystać z udostępnionego folderu

Liczy się konto połączone z aplikacją. Musi ono móc otworzyć link i mieć prawo dodawania plików do folderu.

| Połączone konto | Folder | Co mówi dokumentacja Microsoft |
|---|---|---|
| dowolne | we własnym OneDrive tego konta | Wystarcza zwykłe uprawnienie (**Files.ReadWrite**). Ponowne łączenie nie jest potrzebne |
| służbowe lub szkolne | czyjś OneDrive albo biblioteka SharePoint w tej samej organizacji | Microsoft opisuje **Files.ReadWrite** jako dostęp do własnych plików zalogowanego użytkownika, a **Files.ReadWrite.All** jako dostęp do wszystkich plików, do których użytkownik ma dostęp, i jako przykład użycia drugiego podaje zapis pliku udostępnionego użytkownikowi. Aplikacja prosi o to drugie, gdy łączysz się przy ustawionym folderze. Organizacja może wymagać, aby zatwierdził to administrator; zob. [Konta służbowe i szkolne](#konta-sluzbowe) |
| osobiste | folder udostępniony przez inne konto osobiste | W przypadku kont osobistych zwykłe uprawnienie obejmuje także pliki udostępnione kontu. Gdy folder jest ustawiony, aplikacja i tak prosi o szersze |
| osobiste albo konto innej organizacji | OneDrive lub SharePoint organizacji | Nie ustalono. Projekt nie znalazł stwierdzenia Microsoft, że działa to przez jego interfejs programistyczny, i tego nie sprawdzał. Aplikacja przekazuje to, co odpowie OneDrive |

Szersze uprawnienie pozwala aplikacji sięgać do każdego pliku, do którego ma dostęp konto. Aplikacja używa go wyłącznie do folderu: pyta, do jakiego folderu prowadzi link, dodaje do niego nowe pliki, wyświetla jego zawartość i pyta dysk, na którym folder leży, o wolne miejsce.

Logowanie wykonane, *zanim* ustawiono folder, obejmuje tylko własne pliki konta. Aplikacja pamięta, jakiego rodzaju logowanie ma. Po wybraniu **Zapisz i sprawdź folder** informuje, gdy logowanie jest tego węższego rodzaju, i w tym samym komunikacie daje przycisk **Połącz z OneDrive**; to samo mówi komunikat o odrzuconym przesyłaniu.

### Co się zmienia w aplikacji

- **Archiwum.** Karta **Kopia w chmurze** informuje, że pliki trafiają do udostępnionego folderu ustawionego w Ustawieniach. Przed każdym przesyłaniem aplikacja ponownie pyta OneDrive o link, więc o wycofanym linku dowiesz się, zanim zacznie się długie przesyłanie.
- **Odnośnik przy przesłanym pliku** brzmi **Otwórz udostępniony folder** z nazwą folderu i otwiera wklejony przez Ciebie link udostępniania: folder, a nie pojedynczy plik. Ten link otwiera się każdemu, dla kogo został utworzony, niezależnie od tego, na jakie konto Microsoft zalogowana jest przeglądarka. Adres pojedynczego pliku na czyimś dysku otworzyłby się tylko kontu, które już ma dostęp do tego pliku.
- **Chmura.** Strona pokazuje udostępniony folder zamiast folderu głównego.
- **Miejsce.** Aplikacja pyta dysk, na którym leży folder, ile jest wolnego miejsca. Jeśli dysk tego nie podaje, przesyłanie zaczyna się bez tego sprawdzenia.
- **Nazwy.** Tak jak w folderze głównym, plik o zajętej nazwie zostaje zapisany pod zmienioną nazwą; nic nie jest zastępowane.
- **Przyjęcie linku.** Pytanie OneDrive o link jest jednocześnie przyjęciem go w imieniu połączonego konta, tak jak otwarcie linku w przeglądarce. Według dokumentacji Microsoft daje to kontu trwały dostęp do folderu.

### Gdy to nie działa

| Co mówi aplikacja | Co to znaczy | Co zrobić |
|---|---|---|
| **Wklej cały link do udostępnionego folderu; zaczyna się od https://.** | W polu jest coś innego niż pełny adres `https` | Skopiuj link ponownie przyciskiem **Copy Link** |
| **Usługa OneDrive nie otworzyła folderu, do którego prowadzi ten link** | Link jest niepełny albo został wycofany, albo nie został utworzony dla połączonego konta | Sprawdź, dla kogo link działa; skopiuj go ponownie |
| **Ten link nie prowadzi do folderu.** | Link prowadzi do pojedynczego pliku | Udostępnij sam folder |
| **Usługa OneDrive nie przyjęła pliku w udostępnionym folderze** | Połączone konto nie może tam dodawać plików albo logowanie obejmuje tylko własne pliki konta | Nadaj kontu prawo edycji; jeśli komunikat mówi, że logowanie obejmuje tylko własne pliki konta, ponownie wybierz **Połącz z OneDrive** |
| **Need admin approval** na stronie Microsoft po ustawieniu folderu | Organizacja nie pozwala Ci samodzielnie zatwierdzić szerszego uprawnienia | [Konta służbowe i szkolne](#konta-sluzbowe) |

### Co jest przechowywane i wysyłane

- Link jest przechowywany w pliku `%LOCALAPPDATA%\Diga\Accounts\folder-OneDrive.bin`, zaszyfrowany dla Twojego konta Windows tak jak logowania. Nie trafia do pliku ustawień. Link udostępniania rodzaju **Anyone** sam jest kluczem do folderu i dlatego jest przechowywany w ten sposób.
- Przycisk **Rozłącz** nie usuwa linku; usuwa go wyczyszczenie pola, a także odinstalowanie aplikacji.
- Link jest wysyłany do Microsoft Graph (`graph.microsoft.com`) i nigdzie indziej, razem z logowaniem, za każdym razem, gdy aplikacja pyta, do jakiego folderu prowadzi.
- Wyczyszczenie pola nie zawęża logowania, które ma już szersze uprawnienie. Aby je cofnąć, usuń zgodę aplikacji po stronie Microsoft (zob. [ostatnią część](#dane)) i połącz się ponownie przy pustym polu.

<a name="onedrive-bledy"></a>
## Gdy logowanie do OneDrive się nie udaje

Microsoft podaje przyczynę na własnej stronie w przeglądarce albo aplikacja pokazuje ją po słowach **Komunikat usługi:**.

| Co widzisz | Co to znaczy | Co zrobić |
|---|---|---|
| Komunikat, że aplikacji nie można tworzyć poza katalogiem; `AADSTS16000` lub `AADSTS50020` przy otwieraniu centrum administracyjnego | Konto osobiste nie ma katalogu | Część A |
| `AADSTS50011` | Adres przekierowania nie zgadza się z rejestracją | Krok 11: dokładnie `http://localhost` w sekcji **Mobile and desktop applications**; potem odczekaj kilka minut |
| `AADSTS50194` | Rejestracja jest dla jednej dzierżawy (single tenant) | Krok 8. Najprościej usunąć rejestrację i utworzyć ją ponownie |
| `AADSTS9002331` | Rejestracja jest tylko dla kont osobistych (ten kod wyjaśnia odpowiedź w Microsoft Q&A, nie ma go na oficjalnej liście błędów) | Krok 8, jak wyżej |
| `unauthorized_client`, „not enabled for consumers” | Konto osobiste użyte z rejestracją, która nie dopuszcza kont osobistych, albo błędny identyfikator | Kroki 8 i 10 |
| `AADSTS700016` | Microsoft nie zna tego identyfikatora aplikacji | Wklejono zły identyfikator (krok 10) albo rejestracja została usunięta. Wyczyszczenie pola przywraca wbudowany identyfikator |
| Aplikacja informuje, że identyfikator aplikacji Microsoft ma postać `00000000-0000-0000-0000-000000000000` | W polu **Własny identyfikator aplikacji** jest coś innego niż identyfikator aplikacji | Wklej **Application (client) ID** z kroku 10 albo wyczyść pole, aby używać wbudowanego identyfikatora |
| `AADSTS7000218` | Microsoft oczekuje klucza tajnego, bo adres przekierowania zarejestrowano jako **Web** | Przenieś go do **Mobile and desktop applications** i usuń wpis `localhost` z sekcji **Web**. Jeśli błąd nie znika, choć tak już jest, ustaw **Allow public client flows** na **Yes** (krok 12) i spróbuj ponownie; to rozwiązanie awaryjne nie było sprawdzane |
| `AADSTS65004` albo aplikacja informuje, że logowanie odrzucono | Na stronie ze zgodą wybrano **Cancel** | Połącz ponownie i zaakceptuj |
| `AADSTS65001` | Dla tej aplikacji nie zapisano zgody | Połącz ponownie i zaakceptuj; w organizacji zwróć się do administratora |
| **Need admin approval**, `AADSTS90094`, `AADSTS90093`, `AADSTS900941` | Organizacja nie pozwala Ci samodzielnie zatwierdzić tej aplikacji | [Konta służbowe i szkolne](#konta-sluzbowe) |
| `AADSTS5000225` | Microsoft zablokował katalog z rejestracją z powodu nieaktywności; jeśli blokada trwa dłużej niż 20 dni, katalog jest usuwany | Własna rejestracja: w ciągu 20 dni poproś Microsoft o przywrócenie katalogu albo wykonaj rejestrację od nowa, w nowym katalogu. Wbudowany identyfikator: zgłoś problem |
| Przeglądarka pokazuje błąd, a aplikacja nadal czeka | Część błędów zostaje na stronie Microsoft i nie wraca do aplikacji | Wybierz **Anuluj** na dole okna aplikacji, usuń przyczynę i połącz ponownie |
| Aplikacja informuje, że logowanie nie zostało ukończone w ciągu 10 minut | Strona w przeglądarce pozostała bez odpowiedzi | Ponownie wybierz **Połącz z OneDrive** |
| Po aktualizacji z wersji 0.5.2 aplikacja pokazuje brak połączenia z OneDrive | Wbudowany identyfikator aplikacji jest nowy | Wybierz **Połącz z OneDrive** jeszcze raz; zob. [OneDrive, najprostsza droga](#onedrive-szybko) |
| Podczas przesyłania albo odczytywania listy plików na stronie **Chmura** aplikacja otwiera stronę **Ustawienia** z komunikatem **Logowanie do usługi OneDrive wygasło** | Microsoft nie przyjmuje już zapisanego logowania: wygasło albo zgoda została usunięta | Ponownie wybierz **Połącz z OneDrive**, a potem **Wróć do Archiwum** lub **Wróć do Chmury** |

<a name="google-drive"></a>
## Dysk Google

Dysk Google nie ma wbudowanego identyfikatora: warunki Google nie pozwalają projektowi open source publikować własnych danych uwierzytelniających Google, więc każdy użytkownik tworzy własny, mały *projekt Google Cloud*. Do tego zastosowania jest bezpłatny, a przygotowanie zajmuje około 15 minut. Efektem są dwie wartości, **identyfikator klienta** i **klucz tajny klienta**, które wkleja się do aplikacji.

**Zanim zaczniesz**

- Właścicielem projektu może być dowolne konto Google; nie musi to być konto, na którego Dysk trafią pliki.
- Google wymaga **weryfikacji dwuetapowej** na koncie osobistym, zanim wpuści do konsoli Google Cloud.
- Aplikacja prosi Google o jedno uprawnienie: wgląd w **pliki, które sama utworzyła**, i zarządzanie nimi. Nie widzi, nie zmienia i nie usuwa niczego innego na Dysku. Dlatego strona **Chmura** przy Dysku Google pokazuje tylko pliki przesłane przez aplikację.

### Projekt i klient

1. **Utwórz projekt.** Otwórz <https://console.cloud.google.com/projectcreate>. Wpisz nazwę projektu, pole **Location** zostaw bez zmian, wybierz **Create** i upewnij się, że nowy projekt jest wybrany u góry strony.
2. **Włącz Drive API.** Menu → **APIs & Services → Library**, otwórz **Google Drive API**, wybierz **Enable**.
3. **Opisz ekran logowania.** Menu → **Google Auth platform → Branding** (<https://console.cloud.google.com/auth/branding>). Jeśli zobaczysz *Google Auth platform not configured yet*, wybierz **Get started**.
   - **App Information**: dowolna nazwa aplikacji (nie używaj samej nazwy produktu Google, na przykład „Google Drive”) i Twój adres e-mail jako adres pomocy. **Next**.
   - **Audience**: **External**. (**Internal** istnieje tylko w organizacjach Google Workspace.) **Next**.
   - **Contact Information**: Twój adres e-mail. **Next**.
   - **Finish**: zaznacz zgodę na zasady Google dotyczące danych użytkowników, potem **Continue** i **Create**.
4. **Dodaj uprawnienie.** **Data Access → Add or remove scopes**. Zaznacz pozycję kończącą się na `/auth/drive.file` albo wklej `https://www.googleapis.com/auth/drive.file` w polu **Manually add scopes**. **Update**, potem **Save**. Uprawnienie musi się znaleźć na liście zakresów *non-sensitive*.
5. **Dodaj siebie jako użytkownika testowego.** **Audience → Test users → Add users**; wpisz konto (lub konta) Google, na których Dysk mają trafiać pliki. **Save**.
6. **Utwórz klienta.** **Clients → Create client**; **Application type**: **Desktop app**; dowolna nazwa; **Create**.
   **Od razu skopiuj identyfikator klienta i klucz tajny** albo pobierz plik JSON, który proponuje Google. Klucz tajny jest pokazywany tylko w tej chwili; później widać jedynie jego cztery ostatnie znaki. Jeśli go zgubisz, otwórz klienta i utwórz nowy klucz przyciskiem **Add secret**. Nie wybieraj typu **Web application**: nie działa z aplikacją na komputerze.

### Połączenie w aplikacji

7. W aplikacji otwórz stronę **Ustawienia** i przewiń do sekcji **Twoje połączenia z chmurą**. Na karcie **Dysk Google** wklej wartości w pola **Identyfikator klienta Google** i **Klucz tajny klienta Google**, a potem wybierz **Połącz z Dyskiem Google**. Połączenie zapisuje również ustawienia z tej strony.
8. W przeglądarce wskaż konto Google. Google ostrzega, że aplikacja nie została zweryfikowana albo jest testowana; to Twój własny projekt, więc przejdź dalej. Zezwól na dostęp do *konkretnych plików na Dysku Google używanych z tą aplikacją* (tak mniej więcej Google opisuje to uprawnienie). Google pokazuje te strony przy każdym logowaniu, bo aplikacja o to prosi.
9. Przeglądarka pokaże jeden wiersz tekstu z informacją, że aplikacja otrzymała dane logowania. Zamknij kartę przeglądarki i wróć do aplikacji. Karta w aplikacji pokazuje teraz **Połączono · Dysk Google** i adres e-mail konta.

Jeśli zamiast tego aplikacja pokaże komunikat **Połączono, ale Dysk Google nie jest jeszcze gotowy**, logowanie się powiodło, ale Dysk Google odrzucił pierwsze żądanie aplikacji. Najczęstsza przyczyna: w Twoim projekcie nie włączono Drive API. Wróć do kroku 2, a potem prześlij pliki.

Dokumentacja Google nazywa klucz tajny opcjonalnym dla aplikacji na komputer, ale programiści zgłaszają, że usługa logowania Google odrzuca klienta typu Desktop bez klucza, dlatego aplikacja prosi o obie wartości. Identyfikator klienta trafia do pliku ustawień. Klucz tajny nie. Wpisany klucz jest pamiętany do zamknięcia aplikacji, także wtedy, gdy logowanie się nie uda albo otworzysz inną stronę. Po połączeniu zostaje zapisany razem z logowaniem, zaszyfrowany dla Twojego konta Windows. Od tej chwili pole jest puste i pokazuje tekst **Zapisany. Zostaw puste, aby go zachować**: przy kolejnym łączeniu zostaw je puste, a zostanie użyty zapisany klucz.

Logowanie i zapisany z nim klucz tajny są związane z identyfikatorem klienta, z którym je wykonano. Po zmianie wartości w polu **Identyfikator klienta Google** karta informuje, że nie ma połączenia z Dyskiem Google; wklej klucz tajny nowego klienta i połącz się ponownie. Logowanie zapisane dla poprzedniego identyfikatora klienta zostaje na tym komputerze nieużywane, dopóki nie wybierzesz **Rozłącz**.

Aby użyć innego konta Google, ponownie wybierz **Połącz z Dyskiem Google** i wskaż konto na stronie Google; nie trzeba się wcześniej rozłączać. Konto musi być na liście użytkowników testowych Twojego projektu (krok 5).

Pliki trafiają na najwyższy poziom folderu **Mój dysk**. Plik o nazwie, która już istnieje, zostaje zapisany jako drugi plik o tej samej nazwie; Dysk Google na to pozwala i niczego nie zastępuje.

### Limit siedmiu dni

Nowy projekt Google ma stan publikacji **Testing**. W tym stanie Google kończy każde logowanie po **siedmiu dniach** od udzielenia zgody. Aplikacja nie kontaktuje się z Google przy uruchomieniu, więc karta może nadal informować, że Dysk Google jest połączony. Najbliższe przesyłanie albo najbliższe odczytanie listy plików na stronie **Chmura** przenosi na stronę **Ustawienia** z komunikatem **Logowanie do usługi Dysk Google wygasło**, a lista **Miejsce docelowe w chmurze** pokazuje odtąd przy Dysku Google stan **logowanie wygasło**. Ponownie wybierz **Połącz z Dyskiem Google**. Identyfikator klienta i zapisany klucz tajny zostają bez zmian; klucza nie wpisujesz ponownie.

Aby usunąć ten limit, projekt trzeba przełączyć w stan **In production** przyciskiem **Audience → Publish app**. Pomoc Google podaje, że zewnętrzne projekty w stanie produkcyjnym muszą mieć stronę główną i politykę prywatności, a domeny obu adresów trzeba wcześniej zarejestrować w sekcji **Authorized domains**. Użytkownicy zgłaszają, że przycisk pozostaje nieaktywny, dopóki w sekcji **Branding** nie ma obu adresów; projekt tego nie sprawdzał. W praktyce:

- **Masz własną stronę internetową:** dodaj na niej podstronę opisującą, co Twój projekt robi z danymi (przesyła Twoje nagrania na Twój Dysk i nic poza tym). W sekcji **Branding** dodaj domenę strony w **Authorized domains**, a potem wpisz adres strony jako stronę główną i adres podstrony jako politykę prywatności. Następnie wybierz **Publish app** i połącz się ponownie w aplikacji. Dla tego jednego uprawnienia typu non-sensitive Google nie wymaga weryfikacji.
- **Nie masz:** zostań w stanie **Testing** i łącz się ponownie, gdy aplikacja o to poprosi. Przy archiwum uzupełnianym kilka razy w roku zwykle nie jest to kłopot.

Projekt sam nie przechodził publikacji; relacje użytkowników o tym, jakie adresy Google przyjmuje, są rozbieżne.

### Żeby nie wygasło

Google usuwa klienta nieużywanego przez sześć miesięcy (30 dni wcześniej wysyła wiadomość e-mail, a klienta można przywrócić przez 30 dni), a logowanie nieużywane przez sześć miesięcy przestaje działać. Jeśli archiwizujesz rzadko, licz się z ponownym łączeniem, a po bardzo długiej przerwie z utworzeniem nowego klienta (krok 6).

<a name="google-bledy"></a>
## Gdy logowanie do Google się nie udaje

Część błędów Google pojawia się tylko na stronie Google w przeglądarce i nie wraca do aplikacji, która wtedy nadal czeka, najwyżej dziesięć minut. Wybierz **Anuluj** na dole okna aplikacji, usuń przyczynę i połącz ponownie. Błędy, które wracają do aplikacji, są pokazywane po słowach **Komunikat usługi:**.

| Co widzisz | Co to znaczy | Co zrobić |
|---|---|---|
| Aplikacja prosi o wpisanie identyfikatora klienta Google albo klucza tajnego klienta Google przed połączeniem | Pole jest puste, a dla tego identyfikatora klienta nie ma zapisanego klucza | Wklej obie wartości z kroku 6 |
| Aplikacja informuje, że identyfikator klienta Google kończy się na `.apps.googleusercontent.com` | W polu **Identyfikator klienta Google** jest coś innego niż identyfikator klienta; często wklejono tam klucz tajny | Wklej tam identyfikator klienta, a klucz tajny w pole **Klucz tajny klienta Google** |
| `client_secret is missing`, `invalid_client` | Klucz tajny jest pusty albo błędny | Wklej klucz z kroku 6; jeśli zaginął, utwórz nowy przyciskiem **Add secret** |
| **Access blocked**: aplikacja nie przeszła weryfikacji Google albo nie jesteś testerem | Wybranego konta nie ma na liście **Test users** | Krok 5 |
| **Połączono, ale Dysk Google nie jest jeszcze gotowy** | Logowanie się powiodło, ale Dysk Google odrzucił pierwsze żądanie; zwykle nie włączono Drive API | Krok 2, a potem prześlij pliki |
| Mniej więcej tydzień po połączeniu aplikacja otwiera stronę **Ustawienia** z komunikatem **Logowanie do usługi Dysk Google wygasło** | Projekt ma stan **Testing** | [Limit siedmiu dni](#limit-siedmiu-dni): połącz ponownie |
| `deleted_client` | Google usunął klienta z powodu nieaktywności | Przywróć go w konsoli w ciągu 30 dni albo utwórz nowego klienta (krok 6) i wklej nowe wartości |
| `redirect_uri_mismatch` | Klient nie jest typu **Desktop app** | Utwórz nowego klienta właściwego typu (krok 6) |
| `admin_policy_enforced` | Administrator Google Workspace blokuje aplikacje innych firm | Zezwolić może tylko ten administrator |
| `access_denied` albo aplikacja informuje, że logowanie odrzucono | Na stronie Google wybrano **Cancel** | Połącz ponownie i zezwól |
| Aplikacja informuje, że logowanie nie zostało ukończone w ciągu 10 minut | Strona w przeglądarce pozostała bez odpowiedzi albo pokazała błąd | Usuń przyczynę i ponownie wybierz **Połącz z Dyskiem Google** |
| Strona **Chmura** nie pokazuje plików, choć Dysk jest pełen | Tak ma być: aplikacja widzi tylko pliki przesłane za pomocą Twojego klienta | Nie ma czego naprawiać |
| Przesyłanie zatrzymuje się z komunikatem o limicie lub miejscu | Dysk jest pełny albo Google ogranicza liczbę żądań | Zwolnij miejsce na Dysku albo spróbuj później |

<a name="przesylanie"></a>
## Przesyłanie i podgląd chmury

### Przesyłanie

![Karta Kopia w chmurze na dole strony Archiwum, z listą miejsca docelowego i przyciskiem przesyłania](images/pl/07-archive-upload.png)

Karta **Kopia w chmurze** znajduje się na dole strony **Archiwum**.

1. Sprawdź listę **Miejsce docelowe w chmurze**. Wiersz pod nią informuje, czy ta usługa jest połączona.
2. Zaznacz pliki do wysłania. Pliki zapisane w tej sesji są widoczne na stronie i są już zaznaczone. Plik wideo zapisany wcześniej albo w inny sposób dodaje się przyciskiem **Dodaj pliki z tego komputera…**; aplikacja oznacza taki plik jako niesprawdzony przez nią.
3. Wybierz przycisk przesyłania. Jego nazwa zawiera miejsce docelowe: **Prześlij do usługi OneDrive** albo **Prześlij do usługi Dysk Google**.

Jeśli miejsce docelowe nie jest połączone, aplikacja otwiera zamiast tego stronę **Ustawienia** i wyróżnia przycisk łączenia z tą usługą. Zaloguj się, w komunikacie, który się potem pojawi, wybierz **Wróć do Archiwum** i ponownie wybierz przycisk przesyłania.

Co dzieje się podczas przesyłania:

- **Miejsce.** Aplikacja najpierw pyta usługę o ilość wolnego miejsca. Jeśli zaznaczone pliki się nie zmieszczą, informuje o tym i niczego nie wysyła.
- **Postęp.** Wiersz na dole okna podaje nazwę pliku, ilość wysłanych danych, prędkość i pozostały czas. Na czas przesyłania aplikacja prosi system Windows, aby nie usypiał bezczynnego komputera.
- **Przerwy w połączeniu.** Gdy połączenie zostanie przerwane, przesyłanie nie kończy się od razu błędem. Wiersz stanu kończy się wtedy słowami **oczekiwanie na kolejną próbę** i liczy próby. Aplikacja ponawia próby nawet przez piętnaście minut, w których nic nie odpowiada. Gdy usługa znów odpowie, aplikacja pyta ją, jaką część pliku już ma, i kontynuuje od tego miejsca, bez wysyłania pliku od początku. Po piętnastu minutach bez żadnej odpowiedzi plik zostaje uznany za nieprzesłany. Gdy usługa odpowiada, ale prosi o odczekanie, aplikacja pyta ponownie najwyżej pięć razy. Brak miejsca na dysku w chmurze jest zgłaszany od razu.
- **Niepowodzenia.** Plik, którego nie udało się przesłać, nie zatrzymuje pozostałych. Na końcu komunikat wymienia każdy nieprzesłany plik wraz z przyczyną. Takie pliki pozostają zaznaczone, więc przycisk przesyłania spróbuje wysłać je ponownie.
- **Zatrzymanie.** Przycisk **Anuluj** na dole okna zatrzymuje przesyłanie. Pliki już wysłane zostają w chmurze; plik wysyłany w chwili zatrzymania trzeba przesłać od początku. Przy próbie zamknięcia okna w trakcie przesyłania aplikacja najpierw pyta **Zatrzymać i zamknąć?**
- **Logowanie, które wygasło.** Jeśli usługa nie przyjmuje już zapisanego logowania, przesyłanie zostaje przerwane, a aplikacja otwiera stronę **Ustawienia** z komunikatem **Logowanie do usługi OneDrive wygasło** albo **Logowanie do usługi Dysk Google wygasło**. Połącz się ponownie, wybierz **Wróć do Archiwum** i prześlij pliki, które pozostały zaznaczone.

Po przesłaniu:

- Przesłany plik zostaje odznaczony, a na jego karcie pojawia się wiersz taki jak **Przesłano do usługi OneDrive o 14:05**. Jeśli usługa zapisała plik pod inną nazwą, bo nazwa była zajęta, wiersz podaje tę nazwę. Pod nim jest odnośnik **Otwórz w usłudze OneDrive** albo **Otwórz w usłudze Dysk Google**, o ile usługa zwróciła adres pliku. Przy ustawionym [udostępnionym folderze](#shared-folder) odnośnik otwiera ten folder i nosi jego nazwę.
- Jeśli zaznaczysz taki plik i zechcesz przesłać go ponownie w to samo miejsce, aplikacja najpierw zapyta **Przesłać ponownie?** Przycisk **Prześlij ponownie** zapisuje w chmurze drugą kopię; **Nie przesyłaj** niczego nie wysyła.
- Te wiersze i to pytanie działają do zamknięcia aplikacji. Aplikacja nie sprawdza w chmurze, czy plik już tam jest: plik przesłany w poprzedniej sesji i przesłany ponownie zostanie zapisany dwa razy.

Przesyłanie niczego w chmurze nie zastępuje ani nie usuwa. Do przesyłania nie jest potrzebny FFmpeg.

### Podgląd zawartości chmury

![Strona Chmura, która pokazuje zawartość wybranego miejsca docelowego](images/pl/08-cloud.png)

Strona **Chmura** jest w menu po lewej, pod pięcioma krokami i nad pozycją **Ustawienia**. Otwiera ją też przycisk **Zobacz, co jest w chmurze** na stronach **Archiwum** i **Ustawienia**. Jest dostępna w każdej chwili, nie tylko po przesłaniu plików.

Wybierz **Pokaż pliki**. Dla OneDrive strona pokazuje całą zawartość głównego folderu albo [udostępnionego folderu](#shared-folder), jeśli jest ustawiony, nie tylko nagrania. Dla Dysku Google pokazuje tylko pliki przesłane przez aplikację za pomocą Twojego klienta. Najnowsze pozycje są na górze. Każdy wiersz podaje nazwę, rozmiar albo rodzaj pozycji oraz datę ostatniej zmiany; odnośnik **Otwórz w przeglądarce** otwiera pozycję na stronie usługi. Z bardzo długiej listy aplikacja odczytuje tylko początek, około tysiąca pozycji, i o tym informuje.

Nic nie jest pobierane ani zmieniane. Lista jest odczytywana tylko na Twoje życzenie, a przycisk **Odśwież listę** odczytuje ją ponownie. Aplikacja przechowuje jedną listę naraz: do zamknięcia aplikacji albo do chwili, gdy prześlesz coś w to miejsce, zmienisz jego logowanie lub odczytasz listę plików drugiego miejsca docelowego. Przycisk **Zarządzaj połączeniami** otwiera część strony **Ustawienia** poświęconą chmurze.

<a name="logowanie"></a>
## Logowanie w szczegółach

Ta część jest dla osób, które chcą dokładnie wiedzieć, co się dzieje, na przykład przed zarejestrowaniem własnej aplikacji.

- Aplikacja nigdy nie widzi Twojego hasła. Otwiera domyślną przeglądarkę na stronie usługi logowania Microsoft (`login.microsoftonline.com`, punkt końcowy `common`) albo Google (`accounts.google.com`) i to tam się logujesz.
- Odpowiedź wraca pod adres na Twoim komputerze, który istnieje tylko wtedy, gdy aplikacja czeka: `http://localhost:<port>` dla Microsoft, `http://127.0.0.1:<port>` dla Google. Port jest losowany przy każdym logowaniu z zakresu od 49152 do 65534. Aplikacja nasłuchuje wyłącznie na adresach pętli zwrotnej komputera, do których nic spoza niego nie ma dostępu.
- Używana metoda to przepływ kodu autoryzacji OAuth 2.0 z PKCE (metoda S256) i losową wartością `state`, którą odpowiedź musi powtórzyć. Dla Microsoft nie jest używany żaden klucz tajny. Dla Google klucz tajny Twojego klienta jest wysyłany do Google przy wymianie kodu.
- Aplikacja prosi w Microsoft o delegowane uprawnienie Microsoft Graph `Files.ReadWrite` oraz o `offline_access`, a w Google o `https://www.googleapis.com/auth/drive.file`. Gdy ustawiony jest [udostępniony folder](#shared-folder), w miejsce `Files.ReadWrite` prosi o `Files.ReadWrite.All`. O nic więcej nie prosi. Aplikacja zapamiętuje, które z tych dwóch uprawnień przyznał Microsoft, i o to samo prosi przy odnawianiu logowania.
- Aplikacja zawsze prosi usługę o pokazanie listy kont. W Google prosi też o stronę ze zgodą przy każdym logowaniu, aby Google wydał wartość potrzebną później do odnowienia logowania.
- Na dokończenie logowania w przeglądarce masz dziesięć minut. Potem aplikacja przestaje czekać i o tym informuje. Przycisk **Anuluj** na dole okna kończy czekanie wcześniej.
- Po zalogowaniu aplikacja zadaje usłudze jedno pytanie, żeby móc nazwać połączenie: w Microsoft o rodzaj dysku i nazwę jego właściciela, w Google o adres e-mail albo nazwę konta. Jeśli odpowiedź nie nadejdzie w ciągu 15 sekund, logowanie pozostaje ważne, a karta pokazuje usługę bez nazwy.

<a name="dane"></a>
## Co aplikacja przechowuje i jak cofnąć dostęp

### Na tym komputerze

- **Logowania** są w folderze `%LOCALAPPDATA%\Diga\Accounts`, po jednym pliku dla każdej usługi i każdego identyfikatora aplikacji lub klienta. Plik zawiera tokeny Microsoft lub Google, identyfikator, klucz tajny klienta Google oraz nazwę pokazywaną na karcie. Windows szyfruje go dla Twojego konta Windows, więc inny użytkownik Windows ani osoba, która skopiuje pliki na inny komputer, ich nie odczyta. Program działający na Twoim własnym koncie Windows mógłby poprosić system o odszyfrowanie pliku, jak w przypadku wszystkiego, co Windows chroni w ten sposób. Plik, którego aplikacja nie potrafi odszyfrować, na przykład w profilu przywróconym na innym komputerze albo po zresetowaniu hasła, jest traktowany jak brak zapisanego logowania: połącz się ponownie.
- **Link do udostępnionego folderu**, jeśli jest ustawiony, znajduje się w tym samym folderze, w pliku `folder-OneDrive.bin`, zaszyfrowanym w ten sam sposób. Zob. [Udostępniony folder na przesyłane pliki](#shared-folder).
- **Plik ustawień** `%LOCALAPPDATA%\Diga\settings.json` zawiera z ustawień chmury tylko wybrane miejsce docelowe, własny identyfikator aplikacji Microsoft, jeśli go wpisano, oraz identyfikator klienta Google. Nigdy nie zawiera tokenu ani klucza tajnego.
- **Tylko w pamięci**, do zamknięcia aplikacji, pozostają: klucz tajny klienta Google wpisany, ale jeszcze nieużyty do udanego połączenia, lista ze strony **Chmura** oraz wiersze na stronie **Archiwum** informujące o przesłanych plikach.
- **Dziennik błędów** w folderze `%LOCALAPPDATA%\Diga\logs` zapisuje nieudane logowanie lub przesyłanie wraz ze szczegółami technicznymi, wśród których mogą być nazwy plików i komunikat zwrócony przez usługę. Aplikacja podaje, że w dzienniku nie ma haseł ani danych logowania. Na stronie **Ustawienia** są przyciski do otwarcia jego folderu i do usunięcia dziennika.

### Rozłączanie

Przycisk **Rozłącz** na stronie **Ustawienia** jest dostępny, gdy na tym komputerze jest zapisane jakiekolwiek logowanie do danej usługi, także wykonane z identyfikatorem używanym wcześniej. Usuwa wszystkie logowania do danej usługi zapisane na tym komputerze. Zapisuje też ustawienia z tej strony; jeśli nie da się ich zapisać, logowanie i tak zostaje usunięte, a komunikat o tym informuje. Link do udostępnionego folderu jest ustawieniem, a nie logowaniem, i zostaje.

- **OneDrive.** Logowanie znika z tego komputera. Microsoft nie jest o tym informowany, więc zgoda udzielona w Microsoft pozostaje, dopóki nie usuniesz jej na stronie swojego konta. Komunikat po rozłączeniu zawiera odnośnik **Otwórz uprawnienia aplikacji na koncie Microsoft**, który otwiera tę stronę.
- **Dysk Google.** Aplikacja najpierw pyta: **Rozłączyć Dysk Google?** Razem z logowaniem znika zapisany klucz tajny klienta, a Google nie pokazuje klucza po raz drugi, więc zachowaj klucz albo licz się z dodaniem nowego do klienta (krok 6). Następnie aplikacja prosi Google o zakończenie logowania. Według dokumentacji Google cofa to wszystko, na co konto zezwoliło całemu projektowi Google Cloud, więc logowanie kończy się na każdym komputerze, który używa tego samego projektu z tym kontem. Aplikacja czeka na odpowiedź Google najwyżej dziesięć sekund, a potem informuje, czy Google potwierdził zakończenie. Jeśli nie potwierdził, logowanie i tak zostaje usunięte z tego komputera, a komunikat zawiera odnośnik **Otwórz uprawnienia aplikacji na koncie Google**. Aby niczego nie zmieniać, wybierz **Pozostań połączony**.

Google jest proszony o zakończenie tylko tego logowania, które wykonano z używanym identyfikatorem klienta. Logowanie zapisane dla wcześniejszego identyfikatora klienta jest usuwane z tego komputera bez takiej prośby.

### Cofanie zgody po stronie usługi

- Microsoft, konto osobiste: <https://account.microsoft.com/privacy/app-access>
- Microsoft, konto służbowe lub szkolne: <https://myapps.microsoft.com> albo za pośrednictwem administratora
- Google: <https://myaccount.google.com/connections>

Usunięcie zgody na tych stronach kończy logowanie wszędzie, gdzie jest zapisane. Aplikacja dowiaduje się o tym, gdy usługa następnym razem odrzuci logowanie, przy przesyłaniu albo przy odczytywaniu listy plików na stronie **Chmura**, i wtedy prosi o ponowne połączenie.

### Odinstalowanie

- Odinstalowanie aplikacji usuwa folder `Accounts`, a z nim wszystkie zapisane logowania, zapisany klucz tajny klienta Google i link do udostępnionego folderu. Usuwa też domyślny folder plików tymczasowych, dziennik błędów i program FFmpeg pobrany przez aplikację. Plik ustawień `settings.json` i zapisane nagrania zostają.
- Odinstalowanie o niczym nie informuje ani firmy Microsoft, ani Google. Aby Google zakończył logowanie, wybierz **Rozłącz** przed odinstalowaniem albo usuń potem zgodę na stronie Google. Zgodę udzieloną w Microsoft usuwa się na stronie Microsoft.
- Instalacja nowszej wersji na starszą zachowuje zapisane logowania.
- Wersja przenośna (plik ZIP) nie ma dezinstalatora. Aby usunąć logowania i ustawienia, skasuj samodzielnie folder `%LOCALAPPDATA%\Diga`.
