# Cloud setup: OneDrive or Google Drive

*Polska wersja: [CLOUD-SETUP.pl.md](CLOUD-SETUP.pl.md)*

AMG DIGA Archive can upload the recordings it saved to **your own** OneDrive or Google Drive, and show you what is already stored there. Uploading is optional and never starts by itself. This guide takes you through the setup step by step. The rest of the application is described in the [user guide](USER-GUIDE.md).

| | OneDrive, built-in ID | OneDrive, your own registration | Google Drive |
|---|---|---|---|
| Setup before the first sign-in | none | about 20 minutes, once | about 15 minutes, once |
| What you need | a Microsoft account | a Microsoft account, and for a personal account a free Azure sign-up (phone and payment card for identity checks) | a Google account with 2-Step Verification |
| What the application may access | your OneDrive files | your OneDrive files | only the files it uploaded itself |
| How long the sign-in lasts | until you disconnect or stop using it for a long time | the same | 7 days, unless you publish your Google project |
| Recommended for | almost everyone | organisations that block the built-in ID, or people who prefer their own | people who keep their archive in Google Drive |

**How far this guide has been tested.** The project's owner reported on 2 October 2026 that connecting OneDrive with the built-in ID and uploading worked with their Microsoft account. That report was made with version 0.5.2 and the Microsoft registration that was built in at the time. On 5 October 2026 the application received a new built-in registration (application ID `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`). Nobody has reported connecting or uploading through the new registration yet. The only thing checked for it is that Microsoft's sign-in service knows the ID and accepts the `http://localhost` redirect address; that was checked without signing in.

The steps at Microsoft and Google follow Microsoft's and Google's documentation as it read on 3 October 2026: the project has not itself gone through the registration of an own Microsoft application or of a Google client, has not tried a work or school account, and the Google Drive path has so far run only against simulated Google servers in the project's automated tests. What this guide says about the application itself (its pages and messages, how an upload waits and tries again, what **Disconnect** does) is taken from the application's code. The sign-in, upload and listing code is exercised by automated tests against simulated Microsoft and Google servers; apart from the report above, the project has run no sign-in and no upload against the real services.

Portal labels change; where Microsoft's own pages show two generations of a label, both are given. If a step no longer matches what you see, please [open an issue](https://github.com/lukasz-gratkowski/AmgDigaArchive/issues).

## Contents

- [Choosing the destination in the application](#choosing-the-destination-in-the-application)
- [OneDrive, the quick way](#onedrive-the-quick-way)
- [OneDrive with your own registration](#onedrive-with-your-own-registration)
- [Work or school accounts](#work-or-school-accounts)
- [If OneDrive sign-in fails](#if-onedrive-sign-in-fails)
- [Google Drive](#google-drive)
- [If Google sign-in fails](#if-google-sign-in-fails)
- [Uploading and looking at the cloud](#uploading-and-looking-at-the-cloud)
- [The sign-in in detail](#the-sign-in-in-detail)
- [What the application stores, and how to take access back](#what-the-application-stores-and-how-to-take-access-back)

## Choosing the destination in the application

![The cloud section of Settings: the Cloud destination list above the cards for Microsoft OneDrive and Google Drive](images/en/10-settings-cloud.png)

Open **Settings** and scroll to **Your cloud connections**. The list **Cloud destination** decides where **Archive** uploads to and what the **Cloud** page shows. Beside each service the list says **connected**, **not connected** or **sign-in ended**. The same list is on the **Archive** and **Cloud** pages; changing it in one place changes it everywhere, and the choice is remembered at once. You can connect both services and switch between them at any time.

When you connect a service while the chosen destination has no usable sign-in, the application makes the service you have just connected the destination. Otherwise the destination stays as it was, and the message after the sign-in says where uploads go.

Everything else on the **Settings** page is saved with **Save preferences**, the bar at the bottom of the window. The Connect and **Disconnect** buttons save the page as well. If something on the page cannot be saved, for example an empty folder field or a media tool location that is not an existing file, the application says what is wrong and does not start the sign-in.

## OneDrive, the quick way

The application has a Microsoft registration built in, so there is nothing to register and nothing to type.

1. Open **Settings** and scroll to **Your cloud connections**. In the card **Microsoft OneDrive** choose **Connect OneDrive**.
2. Your browser opens Microsoft's sign-in page. The application shows **Finish the sign-in to OneDrive in your browser** and waits for ten minutes at most.
3. Pick the Microsoft account whose OneDrive should receive the recordings. Microsoft always shows its account list here, because the application asks for it: a work account you happen to be signed in with is not used by accident.
4. Microsoft asks whether the application may **have full access to your files** and **maintain access to data you have given it access to**. Accept. If the page calls the publisher **unverified**, see the note below.
5. The browser shows one line of text saying that the application has received the sign-in. Close the tab and return to the application. The card now shows **Connected · OneDrive (personal)** or **Connected · OneDrive (work or school)**, followed by the name of the drive's owner.

The application uses the permission to add new files to the top folder of your OneDrive, to list that folder, and to ask for the kind of drive, its owner's name and its free space. It never changes or deletes a file that is already there: if a file with the same name exists, OneDrive stores the new one under a different name.

To use another Microsoft account later, choose **Connect OneDrive** again and pick the other account on Microsoft's page. There is no need to disconnect first.

**If you connected OneDrive in version 0.5.2 with the built-in ID.** That version had a different built-in application ID, and a sign-in belongs to the ID it was made with. After the update the card says that OneDrive is not connected: choose **Connect OneDrive** once more. The sign-in saved by version 0.5.2 stays on the PC unused; **Disconnect** removes it together with the new one. The permission you gave to the earlier registration stays in your Microsoft account until you remove it there; see [the last section](#what-the-application-stores-and-how-to-take-access-back).

> **Why "unverified"?** Microsoft shows a publisher name only for applications registered by a company enrolled in its partner programme. The permission itself is the one you see on the page, and it applies only to the account you sign in with. The application ID is an identifier, not a password: it tells Microsoft which application is asking.

With a **work or school** account, read [Work or school accounts](#work-or-school-accounts) first.

## OneDrive with your own registration

You need this only if you prefer not to use the built-in ID, or if your organisation blocks it. A registration identifies the application to Microsoft; it is created once, costs nothing, and holds no password.

The portal's labels are translated when the portal runs in another language. To follow this guide word for word, switch the portal to English with the gear icon at the top (**Language + region**).

### Part A: get a directory (personal Microsoft accounts only)

Since June 2024 Microsoft lets an application be registered only inside a *directory* (also called a *tenant*). A work or school account already belongs to one. A personal account (`outlook.com`, `hotmail.com`, `live.com` and the like) usually does not.

1. Open <https://entra.microsoft.com> and sign in. If you get in and see a directory name under your account name at the top right, you already have a directory: go to Part B. If you see an error saying that your account does not exist in a tenant (for example `AADSTS16000` or `AADSTS50020`), a long ID instead of a name, or a message that applications can no longer be created outside a directory, continue here.
2. Open <https://azure.microsoft.com/free> and choose **Try Azure for free**. Sign in with the same Microsoft account.
3. Fill in **About you**; the country must be the one of your card's billing address. Confirm your phone number by text message or call (internet phone numbers are not accepted), then confirm a credit or debit card (prepaid and virtual cards are refused). Accept the agreement. If the card step does not load, allow third-party cookies for the sign-up page.
4. What it costs: Microsoft states that it does not charge the card at sign-up and charges only if you later choose to move to pay-as-you-go. A temporary hold of about one dollar may appear and disappears again. Do not create any Azure services. After 30 days Microsoft disables the trial subscription; Microsoft documents that the directory remains. A directory that is not used can be removed later: see the note on inactivity after Part C.
5. Microsoft requires two-step sign-in for its admin pages, so expect to be asked to set it up.

The free account is offered once, to people who are new to Azure. If you had Azure before, Microsoft offers pay-as-you-go instead; the project has not tried that path. Students with a school e-mail address can use *Azure for Students*, which needs no card; whether it gives a personal account a directory was not established.

### Part B: register the application

6. In <https://entra.microsoft.com> go to **Entra ID → App registrations → New registration**. (Older menu: **Identity → Applications → App registrations**. In `portal.azure.com`: open **Microsoft Entra ID**, then **App registrations**.) If you have several directories, pick the right one with the gear icon at the top.
7. **Name**: anything you will recognise, for example `My DIGA upload`.
8. **Supported account types**: choose **Any Entra ID Tenant + Personal Microsoft accounts** (older wording: **Accounts in any organizational directory and personal Microsoft accounts**). Microsoft's form suggests **Single tenant only**; do not keep that, and do not choose **Personal accounts only**. The application does not work with either.
9. If the form shows a **Redirect URI** field, leave it empty. Select **Register**.
10. On the **Overview** page copy **Application (client) ID**. Not the Object ID and not the Directory (tenant) ID.
11. Under **Manage** open **Authentication** (it may be labelled **Authentication (Preview)**). On the **Redirect URI configuration** tab choose **Add Redirect URI**, pick **Mobile and desktop applications**, enter `http://localhost` as a custom redirect URI and select **Configure**. (Older layout: **Platform configurations → Add a platform**.)
    - Enter it exactly: `http`, not `https`; no port, no slash, no path. Do not use `127.0.0.1` here, although one of Microsoft's pages recommends it: the application sends `localhost`.
    - Do not add it under **Web** or **Single-page application**.
12. Leave **Allow public client flows** off (it is on the **Settings** tab of the Authentication page in the new layout and under **Advanced settings** in the old one). Do not create anything under **Certificates & secrets**: the application uses no secret for OneDrive.
13. Open **API permissions → Add a permission → Microsoft Graph → Delegated permissions**, tick **Files.ReadWrite** and **offline_access**, then **Add permissions**. Keep the **User.Read** entry Microsoft added. For a personal account this step is optional, because the application asks for the two permissions at sign-in anyway; it matters when an organisation's administrator has to approve the application.
14. Wait about five minutes. Microsoft's changes are not instant.

### Part C: connect in the application

15. In the application open **Settings** and scroll to **Your cloud connections**. In the card **Microsoft OneDrive** open the section **Using your own Microsoft registration** and paste the ID into **Your own application ID**. Then choose **Connect OneDrive**. Connecting saves the settings on the page as well.
16. In the browser pick the account that owns the OneDrive and accept the request. The publisher is shown as unverified; for your own registration that is expected.
17. The card should now show **Connected · OneDrive (personal)** or **Connected · OneDrive (work or school)** and the owner's name, as in the quick way. Try it with one small file from **Archive**.

The field takes the ID in the form Microsoft shows it, 36 characters with four hyphens. The application refuses anything else before it opens the browser. While the field is empty it shows **Empty: the built-in ID is used**, and the line below it names the built-in ID.

A sign-in belongs to the application ID it was made with. After you change the ID, the card says that OneDrive is not connected, and you connect again. Changing the ID does not remove the sign-in saved for the earlier one: it stays on this PC unused, is used again if you put the earlier ID back, and is removed by **Disconnect**, which removes every OneDrive sign-in saved on this PC.

To go back to the built-in ID, clear the field and choose **Save preferences**. A sign-in made earlier with the built-in ID is used again if it is still saved on this PC; otherwise choose **Connect OneDrive**.

**A directory can be lost to inactivity.** Microsoft's documentation says that Microsoft blocks a directory that is no longer in use, and deletes it if it stays blocked for more than 20 days. A registration goes with its directory. The documentation does not say what counts as use. People writing on Microsoft's Q&A site quote an e-mail from Microsoft that calls a directory inactive after more than 200 days and asks for a purchase by a given date; the answers there disagree on whether signing in now and then is enough to keep the directory. The project does not know. Watch for such an e-mail from Microsoft.

If your own registration stops working with `AADSTS5000225`, Microsoft has blocked the directory for inactivity. Microsoft's documentation says an administrator can ask Microsoft to reactivate it within 20 days; after that the directory is deleted, and the registration has to be made again in a new directory.

**To undo everything:** choose **Disconnect** in the application, remove the application under <https://account.microsoft.com/privacy/app-access> (personal accounts), and delete the registration in the admin centre.

## Work or school accounts

This section is based on Microsoft's documentation; the project has not tried a work or school account.

Many organisations do not let their members approve an application themselves, or allow it only for applications from verified publishers. Microsoft also treats a recently registered application that asks for more than basic sign-in, has no verified publisher and comes from another organisation as risky. In those cases you see **Need admin approval** (or an error such as `AADSTS90094`, `AADSTS90093` or `AADSTS900941`) instead of the permission page. What you can do:

- **Ask your administrator** to approve the application. They can do it in the Entra admin centre under **Enterprise apps**, or with Microsoft's admin-consent address; see Microsoft's [Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent). Give them the application ID. The built-in one of this version is `bfd21bf0-32a9-4520-8bbb-d525e3d34aea`; the application shows it in **Settings**, in the section **Using your own Microsoft registration**. The permissions are the delegated Microsoft Graph permissions **Files.ReadWrite** and **offline_access**.
- **Register your own application inside your organisation's directory** (Part B above, if your organisation lets members register applications). Microsoft's rules about unverified publishers do not apply to an application registered in your own organisation.
- If your organisation uses an approval workflow you see **Approval required** with a text box: send the request and wait for the e-mail.

Errors that mention Conditional Access or multi-factor sign-in (`AADSTS53003`, `AADSTS50076`) come from your organisation's own rules; only its IT department can change them.

## If OneDrive sign-in fails

Microsoft names the cause on its own page in the browser, or the application shows it after the words **Message from the service:**.

| What you see | What it means | What to do |
|---|---|---|
| A message that applications cannot be created outside a directory; `AADSTS16000` or `AADSTS50020` when opening the admin centre | The personal account has no directory | Part A |
| `AADSTS50011` | The redirect address does not match the registration | Step 11: exactly `http://localhost` under **Mobile and desktop applications**; then wait a few minutes |
| `AADSTS50194` | The registration is single-tenant | Step 8. The simplest fix is to delete the registration and create it again |
| `AADSTS9002331` | The registration is for personal accounts only (this code is explained in a Microsoft Q&A answer, not in the official error list) | Step 8, as above |
| `unauthorized_client`, "not enabled for consumers" | A personal account was used with a registration that excludes personal accounts, or the ID is wrong | Steps 8 and 10 |
| `AADSTS700016` | Microsoft does not know this application ID | The pasted ID is wrong (step 10) or the registration was deleted. Clearing the field returns to the built-in ID |
| The application says that a Microsoft application ID has the form `00000000-0000-0000-0000-000000000000` | What is in **Your own application ID** is not an application ID | Paste the **Application (client) ID** from step 10, or clear the field to use the built-in ID |
| `AADSTS7000218` | Microsoft expects a client secret, because the redirect address is registered as **Web** | Move it to **Mobile and desktop applications** and remove any `localhost` entry under **Web**. If the error stays although that is already the case, set **Allow public client flows** to **Yes** (step 12) and try again; this fallback is untested |
| `AADSTS65004`, or the application says the sign-in was declined | **Cancel** was chosen on the permission page | Connect again and accept |
| `AADSTS65001` | No permission is recorded for this application | Connect again and accept; in an organisation, ask the administrator |
| **Need admin approval**, `AADSTS90094`, `AADSTS90093`, `AADSTS900941` | Your organisation does not let you approve this application | [Work or school accounts](#work-or-school-accounts) |
| `AADSTS5000225` | Microsoft has blocked the directory holding the registration for inactivity, and deletes it if it stays blocked for more than 20 days | Your own registration: ask Microsoft to reactivate the directory within 20 days, or make the registration again in a new directory. Built-in ID: report it |
| The browser shows an error and the application keeps waiting | Some errors stay on Microsoft's page and never return to the application | Choose **Cancel** at the bottom of the application's window, fix the cause and connect again |
| The application says the sign-in was not finished within 10 minutes | The page in the browser was left unanswered | Choose **Connect OneDrive** again |
| OneDrive is shown as not connected after an update from version 0.5.2 | The built-in application ID is a new one | Choose **Connect OneDrive** once more; see [OneDrive, the quick way](#onedrive-the-quick-way) |
| During an upload, or when you list the files on the **Cloud** page, the application opens **Settings** and says **The sign-in to OneDrive has ended** | Microsoft no longer accepts the saved sign-in: it expired, or the permission was removed | Choose **Connect OneDrive** again, then **Back to Archive** or **Back to Cloud** |

## Google Drive

Google Drive has no built-in ID: Google's terms do not allow an open-source project to publish its own Google credentials, so every user creates a small *Google Cloud project* of their own. It is free for this use and takes about 15 minutes. What you create is a pair of values, a **client ID** and a **client secret**, which you paste into the application.

**Before you start**

- Any Google account can own the project; it need not be the account whose Drive receives the files.
- Google requires **2-Step Verification** on a personal account before it opens the Google Cloud console.
- The application asks Google for one permission only: to see and manage **the files it created itself**. It cannot see, change or delete anything else in the Drive. That is also why the **Cloud** page lists only the application's own uploads when Google Drive is the destination.

### Create the project and the client

1. **Create a project.** Open <https://console.cloud.google.com/projectcreate>. Enter a project name, leave **Location** as it is, choose **Create**, and make sure the new project is the one selected at the top of the page.
2. **Enable the Drive API.** Menu → **APIs & Services → Library**, open **Google Drive API**, choose **Enable**.
3. **Describe the sign-in screen.** Menu → **Google Auth platform → Branding** (<https://console.cloud.google.com/auth/branding>). If you see *Google Auth platform not configured yet*, choose **Get started**.
   - **App Information**: an app name of your choice (do not use a Google product name on its own, such as "Google Drive") and your e-mail address as the support address. **Next**.
   - **Audience**: **External**. (**Internal** exists only for Google Workspace organisations.) **Next**.
   - **Contact Information**: your e-mail address. **Next**.
   - **Finish**: tick the agreement to Google's user data policy, then **Continue** and **Create**.
4. **Add the permission.** **Data Access → Add or remove scopes**. Tick the entry ending in `/auth/drive.file`, or paste `https://www.googleapis.com/auth/drive.file` under **Manually add scopes**. **Update**, then **Save**. It must appear in the list of *non-sensitive* scopes.
5. **Add yourself as a test user.** **Audience → Test users → Add users**; enter the Google account (or accounts) whose Drive will receive the uploads. **Save**.
6. **Create the client.** **Clients → Create client**; **Application type**: **Desktop app**; any name; **Create**.
   **Copy the client ID and the client secret now**, or download the JSON file Google offers. Google shows the secret only at this moment; afterwards only its last four characters are visible. If you lose it, open the client and use **Add secret** to make a new one. Do not choose **Web application**: it does not work with a desktop application.

### Connect in the application

7. In the application open **Settings** and scroll to **Your cloud connections**. In the card **Google Drive** paste the values into **Google client ID** and **Google client secret**, then choose **Connect Google Drive**. Connecting saves the settings on the page as well.
8. In the browser pick the Google account. Google warns that the app has not been verified or is being tested; that is your own project, so continue. Allow access to *the specific Google Drive files you use with this app*. Google shows these pages at every sign-in, because the application asks for them.
9. The browser shows one line of text saying that the application has received the sign-in. Close the tab and return to the application. The card now shows **Connected · Google Drive** and the account's e-mail address.

If the application says **Connected, but Google Drive is not ready yet** instead, the sign-in worked but Google Drive refused the application's first request. The usual reason is that the Drive API is not enabled in your project: go back to step 2, then upload.

Google's documentation calls the client secret optional for desktop applications, but developers report that Google's sign-in service rejects a Desktop client without it, so the application asks for both values. The client ID is written to the settings file. The secret is not. A secret you typed is remembered until you close the application, also when a sign-in fails or you open another page. Once you have connected it is saved together with the sign-in, encrypted for your Windows account. From then on the field is empty and shows **Saved. Leave empty to keep it**: when you connect again later, leave it empty and the saved secret is used.

A sign-in and its saved secret belong to the client ID they were made with. After you change **Google client ID**, the card says that Google Drive is not connected, and you paste the secret of the new client and connect again. The sign-in saved for the earlier client ID stays on this PC unused until you choose **Disconnect**.

To use another Google account, choose **Connect Google Drive** again and pick the account on Google's page; there is no need to disconnect first. The account has to be among the test users of your project (step 5).

Uploads go to the top level of **My Drive**. A file with a name that already exists is stored as a second file with the same name; Google Drive allows that, and nothing is replaced.

### The seven-day limit

A new Google project is in the publishing status **Testing**. In that status Google ends every sign-in **seven days** after you gave permission. The application does not contact Google when it starts, so the card can still say that Google Drive is connected. The next upload, or the next time you list the files on the **Cloud** page, takes you to **Settings** with the message **The sign-in to Google Drive has ended**, and the list **Cloud destination** then shows **sign-in ended** beside Google Drive. Choose **Connect Google Drive** again. The client ID and the saved secret stay as they are; you do not type the secret again.

To remove the limit the project has to be switched to **In production** with **Audience → Publish app**. Google's help says that a home page and a privacy policy are required for external projects in production, and that the domains of both addresses must first be registered under **Authorized domains**. Users report that the button stays disabled until **Branding** contains both addresses; the project has not checked this. In practice this means:

- **You have a website of your own:** add a page to it describing what your project does with data (it uploads your own recordings to your own Drive and nothing else). In **Branding**, add the site's domain under **Authorized domains**, then enter the site's address as the home page and the new page as the privacy policy. Then choose **Publish app** and connect again in the application. For this single non-sensitive permission Google does not demand a review.
- **You do not:** stay in **Testing** and connect again when the application asks. For an archive you add to a few times a year this is usually no burden.

The project has not gone through publishing itself; users' reports about which addresses Google accepts differ.

### Keep it alive

Google deletes a client that has not been used for six months (it sends an e-mail 30 days before, and the client can be restored for 30 days), and a sign-in that has not been used for six months stops working. If you archive rarely, expect to connect again, and after a very long pause to create a new client (step 6).

## If Google sign-in fails

Several of Google's errors appear only on Google's page in the browser and never return to the application, which then keeps waiting, for ten minutes at most. Choose **Cancel** at the bottom of the application's window, fix the cause and connect again. Errors that do return are shown by the application after the words **Message from the service:**.

| What you see | What it means | What to do |
|---|---|---|
| The application asks you to enter the Google client ID or the Google client secret before connecting | A field is empty, and no secret is saved for this client ID | Paste both values from step 6 |
| The application says that a Google client ID ends in `.apps.googleusercontent.com` | What is in **Google client ID** is not a client ID; often the secret was pasted there | Paste the client ID there and the secret into **Google client secret** |
| `client_secret is missing`, `invalid_client` | The secret is empty or wrong | Paste the secret from step 6; if it is lost, create a new one with **Add secret** |
| **Access blocked**: the app has not completed Google's verification, or you are not a tester | The account you picked is not in **Test users** | Step 5 |
| **Connected, but Google Drive is not ready yet** | The sign-in worked, but Google Drive refused the first request; usually the Drive API is not enabled | Step 2, then upload |
| The application opens **Settings** and says **The sign-in to Google Drive has ended**, about a week after connecting | The project is in **Testing** | [The seven-day limit](#the-seven-day-limit): connect again |
| `deleted_client` | Google removed the client for inactivity | Restore it in the console within 30 days, or create a new client (step 6) and paste the new values |
| `redirect_uri_mismatch` | The client is not of the type **Desktop app** | Create a new client of the right type (step 6) |
| `admin_policy_enforced` | A Google Workspace administrator blocks third-party applications | Only that administrator can allow it |
| `access_denied`, or the application says the sign-in was declined | **Cancel** was chosen on Google's page | Connect again and allow |
| The application says the sign-in was not finished within 10 minutes | The page in the browser was left unanswered, or showed an error | Fix the cause and choose **Connect Google Drive** again |
| The **Cloud** page shows no files although the Drive is full | By design: the application sees only files it uploaded with your client | Nothing to fix |
| An upload stops with a message about quota or storage | The Drive is full, or Google is limiting requests | Free space in the Drive, or try again later |

## Uploading and looking at the cloud

### Uploading

![The card A copy in the cloud at the bottom of the Archive page, with the destination list and the upload button](images/en/07-archive-upload.png)

The card **A copy in the cloud** is at the bottom of the **Archive** page.

1. Check **Cloud destination**. The line below it says whether that service is connected.
2. Tick the files to send. Files saved in this session are listed on the page and are ticked already. A video file saved earlier, or by other means, is added with **Add files from this PC…**; the application marks such a file as not checked by it.
3. Choose the upload button. It is named after the destination: **Upload to OneDrive** or **Upload to Google Drive**.

If the destination is not connected, the application opens **Settings** instead, with the Connect button for that service highlighted. Sign in, choose **Back to Archive** in the message that follows, and choose the upload button again.

What happens during an upload:

- **Room.** The application first asks the service how much room is free. If the ticked files do not fit, it says so and sends nothing.
- **Progress.** The line at the bottom of the window names the file and shows how much of it has been sent, the speed and the time left. While an upload runs, the application asks Windows not to put the idle PC to sleep.
- **Interruptions.** When the connection is lost, the upload does not fail at once. The status line then ends with **waiting to try again** and counts the attempts. The application keeps trying for as long as fifteen minutes in which nothing answers. As soon as the service answers again, the application asks it how much of the file it holds and continues from there, without starting the file again. After fifteen minutes without any answer the file counts as failed. When the service answers but asks to wait, it is asked again up to five times. A full drive is reported at once.
- **Failures.** A file that fails does not stop the others. At the end the message lists every file that was not uploaded, with the reason. Those files stay ticked, so the upload button tries them again.
- **Stopping.** **Cancel** at the bottom of the window stops the upload. Files already sent stay in the cloud; the file that was being sent has to start again. Closing the window during an upload asks **Stop and close?** first.
- **A sign-in that has ended.** If the service no longer accepts the saved sign-in, the upload stops and the application opens **Settings** with the message **The sign-in to OneDrive has ended** or **The sign-in to Google Drive has ended**. Connect again, choose **Back to Archive**, and upload the files that are still ticked.

After an upload:

- An uploaded file is unticked and gets a line such as **Uploaded to OneDrive at 14:05** on its card. If the service stored the file under another name because the name was taken, the line gives that name. Below it is a link, **Open in OneDrive** or **Open in Google Drive**, when the service returned an address for the file.
- If you tick such a file and upload it to the same destination again, the application asks **Upload again?** first. **Upload again** stores a second copy; **Do not upload** sends nothing.
- These lines and the question last until you close the application. The application does not look in the cloud for a file that is already there: a file uploaded in an earlier session and uploaded again is stored twice.

Nothing in the cloud is replaced or deleted by an upload. Uploading does not need FFmpeg.

### Looking at what is stored

![The Cloud page, which lists what is stored in the chosen destination](images/en/08-cloud.png)

The **Cloud** page is in the menu on the left, below the five steps and above **Settings**. **See what is in the cloud** on the **Archive** page and in **Settings** opens it too. It is available at any time, not only after an upload.

Choose **List the files**. For OneDrive the page lists everything in the top folder, not only recordings. For Google Drive it lists only the files the application uploaded with your client. The newest items come first. Each line gives the name, the size or the kind of item, and the date of the last change; **Open in browser** opens the item on the service's own site. Of a very long list the application reads only the first part, about a thousand items, and says so.

Nothing is downloaded and nothing is changed. The list is read only when you ask, and **Refresh the list** reads it again. The application keeps one list at a time: until you close the application, upload to that destination, change its sign-in, or list the files of the other destination. **Manage connections** opens the cloud section of **Settings**.

## The sign-in in detail

This section is for readers who want to know exactly what happens, for example before they register an application of their own.

- The application never sees your password. It opens your default browser at Microsoft's sign-in service (`login.microsoftonline.com`, the `common` endpoint) or at Google's (`accounts.google.com`), and you sign in there.
- The answer comes back to an address on your own PC that exists only while the application waits: `http://localhost:<port>` for Microsoft, `http://127.0.0.1:<port>` for Google. The port is chosen at random for each sign-in, between 49152 and 65534. The application listens on the PC's loopback addresses only, which nothing outside the PC reaches.
- The method is the OAuth 2.0 authorisation code flow with PKCE (method S256) and a random `state` value that the answer has to repeat. For Microsoft no client secret is used. For Google the secret of your own client is sent to Google when the code is exchanged.
- The permissions asked for are, at Microsoft, the delegated Microsoft Graph permission `Files.ReadWrite` and `offline_access`, and at Google `https://www.googleapis.com/auth/drive.file`. Nothing else is asked for.
- The application always asks the service to show its account list. At Google it also asks for the permission page at every sign-in, so that Google issues the value the application needs to renew the sign-in later.
- You have ten minutes to finish in the browser. After that the application stops waiting and says so. **Cancel** at the bottom of the window ends the wait sooner.
- After the sign-in the application asks the service one question, so that it can name the connection: the kind of drive and its owner's name at Microsoft, the account's e-mail address or name at Google. If no answer comes within 15 seconds, the sign-in stands and the card shows the service without a name.

## What the application stores, and how to take access back

### On this PC

- **The sign-ins** are in `%LOCALAPPDATA%\Diga\Accounts`, one file for each service and each application or client ID. A file holds Microsoft's or Google's tokens, the ID, the Google client secret, and the name shown on the card. Windows encrypts it for your Windows account, so another Windows user, or someone who copies the files to another PC, cannot read it. A program that runs under your own Windows account could ask Windows to decrypt it, as with anything Windows protects this way. A file the application cannot decrypt, for example in a profile restored on another PC or after a password reset, counts as no saved sign-in: connect again.
- **The settings file** `%LOCALAPPDATA%\Diga\settings.json` holds, of the cloud settings, only the chosen destination, your own Microsoft application ID if you entered one, and the Google client ID. It never holds a token or the client secret.
- **Kept in memory only**, until you close the application: a Google client secret you typed but have not connected with yet, the list on the **Cloud** page, and the lines on **Archive** that say what was uploaded.
- **The error log** in `%LOCALAPPDATA%\Diga\logs` records a failed sign-in or upload with its technical details, which can include file names and the message the service returned. The application states that the log holds no passwords and no sign-ins. **Settings** has buttons to open its folder and to delete it.

### Disconnect

**Disconnect** in **Settings** is available when any sign-in for that service is saved on this PC, also when the only one left was made with an ID you used earlier. It removes every sign-in saved on this PC for that service. It also saves the settings on the page; if they cannot be saved, the sign-in is removed all the same and the message says so.

- **OneDrive.** The sign-in leaves this PC. Microsoft is not told, so the permission you gave at Microsoft stays until you remove it on your account's page. The message after disconnecting has the link **Open the app permissions of your Microsoft account**, which opens that page.
- **Google Drive.** The application asks first: **Disconnect Google Drive?** The saved client secret goes with the sign-in, and Google does not show a secret a second time, so keep the secret somewhere or be ready to add a new one to the client (step 6). The application then asks Google to end the sign-in. According to Google's documentation this withdraws what the account granted to the whole Google Cloud project, so the sign-in ends on every PC that uses the same project with this account. The application waits up to ten seconds for Google's answer and then says whether Google confirmed. If Google did not confirm, the sign-in is removed from this PC all the same, and the message has the link **Open the app permissions of your Google account**. Choose **Stay connected** to change nothing.

Google is asked to end only the sign-in made with the client ID in use. A sign-in saved for an earlier client ID is removed from this PC without that request.

### Taking the permission back at the service

- Microsoft, personal account: <https://account.microsoft.com/privacy/app-access>
- Microsoft, work or school account: <https://myapps.microsoft.com>, or ask your administrator
- Google: <https://myaccount.google.com/connections>

Removing the permission there ends the sign-in wherever it is saved. The application learns of it when the service next refuses the sign-in, at an upload or when you list the files on the **Cloud** page, and then asks you to connect again.

### Uninstalling

- Uninstalling the application removes the `Accounts` folder, and with it every saved sign-in and the saved Google client secret. It also removes the default folder for temporary files, the error log and the FFmpeg the application downloaded. The settings file `settings.json` and your saved recordings stay.
- Uninstalling tells neither Microsoft nor Google anything. To have Google end the sign-in, choose **Disconnect** before you uninstall, or remove the permission on Google's page afterwards. The permission at Microsoft is removed on Microsoft's page.
- Installing a newer version over an older one keeps the saved sign-ins.
- The portable ZIP has no uninstaller. Delete the folder `%LOCALAPPDATA%\Diga` yourself to remove the sign-ins and the settings.
